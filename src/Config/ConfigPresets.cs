using System;
using Vintagestory.API.Common;

namespace Shinimodori.Config
{
    /// <summary>
    /// The four shipped presets (§14). A preset is a bundle of overrides applied on
    /// top of the defaults; "custom" means "leave my file alone".
    /// </summary>
    public static class ConfigPresets
    {
        public static readonly string[] Names = { "balanced", "anime", "forgiving", "cinematic", "custom" };

        public static bool IsKnown(string name) => Array.IndexOf(Names, name?.ToLowerInvariant()) >= 0;

        /// <summary>Applies a preset in place. Returns false if the name is unknown.</summary>
        public static bool Apply(ShinimodoriConfig c, string preset)
        {
            switch (preset?.ToLowerInvariant())
            {
                case "custom":
                    return true;

                case "balanced":
                    // The defaults in ShinimodoriConfig *are* balanced; nothing to override.
                    return true;

                case "anime":
                    // Maximum fidelity to the show. Unkind on purpose.
                    c.Anchors.AnchorFeedback = "none";
                    c.Taboo.KillsListeners = true;
                    c.Core.RewindKnowledge = false;
                    c.Core.DeathScars = true;
                    c.Visuals.HudMode = "diegetic";
                    c.Miasma.DecayPerHour = 0.375f;
                    c.Miasma.ClingDecayMultiplier = 0.5f;
                    c.Visuals.EffectIntensity = 1.0f;
                    return true;

                case "forgiving":
                    c.Anchors.AnchorTimeoutHours = 8.0;
                    c.Anchors.MinAnchorSpacingHours = 3.0;
                    c.Miasma.GainPerReturn = 5f;
                    c.Miasma.DecayPerHour = 1.5f;
                    c.Trauma.PhantomPainPerReturn = 0.075f;
                    c.Trauma.PhantomPainFloor = 0.25f;
                    c.Taboo.KillsListeners = false;
                    c.Taboo.Stage3Seconds = 2.5f;
                    // Stage 3 still grips, but it does not end in a return.
                    c.Taboo.ForgivingStage3 = true;
                    c.Anchors.AnchorFeedback = "explicit";
                    c.Visuals.HudMode = "minimal";
                    return true;

                case "cinematic":
                    // Everything looks its best and almost nothing hurts. For capture.
                    c.Visuals.EffectIntensity = 1.0f;
                    c.Visuals.UseCustomShaders = true;
                    c.Visuals.ScreenShake = true;
                    c.Visuals.PeripheralSilhouettes = true;
                    c.Miasma.GainPerReturn = 2f;
                    c.Miasma.DecayPerHour = 3f;
                    c.Trauma.PhantomPainPerReturn = 0f;
                    c.Trauma.DespairPerDeathAtAnchor = 6f;
                    c.Taboo.KillsListeners = false;
                    c.Taboo.ForgivingStage3 = true;
                    c.Anchors.AnchorFeedback = "explicit";
                    c.Visuals.HudMode = "full";
                    c.TeaParty.DeathThreshold = 2;
                    c.Authority.DeathThreshold = 5;
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Loads the config, applies the named preset, validates it, and writes it back.
        /// Never throws: a broken config file is replaced with defaults and logged.
        /// </summary>
        public static ShinimodoriConfig LoadOrCreate(ICoreAPI api, string filename)
        {
            ShinimodoriConfig cfg = null;
            try
            {
                cfg = api.LoadModConfig<ShinimodoriConfig>(filename);
            }
            catch (Exception e)
            {
                api.Logger.Error("[shinimodori] Config file is unreadable, falling back to defaults: {0}", e.Message);
            }

            if (cfg == null) cfg = new ShinimodoriConfig();

            if (!IsKnown(cfg.Preset))
            {
                api.Logger.Warning("[shinimodori] Unknown preset '{0}', using 'balanced'.", cfg.Preset);
                cfg.Preset = "balanced";
            }
            Apply(cfg, cfg.Preset);
            Validate(cfg, api);

            try { api.StoreModConfig(cfg, filename); }
            catch (Exception e) { api.Logger.Error("[shinimodori] Could not write config: {0}", e.Message); }

            return cfg;
        }

        /// <summary>Clamps anything that would break the mod if a user typed nonsense.</summary>
        public static void Validate(ShinimodoriConfig c, ICoreAPI api)
        {
            c.Core.DyingSeconds = Clamp(c.Core.DyingSeconds, 0f, 10f);
            c.Core.VoidSeconds = Clamp(c.Core.VoidSeconds, 0.1f, 60f);
            c.Core.RewindSeconds = Clamp(c.Core.RewindSeconds, 0f, 30f);
            c.Core.ArrivalSeconds = Clamp(c.Core.ArrivalSeconds, 0.1f, 30f);
            c.Core.ArrivalInputUnlockSeconds = Clamp(c.Core.ArrivalInputUnlockSeconds, 0f, c.Core.ArrivalSeconds);

            c.Anchors.CaptureRadius = (int)Clamp(c.Anchors.CaptureRadius, 16, 512);
            c.Anchors.MaxDeltas = (int)Clamp(c.Anchors.MaxDeltas, 1000, 5_000_000);
            c.Anchors.MaxJournalMB = (int)Clamp(c.Anchors.MaxJournalMB, 1, 1024);
            c.Anchors.RewindBudgetMs = (int)Clamp(c.Anchors.RewindBudgetMs, 250, 120_000);
            c.Anchors.RewindBlocksPerTick = (int)Clamp(c.Anchors.RewindBlocksPerTick, 64, 1_000_000);
            c.Anchors.EntityDriftSampleSeconds = (int)Clamp(c.Anchors.EntityDriftSampleSeconds, 1, 600);
            c.Anchors.VerifySampleCount = (int)Clamp(c.Anchors.VerifySampleCount, 0, 4096);
            c.Anchors.RegionCellSize = (int)Clamp(c.Anchors.RegionCellSize, 32, 8192);

            c.Miasma.GainPerReturn = Clamp(c.Miasma.GainPerReturn, 0f, 100f);
            c.Miasma.DecayPerHour = Clamp(c.Miasma.DecayPerHour, 0f, 100f);
            if (c.Miasma.Tiers == null || c.Miasma.Tiers.Count == 0)
            {
                api.Logger.Warning("[shinimodori] miasma.tiers was empty; restoring defaults.");
                c.Miasma.Tiers = new MiasmaConfig().Tiers;
            }

            c.Taboo.TriggerScore = (int)Clamp(c.Taboo.TriggerScore, 1, 100);
            c.Taboo.Stage2HealthDrainFraction = Clamp(c.Taboo.Stage2HealthDrainFraction, 0f, 0.95f);

            c.Trauma.PhantomPainPerReturn = Clamp(c.Trauma.PhantomPainPerReturn, 0f, 0.9f);
            c.Trauma.PhantomPainFloor = Clamp(c.Trauma.PhantomPainFloor, 0f, 0.9f);

            c.Visuals.EffectIntensity = Clamp(c.Visuals.EffectIntensity, 0f, 1f);
            c.Visuals.VoidMoteCount = (int)Clamp(c.Visuals.VoidMoteCount, 0, 4000);
            c.Visuals.TabooHandCount = (int)Clamp(c.Visuals.TabooHandCount, 0, 64);
            if (c.Visuals.HudMode != "diegetic" && c.Visuals.HudMode != "minimal" && c.Visuals.HudMode != "full")
                c.Visuals.HudMode = "diegetic";

            c.Audio.MasterVolume = Clamp(c.Audio.MasterVolume, 0f, 1f);
            c.Audio.DuckedVolume = Clamp(c.Audio.DuckedVolume, 0f, 1f);

            if (c.Multiplayer.Mode != "soloReturner" && c.Multiplayer.Mode != "personalLoop" && c.Multiplayer.Mode != "everyoneReturns")
            {
                api.Logger.Warning("[shinimodori] Unknown multiplayerMode '{0}', using 'soloReturner'.", c.Multiplayer.Mode);
                c.Multiplayer.Mode = "soloReturner";
            }

            if (c.Anchors.AnchorFeedback != "none" && c.Anchors.AnchorFeedback != "subtle" && c.Anchors.AnchorFeedback != "explicit")
                c.Anchors.AnchorFeedback = "subtle";
        }

        private static float Clamp(float v, float min, float max) =>
            float.IsNaN(v) ? min : (v < min ? min : (v > max ? max : v));
    }
}
