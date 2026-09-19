using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace Shinimodori.Client
{
    /// <summary>
    /// Owns everything the mod makes a noise with (§12.4).
    ///
    /// World audio is ducked as a whole rather than stopped sound by sound, so the
    /// silence during a grip or the Void is total and instant — which is most of why
    /// those moments land.
    /// </summary>
    public class AudioDirector : IDisposable
    {
        private readonly ICoreClientAPI capi;
        private readonly ShinimodoriConfig cfg;
        private readonly Dictionary<string, ILoadedSound> loops = new Dictionary<string, ILoadedSound>();

        private bool ducked;
        private float duckLevel = 1f;
        private float duckTarget = 1f;
        /// <summary>The player's own volume, remembered so it is always given back.</summary>
        private int originalSoundLevel = -1;

        public AudioDirector(ICoreClientAPI capi, ShinimodoriConfig cfg)
        {
            this.capi = capi;
            this.cfg = cfg;
            // Ducking borrows the player's volume setting, so give it back on every
            // way out of a world, not just on a clean Dispose.
            capi.Event.LeaveWorld += RestoreLevel;
        }

        private static AssetLocation Loc(string name) => new AssetLocation("shinimodori", "sounds/" + name);

        /// <summary>Plays a one-shot at the player, in their head rather than in the world.</summary>
        public void PlayOneShot(string name, float volume = 1f, float pitch = 1f)
        {
            try
            {
                var sound = capi.World.LoadSound(new SoundParams
                {
                    Location = Loc(name),
                    ShouldLoop = false,
                    RelativePosition = true,
                    Position = new Vintagestory.API.MathTools.Vec3f(0, 0, 0),
                    DisposeOnFinish = true,
                    Volume = volume * cfg.Audio.MasterVolume,
                    Pitch = pitch,
                    Range = 8,
                    SoundType = EnumSoundType.Sound,
                });
                sound?.Start();
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[shinimodori] sound '{0}' unavailable: {1}", name, e.Message);
            }
        }

        /// <summary>Starts a looping bed, or leaves it alone if it is already running.</summary>
        public void StartLoop(string name, float volume = 1f, float pitch = 1f)
        {
            if (loops.TryGetValue(name, out var existing) && existing != null && !existing.IsDisposed)
            {
                existing.SetVolume(volume * cfg.Audio.MasterVolume);
                return;
            }

            try
            {
                var sound = capi.World.LoadSound(new SoundParams
                {
                    Location = Loc(name),
                    ShouldLoop = true,
                    RelativePosition = true,
                    Position = new Vintagestory.API.MathTools.Vec3f(0, 0, 0),
                    DisposeOnFinish = false,
                    Volume = volume * cfg.Audio.MasterVolume,
                    Pitch = pitch,
                    Range = 8,
                    SoundType = EnumSoundType.Ambient,
                });
                if (sound == null) return;
                sound.Start();
                loops[name] = sound;
            }
            catch (Exception e)
            {
                capi.Logger.Debug("[shinimodori] loop '{0}' unavailable: {1}", name, e.Message);
            }
        }

        public void SetLoopVolume(string name, float volume)
        {
            if (loops.TryGetValue(name, out var s) && s != null && !s.IsDisposed)
                s.SetVolume(volume * cfg.Audio.MasterVolume);
        }

        public void SetLoopPitch(string name, float pitch)
        {
            if (loops.TryGetValue(name, out var s) && s != null && !s.IsDisposed) s.SetPitch(pitch);
        }

        public void StopLoop(string name, float fadeSeconds = 0.3f)
        {
            if (!loops.TryGetValue(name, out var s) || s == null) return;
            try { if (fadeSeconds > 0) s.FadeOutAndStop(fadeSeconds); else s.Stop(); } catch { }
            loops.Remove(name);
        }

        public void StopAllLoops()
        {
            foreach (var key in new List<string>(loops.Keys)) StopLoop(key, 0.2f);
        }

        /// <summary>
        /// Ducks the whole world to near silence, or lets it back up. The ramp is fast
        /// but not instantaneous, which reads as the world being taken away rather
        /// than as an audio glitch.
        /// </summary>
        public void Duck(bool on, float toLevel = -1f)
        {
            ducked = on;
            duckTarget = on ? (toLevel >= 0 ? toLevel : cfg.Audio.DuckedVolume) : 1f;
        }

        public void OnTick(float dt)
        {
            if (Math.Abs(duckLevel - duckTarget) < 0.005f)
            {
                if (!ducked && originalSoundLevel >= 0 && duckLevel >= 0.999f) RestoreLevel();
                return;
            }

            // 120ms to cut, slower to come back: §12.1 A and D.
            float speed = duckTarget < duckLevel ? dt / 0.12f : dt / 1.5f;
            duckLevel += Math.Sign(duckTarget - duckLevel) * Math.Min(Math.Abs(duckTarget - duckLevel), speed);
            ApplyLevel(duckLevel);
        }

        private void ApplyLevel(float level)
        {
            try
            {
                if (originalSoundLevel < 0) originalSoundLevel = ClientSettings.SoundLevel;
                ClientSettings.SoundLevel = (int)Math.Round(originalSoundLevel * level);
            }
            catch (Exception)
            {
                // If the setting is not writable in this build, the mod's own beds still
                // carry the scene; the world simply stays audible underneath them.
            }
        }

        private void RestoreLevel()
        {
            try { if (originalSoundLevel >= 0) ClientSettings.SoundLevel = originalSoundLevel; } catch { }
            originalSoundLevel = -1;
        }

        public void Dispose()
        {
            StopAllLoops();
            RestoreLevel();
            try { capi.Event.LeaveWorld -= RestoreLevel; } catch { }
        }
    }
}
