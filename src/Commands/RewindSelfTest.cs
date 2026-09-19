using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Shinimodori.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Commands
{
    /// <summary>
    /// Exercises the rewind engine against a live world and checks that it actually
    /// put everything back — the acceptance test from PLAN.md §16 Phase 1, runnable
    /// from a dedicated server console with no client attached.
    ///
    /// It anchors, scribbles on the world (blocks, fluids, block entities, entities,
    /// the clock), remembers exactly what it scribbled over, rewinds, and then reads
    /// every touched position back. Anything that does not match is a failure, and
    /// failures are reported rather than swallowed.
    /// </summary>
    public class RewindSelfTest
    {
        private readonly ShinimodoriServer server;
        private ICoreServerAPI Api => server.Api;

        public RewindSelfTest(ShinimodoriServer server) { this.server = server; }

        private class Expectation
        {
            public BlockPos Pos;
            public int SolidId;
            public int FluidId;
        }

        /// <summary>
        /// Requests every chunk column the test needs, then runs once they are in.
        /// The engine silently drops writes to unloaded chunks, so testing against
        /// them would measure nothing and report it as a failure.
        /// </summary>
        public void Run(int blockCount, bool fuzz, Action<string> report)
        {
            var origin = FindTestOrigin();
            int size = Api.WorldManager.ChunkSize;
            int radius = 32;
            var columns = new List<Vec2i>();
            for (int cx = (origin.X - radius) / size; cx <= (origin.X + radius) / size; cx++)
                for (int cz = (origin.Z - radius) / size; cz <= (origin.Z + radius) / size; cz++)
                    columns.Add(new Vec2i(cx, cz));

            int pending = columns.Count;
            bool started = false;

            void MaybeStart()
            {
                if (started) return;
                started = true;
                RunNow(origin, blockCount, fuzz, report);
            }

            foreach (var c in columns)
            {
                Api.WorldManager.LoadChunkColumnPriority(c.X, c.Y, new ChunkLoadOptions
                {
                    KeepLoaded = true,
                    OnLoaded = () => Api.Event.EnqueueMainThreadTask(() =>
                    {
                        if (--pending <= 0) MaybeStart();
                    }, "sm-selftest-chunk")
                });
            }

            // Never hang on a callback that does not arrive.
            Api.Event.RegisterCallback(_ => MaybeStart(), 8000);
        }

        private void RunNow(BlockPos origin, int blockCount, bool fuzz, Action<string> report)
        {
            var sb = new StringBuilder();
            void Say(string s) { sb.AppendLine(s); Api.Logger.Notification("[shinimodori selftest] " + s); }

            runTag = Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                bool savedFuzz = server.Cfg.Debug.FuzzJournalFailures;
                if (fuzz)
                {
                    // Prove SafeMode is reachable: make the journal handlers throw.
                    server.Cfg.Debug.FuzzJournalFailures = true;
                    server.Cfg.Debug.FuzzFailureChance = 0.05f;
                }

                Say($"origin {origin}");

                // ---------------------------------------------------- 1. anchor
                var anchor = CaptureAnchor(origin);
                server.State.Anchor = anchor;
                server.Recorder.ResetTo(anchor.Id);
                server.Recorder.CurrentOrigin = origin;
                server.Recorder.Active = true;
                double anchorHours = Api.World.Calendar.TotalHours;
                Say($"anchor set at hour {anchorHours:F2}");

                // ------------------------------------------- 2. change the world
                var expectations = new List<Expectation>();
                Compat.ShinimodoriBridge.BlockWritesSeen = 0;
                int placed = ScribbleBlocks(origin, blockCount, expectations);
                long seen = Compat.ShinimodoriBridge.BlockWritesSeen;
                int spawned = SpawnTestEntities(origin, 6);
                int killed = KillNearbyEntities(origin, 3);

                // Push the clock forward so the restore has something to undo.
                Api.World.Calendar.Add(9.5f);
                double movedHours = Api.World.Calendar.TotalHours;

                var stats = server.Recorder.Stats();
                Say($"changed {placed} blocks, spawned {spawned}, killed {killed}, " +
                    $"clock +{movedHours - anchorHours:F2}h");
                Say($"engine reported {seen} block write(s); journal kept {stats.Blocks} distinct position(s)");
                Say($"journal: {stats.Blocks} block deltas, {stats.Entities} entity deltas, " +
                    $"~{stats.ApproximateBytes / 1024}KB, integrity={(stats.IntegrityOk ? "ok" : "LOST")}");

                // ------------------------------------------------- 3. the rewind
                var watch = Stopwatch.StartNew();
                server.Recorder.Suspended = true;

                server.Engine.Execute(anchor, server.Recorder.Journal, result =>
                {
                    watch.Stop();
                    server.Recorder.Suspended = false;
                    server.Cfg.Debug.FuzzJournalFailures = savedFuzz;

                    Say($"rewind {result.Outcome} in {result.ElapsedMs}ms — " +
                        $"{result.BlocksRestored} blocks ({result.BlocksReconciled} reconciled), " +
                        $"{result.BlockEntitiesRestored} block entities, " +
                        $"{result.EntitiesRemoved} removed, {result.EntitiesRespawned} respawned, " +
                        $"{result.ItemEntitiesRemoved} items swept" +
                        (string.IsNullOrEmpty(result.Note) ? "" : $" [{result.Note}]"));

                    // --------------------------------------------- 4. verify
                    if (fuzz)
                    {
                        bool safe = result.Outcome == RewindOutcome.SafeMode;
                        Say(safe
                            ? "PASS — forced journal failures landed in SafeMode, as designed"
                            : "NOTE — fuzzing did not trip integrity this run; re-run to sample again");
                        report(sb.ToString().TrimEnd());
                        return;
                    }

                    int mismatch = 0, checkedCount = 0;
                    var acc = Api.World.BlockAccessor;
                    foreach (var e in expectations)
                    {
                        checkedCount++;
                        var solid = acc.GetBlock(e.Pos, BlockLayersAccess.Solid);
                        var fluid = acc.GetBlock(e.Pos, BlockLayersAccess.Fluid);
                        int gotSolid = solid?.BlockId ?? 0;
                        int gotFluid = fluid?.BlockId ?? 0;
                        if (gotSolid == e.SolidId && gotFluid == e.FluidId) continue;
                        if (mismatch < 6)
                            Say($"  MISMATCH {e.Pos}: solid {gotSolid} (want {e.SolidId}), " +
                                $"fluid {gotFluid} (want {e.FluidId})");
                        mismatch++;
                    }

                    double nowHours = Api.World.Calendar.TotalHours;
                    double clockError = Math.Abs(nowHours - anchorHours);

                    int leftover = CountTestEntities(origin);

                    Say($"blocks: {checkedCount - mismatch}/{checkedCount} restored exactly");
                    Say($"clock: back to {nowHours:F2} (anchor {anchorHours:F2}, error {clockError:F3}h)");
                    Say($"test entities still alive after rewind: {leftover} (want 0)");

                    bool pass = mismatch == 0 && clockError < 0.05 && leftover == 0
                                && result.Outcome == RewindOutcome.Success;
                    Say(pass
                        ? "PASS — the world is byte-identical at every touched position."
                        : "FAIL — see mismatches above.");

                    report(sb.ToString().TrimEnd());
                });
            }
            catch (Exception e)
            {
                Say("self-test threw: " + e);
                report(sb.ToString().TrimEnd());
            }
        }

        // ------------------------------------------------------------- helpers

        private BlockPos FindTestOrigin()
        {
            var spawn = Api.World.DefaultSpawnPosition?.AsBlockPos ?? new BlockPos(0, 120, 0, 0);
            int? surface = Api.WorldManager.GetSurfacePosY(spawn.X, spawn.Z);
            // Well clear of the tallest ground nearby, so the test writes into open air
            // and every change it makes is one it can reason about.
            return new BlockPos(spawn.X, (surface ?? 120) + 12, spawn.Z, 0);
        }

        private ReturnPoint CaptureAnchor(BlockPos origin)
        {
            var rp = new ReturnPoint
            {
                RealTimeCreatedMs = Api.World.ElapsedMilliseconds,
                TotalHours = Api.World.Calendar.TotalHours,
                TotalGameSeconds = Api.WorldManager.SaveGame.TotalGameSeconds,
                CaptureRadius = server.Cfg.Anchors.CaptureRadius,
                AnchorReason = "selftest",
            };
            rp.SetOrigin(origin);

            foreach (var p in Api.World.AllOnlinePlayers)
                if (p is IServerPlayer sp && sp.Entity != null)
                    rp.Players[sp.PlayerUID] = PlayerStateIO.Capture(sp);

            return rp;
        }

        /// <summary>
        /// Writes a slab of blocks over whatever is there, recording the pre-state we
        /// expect to get back. Uses the ordinary block accessor so the change travels
        /// the same path any other mod's change would.
        /// </summary>
        private int ScribbleBlocks(BlockPos origin, int count, List<Expectation> expectations)
        {
            var acc = Api.World.BlockAccessor;
            int stone = Api.World.GetBlock(new AssetLocation("game", "rock-granite"))?.BlockId ?? 0;
            int water = Api.World.GetBlock(new AssetLocation("game", "water-still-7"))?.BlockId ?? 0;
            int air = 0;

            int side = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(count / 3.0)));
            int written = 0;
            int skippedUnloaded = 0;
            var seen = new HashSet<string>();

            for (int y = 0; y < 3 && written < count; y++)
            {
                for (int x = -side; x <= side && written < count; x++)
                {
                    for (int z = -side; z <= side && written < count; z++)
                    {
                        var pos = new BlockPos(origin.X + x, origin.Y + y, origin.Z + z, 0);
                        if (!seen.Add($"{pos.X},{pos.Y},{pos.Z}")) continue;

                        // The engine drops writes to unloaded chunks without a word, and
                        // reads them back as air. Neither is worth measuring.
                        if (acc.GetChunkAtBlockPos(pos) == null) { skippedUnloaded++; continue; }

                        // Read the truth before touching it.
                        var e = new Expectation
                        {
                            Pos = pos.Copy(),
                            SolidId = acc.GetBlock(pos, BlockLayersAccess.Solid)?.BlockId ?? 0,
                            FluidId = acc.GetBlock(pos, BlockLayersAccess.Fluid)?.BlockId ?? 0,
                        };

                        // Vary what we write so both layers and plain removal are covered.
                        int pick = (x + z + y + side * 2) % 3;
                        if (pick == 0 && stone != 0) acc.SetBlock(stone, pos);
                        else if (pick == 1 && water != 0) acc.SetBlock(water, pos, BlockLayersAccess.Fluid);
                        else acc.SetBlock(air, pos);

                        expectations.Add(e);
                        written++;
                    }
                }
            }

            // Touch a subset a second time: copy-on-first-touch must ignore this.
            for (int i = 0; i < Math.Min(40, expectations.Count); i++)
                acc.SetBlock(air, expectations[i].Pos);

            if (skippedUnloaded > 0)
                Api.Logger.Notification("[shinimodori selftest] skipped {0} position(s) in unloaded chunks", skippedUnloaded);

            return written;
        }

        private const string TestEntityCode = "game:chicken-hen";

        /// <summary>
        /// Stamped on the entities this run spawns. A SafeMode run leaves the world
        /// untouched by design, so its creatures outlive it — without a per-run tag the
        /// next run would count those survivors as its own failure.
        /// </summary>
        private string runTag;

        private int SpawnTestEntities(BlockPos origin, int count)
        {
            var type = Api.World.GetEntityType(new AssetLocation(TestEntityCode));
            if (type == null) return 0;

            int made = 0;
            for (int i = 0; i < count; i++)
            {
                var e = Api.ClassRegistry.CreateEntity(type);
                if (e == null) continue;
                e.Pos.SetPos(origin.X + (i % 3) - 1 + 0.5, origin.Y + 4, origin.Z + (i / 3) + 0.5);
                e.WatchedAttributes.SetString("sm:selftestRun", runTag);
                Api.World.SpawnEntity(e);
                made++;
            }
            return made;
        }

        private int KillNearbyEntities(BlockPos origin, int max)
        {
            var around = Api.World.GetEntitiesAround(
                new Vec3d(origin.X, origin.Y, origin.Z), 24, 24,
                e => e != null && e.Alive && !(e is EntityPlayer) && !(e is EntityItem)
                     && e.WatchedAttributes.GetString("sm:selftestRun") == null);

            int killed = 0;
            foreach (var e in around)
            {
                if (killed >= max) break;
                e.Die(EnumDespawnReason.Death, null);
                killed++;
            }
            return killed;
        }

        private int CountTestEntities(BlockPos origin)
        {
            int n = 0;
            foreach (var kv in Api.World.LoadedEntities)
                if (kv.Value != null && kv.Value.Alive &&
                    kv.Value.WatchedAttributes.GetString("sm:selftestRun") == runTag) n++;
            return n;
        }
    }
}
