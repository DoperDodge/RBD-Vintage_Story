using System;
using System.Collections.Generic;
using System.Linq;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Death;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Witches
{
    /// <summary>
    /// Echidna's tea party (§10).
    ///
    /// A dream between deaths, in a white field, with tea brewed from your experiences.
    /// She is the only being you can speak to freely — the grip has no power here — and
    /// after four deaths at the same anchor that relief is the point of the whole scene.
    ///
    /// The dream is a reserved island rather than a custom dimension, and it is safe to
    /// be stranded in: the rewind that follows restores every player from the anchor
    /// snapshot, so a failed teleport home fixes itself.
    /// </summary>
    public class TeaPartySystem
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private TeaPartyConfig C => server.Cfg.TeaParty;

        /// <summary>Far from anywhere a player will ever walk, and high above any terrain.</summary>
        private const int IslandX = 1_000_000;
        private const int IslandZ = 1_000_000;
        private const int IslandY = 240;
        private const int IslandRadius = 14;

        private class Session
        {
            public string PlayerUID;
            public Action OnExit;
            public string WitchCode = "echidna";
            public string CurrentNode = "greeting";
            public bool TeaOffered;
        }

        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private bool islandBuilt;

        public TeaPartySystem(ShinimodoriServer server) { this.server = server; }

        public void Register() { }
        public void Unregister() { }

        public bool IsInside(string playerUid) => sessions.ContainsKey(playerUid);

        // ---------------------------------------------------------------- trigger

        public bool ShouldOffer(IServerPlayer plr, PlayerState ps)
        {
            if (!C.Enabled || !server.Cfg.Core.Enabled) return false;

            bool eligible = ps.DeathsAtAnchor + 1 >= C.DeathThreshold || ps.Despair >= C.DespairThreshold;
            if (!eligible) return false;

            double now = Api.World.Calendar.TotalHours;
            // Once per anchor: she is not a vending machine.
            if (ps.LastTeaPartyHours > double.MinValue && server.State.Anchor != null &&
                ps.LastTeaPartyHours >= server.State.Anchor.TotalHours) return false;

            double realMinutes = (Api.World.ElapsedMilliseconds - ps.LastTeaPartyHours) / 60000.0;
            if (ps.LastTeaPartyHours > double.MinValue && realMinutes < C.CooldownMinutes && realMinutes >= 0) return false;

            bool first = !ps.WitchesMet.Contains("echidna");
            float chance = first ? C.FirstChance : C.RepeatChance;
            return Api.World.Rand.NextDouble() <= chance;
        }

        // ------------------------------------------------------------------ enter

        public void Enter(IServerPlayer plr, Action onExit)
        {
            var ps = server.StateOf(plr);
            string witch = PickWitch(ps);

            var session = new Session { PlayerUID = plr.PlayerUID, OnExit = onExit, WitchCode = witch };
            sessions[plr.PlayerUID] = session;

            ps.LastTeaPartyHours = Api.World.Calendar.TotalHours;
            ps.WitchesMet.Add(witch);

            try
            {
                BuildIslandOnce();
                plr.Entity.TeleportToDouble(IslandX + 0.5, IslandY + 1, IslandZ + 3.5);
                plr.Entity.ServerPos.Yaw = (float)Math.PI;
                // Gravity, hunger, temperature and damage are all off inside the dream.
                plr.Entity.WatchedAttributes.SetBool("sm:inDream", true);
            }
            catch (Exception e)
            {
                server.Warn($"tea party island failed ({e.Message}); running the dream in place.");
            }

            server.StateChannel.SendPacket(new PktTeaParty { Entering = true, WitchCode = witch }, plr);
            SendNode(plr, session, "greeting");

            Api.Logger.Notification("[shinimodori] {0} was pulled into {1}'s tea party.", plr.PlayerName, witch);
        }

        private string PickWitch(PlayerState ps)
        {
            if (!C.OtherWitchesEnabled) return "echidna";

            // Each of the others appears once, rarely, in her place (§10.4).
            var candidates = new List<(string code, int deaths)>
            {
                ("minerva", C.MinervaDeaths), ("typhon", C.TyphonDeaths), ("daphne", C.DaphneDeaths),
                ("sekhmet", C.SekhmetDeaths), ("carmilla", C.CarmillaDeaths),
            };
            foreach (var (code, deaths) in candidates)
            {
                if (ps.TotalDeaths < deaths || ps.WitchesMet.Contains(code)) continue;
                if (Api.World.Rand.NextDouble() < 0.5) return code;
            }
            return "echidna";
        }

        // ----------------------------------------------------------------- dialog

        private void SendNode(IServerPlayer plr, Session s, string nodeId)
        {
            s.CurrentNode = nodeId;
            var ps = server.StateOf(plr);
            var pkt = new PktDialogNode
            {
                NodeId = nodeId,
                Speaker = Lang.Get($"shinimodori:witch-{s.WitchCode}"),
                Text = ComposeText(plr, ps, s, nodeId),
            };

            switch (nodeId)
            {
                case "greeting":
                    pkt.Choices.Add(new DialogChoice { Id = "speak", Label = Lang.Get("shinimodori:tea-choice-speak") });
                    pkt.Choices.Add(new DialogChoice { Id = "tea", Label = Lang.Get("shinimodori:tea-choice-tea") });
                    pkt.Choices.Add(new DialogChoice { Id = "witch", Label = Lang.Get("shinimodori:tea-choice-witch") });
                    pkt.Choices.Add(new DialogChoice { Id = "leave", Label = Lang.Get("shinimodori:tea-choice-leave") });
                    break;

                case "speak":
                    // The one place the taboo has no power. That is the whole node.
                    pkt.AllowFreeText = true;
                    pkt.Choices.Add(new DialogChoice { Id = "greeting", Label = Lang.Get("shinimodori:tea-choice-back") });
                    break;

                case "tea":
                    pkt.Choices.Add(new DialogChoice { Id = "tea-clarity", Label = Lang.Get("shinimodori:tea-gift-clarity") });
                    pkt.Choices.Add(new DialogChoice { Id = "tea-foresight", Label = Lang.Get("shinimodori:tea-gift-foresight") });
                    pkt.Choices.Add(new DialogChoice { Id = "tea-endurance", Label = Lang.Get("shinimodori:tea-gift-endurance") });
                    pkt.Choices.Add(new DialogChoice { Id = "tea-refuse", Label = Lang.Get("shinimodori:tea-gift-refuse") });
                    break;

                case "witch":
                    pkt.Choices.Add(new DialogChoice { Id = "greeting", Label = Lang.Get("shinimodori:tea-choice-back") });
                    break;

                default:
                    pkt.Closing = true;
                    break;
            }

            server.StateChannel.SendPacket(pkt, plr);
        }

        public void OnDialogChoice(IServerPlayer plr, PktDialogChoice msg)
        {
            if (!sessions.TryGetValue(plr.PlayerUID, out var s)) return;
            var ps = server.StateOf(plr);

            switch (msg.ChoiceId)
            {
                case "speak":
                case "witch":
                case "greeting":
                case "tea":
                    SendNode(plr, s, msg.ChoiceId);
                    return;

                case "freetext":
                    // She answers whatever was said, referencing their real numbers.
                    // No grip. No punishment. The relief is the mechanic.
                    var reply = new PktDialogNode
                    {
                        NodeId = "speak",
                        Speaker = Lang.Get($"shinimodori:witch-{s.WitchCode}"),
                        Text = ComposeFreeTextReply(ps, msg.FreeText ?? ""),
                        AllowFreeText = true,
                    };
                    reply.Choices.Add(new DialogChoice { Id = "greeting", Label = Lang.Get("shinimodori:tea-choice-back") });
                    server.StateChannel.SendPacket(reply, plr);
                    return;

                case "tea-clarity":
                    GrantTea(plr, ps, s, "clarity");
                    return;
                case "tea-foresight":
                    GrantTea(plr, ps, s, "foresight");
                    return;
                case "tea-endurance":
                    GrantTea(plr, ps, s, "endurance");
                    return;

                case "tea-refuse":
                    // Refusing pleases her more. There is no mechanical reward, and that
                    // is exactly why it is the better answer.
                    ps.EchidnaFavor++;
                    var refused = new PktDialogNode
                    {
                        NodeId = "tea",
                        Speaker = Lang.Get($"shinimodori:witch-{s.WitchCode}"),
                        Text = ps.EchidnaFavor >= C.FavorGiftThreshold
                             ? Lang.Get("shinimodori:tea-refuse-gift", NextAnchorHint())
                             : Lang.Get("shinimodori:tea-refuse-" + (1 + Api.World.Rand.Next(3))),
                    };
                    refused.Choices.Add(new DialogChoice { Id = "greeting", Label = Lang.Get("shinimodori:tea-choice-back") });
                    server.StateChannel.SendPacket(refused, plr);
                    return;

                case "leave":
                default:
                    Leave(plr, s);
                    return;
            }
        }

        private void GrantTea(IServerPlayer plr, PlayerState ps, Session s, string gift)
        {
            if (s.TeaOffered) { Leave(plr, s); return; }
            s.TeaOffered = true;

            ps.Miasma = Math.Min(100f, ps.Miasma + C.TeaMiasmaCost);
            ps.EchidnaDebt++;

            switch (gift)
            {
                case "clarity":
                    // The exact cause and place of the last death, marked forever.
                    var last = ps.Ledger.Count > 0 ? ps.Ledger[ps.Ledger.Count - 1] : null;
                    if (last != null) server.SendCue(plr, $"waypoint:{last.X}:{last.Y}:{last.Z}:{last.Cause}", 1f, 0f);
                    break;
                case "foresight":
                    ps.ForesightUntilHours = Api.World.Calendar.TotalHours + C.ForesightMinutes / 60.0;
                    break;
                case "endurance":
                    ps.SkipNextPhantomPain = true;
                    break;
            }

            var node = new PktDialogNode
            {
                NodeId = "tea",
                Speaker = Lang.Get($"shinimodori:witch-{s.WitchCode}"),
                Text = Lang.Get("shinimodori:tea-drink-" + gift),
            };
            node.Choices.Add(new DialogChoice { Id = "greeting", Label = Lang.Get("shinimodori:tea-choice-back") });
            server.StateChannel.SendPacket(node, plr);
            server.SyncState(plr);
        }

        private void Leave(IServerPlayer plr, Session s)
        {
            var ps = server.StateOf(plr);

            var farewell = new PktDialogNode
            {
                NodeId = "leave",
                Speaker = Lang.Get($"shinimodori:witch-{s.WitchCode}"),
                Text = Lang.Get("shinimodori:tea-farewell-" + (1 + Api.World.Rand.Next(6))),
                Closing = true,
            };
            server.StateChannel.SendPacket(farewell, plr);

            ApplyWitchGift(plr, ps, s.WitchCode);

            sessions.Remove(plr.PlayerUID);
            plr.Entity?.WatchedAttributes.SetBool("sm:inDream", false);
            server.StateChannel.SendPacket(new PktTeaParty { Entering = false, WitchCode = s.WitchCode }, plr);

            // The rewind follows, and it is what actually puts them back in the world.
            try { s.OnExit?.Invoke(); }
            catch (Exception e) { server.Warn($"tea party exit handler threw: {e.Message}"); }
        }

        /// <summary>The other witches each leave one mark, exactly once (§10.4).</summary>
        private void ApplyWitchGift(IServerPlayer plr, PlayerState ps, string witch)
        {
            double now = Api.World.Calendar.TotalHours;
            switch (witch)
            {
                case "minerva":
                    // Furious that you're hurt. Punches you. Heals you completely.
                    ps.PhantomPain.Clear();
                    var health = plr.Entity?.GetBehavior<Vintagestory.GameContent.EntityBehaviorHealth>();
                    if (health != null) health.Health = health.MaxHealth;
                    server.SendCue(plr, "minerva-punch", 1f, 1.5f);
                    break;
                case "sekhmet":
                    ps.Milestones.Add("gift:sekhmet");     // permanent, slower stability drain
                    break;
                case "daphne":
                    ps.Miasma = Math.Min(100f, ps.Miasma + 20f);
                    ps.Milestones.Add("gift:daphne");
                    break;
                case "carmilla":
                    ps.Milestones.Add("gift:carmilla");    // reads as a buff, is a curse
                    break;
                case "typhon":
                    ps.Milestones.Add("gift:typhon");
                    break;
            }
            server.SyncState(plr);
        }

        // ---------------------------------------------------------------- her voice

        /// <summary>
        /// She always quotes the ledger, because she has read it and she is delighted.
        /// The specificity is what makes her feel like she knows you.
        /// </summary>
        private string ComposeText(IServerPlayer plr, PlayerState ps, Session s, string nodeId)
        {
            if (s.WitchCode != "echidna")
                return Lang.Get($"shinimodori:witch-{s.WitchCode}-{nodeId}", ps.TotalDeaths);

            switch (nodeId)
            {
                case "greeting":
                    bool first = ps.Ledger.Count <= 1 || !ps.Milestones.Contains("met:echidna");
                    ps.Milestones.Add("met:echidna");
                    string opener = first
                        ? Lang.Get("shinimodori:tea-greet-first")
                        : Lang.Get("shinimodori:tea-greet-again-" + (1 + Api.World.Rand.Next(4)));
                    return opener + "\n\n" + LedgerRecital(ps);

                case "speak":
                    return Lang.Get("shinimodori:tea-speak-invite");

                case "witch":
                    return LoreFragment(ps);

                default:
                    return "";
            }
        }

        /// <summary>"Four times at this same point. Drowning, then the cold, then the wolves — twice."</summary>
        private string LedgerRecital(PlayerState ps)
        {
            string anchorId = server.State.Anchor?.Id.ToString() ?? "";
            var here = ps.Ledger.Where(d => d.AnchorId == anchorId).ToList();
            if (here.Count == 0) return Lang.Get("shinimodori:tea-recital-none");

            // Consecutive identical causes collapse into "— twice", the way a person
            // recounting a list actually speaks.
            var parts = new List<string>();
            int i = 0;
            while (i < here.Count)
            {
                int run = 1;
                while (i + run < here.Count && here[i + run].Cause == here[i].Cause) run++;
                string name = Lang.Get(DeathCauses.LangKey((DeathCause)here[i].Cause));
                parts.Add(run == 1 ? name : Lang.Get("shinimodori:tea-recital-times", name, run));
                i += run;
            }

            string list = string.Join(Lang.Get("shinimodori:tea-recital-sep"), parts);
            return Lang.Get("shinimodori:tea-recital", here.Count, list);
        }

        /// <summary>Lore, released by death count. Never a full explanation.</summary>
        private string LoreFragment(PlayerState ps)
        {
            int unlocked = Math.Min(8, 1 + ps.TotalDeaths / 6);
            return Lang.Get("shinimodori:tea-lore-" + (1 + Api.World.Rand.Next(unlocked)));
        }

        private string ComposeFreeTextReply(PlayerState ps, string said)
        {
            // She responds to the *shape* of what was said, with their real numbers in it.
            var score = Taboo.TabooDetector.Evaluate(said, 3, server.Cfg.Taboo.OocPrefix);
            string key = score.Triggered
                ? "shinimodori:tea-reply-taboo-" + (1 + Api.World.Rand.Next(5))
                : "shinimodori:tea-reply-plain-" + (1 + Api.World.Rand.Next(4));
            return Lang.Get(key, ps.TotalDeaths, ps.DeathsAtAnchor, (int)ps.Miasma);
        }

        private string NextAnchorHint()
        {
            var reason = server.State.Anchor?.AnchorReason ?? "";
            return Lang.Get("shinimodori:anchorreason-" + (reason.Contains(":") ? reason.Split(':')[0] : reason));
        }

        // ---------------------------------------------------------------- the field

        /// <summary>
        /// A flat white field with a single table, built once and left alone. Cheap,
        /// deterministic, and far enough away that no player will ever stumble into it.
        /// </summary>
        private void BuildIslandOnce()
        {
            if (islandBuilt) return;
            islandBuilt = true;

            int flowerId = FindBlockId("shinimodori:witch-whiteflower", "flower-lilyofthevalley-free", "snowblock");
            int groundId = FindBlockId("shinimodori:witch-ground", "snowblock");
            if (groundId == 0) { server.Warn("no block available for the tea party field"); return; }

            var bulk = Api.World.GetBlockAccessorBulkUpdate(true, true);
            var pos = new BlockPos(0, 0, 0, 0);

            for (int dx = -IslandRadius; dx <= IslandRadius; dx++)
            {
                for (int dz = -IslandRadius; dz <= IslandRadius; dz++)
                {
                    if (dx * dx + dz * dz > IslandRadius * IslandRadius) continue;
                    pos.Set(IslandX + dx, IslandY, IslandZ + dz);
                    bulk.SetBlock(groundId, pos);

                    if (flowerId != 0 && flowerId != groundId && (dx + dz) % 3 == 0)
                    {
                        pos.Set(IslandX + dx, IslandY + 1, IslandZ + dz);
                        bulk.SetBlock(flowerId, pos);
                    }
                }
            }

            int tableId = FindBlockId("shinimodori:witch-tea-table", "table-oak", "chest");
            if (tableId != 0)
            {
                pos.Set(IslandX, IslandY + 1, IslandZ);
                bulk.SetBlock(tableId, pos);
            }

            bulk.Commit();
            Api.Logger.Notification("[shinimodori] The white field is ready.");
        }

        private int FindBlockId(params string[] codes)
        {
            foreach (var code in codes)
            {
                try
                {
                    var loc = code.Contains(":") ? new AssetLocation(code) : new AssetLocation("game", code);
                    var block = Api.World.GetBlock(loc);
                    if (block != null) return block.BlockId;
                }
                catch { }
            }
            return 0;
        }

        /// <summary>Admin `/rbd teaparty`.</summary>
        public void ForceEnter(IServerPlayer plr)
        {
            if (IsInside(plr.PlayerUID)) return;
            Enter(plr, () => { });
        }
    }
}
