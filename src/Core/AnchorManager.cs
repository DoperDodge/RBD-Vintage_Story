using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Core
{
    /// <summary>
    /// Decides when the world gets a new point to come back to (§4.9).
    ///
    /// The player never asks for this and is never told the rules. An anchor advances
    /// only when they are *stable* and have *progressed* — which is why advancing it is
    /// the one escape from a death spiral, and why that escape is called hope.
    /// </summary>
    public class AnchorManager
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private ShinimodoriConfig Cfg => server.Cfg;

        private long tickListener = -1;
        /// <summary>Per player: the region cell they are in and when they entered it.</summary>
        private readonly Dictionary<string, (long cell, double sinceHours)> regionDwell = new Dictionary<string, (long, double)>();
        /// <summary>Item codes already seen in a player's inventory, for "first of a tier" milestones.</summary>
        private readonly Dictionary<string, HashSet<string>> seenItems = new Dictionary<string, HashSet<string>>();

        public AnchorManager(ShinimodoriServer server) { this.server = server; }

        public void Register()
        {
            tickListener = Api.Event.RegisterGameTickListener(OnTick, 2000);
            Api.Event.DidUseBlock += OnDidUseBlock;
            Api.Event.OnPlayerInteractEntity += OnInteractEntity;
        }

        public void Unregister()
        {
            if (tickListener != -1) { Api.Event.UnregisterGameTickListener(tickListener); tickListener = -1; }
            Api.Event.DidUseBlock -= OnDidUseBlock;
            Api.Event.OnPlayerInteractEntity -= OnInteractEntity;
        }

        // --------------------------------------------------------------- the rules

        private void OnTick(float dt)
        {
            if (!Cfg.Core.Enabled) return;
            double now = Api.World.Calendar.TotalHours;

            foreach (var plr in server.BlessedPlayers())
            {
                if (server.Returns.IsReturning(plr.PlayerUID)) continue;
                var ps = server.StateOf(plr);

                string milestone = DetectMilestone(plr, ps, now);
                bool timeout = server.State.Anchor != null &&
                               now - server.State.Anchor.TotalHours >= Cfg.Anchors.AnchorTimeoutHours;

                if (milestone == null && !timeout) continue;
                if (!PreconditionsMet(plr, ps, now, out string blockedBy))
                {
                    server.Debug($"anchor deferred for {plr.PlayerName}: {blockedBy}");
                    continue;
                }

                SetAnchor(plr, milestone ?? "timeout");
            }
        }

        /// <summary>The stability half of §4.9. All of it must hold, or the anchor waits.</summary>
        public bool PreconditionsMet(IServerPlayer plr, PlayerState ps, double now, out string blockedBy)
        {
            blockedBy = null;

            if (server.State.Anchor != null &&
                now - server.State.Anchor.TotalHours < Cfg.Anchors.MinAnchorSpacingHours)
            {
                blockedBy = "too soon since the last anchor";
                return false;
            }

            var e = plr.Entity;
            if (e == null || !e.Alive) { blockedBy = "player not alive"; return false; }

            var health = e.GetBehavior<EntityBehaviorHealth>();
            if (health != null && health.MaxHealth > 0 && health.Health / health.MaxHealth < Cfg.Anchors.MinHealthFraction)
            {
                blockedBy = "hurt";
                return false;
            }

            float stability = GetTemporalStability(e.ServerPos.AsBlockPos);
            if (stability < Cfg.Anchors.MinTemporalStability) { blockedBy = "unstable ground"; return false; }

            if (Cfg.Anchors.StormsBlockAnchors && IsTemporalStormActive()) { blockedBy = "temporal storm"; return false; }

            if (HostileWithin(e.ServerPos.XYZ, Cfg.Anchors.NoHostilesWithin)) { blockedBy = "hostiles nearby"; return false; }

            return true;
        }

        private bool HostileWithin(Vec3d pos, float radius)
        {
            var found = Api.World.GetNearestEntity(pos, radius, radius, e =>
                e != null && e.Alive && !(e is EntityPlayer) && IsHostile(e));
            return found != null;
        }

        private static bool IsHostile(Entity e)
        {
            // Creatures that hunt are the ones that make a moment unsafe. The engine
            // does not label them, so go by the AI they carry plus their diet.
            var code = e.Code?.Path ?? "";
            if (code.StartsWith("drifter") || code.StartsWith("locust") || code.StartsWith("bell")
                || code.StartsWith("mabeast") || code.Contains("wolf") || code.Contains("bear")) return true;

            var task = e.GetBehavior<EntityBehaviorTaskAI>();
            if (task?.TaskManager == null) return false;
            foreach (var t in task.TaskManager.AllTasks)
                if (t is AiTaskMeleeAttack || t is AiTaskSeekEntity) return true;
            return false;
        }

        public float GetTemporalStability(BlockPos pos)
        {
            var sys = Api.ModLoader.GetModSystem<SystemTemporalStability>();
            if (sys == null) return 1f;
            try { return sys.GetTemporalStability(pos); } catch { return 1f; }
        }

        public bool IsTemporalStormActive()
        {
            var sys = Api.ModLoader.GetModSystem<SystemTemporalStability>();
            try { return sys?.StormData?.nowStormActive == true; } catch { return false; }
        }

        // ------------------------------------------------------------- milestones

        /// <summary>Returns a milestone code the first time each is achieved, else null.</summary>
        private string DetectMilestone(IServerPlayer plr, PlayerState ps, double now)
        {
            var e = plr.Entity;
            if (e == null) return null;

            // Depth: descending past a new floor for the first time.
            int y = (int)e.ServerPos.Y;
            foreach (int depth in new[] { 0, -40, -100 })
            {
                // Depths are expressed relative to sea level, which is where the player's
                // intuition of "surface" lives.
                int threshold = Api.World.SeaLevel + depth;
                if (y < threshold && Claim(ps, "depth:" + depth)) return "milestone:depth" + depth;
            }

            // A new region cell, survived for a while.
            long cell = RegionCell(e.ServerPos.AsBlockPos);
            if (!regionDwell.TryGetValue(plr.PlayerUID, out var dwell) || dwell.cell != cell)
            {
                regionDwell[plr.PlayerUID] = (cell, now);
            }
            else if (!ps.VisitedRegions.Contains(cell) &&
                     now - dwell.sinceHours >= Cfg.Anchors.RegionDwellMinutes / 60.0)
            {
                ps.VisitedRegions.Add(cell);
                return "region:" + cell;
            }

            // First of a metal tier, and first tool head of a new tier: both show up as
            // an item code appearing in the player's own inventory for the first time.
            string item = FirstNewNotableItem(plr, ps);
            if (item != null) return "milestone:" + item;

            // Surviving a temporal storm without dying.
            if (!IsTemporalStormActive() && ps.Milestones.Contains("storm:inprogress"))
            {
                ps.Milestones.Remove("storm:inprogress");
                if (Claim(ps, "storm:survived")) return "milestone:stormSurvived";
            }
            else if (IsTemporalStormActive())
            {
                ps.Milestones.Add("storm:inprogress");
            }

            return null;
        }

        private static readonly string[] NotablePrefixes = { "ingot-", "metalplate-", "toolhead-" };

        private string FirstNewNotableItem(IServerPlayer plr, PlayerState ps)
        {
            if (!seenItems.TryGetValue(plr.PlayerUID, out var seen))
                seen = seenItems[plr.PlayerUID] = new HashSet<string>();

            foreach (var inv in plr.InventoryManager.Inventories.Values)
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    var code = slot?.Itemstack?.Collectible?.Code?.ToShortString();
                    if (code == null) continue;
                    foreach (var prefix in NotablePrefixes)
                    {
                        int idx = code.IndexOf(prefix, StringComparison.Ordinal);
                        if (idx < 0) continue;
                        if (!seen.Add(code)) break;
                        if (Claim(ps, "item:" + code)) return code.Replace(':', '-');
                        break;
                    }
                }
            }
            return null;
        }

        private void OnDidUseBlock(IServerPlayer plr, BlockSelection blockSel)
        {
            if (!Cfg.Core.Enabled || blockSel?.Position == null || !server.IsBlessed(plr)) return;
            var ps = server.StateOf(plr);
            string code = Api.World.BlockAccessor.GetBlock(blockSel.Position)?.Code?.Path ?? "";

            // Sleeping in a bed you built is the canonical "you are safe and time passed"
            // beat, and unlike the others it repeats.
            if (code.Contains("bed"))
            {
                ps.Milestones.Add("bed:used");
                TrySetIfStable(plr, ps, "sleep");
                return;
            }
            if (code.Contains("translocator") && Claim(ps, "translocator")) TrySetIfStable(plr, ps, "milestone:translocator");
        }

        private void OnInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition,
                                      int mode, ref EnumHandling handling)
        {
            if (!Cfg.Core.Enabled || entity == null) return;
            if (!(byPlayer is IServerPlayer plr) || !server.IsBlessed(plr)) return;
            var ps = server.StateOf(plr);

            if (entity is EntityTrader && Claim(ps, "trade:first"))
                TrySetIfStable(plr, ps, "milestone:firstTrade");

            // A domesticated animal is a whole chapter of a survival game.
            var dom = entity.WatchedAttributes?.GetTreeAttribute("domesticationstatus");
            if (dom != null && dom.GetBool("isDomesticated") && Claim(ps, "tame:first"))
                TrySetIfStable(plr, ps, "milestone:domesticated");
        }

        private void TrySetIfStable(IServerPlayer plr, PlayerState ps, string reason)
        {
            double now = Api.World.Calendar.TotalHours;
            if (PreconditionsMet(plr, ps, now, out _)) SetAnchor(plr, reason);
        }

        private static bool Claim(PlayerState ps, string key) => ps.Milestones.Add(key);

        private long RegionCell(BlockPos pos)
        {
            int size = Cfg.Anchors.RegionCellSize;
            long cx = (long)Math.Floor(pos.X / (double)size);
            long cz = (long)Math.Floor(pos.Z / (double)size);
            return (cx << 32) ^ (cz & 0xFFFFFFFFL);
        }

        // ------------------------------------------------------------ setting one

        /// <summary>
        /// Captures a new anchor here and now, and clears the journal behind it.
        /// This is the only escape from a death spiral, so it also cleanses.
        /// </summary>
        public ReturnPoint SetAnchor(IServerPlayer plr, string reason)
        {
            var rp = new ReturnPoint
            {
                RealTimeCreatedMs = Api.World.ElapsedMilliseconds,
                TotalHours = Api.World.Calendar.TotalHours,
                TotalGameSeconds = Api.WorldManager.SaveGame.TotalGameSeconds,
                CaptureRadius = Cfg.Anchors.CaptureRadius,
                AnchorReason = reason,
                DeathsAtThisAnchor = 0,
            };
            rp.SetOrigin(plr.Entity.ServerPos.AsBlockPos);

            // Every player is captured, not just the returner: in soloReturner mode the
            // whole world rewinds, and the others must come back with it (§13).
            foreach (var p in Api.World.AllOnlinePlayers)
            {
                if (!(p is IServerPlayer sp) || sp.Entity == null) continue;
                try { rp.Players[sp.PlayerUID] = PlayerStateIO.Capture(sp); }
                catch (Exception e) { server.Warn($"could not capture {sp.PlayerName}: {e.Message}"); }
            }

            CaptureNearbyEntities(rp);

            server.State.Anchor = rp;
            server.Recorder.ResetTo(rp.Id);
            server.Recorder.CurrentOrigin = rp.Origin;
            server.Recorder.Active = true;

            int seeded = server.Recorder.SeedNearbyBlockEntities(rp.Origin, rp.CaptureRadius, 4096);

            // Progress cleanses: despair resets and miasma decays twice as fast for a while.
            foreach (var p in server.BlessedPlayers())
            {
                var ps = server.StateOf(p);
                ps.DeathsAtAnchor = 0;
                ps.Despair = 0;
                ps.AnchorAdvancedAtHours = rp.TotalHours;
                ps.BreakdownAtAnchorHours = double.MinValue;
                server.SyncState(p);
                AnchorFeedback(p);
            }

            Api.Logger.Notification("[shinimodori] Anchor set ({0}) at {1} — {2} entities, {3} block entities seeded.",
                reason, rp.Origin, rp.Entities.Count, seeded);

            return rp;
        }

        private void CaptureNearbyEntities(ReturnPoint rp)
        {
            var centre = new Vec3d(rp.OriginX, rp.OriginY, rp.OriginZ);
            float r = rp.CaptureRadius;
            var around = Api.World.GetEntitiesAround(centre, r, r,
                e => e != null && e.Alive && !(e is EntityPlayer));

            foreach (var e in around)
            {
                try
                {
                    rp.Entities.Add(new EntitySnapshot
                    {
                        EntityId = e.EntityId,
                        EntityCode = e.Code?.ToString() ?? "",
                        X = e.ServerPos.X, Y = e.ServerPos.Y, Z = e.ServerPos.Z,
                        Data = JournalRecorder.SerializeEntity(e),
                    });
                }
                catch (Exception ex) { server.Warn($"entity capture failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// §4.9: easy to miss on purpose. The player is never told what an anchor is.
        /// </summary>
        private void AnchorFeedback(IServerPlayer plr)
        {
            switch (Cfg.Anchors.AnchorFeedback)
            {
                case "none":
                    break;
                case "explicit":
                    Api.SendMessage(plr, GlobalConstants.GeneralChatGroup,
                        Lang.Get("shinimodori:anchor-explicit"), EnumChatType.Notification);
                    break;
                default:
                    server.SendCue(plr, "anchor", 1f, 0.8f);
                    break;
            }
        }
    }
}
