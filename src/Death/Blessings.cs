using System;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Shinimodori.Death
{
    /// <summary>
    /// Who can come back, and how they found out they could (§3).
    ///
    /// Nobody earns Return by Death. In the default mode a player simply wakes up with
    /// it, hears something, and is never told anything again.
    /// </summary>
    public static class Blessings
    {
        /// <summary>Mirrored onto the player entity so client-side systems can read it cheaply.</summary>
        public const string BlessedAttr = "sm:blessed";

        public static void OnPlayerJoin(ShinimodoriServer server, IServerPlayer plr, Core.PlayerState ps)
        {
            AttachBehavior(server, plr);

            if (!ps.Blessed && ShouldAutoBless(server, plr))
            {
                Grant(server, plr, ps, coldOpen: true);
                return;
            }

            plr.Entity.WatchedAttributes.SetBool(BlessedAttr, ps.Blessed);
            server.StateChannel.SendPacket(new PktBlessing { Blessed = ps.Blessed, PlayColdOpen = false }, plr);
        }

        private static bool ShouldAutoBless(ShinimodoriServer server, IServerPlayer plr)
        {
            if (server.Cfg.Core.BlessingMode != "auto") return false;

            // soloReturner means exactly one, enforced (§13). The first to arrive is the one.
            if (server.Cfg.Multiplayer.Mode == "soloReturner")
            {
                foreach (var kv in server.State.Players)
                    if (kv.Value.Blessed && kv.Key != plr.PlayerUID) return false;
            }
            return true;
        }

        /// <summary>Grants the blessing. There is no in-game way to take it back.</summary>
        public static bool Grant(ShinimodoriServer server, IServerPlayer plr, Core.PlayerState ps, bool coldOpen)
        {
            if (ps.Blessed) return false;

            if (server.Cfg.Multiplayer.Mode == "soloReturner")
            {
                foreach (var kv in server.State.Players)
                {
                    if (kv.Value.Blessed && kv.Key != plr.PlayerUID)
                    {
                        server.Warn($"refused to bless {plr.PlayerName}: soloReturner already has one.");
                        return false;
                    }
                }
            }

            ps.Blessed = true;
            AttachBehavior(server, plr);
            plr.Entity.WatchedAttributes.SetBool(BlessedAttr, true);

            bool playIntro = coldOpen && !ps.ColdOpenShown;
            if (playIntro) ps.ColdOpenShown = true;

            server.StateChannel.SendPacket(new PktBlessing { Blessed = true, PlayColdOpen = playIntro }, plr);

            if (server.State.Anchor == null) server.Anchors.SetAnchor(plr, "initial");
            server.SyncState(plr);

            server.Api.Logger.Notification("[shinimodori] {0} is blessed.", plr.PlayerName);
            return true;
        }

        /// <summary>Admin-only. The config can force-revoke; the game never can.</summary>
        public static void Revoke(ShinimodoriServer server, IServerPlayer plr, Core.PlayerState ps)
        {
            ps.Blessed = false;
            plr.Entity?.WatchedAttributes.SetBool(BlessedAttr, false);
            server.StateChannel.SendPacket(new PktBlessing { Blessed = false, PlayColdOpen = false }, plr);
            server.SyncState(plr);
        }

        private static void AttachBehavior(ShinimodoriServer server, IServerPlayer plr)
        {
            var e = plr.Entity;
            if (e == null) return;
            var existing = e.GetBehavior<EntityBehaviorReturner>();
            if (existing != null) { existing.Init(server); return; }

            var beh = new EntityBehaviorReturner(e);
            beh.Init(server);
            e.AddBehavior(beh);
        }
    }
}
