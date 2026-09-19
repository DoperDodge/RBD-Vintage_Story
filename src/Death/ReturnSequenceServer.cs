using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Death
{
    /// <summary>
    /// The server-side orchestration of a return (§5.2).
    ///
    /// The server owns every transition. A client timer never decides when control comes
    /// back — it only renders what it is told, which is what keeps a laggy or hostile
    /// client from shortening the Void or skipping the cost.
    /// </summary>
    public class ReturnSequenceServer
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;
        private ShinimodoriConfig Cfg => server.Cfg;

        private class ReturnRun
        {
            public string PlayerUID;
            public ReturnStage Stage;
            public double StageEndsAtMs;
            public DeathCause Cause;
            public string KillerName = "";
            public bool Voluntary;
            public bool RewindDone;
            public bool SafeMode;
            public bool TeaPartyPending;
            public bool InTeaParty;
            public List<string> Bystanders = new List<string>();
        }

        private readonly Dictionary<string, ReturnRun> runs = new Dictionary<string, ReturnRun>();
        private long tickListener = -1;

        public ReturnSequenceServer(ShinimodoriServer server)
        {
            this.server = server;
            tickListener = server.Api.Event.RegisterGameTickListener(OnTick, 20);
        }

        public bool IsReturning(string playerUid) => runs.ContainsKey(playerUid);
        public bool AnyReturning => runs.Count > 0;

        // ------------------------------------------------------------- entry points

        /// <summary>
        /// Harmony bridge: a player is about to die outright — /kill, the void, another
        /// mod's Die() call. Returning true cancels the death (§5.1).
        /// </summary>
        public bool OnEntityWillDie(Entity entity, EnumDespawnReason reason, DamageSource src)
        {
            if (!Cfg.Core.Enabled) return false;
            if (reason != EnumDespawnReason.Death) return false;
            if (!(entity is EntityPlayer ep)) return false;
            if (!(ep.Player is IServerPlayer plr)) return false;
            if (!server.IsBlessed(plr)) return false;

            if (IsReturning(plr.PlayerUID)) return true;   // already going; swallow it

            var cause = DeathCauses.From(src, out string killer);
            Begin(plr, cause, killer, voluntary: cause == DeathCause.Voluntary);
            return true;
        }

        /// <summary>
        /// Called by <see cref="EntityBehaviorReturner"/> when damage would be lethal.
        /// The damage is nulled by the caller; death never happens.
        /// </summary>
        public void BeginFromLethalDamage(IServerPlayer plr, DamageSource src)
        {
            var cause = DeathCauses.From(src, out string killer);
            Begin(plr, cause, killer, voluntary: false);
        }

        public void OnVoluntaryRequest(IServerPlayer plr, PktVoluntaryReturn msg)
        {
            if (!msg.Commit || !Cfg.Core.AllowVoluntaryReturn) return;
            if (!server.IsBlessed(plr) || IsReturning(plr.PlayerUID)) return;

            var ps = server.StateOf(plr);
            double nowMs = Api.World.ElapsedMilliseconds;
            double cooldownMs = Cfg.Core.VoluntaryCooldownMinutes * 60_000.0;
            if (ps.LastVoluntaryReturnRealMs > double.MinValue && nowMs - ps.LastVoluntaryReturnRealMs < cooldownMs)
            {
                server.Debug($"{plr.PlayerName} tried a voluntary return during cooldown");
                return;
            }

            if (Cfg.Core.VoluntaryRequiresBlade && !HoldingBlade(plr))
            {
                server.Debug($"{plr.PlayerName} tried a voluntary return without a blade");
                return;
            }

            ps.LastVoluntaryReturnRealMs = nowMs;
            Begin(plr, DeathCause.Voluntary, "", voluntary: true);
        }

        private static bool HoldingBlade(IServerPlayer plr)
        {
            var stack = plr.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            var code = stack?.Collectible?.Code?.Path ?? "";
            return code.Contains("knife") || code.Contains("sword") || code.Contains("blade")
                   || code.Contains("falx") || code.Contains("spear");
        }

        /// <summary>Taboo stage 3 and admin commands come through here.</summary>
        public void BeginTaboo(IServerPlayer plr)
        {
            if (IsReturning(plr.PlayerUID)) return;
            Begin(plr, DeathCause.Taboo, "", voluntary: false);
        }

        // ------------------------------------------------------------------- begin

        public void Begin(IServerPlayer plr, DeathCause cause, string killerName, bool voluntary)
        {
            if (IsReturning(plr.PlayerUID)) return;
            if (server.State.Anchor == null)
            {
                // Nothing to come back to. Set one here so the player is not deleted,
                // then let the damage stand as an ordinary near-miss.
                server.Warn($"{plr.PlayerName} would have returned, but no anchor exists; setting one now.");
                server.Anchors.SetAnchor(plr, "emergency");
                return;
            }

            var ps = server.StateOf(plr);
            var run = new ReturnRun
            {
                PlayerUID = plr.PlayerUID,
                Stage = ReturnStage.Dying,
                Cause = cause,
                KillerName = killerName ?? "",
                Voluntary = voluntary,
                StageEndsAtMs = Api.World.ElapsedMilliseconds + Cfg.Core.DyingSeconds * 1000,
            };
            runs[plr.PlayerUID] = run;

            // The ledger is written at the moment of death, not at arrival: if the
            // server dies mid-return the death still happened.
            RecordDeath(plr, ps, cause, killerName);

            server.State.Anchor.DeathsAtThisAnchor++;
            ps.DeathsAtAnchor++;
            ps.TotalDeaths++;

            run.TeaPartyPending = server.TeaParty.ShouldOffer(plr, ps);

            int seed = Api.World.Rand.Next();
            var begin = new PktReturnBegin
            {
                DeathCause = (int)cause,
                DyingSeconds = Cfg.Core.DyingSeconds,
                VoidSeconds = Cfg.Core.VoidSeconds,
                RewindSeconds = Cfg.Core.RewindSeconds,
                ArrivalSeconds = Cfg.Core.ArrivalSeconds,
                Seed = seed,
                Bystander = false,
                Voluntary = voluntary,
                KillerName = run.KillerName,
                DeathIndex = ps.TotalDeaths,
            };
            server.StateChannel.SendPacket(begin, plr);

            NotifyBystanders(plr, run, seed);

            // Nothing can touch a returning player (§13).
            plr.Entity.WatchedAttributes.SetBool("sm:returning", true);
            plr.Entity.Pos.Motion.Set(0, 0, 0);

            if (WorldWideRewind) server.Freezer.Freeze();

            Api.Logger.Notification("[shinimodori] {0} died ({1}{2}) — return #{3}, {4} at this anchor.",
                plr.PlayerName, cause, string.IsNullOrEmpty(killerName) ? "" : ": " + killerName,
                ps.TotalDeaths, ps.DeathsAtAnchor);
        }

        /// <summary>personalLoop keeps other players out of it entirely (§13).</summary>
        private bool WorldWideRewind => Cfg.Multiplayer.Mode != "personalLoop";

        private void NotifyBystanders(IServerPlayer returner, ReturnRun run, int seed)
        {
            if (!WorldWideRewind || !Cfg.Multiplayer.FreezeNonReturners) return;

            foreach (var p in Api.World.AllOnlinePlayers)
            {
                if (!(p is IServerPlayer sp) || sp.PlayerUID == returner.PlayerUID) continue;

                bool alsoBlessed = Cfg.Multiplayer.Mode == "everyoneReturns" && server.IsBlessed(sp);
                run.Bystanders.Add(sp.PlayerUID);

                server.StateChannel.SendPacket(new PktReturnBegin
                {
                    DeathCause = (int)run.Cause,
                    DyingSeconds = Cfg.Core.DyingSeconds,
                    VoidSeconds = Cfg.Core.VoidSeconds,
                    RewindSeconds = Cfg.Core.RewindSeconds,
                    ArrivalSeconds = Cfg.Core.ArrivalSeconds,
                    Seed = seed,
                    // A bystander gets the Void without the whisper, the silhouette or
                    // the text. In fiction, nothing happened to them.
                    Bystander = !alsoBlessed,
                    Voluntary = run.Voluntary,
                    DeathIndex = 0,
                }, sp);

                if (!string.IsNullOrEmpty(Cfg.Multiplayer.NonReturnerMessage) && !alsoBlessed)
                    Api.SendMessage(sp, GlobalConstants.GeneralChatGroup,
                        Cfg.Multiplayer.NonReturnerMessage, EnumChatType.Notification);
            }
        }

        private void RecordDeath(IServerPlayer plr, PlayerState ps, DeathCause cause, string killerName)
        {
            var pos = plr.Entity.Pos.AsBlockPos;
            ps.Ledger.Add(new DeathRecord
            {
                Index = ps.TotalDeaths + 1,
                Cause = (int)cause,
                KillerName = killerName ?? "",
                X = pos.X, Y = pos.Y, Z = pos.Z,
                TotalHours = Api.World.Calendar.TotalHours,
                Depth = Api.World.SeaLevel - pos.Y,
                AnchorId = server.State.Anchor?.Id.ToString() ?? "",
                MiasmaAtDeath = ps.Miasma,
            });

            server.Trauma.OnDeathRecorded(plr, ps, pos, cause);
        }

        // -------------------------------------------------------------- the machine

        private void OnTick(float dt)
        {
            if (runs.Count == 0) return;
            double now = Api.World.ElapsedMilliseconds;

            // ToArray: a stage handler may finish and remove its own run.
            foreach (var uid in new List<string>(runs.Keys))
            {
                if (!runs.TryGetValue(uid, out var run)) continue;
                var plr = Api.World.PlayerByUid(uid) as IServerPlayer;
                if (plr?.Entity == null) continue;   // disconnected: resumed on rejoin

                // A returning player is untouchable and motionless for the duration.
                plr.Entity.Pos.Motion.Set(0, 0, 0);

                if (now < run.StageEndsAtMs) continue;

                switch (run.Stage)
                {
                    case ReturnStage.Dying:
                        EnterVoid(plr, run);
                        break;

                    case ReturnStage.Void:
                        if (run.InTeaParty) break;                  // her dream holds the Void open
                        if (!run.RewindDone) { run.StageEndsAtMs = now + 250; break; }  // extend, don't rush
                        EnterRewind(plr, run);
                        break;

                    case ReturnStage.Rewind:
                        EnterArrival(plr, run);
                        break;

                    case ReturnStage.Arrival:
                        Finish(plr, run);
                        break;
                }
            }
        }

        private void EnterVoid(IServerPlayer plr, ReturnRun run)
        {
            run.Stage = ReturnStage.Void;
            run.StageEndsAtMs = Api.World.ElapsedMilliseconds + Cfg.Core.VoidSeconds * 1000;
            Advance(plr, run, ReturnStage.Void, Cfg.Core.VoidSeconds);

            if (run.TeaPartyPending)
            {
                // The dream happens *before* the rewind, not alongside it: the rewind
                // puts the player back at the anchor, and it must not do that while
                // they are sitting at her table.
                run.InTeaParty = true;
                server.TeaParty.Enter(plr, () =>
                {
                    run.InTeaParty = false;
                    StartRewind(plr, run);
                    // The Void resumes where it left off, never shortened by the dream.
                    run.StageEndsAtMs = Api.World.ElapsedMilliseconds + 800;
                });
                return;
            }

            StartRewind(plr, run);
        }

        private void StartRewind(IServerPlayer plr, ReturnRun run)
        {
            var anchor = server.State.Anchor;
            server.Recorder.Suspended = true;

            server.Engine.Execute(anchor, server.Recorder.Journal, result =>
            {
                server.Recorder.Suspended = false;
                run.RewindDone = true;
                run.SafeMode = result.Outcome != RewindOutcome.Success;

                if (Cfg.Debug.LogRewindTimings)
                {
                    Api.Logger.Notification(
                        "[shinimodori] Rewind {0} in {1}ms — {2} blocks ({3} reconciled), {4} block entities, " +
                        "{5} removed, {6} respawned, {7} restored, {8} items swept{9}",
                        result.Outcome, result.ElapsedMs, result.BlocksRestored, result.BlocksReconciled,
                        result.BlockEntitiesRestored,
                        result.EntitiesRemoved, result.EntitiesRespawned, result.EntitiesRestored,
                        result.ItemEntitiesRemoved,
                        string.IsNullOrEmpty(result.Note) ? "" : " (" + result.Note + ")");
                }

                if (result.Outcome == RewindOutcome.SafeMode)
                {
                    // Lore-wrapped, never an error dialog (§4.8). A fresh anchor follows,
                    // because the journal we just failed to trust must not be reused.
                    server.SendCue(plr, "frayed", 1f, 3f);
                    Api.Event.EnqueueMainThreadTask(() =>
                    {
                        if (plr.Entity != null) server.Anchors.SetAnchor(plr, "safeMode");
                    }, "sm-safemode-anchor");
                }
                else
                {
                    // The journal restarts from the same anchor, which is still valid.
                    server.Recorder.ResetTo(server.State.Anchor.Id);
                }
            });
        }

        private void EnterRewind(IServerPlayer plr, ReturnRun run)
        {
            run.Stage = ReturnStage.Rewind;
            run.StageEndsAtMs = Api.World.ElapsedMilliseconds + Cfg.Core.RewindSeconds * 1000;
            Advance(plr, run, ReturnStage.Rewind, Cfg.Core.RewindSeconds);
        }

        private void EnterArrival(IServerPlayer plr, ReturnRun run)
        {
            run.Stage = ReturnStage.Arrival;
            run.StageEndsAtMs = Api.World.ElapsedMilliseconds + Cfg.Core.ArrivalSeconds * 1000;

            var ps = server.StateOf(plr);

            // Step 7 of §4.6: everything that is *not* rewound is re-applied on top,
            // after the world has been put back. This ordering is the mod's thesis.
            server.Miasma.OnReturn(plr, ps, run.Cause, run.Voluntary);
            server.Trauma.OnArrival(plr, ps, run.Cause);
            server.Miasma.TearTemporalFabric(plr);
            server.AuthoritySystem.CheckUnlock(plr, ps);

            var complete = new PktReturnComplete
            {
                ArrivalSeconds = Cfg.Core.ArrivalSeconds,
                InputUnlockSeconds = Cfg.Core.ArrivalInputUnlockSeconds,
                DeathCause = (int)run.Cause,
                Voluntary = run.Voluntary,
                SafeMode = run.SafeMode,
                Afterimage = (int)DeathCauses.AfterimageFor(run.Cause),
            };
            server.StateChannel.SendPacket(complete, plr);

            foreach (var uid in run.Bystanders)
            {
                if (Api.World.PlayerByUid(uid) is IServerPlayer sp)
                    server.StateChannel.SendPacket(new PktReturnComplete
                    {
                        ArrivalSeconds = Cfg.Core.ArrivalSeconds,
                        InputUnlockSeconds = Cfg.Core.ArrivalInputUnlockSeconds,
                        DeathCause = 0,
                        Voluntary = false,
                        SafeMode = false,
                        Afterimage = 0,
                    }, sp);
            }

            if (server.Freezer.IsFrozen && !AnyOtherRunNeedsFreeze(run)) server.Freezer.Thaw();

            server.SyncState(plr);
        }

        private bool AnyOtherRunNeedsFreeze(ReturnRun self)
        {
            foreach (var kv in runs)
                if (kv.Value != self && kv.Value.Stage < ReturnStage.Arrival) return true;
            return false;
        }

        private void Finish(IServerPlayer plr, ReturnRun run)
        {
            runs.Remove(run.PlayerUID);
            plr.Entity.WatchedAttributes.SetBool("sm:returning", false);
            server.State.MidReturn.Remove(run.PlayerUID);
            server.SyncState(plr);
        }

        private void Advance(IServerPlayer plr, ReturnRun run, ReturnStage stage, float seconds)
        {
            var pkt = new PktStageAdvance { Stage = stage, Seconds = seconds };
            server.StateChannel.SendPacket(pkt, plr);
            foreach (var uid in run.Bystanders)
                if (Api.World.PlayerByUid(uid) is IServerPlayer sp)
                    server.StateChannel.SendPacket(pkt, sp);
        }

        // --------------------------------------------------------------- recovery

        /// <summary>
        /// A player who quit mid-return is put back at the anchor before they get
        /// control, rather than resuming inside the moment that killed them (§19).
        /// </summary>
        public void CompleteInterruptedReturn(IServerPlayer plr, PlayerState ps)
        {
            var anchor = server.State.Anchor;
            if (anchor != null && anchor.Players.TryGetValue(plr.PlayerUID, out var snap))
            {
                try { PlayerStateIO.Restore(Api, plr, snap); }
                catch (Exception e) { server.Warn($"interrupted-return restore failed: {e.Message}"); }
            }
            plr.Entity.WatchedAttributes.SetBool("sm:returning", false);
            server.StateChannel.SendPacket(new PktReturnComplete
            {
                ArrivalSeconds = Cfg.Core.ArrivalSeconds,
                InputUnlockSeconds = Cfg.Core.ArrivalInputUnlockSeconds,
                DeathCause = 0,
                SafeMode = true,
            }, plr);
            server.SyncState(plr);
        }

        /// <summary>
        /// Drops a run whose player has gone. The debt is recorded separately and is
        /// settled on rejoin; what must not happen is this machine ticking forever
        /// against a player who is not there.
        /// </summary>
        public void AbandonRun(string playerUid)
        {
            if (!runs.Remove(playerUid)) return;
            if (!AnyReturning && server.Freezer.IsFrozen) server.Freezer.Thaw();
            server.Recorder.Suspended = false;
        }

        public void Dispose()
        {
            if (tickListener != -1) Api.Event.UnregisterGameTickListener(tickListener);
            tickListener = -1;
            runs.Clear();
        }
    }
}
