using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Miasma
{
    /// <summary>
    /// Attached to anything that hunts. It widens the creature's own seeking range in
    /// proportion to the nearest blessed player's miasma, and at the high tiers hands
    /// the AI a target it did not find for itself (§6.3).
    ///
    /// The distinction matters: a bigger number makes wolves notice sooner, but handing
    /// them a target makes them arrive — from off-screen, from several directions, on
    /// purpose. That is the difference between a harder game and being hunted.
    /// </summary>
    public class ScentAggroBehavior : EntityBehavior
    {
        public const string Name = "shinimodoriscentaggro";

        /// <summary>
        /// AiTaskSeekEntity recomputes NowSeekRange from the protected `seekingRange`
        /// field at the top of every ShouldExecute, so the public property is not a
        /// usable seam — the field itself is. Resolved once and cached; if a future
        /// version renames it, the behaviour degrades to doing nothing rather than
        /// throwing on every predator in the world.
        /// </summary>
        private static readonly FieldInfo SeekRangeField =
            typeof(AiTaskSeekEntity).GetField("seekingRange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private ShinimodoriServer server;
        private readonly Dictionary<AiTaskSeekEntity, float> baseRanges = new Dictionary<AiTaskSeekEntity, float>();
        private float accum;
        private bool initialised;
        private bool drifterKin;

        public ScentAggroBehavior(Entity entity) : base(entity) { }

        public override string PropertyName() => Name;

        public void Init(ShinimodoriServer server)
        {
            this.server = server;
            string code = entity.Code?.Path ?? "";
            // Drifters and their relatives come from the same kind of elsewhere she does,
            // so they notice the scent far more keenly than an animal ever could (§8).
            drifterKin = code.StartsWith("drifter") || code.StartsWith("locust")
                      || code.StartsWith("bell") || code.StartsWith("shiver") || code.StartsWith("eidolon");
        }

        public override void OnGameTick(float deltaTime)
        {
            if (server == null || entity.World.Side != EnumAppSide.Server) return;
            if (!server.Cfg.Miasma.Enabled) return;

            accum += deltaTime;
            if (accum < 1.0f) return;   // once a second is plenty; this runs on every predator alive
            accum = 0;

            if (!initialised) CacheTasks();
            if (baseRanges.Count == 0) return;

            var (player, tier) = server.Miasma.NearestBlessedScent(entity.Pos.XYZ, 200);
            if (player == null) { ResetRanges(); return; }

            float mult = server.Miasma.DetectionMultiplierFor(tier);
            if (drifterKin) mult = 1f + (mult - 1f) * server.Cfg.Miasma.DrifterSensitivity;

            foreach (var kv in baseRanges)
            {
                var task = kv.Key;
                float wanted = kv.Value * mult;
                SetSeekRange(task, wanted);

                if (mult <= 1.0001f) continue;

                // At tier 3 and up the scent is a summons, not a hint.
                if (tier >= 3 && task.TargetEntity == null)
                {
                    double dist = entity.Pos.DistanceTo(player.Entity.Pos.XYZ);
                    if (dist <= wanted) task.targetEntity = player.Entity;
                }
            }
        }

        private void CacheTasks()
        {
            initialised = true;
            if (SeekRangeField == null) return;
            var ai = entity.GetBehavior<EntityBehaviorTaskAI>();
            if (ai?.TaskManager == null) return;
            foreach (var t in ai.TaskManager.AllTasks)
                if (t is AiTaskSeekEntity seek) baseRanges[seek] = GetSeekRange(seek);
        }

        private void ResetRanges()
        {
            foreach (var kv in baseRanges) SetSeekRange(kv.Key, kv.Value);
        }

        private static float GetSeekRange(AiTaskSeekEntity task)
        {
            try { return (float)SeekRangeField.GetValue(task); } catch { return 0f; }
        }

        private static void SetSeekRange(AiTaskSeekEntity task, float value)
        {
            try { SeekRangeField.SetValue(task, value); } catch { }
        }
    }

    /// <summary>
    /// Attached to traders. At tier 3 and up they will not deal with you at all: they
    /// can smell what follows you around, and they want no part of it (§6.2).
    /// </summary>
    public class ScentAversionBehavior : EntityBehavior
    {
        public const string Name = "shinimodoriscentaversion";

        private ShinimodoriServer server;

        public ScentAversionBehavior(Entity entity) : base(entity) { }

        public override string PropertyName() => Name;

        public void Init(ShinimodoriServer server) { this.server = server; }

        public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition,
                                        EnumInteractMode mode, ref EnumHandling handled)
        {
            if (server == null || entity.World.Side != EnumAppSide.Server) return;
            if (mode != EnumInteractMode.Interact) return;
            if (!(byEntity is EntityPlayer ep) || !(ep.Player is IServerPlayer plr)) return;
            if (!server.IsBlessed(plr)) return;

            var ps = server.StateOf(plr);
            int tier = server.Miasma.TierIndexFor(plr, ps);
            if (!server.Miasma.TradersRefuseAt(tier)) return;

            handled = EnumHandling.PreventDefault;
            server.Api.SendMessage(plr, Vintagestory.API.Config.GlobalConstants.GeneralChatGroup,
                Vintagestory.API.Config.Lang.Get("shinimodori:trader-refuses"), EnumChatType.Notification);
        }
    }
}
