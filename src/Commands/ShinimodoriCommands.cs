using System;
using System.Text;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Death;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Shinimodori.Commands
{
    /// <summary>
    /// /rbd (§18). `status` is for anyone; everything that changes the world needs the
    /// controlserver privilege. `fx` exists because being able to play any client
    /// effect in isolation pays for itself ten times over while tuning the cinematics.
    /// </summary>
    public static class ShinimodoriCommands
    {
        public static void Register(ICoreServerAPI sapi, ShinimodoriServer server)
        {
            var parsers = sapi.ChatCommands.Parsers;

            sapi.ChatCommands.Create("rbd")
                .WithDescription("Return by Death")
                .RequiresPrivilege(Privilege.chat)
                .WithAlias("shinimodori")

                .BeginSubCommand("status")
                    .WithDescription("Your anchor, your deaths, your scent")
                    .RequiresPlayer()
                    .HandleWith(args => Status(server, args))
                .EndSubCommand()

                .BeginSubCommand("ledger")
                    .WithDescription("Every death you have ever died")
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalInt("page"))
                    .HandleWith(args => Ledger(server, args))
                .EndSubCommand()

                .BeginSubCommand("anchor")
                    .WithDescription("[admin] Force an anchor here")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .HandleWith(args =>
                    {
                        var plr = (IServerPlayer)args.Caller.Player;
                        var rp = server.Anchors.SetAnchor(plr, "manual");
                        return TextCommandResult.Success($"Anchor set at {rp.Origin}.");
                    })
                .EndSubCommand()

                .BeginSubCommand("return")
                    .WithDescription("[admin] Force a return now")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .HandleWith(args =>
                    {
                        var plr = (IServerPlayer)args.Caller.Player;
                        if (server.State.Anchor == null) return TextCommandResult.Error("No anchor exists yet.");
                        server.Returns.Begin(plr, DeathCause.Voluntary, "", voluntary: true);
                        return TextCommandResult.Success("Returning.");
                    })
                .EndSubCommand()

                .BeginSubCommand("journal")
                    .WithDescription("[admin] Journal diagnostics")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.OptionalWord("action"))
                    .HandleWith(args => Journal(server, args))
                .EndSubCommand()

                .BeginSubCommand("verify")
                    .WithDescription("[admin] Sample the world against the journal")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(args => Verify(server))
                .EndSubCommand()

                .BeginSubCommand("miasma")
                    .WithDescription("[admin] Read or set the Witch's scent")
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalWord("get|set"), parsers.OptionalFloat("value"))
                    .HandleWith(args => Miasma(server, args))
                .EndSubCommand()

                .BeginSubCommand("despair")
                    .WithDescription("[admin] Set despair")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .WithArgs(parsers.Float("value"))
                    .HandleWith(args =>
                    {
                        var plr = (IServerPlayer)args.Caller.Player;
                        var ps = server.StateOf(plr);
                        ps.Despair = Math.Max(0, Math.Min(100, (float)args[0]));
                        server.SyncState(plr);
                        return TextCommandResult.Success($"Despair = {ps.Despair:F0}.");
                    })
                .EndSubCommand()

                .BeginSubCommand("taboo")
                    .WithDescription("[admin] Score text, or force a grip stage")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .WithArgs(parsers.Word("test|trigger"), parsers.OptionalAll("argument"))
                    .HandleWith(args => Taboo(server, args))
                .EndSubCommand()

                .BeginSubCommand("teaparty")
                    .WithDescription("[admin] Pull yourself into the dream")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .HandleWith(args =>
                    {
                        server.TeaParty.ForceEnter((IServerPlayer)args.Caller.Player);
                        return TextCommandResult.Success("She was expecting you.");
                    })
                .EndSubCommand()

                .BeginSubCommand("bless")
                    .WithDescription("[admin] Grant or revoke the blessing")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.OnlinePlayer("player"), parsers.OptionalBool("grant"))
                    .HandleWith(args => Bless(server, args))
                .EndSubCommand()

                .BeginSubCommand("fx")
                    .WithDescription("[admin] Play one client effect in isolation")
                    .RequiresPlayer()
                    .WithArgs(parsers.Word("effect"), parsers.OptionalFloat("seconds"))
                    .HandleWith(args => Fx(server, args))
                .EndSubCommand()

                .BeginSubCommand("preset")
                    .WithDescription("[admin] Apply a config preset")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("name"))
                    .HandleWith(args => Preset(sapi, server, args))
                .EndSubCommand()

                .BeginSubCommand("mabeasts")
                    .WithDescription("[admin] Send a pack after yourself")
                    .RequiresPrivilege(Privilege.controlserver)
                    .RequiresPlayer()
                    .HandleWith(args =>
                    {
                        int n = server.Miasma.SpawnPack((IServerPlayer)args.Caller.Player);
                        return n > 0
                            ? TextCommandResult.Success($"{n} Ulgarm are on their way.")
                            : TextCommandResult.Error("Could not spawn any — is the mabeast entity registered?");
                    })
                .EndSubCommand();
        }

        // ------------------------------------------------------------------ handlers

        private static TextCommandResult Status(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var plr = (IServerPlayer)args.Caller.Player;
            var ps = server.StateOf(plr);
            var anchor = server.State.Anchor;
            var stats = server.Recorder.Stats();
            double now = server.Api.World.Calendar.TotalHours;

            var sb = new StringBuilder();
            sb.AppendLine(ps.Blessed ? "You cannot die." : "You are an ordinary person.");
            if (anchor != null)
            {
                sb.AppendLine($"Anchor: {anchor.AnchorReason}, {now - anchor.TotalHours:F1}h ago, at {anchor.Origin}" +
                              (anchor.Degraded ? " (degraded)" : ""));
            }
            else sb.AppendLine("Anchor: none.");

            sb.AppendLine($"Deaths: {ps.TotalDeaths} total, {ps.DeathsAtAnchor} at this anchor.");
            sb.AppendLine($"Miasma: {ps.Miasma:F1} (tier {server.Miasma.TierIndexFor(plr, ps)})" +
                          (server.Miasma.IsWearingPeltCloak(plr) ? ", cloaked" : ""));
            sb.AppendLine($"Despair: {ps.Despair:F0}   Phantom pain: {server.Trauma.CurrentPhantomPain(ps, now) * 100:F0}%");
            sb.AppendLine($"Journal: {stats.Blocks} blocks, {stats.Entities} entities, ~{stats.ApproximateBytes / 1024}KB" +
                          (stats.IntegrityOk ? "" : $" — INTEGRITY LOST ({stats.IntegrityFailReason})") +
                          (stats.Overflowed ? " — OVERFLOWED" : ""));
            if (ps.AuthorityUnlocked) sb.AppendLine("The arms are listening.");

            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private static TextCommandResult Ledger(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var plr = (IServerPlayer)args.Caller.Player;
            var ps = server.StateOf(plr);
            if (ps.Ledger.Count == 0) return TextCommandResult.Success("You have never died.");

            int page = args[0] == null ? 1 : Math.Max(1, (int)args[0]);
            const int perPage = 10;
            int pages = (ps.Ledger.Count + perPage - 1) / perPage;
            page = Math.Min(page, pages);

            var sb = new StringBuilder();
            sb.AppendLine($"Death ledger — page {page}/{pages} ({ps.Ledger.Count} deaths)");
            for (int i = (page - 1) * perPage; i < Math.Min(ps.Ledger.Count, page * perPage); i++)
            {
                var d = ps.Ledger[i];
                sb.AppendLine($"  {d.Index,4}. {(DeathCause)d.Cause}" +
                              (string.IsNullOrEmpty(d.KillerName) ? "" : $" ({d.KillerName})") +
                              $" at {d.X},{d.Y},{d.Z}, hour {d.TotalHours:F0}, miasma {d.MiasmaAtDeath:F0}");
            }

            // Also push the full ledger to the client so the GUI can open.
            server.StateChannel.SendPacket(new PktLedgerRequest(), plr);
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        private static TextCommandResult Journal(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            string action = (args[0] as string ?? "stats").ToLowerInvariant();
            var stats = server.Recorder.Stats();

            if (action == "clear")
            {
                var id = server.State.Anchor?.Id ?? Guid.Empty;
                server.Recorder.ResetTo(id);
                return TextCommandResult.Success("Journal cleared. The world will no longer rewind past this point.");
            }

            return TextCommandResult.Success(
                $"blocks={stats.Blocks} entities={stats.Entities} ~{stats.ApproximateBytes / 1024}KB " +
                $"integrity={(stats.IntegrityOk ? "ok" : "LOST: " + stats.IntegrityFailReason)} " +
                $"overflowed={stats.Overflowed}");
        }

        private static TextCommandResult Verify(ShinimodoriServer server)
        {
            var journal = server.Recorder.Journal;
            if (journal.TouchOrder.Count == 0) return TextCommandResult.Success("Nothing journaled yet.");

            var acc = server.Api.World.BlockAccessor;
            int checks = Math.Min(server.Cfg.Anchors.VerifySampleCount, journal.TouchOrder.Count);
            int differ = 0;
            var rand = new Random();

            for (int i = 0; i < checks; i++)
            {
                var key = journal.TouchOrder[rand.Next(journal.TouchOrder.Count)];
                var d = journal.Blocks[key];
                var block = acc.GetBlock(key.ToBlockPos(), BlockLayersAccess.Solid);
                if (block == null || block.BlockId != d.OldSolidId) differ++;
            }

            // Differences here are expected — they are what a return would undo.
            return TextCommandResult.Success(
                $"{differ}/{checks} sampled positions differ from their anchor state. " +
                "That is the size of what a return would undo.");
        }

        private static TextCommandResult Miasma(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var plr = (IServerPlayer)args.Caller.Player;
            var ps = server.StateOf(plr);
            string action = (args[0] as string ?? "get").ToLowerInvariant();

            if (action == "set")
            {
                if (!plr.HasPrivilege(Privilege.controlserver))
                    return TextCommandResult.Error("You do not have permission to set that.");
                if (args[1] == null) return TextCommandResult.Error("Usage: /rbd miasma set <0-100>");
                ps.Miasma = Math.Max(0, Math.Min(100, (float)args[1]));
                server.SyncState(plr);
            }

            return TextCommandResult.Success($"Miasma {ps.Miasma:F1}, tier {server.Miasma.TierIndexFor(plr, ps)}.");
        }

        private static TextCommandResult Taboo(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var plr = (IServerPlayer)args.Caller.Player;
            string action = ((string)args[0]).ToLowerInvariant();
            string argument = args[1] as string ?? "";

            if (action == "trigger")
            {
                int stage = int.TryParse(argument.Trim(), out int s) ? s : 1;
                server.TabooSystem.Trigger(plr, Math.Max(1, Math.Min(3, stage)));
                return TextCommandResult.Success($"Forced grip stage {stage}.");
            }

            if (string.IsNullOrWhiteSpace(argument))
                return TextCommandResult.Error("Usage: /rbd taboo test <text>");

            // Scores without triggering, so false positives can be diagnosed safely.
            return TextCommandResult.Success(server.TabooSystem.Test(argument).Explain());
        }

        private static TextCommandResult Bless(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var target = args[0] as IServerPlayer;
            if (target == null) return TextCommandResult.Error("No such player online.");

            bool grant = args[1] == null || (bool)args[1];
            var ps = server.StateOf(target);

            if (grant)
            {
                return Blessings.Grant(server, target, ps, coldOpen: true)
                    ? TextCommandResult.Success($"{target.PlayerName} cannot die any more.")
                    : TextCommandResult.Error($"{target.PlayerName} could not be blessed " +
                                              "(already blessed, or soloReturner already has one).");
            }

            Blessings.Revoke(server, target, ps);
            return TextCommandResult.Success($"{target.PlayerName} is mortal again.");
        }

        private static TextCommandResult Fx(ShinimodoriServer server, TextCommandCallingArgs args)
        {
            var plr = (IServerPlayer)args.Caller.Player;
            string effect = (string)args[0];
            float seconds = args[1] == null ? 3f : (float)args[1];
            server.SendCue(plr, effect, 1f, seconds);
            return TextCommandResult.Success($"Playing '{effect}' for {seconds:F1}s. " +
                "Try: void, rewind, arrival, grip1, grip2, grip3, breakdown, resolve, frayed, " +
                "coldopen, anchor, silhouette, scar, mabeast-howl, authority-unlock.");
        }

        private static TextCommandResult Preset(ICoreServerAPI sapi, ShinimodoriServer server, TextCommandCallingArgs args)
        {
            string name = ((string)args[0]).ToLowerInvariant();
            if (!ConfigPresets.IsKnown(name))
                return TextCommandResult.Error("Unknown preset. Try: balanced, anime, forgiving, cinematic, custom.");

            server.Cfg.Preset = name;
            ConfigPresets.Apply(server.Cfg, name);
            ConfigPresets.Validate(server.Cfg, sapi);
            sapi.StoreModConfig(server.Cfg, ShinimodoriModSystem.ConfigFile);

            foreach (var p in server.BlessedPlayers()) server.SyncState(p);
            return TextCommandResult.Success($"Preset '{name}' applied and saved. " +
                "Effects that are already running keep their old timings until they end.");
        }
    }
}
