using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Authority
{
    /// <summary>
    /// Invisible Providence (§11).
    ///
    /// Shadow arms only the owner can see. Every use costs scent and stability and
    /// risks drawing her eye, and five uses in two hours forces a stage-2 grip — this
    /// must never settle into being a normal tool.
    /// </summary>
    public class AuthoritySystem
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private AuthorityConfig A => server.Cfg.Authority;

        /// <summary>Entities currently held still by a yank.</summary>
        private readonly Dictionary<long, double> stunned = new Dictionary<long, double>();
        /// <summary>Players whose next incoming hit is negated.</summary>
        private readonly HashSet<string> guarding = new HashSet<string>();
        private long tickListener = -1;

        public AuthoritySystem(ShinimodoriServer server) { this.server = server; }

        public void Register()
        {
            tickListener = Api.Event.RegisterGameTickListener(OnTick, 200);
        }

        public void Unregister()
        {
            if (tickListener != -1) { Api.Event.UnregisterGameTickListener(tickListener); tickListener = -1; }
        }

        public bool IsGuarding(string playerUid) => guarding.Contains(playerUid);

        /// <summary>Consumes a guard. Called by the damage path.</summary>
        public bool ConsumeGuard(string playerUid) => guarding.Remove(playerUid);

        public void CheckUnlock(IServerPlayer plr, PlayerState ps)
        {
            if (!A.Enabled || ps.AuthorityUnlocked) return;
            if (ps.TotalDeaths < A.DeathThreshold && !ps.Milestones.Contains("witchfactor")) return;

            ps.AuthorityUnlocked = true;
            server.StateChannel.SendPacket(new PktAuthorityState { Unlocked = true, Extended = false }, plr);
            server.SendCue(plr, "authority-unlock", 1f, 5f);
            Api.Logger.Notification("[shinimodori] {0} can feel the arms now.", plr.PlayerName);
        }

        // -------------------------------------------------------------------- cast

        /// <summary>
        /// The client asks; the server decides. Range, cost, cooldown and outcome are
        /// all checked here, because a client that lies must gain nothing (§13).
        /// </summary>
        public void OnCast(IServerPlayer plr, PktAuthorityCast msg)
        {
            if (!A.Enabled || !server.Cfg.Core.Enabled) return;
            if (!server.IsBlessed(plr)) return;

            var ps = server.StateOf(plr);
            if (!ps.AuthorityUnlocked) return;
            if (server.Returns.IsReturning(plr.PlayerUID) || server.TabooSystem.IsGripped(plr.PlayerUID)) return;

            var e = plr.Entity;
            if (e == null) return;

            int kind;
            if (msg.Guarding)
            {
                guarding.Add(plr.PlayerUID);
                kind = 3;
            }
            else if (msg.TargetEntityId != 0)
            {
                var target = Api.World.GetEntityById(msg.TargetEntityId);
                if (target == null || !target.Alive) return;
                if (target.Pos.DistanceTo(e.Pos.XYZ) > A.GrabRange) return;
                if (target is EntityPlayer) return;             // she does not lend you people

                // Grab and yank: pull it in, then hold it still.
                var pull = e.Pos.XYZ.SubCopy(target.Pos.XYZ).Normalize() * 0.9;
                target.Pos.Motion.Set(pull.X, Math.Max(0.15, pull.Y), pull.Z);
                stunned[target.EntityId] = Api.World.ElapsedMilliseconds + A.GrabStunSeconds * 1000;
                kind = 1;
            }
            else if (msg.HasBlockTarget)
            {
                var pos = new BlockPos(msg.TargetX, msg.TargetY, msg.TargetZ, e.Pos.Dimension);
                if (pos.DistanceTo(e.Pos.AsBlockPos) > A.RetrieveRange) return;
                if (!Retrieve(plr, pos)) return;
                kind = 2;
            }
            else return;

            PayCost(plr, ps);
            server.StateChannel.SendPacket(new PktAuthorityState { Unlocked = true, Extended = true, Kind = kind }, plr);
        }

        private bool Retrieve(IServerPlayer plr, BlockPos pos)
        {
            var acc = Api.World.BlockAccessor;
            var block = acc.GetBlock(pos);
            if (block == null || block.Id == 0) return false;

            // Breaking through the arms respects the world's rules: the player must be
            // allowed to break it, and the drops are what they would have got anyway.
            if (!Api.World.Claims.TryAccess(plr, pos, EnumBlockAccessFlags.BuildOrBreak)) return false;

            var drops = block.GetDrops(Api.World, pos, plr);
            block.OnBlockBroken(Api.World, pos, plr);

            if (drops != null)
                foreach (var stack in drops)
                    if (!plr.InventoryManager.TryGiveItemstack(stack, true))
                        Api.World.SpawnItemEntity(stack, plr.Entity.Pos.XYZ);

            return true;
        }

        private void PayCost(IServerPlayer plr, PlayerState ps)
        {
            ps.Miasma = Math.Min(100f, ps.Miasma + A.MiasmaPerUse);

            var attr = plr.Entity?.WatchedAttributes?.GetTreeAttribute("temporalStability");
            if (attr != null)
            {
                attr.SetDouble("stability", Math.Max(0, attr.GetDouble("stability", 1.0) - A.StabilityCostPerUse));
                plr.Entity.WatchedAttributes.MarkPathDirty("temporalStability");
            }

            double now = Api.World.Calendar.TotalHours;
            ps.AuthorityUses.Add(now);
            ps.AuthorityUses.RemoveAll(t => now - t > A.OveruseHours);

            // Using her power draws her eye.
            if (Api.World.Rand.NextDouble() < A.GripChancePerUse)
                server.TabooSystem.Trigger(plr, 1);

            if (ps.AuthorityUses.Count >= A.OveruseCount)
            {
                ps.AuthorityUses.Clear();
                server.TabooSystem.Trigger(plr, 2);
            }

            server.SyncState(plr);
        }

        private void OnTick(float dt)
        {
            if (stunned.Count == 0) return;
            double now = Api.World.ElapsedMilliseconds;
            var done = new List<long>();

            foreach (var kv in stunned)
            {
                if (now >= kv.Value) { done.Add(kv.Key); continue; }
                var e = Api.World.GetEntityById(kv.Key);
                if (e == null || !e.Alive) { done.Add(kv.Key); continue; }
                e.Pos.Motion.Set(0, e.Pos.Motion.Y, 0);
            }

            foreach (var id in done) stunned.Remove(id);
        }
    }
}
