using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Shinimodori.Death
{
    /// <summary>
    /// Sits on every blessed player and makes death impossible (§5.1).
    ///
    /// The vanilla death screen is never suppressed after the fact — lethal damage is
    /// simply not allowed to land. That is what makes the cut from "about to die" to
    /// "back at the anchor" a single unbroken shot rather than a menu.
    /// </summary>
    public class EntityBehaviorReturner : EntityBehavior
    {
        public const string Name = "shinimodorireturner";

        private ShinimodoriServer server;

        public EntityBehaviorReturner(Entity entity) : base(entity) { }

        public override string PropertyName() => Name;

        public void Init(ShinimodoriServer server) { this.server = server; }

        public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
        {
            if (server == null || entity?.World?.Side != EnumAppSide.Server) return;
            if (!(entity is EntityPlayer ep) || !(ep.Player is IServerPlayer plr)) return;
            if (!server.Cfg.Core.Enabled || !server.IsBlessed(plr)) return;

            // Already returning: untouchable, and no second return can be queued.
            if (server.Returns.IsReturning(plr.PlayerUID)) { damage = 0; return; }

            // Healing and the like must pass through untouched.
            if (damage <= 0 || damageSource?.Type == EnumDamageType.Heal) return;

            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health == null) return;

            if (damage < health.Health) return;     // survivable; carry on

            // Death never occurs.
            damage = 0;
            try
            {
                server.Returns.BeginFromLethalDamage(plr, damageSource);
            }
            catch (Exception e)
            {
                server.Warn($"return could not start for {plr.PlayerName}: {e}");
                // Leaving damage at 0 is the safe failure: the player survives on a
                // sliver rather than seeing a death screen the mod promised to prevent.
                health.Health = Math.Max(0.5f, health.Health);
            }
        }

        public override void OnGameTick(float deltaTime)
        {
            if (server == null) return;
            if (!(entity is EntityPlayer ep) || !(ep.Player is IServerPlayer plr)) return;
            if (!server.Returns.IsReturning(plr.PlayerUID)) return;

            // Belt and braces for the duration of the cinematic: the player cannot
            // starve, freeze or suffocate their way into a second death mid-return.
            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null && health.Health < 1f) health.Health = 1f;
        }
    }
}
