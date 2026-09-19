using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Taboo
{
    /// <summary>
    /// Satella's grip (§7).
    ///
    /// Three things make this work, and all three are non-negotiable: the message is
    /// eaten rather than sent, the punishment escalates, and **nobody else perceives
    /// any of it**. From outside, the player simply seized up.
    /// </summary>
    public class TabooSystem
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private TabooConfig T => server.Cfg.Taboo;

        private class Grip
        {
            public int Stage;
            public double LastTriggerMs;
            public double StageEndsAtMs;
            public bool Active;
            public float HealthDrainPerSecond;
        }

        private readonly Dictionary<string, Grip> grips = new Dictionary<string, Grip>();
        private long tickListener = -1;

        public TabooSystem(ShinimodoriServer server) { this.server = server; }

        public void Register()
        {
            Api.Event.PlayerChat += OnPlayerChat;
            Compat.ShinimodoriBridge.OnTextWritten = OnTextWritten;
            tickListener = Api.Event.RegisterGameTickListener(OnTick, 50);
        }

        public void Unregister()
        {
            Api.Event.PlayerChat -= OnPlayerChat;
            Compat.ShinimodoriBridge.OnTextWritten = null;
            if (tickListener != -1) { Api.Event.UnregisterGameTickListener(tickListener); tickListener = -1; }
        }

        public bool IsGripped(string playerUid) =>
            grips.TryGetValue(playerUid, out var g) && g.Active;

        // ------------------------------------------------------------------- chat

        private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
        {
            if (!T.Enabled || !server.Cfg.Core.Enabled) return;
            if (byPlayer == null || !server.IsBlessed(byPlayer)) return;

            // Canon: inside her dream, the witches already know. There is nothing to hide.
            if (server.TeaParty.IsInside(byPlayer.PlayerUID)) return;

            var score = TabooDetector.Evaluate(message, T.TriggerScore, T.OocPrefix);

            if (server.Cfg.Debug.LogTabooScores)
                Api.Logger.Notification("[shinimodori] taboo eval for {0}: {1}\n  \"{2}\"",
                    byPlayer.PlayerName, score.Explain(), message);

            if (!score.Triggered) return;

            // The message is never sent. It is eaten. Other players see nothing.
            consumed.value = true;
            message = "";

            Api.Logger.Notification("[shinimodori] Taboo triggered by {0}: {1}", byPlayer.PlayerName, score.Explain());
            Trigger(byPlayer);
        }

        /// <summary>
        /// Written words are a different horror (§7.2): the writing is *allowed*, and
        /// then the letters go. No grip, no damage. Just the quiet fact of being erased.
        /// </summary>
        private bool OnTextWritten(IPlayer player, object blockEntity, string text)
        {
            if (!(player is IServerPlayer plr)) return false;
            if (!ScreenWrittenText(plr, text)) return false;
            if (!T.ErasesWriting) return true;

            // Half a second of it existing, so they see it land before it goes.
            Api.Event.RegisterCallback(_ => EraseSign(blockEntity), 500);
            return true;
        }

        private void EraseSign(object blockEntity)
        {
            try
            {
                var setText = blockEntity.GetType().GetMethod("SetText", new[] { typeof(string) });
                if (setText != null) { setText.Invoke(blockEntity, new object[] { "" }); return; }

                var field = blockEntity.GetType().GetField("text");
                if (field != null)
                {
                    field.SetValue(blockEntity, "");
                    (blockEntity as BlockEntity)?.MarkDirty(true);
                }
            }
            catch (Exception e) { server.Warn($"could not erase written text: {e.Message}"); }
        }

        /// <summary>Scores a piece of written text; used by the sign hook and by admins.</summary>
        public bool ScreenWrittenText(IServerPlayer plr, string text)
        {
            if (!T.Enabled || !server.Cfg.Core.Enabled || !server.IsBlessed(plr)) return false;
            if (server.TeaParty.IsInside(plr.PlayerUID)) return false;
            if (!TabooDetector.Evaluate(text, T.TriggerScore, T.OocPrefix).Triggered) return false;

            Api.Logger.Notification("[shinimodori] {0} wrote the forbidden thing down.", plr.PlayerName);
            server.StateChannel.SendPacket(new PktTabooStage { Stage = 0, Seconds = 2.5f, WritingErased = true }, plr);
            if (!T.ErasesWriting) Trigger(plr);
            return true;
        }

        // ---------------------------------------------------------------- the grip

        public void Trigger(IServerPlayer plr) => Trigger(plr, 0);

        /// <summary>Forces a specific stage (admin command); 0 means "advance normally".</summary>
        public void Trigger(IServerPlayer plr, int forceStage)
        {
            double nowMs = Api.World.ElapsedMilliseconds;
            if (!grips.TryGetValue(plr.PlayerUID, out var g)) g = grips[plr.PlayerUID] = new Grip();

            if (forceStage > 0)
            {
                g.Stage = Math.Min(3, forceStage);
            }
            else
            {
                bool within = g.LastTriggerMs > 0 && nowMs - g.LastTriggerMs <= T.StageAdvanceWindowSeconds * 1000;
                g.Stage = within ? Math.Min(3, g.Stage + 1) : Math.Max(1, g.Stage);
                if (g.Stage == 0) g.Stage = 1;
            }
            g.LastTriggerMs = nowMs;

            var ps = server.StateOf(plr);
            if (g.Stage >= 3)
            {
                double since = nowMs - ps.LastStage3RealMs;
                if (ps.LastStage3RealMs > double.MinValue && since < T.Stage3CooldownMinutes * 60_000)
                    g.Stage = 2;   // rate-limited: she is patient
            }

            float seconds = g.Stage == 1 ? T.Stage1Seconds : g.Stage == 2 ? T.Stage2Seconds : T.Stage3Seconds;
            g.Active = true;
            g.StageEndsAtMs = nowMs + seconds * 1000;
            g.HealthDrainPerSecond = 0;

            if (g.Stage == 2)
            {
                var health = plr.Entity?.GetBehavior<EntityBehaviorHealth>();
                if (health != null && seconds > 0)
                    g.HealthDrainPerSecond = health.MaxHealth * T.Stage2HealthDrainFraction / seconds;
            }

            LockInput(plr, true);
            server.StateChannel.SendPacket(new PktTabooStage { Stage = g.Stage, Seconds = seconds }, plr);

            if (g.Stage >= 3) ps.LastStage3RealMs = nowMs;
        }

        private void OnTick(float dt)
        {
            if (grips.Count == 0) return;
            double now = Api.World.ElapsedMilliseconds;

            foreach (var uid in new List<string>(grips.Keys))
            {
                var g = grips[uid];
                var plr = Api.World.PlayerByUid(uid) as IServerPlayer;

                if (g.Active)
                {
                    if (plr?.Entity == null) { g.Active = false; continue; }

                    // A statue in the world. Other players see them standing perfectly still.
                    plr.Entity.ServerPos.Motion.Set(0, 0, 0);

                    if (g.HealthDrainPerSecond > 0)
                    {
                        var health = plr.Entity.GetBehavior<EntityBehaviorHealth>();
                        if (health != null)
                        {
                            // Uncancellable, and never lethal by itself: stage 3 is what kills.
                            health.Health = Math.Max(0.5f, health.Health - g.HealthDrainPerSecond * 0.05f);
                        }
                    }

                    if (now >= g.StageEndsAtMs) EndStage(plr, g);
                }
                else if (g.Stage > 0 && now - g.LastTriggerMs > T.StageDecaySeconds * 1000)
                {
                    g.Stage--;                      // one step per silence window
                    g.LastTriggerMs = now;
                    if (g.Stage <= 0) grips.Remove(uid);
                }
            }
        }

        private void EndStage(IServerPlayer plr, Grip g)
        {
            g.Active = false;
            LockInput(plr, false);
            server.StateChannel.SendPacket(new PktTabooStage { Stage = 0, Seconds = 0 }, plr);

            if (g.Stage < 3) return;

            // Stage 3: she takes.
            if (T.KillsListeners) KillListeners(plr);

            if (!T.ForgivingStage3)
            {
                server.Returns.BeginTaboo(plr);
                g.Stage = 0;
            }
        }

        /// <summary>
        /// Canon: persisting kills the listener, and the Witch does it, not the player.
        /// No damage source, no drops, nothing to trace it to them (§7.3).
        /// </summary>
        private void KillListeners(IServerPlayer plr)
        {
            var centre = plr.Entity.ServerPos.XYZ;
            int r = T.ListenerKillRadius;

            var nearby = Api.World.GetEntitiesAround(centre, r, r, e =>
            {
                if (e == null || !e.Alive) return false;
                if (e is EntityPlayer ep)
                    return T.KillsPlayers && ep.Player is IServerPlayer sp
                           && sp.PlayerUID != plr.PlayerUID && !server.IsBlessed(sp);
                return e is EntityTrader || e is EntityAgent && (e.Code?.Path?.Contains("humanoid") ?? false);
            });

            foreach (var e in nearby)
            {
                try { e.Die(EnumDespawnReason.Removed, null); }
                catch (Exception ex) { server.Warn($"listener removal failed: {ex.Message}"); }
            }

            if (nearby.Length > 0)
                Api.Logger.Notification("[shinimodori] The taboo took {0} listener(s) near {1}.", nearby.Length, plr.PlayerName);
        }

        /// <summary>
        /// Input is suppressed server-side so a modified client cannot walk out of it.
        /// The flag is watched, so the owning client's renderers can read it too.
        /// </summary>
        private void LockInput(IServerPlayer plr, bool locked)
        {
            var e = plr.Entity;
            if (e == null) return;
            e.WatchedAttributes.SetBool("sm:gripped", locked);
            if (locked)
            {
                e.ServerPos.Motion.Set(0, 0, 0);
                e.Controls.StopAllMovement();
            }
        }

        /// <summary>Exposed for `/rbd taboo test`, which scores text without triggering anything.</summary>
        public TabooScore Test(string text) => TabooDetector.Evaluate(text, T.TriggerScore, T.OocPrefix);
    }
}
