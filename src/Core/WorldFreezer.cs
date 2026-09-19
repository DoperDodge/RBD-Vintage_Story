using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Core
{
    /// <summary>
    /// Approximates a paused world for the duration of a return (§4.7).
    ///
    /// There is no real pause in Vintage Story, so this does the three things that
    /// matter: it vetoes AI task execution, it zeroes motion, and it leaves the clients
    /// staring at the Void — which is what actually hides the imperfection. Any entity
    /// that loads mid-freeze is caught on the next sweep.
    /// </summary>
    public class WorldFreezer
    {
        private readonly ICoreServerAPI sapi;
        private readonly HashSet<long> frozen = new HashSet<long>();
        private readonly ActionBoolReturn<IAiTask> veto;
        private long sweepListener = -1;

        public bool IsFrozen { get; private set; }

        public WorldFreezer(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            // One delegate instance, subscribed and unsubscribed per entity.
            veto = _ => false;
        }

        public void Freeze()
        {
            if (IsFrozen) return;
            IsFrozen = true;
            frozen.Clear();
            SweepOnce(0);
            // Entities stream in constantly; keep catching them while the world is held.
            sweepListener = sapi.Event.RegisterGameTickListener(SweepOnce, 200);
        }

        public void Thaw()
        {
            if (!IsFrozen) return;
            IsFrozen = false;

            if (sweepListener != -1) { sapi.Event.UnregisterGameTickListener(sweepListener); sweepListener = -1; }

            foreach (long id in frozen)
            {
                var e = sapi.World.GetEntityById(id);
                var ai = e?.GetBehavior<EntityBehaviorTaskAI>();
                if (ai?.TaskManager != null)
                {
                    try { ai.TaskManager.OnShouldExecuteTask -= veto; }
                    catch { /* the entity may have unloaded; nothing to undo */ }
                }
            }
            frozen.Clear();
        }

        private void SweepOnce(float dt)
        {
            if (!IsFrozen) return;
            try
            {
                foreach (var kv in sapi.World.LoadedEntities)
                {
                    var e = kv.Value;
                    if (e == null || e is EntityPlayer) continue;

                    e.Pos.Motion.Set(0, 0, 0);

                    if (!frozen.Add(e.EntityId)) continue;

                    var ai = e.GetBehavior<EntityBehaviorTaskAI>();
                    if (ai?.TaskManager != null) ai.TaskManager.OnShouldExecuteTask += veto;
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Warning("[shinimodori] freeze sweep hiccuped: {0}", ex.Message);
            }
        }
    }
}
