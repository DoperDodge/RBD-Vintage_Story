using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Shinimodori.Death
{
    /// <summary>
    /// The three things a player can hold that the Witch has an opinion about:
    /// the Shard of Envy (§3), a Witch Factor (§11), and — at high miasma — a
    /// temporal gear, which is the one lever the world gives you (§8).
    ///
    /// One class covers all three; which behaviour applies is decided by the item's
    /// own attributes, so adding another is a JSON change.
    /// </summary>
    public class ItemWitchGift : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel,
            EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (api.Side != EnumAppSide.Server) { handling = EnumHandHandling.PreventDefault; return; }
            if (!(byEntity is EntityPlayer ep) ||
                !(ep.Player is IServerPlayer plr))
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            var mod = api.ModLoader.GetModSystem<ShinimodoriModSystem>();
            var server = mod?.Server;
            if (server == null)
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            var ps = server.StateOf(plr);
            bool consumed = false;

            if (Attributes?["shinimodoriBlessing"].AsBool() == true)
            {
                if (server.Cfg.Core.BlessingMode != "item")
                {
                    Tell(plr, "shinimodori:shard-inert");
                }
                else if (ps.Blessed)
                {
                    Tell(plr, "shinimodori:shard-already");
                }
                else
                {
                    consumed = Blessings.Grant(server, plr, ps, coldOpen: true);
                    if (!consumed) Tell(plr, "shinimodori:shard-refused");
                }
            }
            else if (Attributes?["shinimodoriWitchFactor"].AsBool() == true)
            {
                if (ps.AuthorityUnlocked) Tell(plr, "shinimodori:factor-already");
                else
                {
                    ps.Milestones.Add("witchfactor");
                    server.AuthoritySystem.CheckUnlock(plr, ps);
                    consumed = ps.AuthorityUnlocked;
                }
            }

            if (consumed)
            {
                slot.TakeOut(1);
                slot.MarkDirty();
            }

            handling = EnumHandHandling.PreventDefault;
        }

        private void Tell(IServerPlayer plr, string langKey)
        {
            (api as ICoreServerAPI)?.SendMessage(plr, GlobalConstants.GeneralChatGroup,
                Lang.Get(langKey), EnumChatType.Notification);
        }
    }
}
