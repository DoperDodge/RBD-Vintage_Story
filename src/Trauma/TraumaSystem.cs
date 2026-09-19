using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Death;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Trauma
{
    /// <summary>
    /// What the rewind cannot undo (§9).
    ///
    /// The physical reset is not a psychological one. Phantom pain is the body
    /// remembering; despair is the person giving up; the ledger is the world becoming
    /// a map of your failures. Despair has to be escapable *through play* — which is
    /// what the Breakdown is for.
    /// </summary>
    public class TraumaSystem
    {
        private const string MaxHealthModifierKey = "shinimodori:phantompain";

        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private TraumaConfig T => server.Cfg.Trauma;

        private long tickListener = -1;
        /// <summary>Death sites already flashed back at, so one walk past does not loop.</summary>
        private readonly Dictionary<string, HashSet<int>> recentRecall = new Dictionary<string, HashSet<int>>();

        public TraumaSystem(ShinimodoriServer server) { this.server = server; }

        public void Register()
        {
            tickListener = Api.Event.RegisterGameTickListener(OnTick, 1000);
        }

        public void Unregister()
        {
            if (tickListener != -1) { Api.Event.UnregisterGameTickListener(tickListener); tickListener = -1; }
        }

        // ------------------------------------------------------------ phantom pain

        /// <summary>Total max-health fraction currently lost, after decay.</summary>
        public float CurrentPhantomPain(PlayerState ps, double nowHours)
        {
            if (!T.Enabled) return 0f;
            float total = 0f;
            foreach (var stack in ps.PhantomPain)
            {
                double age = nowHours - stack.AppliedHours;
                if (age < 0) age = 0;                                  // the clock rewound
                if (age >= T.PhantomPainDecayHours) continue;
                float remaining = 1f - (float)(age / T.PhantomPainDecayHours);
                total += stack.Amount * remaining;
            }
            return Math.Min(total, T.PhantomPainFloor);
        }

        public void OnArrival(IServerPlayer plr, PlayerState ps, DeathCause cause)
        {
            if (!T.Enabled) return;
            double now = Api.World.Calendar.TotalHours;

            if (ps.SkipNextPhantomPain)
            {
                // Echidna's Endurance was spent on this one.
                ps.SkipNextPhantomPain = false;
            }
            else if (T.PhantomPainPerReturn > 0)
            {
                ps.PhantomPain.Add(new PhantomPainStack { Amount = T.PhantomPainPerReturn, AppliedHours = now });
            }

            ps.PhantomPain.RemoveAll(s => now - s.AppliedHours >= T.PhantomPainDecayHours);
            ApplyPhantomPain(plr, ps, now);

            if (T.DespairEnabled)
            {
                ps.Despair = Math.Min(100f, ps.Despair + T.DespairPerDeathAtAnchor);
                if (ps.Despair >= T.BreakdownThreshold && ps.BreakdownAtAnchorHours <= double.MinValue)
                {
                    ps.BreakdownAtAnchorHours = now;
                    // Fired a beat after arrival so it does not collide with the cinematic.
                    Api.Event.RegisterCallback(_ => TriggerBreakdown(plr, ps), 3000);
                }
            }
        }

        private void ApplyPhantomPain(IServerPlayer plr, PlayerState ps, double nowHours)
        {
            var health = plr.Entity?.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            float fraction = CurrentPhantomPain(ps, nowHours);
            // A named modifier, never BaseMaxHealth: the penalty must not bake itself
            // into the save, and it must come off cleanly when it decays.
            float delta = -health.BaseMaxHealth * fraction;
            if (Math.Abs(delta) < 0.001f) health.MaxHealthModifiers.Remove(MaxHealthModifierKey);
            else health.SetMaxHealthModifiers(MaxHealthModifierKey, delta);
            health.UpdateMaxHealth();

            if (health.Health > health.MaxHealth) health.Health = health.MaxHealth;
        }

        // ---------------------------------------------------------------- despair

        private void TriggerBreakdown(IServerPlayer plr, PlayerState ps)
        {
            if (plr?.Entity == null) return;

            // Rock bottom is where he gets up. Six seconds of everything at once, then Resolve.
            server.SendCue(plr, "breakdown", 1f, T.BreakdownSeconds);

            Api.Event.RegisterCallback(_ =>
            {
                if (plr.Entity == null) return;
                ps.Despair = T.BreakdownResolveDespair;
                ps.PhantomPain.Clear();
                ApplyPhantomPain(plr, ps, Api.World.Calendar.TotalHours);
                ps.ResolveUntilHours = Api.World.Calendar.TotalHours + T.ResolveMinutes / 60.0;
                server.SendCue(plr, "resolve", 1f, 2f);
                server.SyncState(plr);
                Api.Logger.Notification("[shinimodori] {0} broke, and got back up.", plr.PlayerName);
            }, (int)(T.BreakdownSeconds * 1000));
        }

        public bool HasResolve(PlayerState ps) =>
            ps.ResolveUntilHours > Api.World.Calendar.TotalHours;

        /// <summary>Damage multiplier granted by Resolve, for whoever asks.</summary>
        public float OutgoingDamageMultiplier(PlayerState ps) =>
            HasResolve(ps) ? 1f + T.ResolveDamageBonus : 1f;

        private const string ResolveStatKey = "shinimodori:resolve";

        /// <summary>
        /// Applies or clears Resolve's damage bonus. Written as an entity stat so the
        /// game's own blending applies it to every weapon, rather than the mod having
        /// to intercept each attack.
        /// </summary>
        private void ApplyResolve(IServerPlayer plr, PlayerState ps)
        {
            var e = plr.Entity;
            if (e == null) return;

            if (HasResolve(ps))
            {
                e.Stats.Set("meleeWeaponsDamage", ResolveStatKey, T.ResolveDamageBonus, false);
                e.Stats.Set("rangedWeaponsDamage", ResolveStatKey, T.ResolveDamageBonus, false);
            }
            else
            {
                e.Stats.Remove("meleeWeaponsDamage", ResolveStatKey);
                e.Stats.Remove("rangedWeaponsDamage", ResolveStatKey);
            }
        }

        // ----------------------------------------------------------------- ledger

        public void OnDeathRecorded(IServerPlayer plr, PlayerState ps, BlockPos pos, DeathCause cause)
        {
            // The whisper at 10 / 25 / 50 / 100. Achievement-flavoured, and not congratulatory.
            int n = ps.TotalDeaths + 1;
            if (n == 10 || n == 25 || n == 50 || n == 100)
                server.SendCue(plr, "milestone-whisper-" + n, 1f, 4f);
        }

        /// <summary>
        /// Death Recall (§9.3): walking near a place you died shows you dying there.
        /// The world becomes a map of your failures.
        /// </summary>
        private void OnTick(float dt)
        {
            if (!server.Cfg.Core.Enabled || !T.Enabled) return;
            double now = Api.World.Calendar.TotalHours;

            foreach (var plr in server.BlessedPlayers())
            {
                var ps = server.StateOf(plr);
                if (server.Returns.IsReturning(plr.PlayerUID)) continue;

                ApplyPhantomPain(plr, ps, now);
                ApplyResolve(plr, ps);

                if (ps.Ledger.Count == 0 || plr.Entity == null) continue;
                var pos = plr.Entity.ServerPos.AsBlockPos;

                if (!recentRecall.TryGetValue(plr.PlayerUID, out var seen))
                    seen = recentRecall[plr.PlayerUID] = new HashSet<int>();

                int r = T.DeathRecallRadius;
                bool nearAny = false;

                for (int i = ps.Ledger.Count - 1; i >= 0 && i >= ps.Ledger.Count - 40; i--)
                {
                    var d = ps.Ledger[i];
                    int dx = d.X - pos.X, dy = d.Y - pos.Y, dz = d.Z - pos.Z;
                    if (dx * dx + dy * dy + dz * dz > r * r) continue;
                    nearAny = true;
                    if (!seen.Add(d.Index)) continue;

                    server.SendCue(plr, "recall-" + d.Cause, 1f, 2f);
                    break;
                }

                // Leaving the area re-arms every flashback there.
                if (!nearAny && seen.Count > 0) seen.Clear();
            }
        }
    }
}
