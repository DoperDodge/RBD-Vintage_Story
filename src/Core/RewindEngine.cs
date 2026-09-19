using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Shinimodori.Config;
using Shinimodori.Core.Deltas;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Core
{
    public enum RewindOutcome { Success, SafeMode, Failed }

    public class RewindResult
    {
        public RewindOutcome Outcome;
        public int BlocksRestored;
        /// <summary>Positions that needed a second write because their support moved under them.</summary>
        public int BlocksReconciled;
        public int BlockEntitiesRestored;
        public int EntitiesRemoved;
        public int EntitiesRespawned;
        public int EntitiesRestored;
        public int ItemEntitiesRemoved;
        public int PlayersRestored;
        public long ElapsedMs;
        public float MismatchFraction;
        public string Note = "";
    }

    /// <summary>
    /// The undo executor (§4.6). Runs on the server main thread inside a frozen world,
    /// chunking the block pass across ticks when a journal is large — the client is in
    /// the Void the whole time, which is why the Void exists.
    ///
    /// Nothing in here may throw out: every failure path lands in SafeMode (§4.8).
    /// </summary>
    public class RewindEngine
    {
        private readonly ICoreServerAPI sapi;
        private readonly ShinimodoriConfig cfg;
        private readonly Action<string> onWarn;

        public bool IsBusy { get; private set; }

        // Active run state
        private ReturnPoint rp;
        private WorldJournal journal;
        private Action<RewindResult> onComplete;
        private RewindResult result;
        private Stopwatch watch;
        private List<PosKey> reverseOrder;
        private int blockCursor;
        private long tickListener = -1;
        private int pendingChunkLoads;
        private bool preloadDone;
        private long preloadStartMs;

        public RewindEngine(ICoreServerAPI sapi, ShinimodoriConfig cfg, Action<string> onWarn)
        {
            this.sapi = sapi;
            this.cfg = cfg;
            this.onWarn = onWarn ?? (_ => { });
        }

        /// <summary>
        /// Starts a rewind. <paramref name="onComplete"/> is always invoked exactly once,
        /// on the main thread, whatever happens.
        /// </summary>
        public void Execute(ReturnPoint returnPoint, WorldJournal worldJournal, Action<RewindResult> completion)
        {
            if (IsBusy)
            {
                completion?.Invoke(new RewindResult { Outcome = RewindOutcome.Failed, Note = "a rewind was already running" });
                return;
            }

            IsBusy = true;
            rp = returnPoint;
            journal = worldJournal;
            onComplete = completion;
            result = new RewindResult();
            watch = Stopwatch.StartNew();
            blockCursor = 0;
            preloadDone = false;
            pendingChunkLoads = 0;

            try
            {
                // A journal that lost integrity, overflowed, or belongs to a different
                // anchor cannot be trusted to undo the world. Restore the player only.
                if (!journal.IntegrityOk || journal.Overflowed || journal.AnchorId != rp.Id)
                {
                    string why = !journal.IntegrityOk ? ("journal integrity lost: " + journal.IntegrityFailReason)
                               : journal.Overflowed ? "journal overflowed"
                               : "journal belongs to a different anchor";
                    RunSafeMode(why);
                    return;
                }

                reverseOrder = new List<PosKey>(journal.TouchOrder);
                reverseOrder.Reverse();

                BeginPreload();
            }
            catch (Exception e)
            {
                onWarn($"rewind failed before it started: {e}");
                RunSafeMode("exception during setup: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ 2. PRELOAD

        private void BeginPreload()
        {
            var columns = new HashSet<long>();
            var toLoad = new List<Vec2i>();
            int chunkSize = sapi.WorldManager.ChunkSize;

            void Want(int bx, int bz)
            {
                int cx = bx / chunkSize, cz = bz / chunkSize;
                if (bx < 0 && bx % chunkSize != 0) cx--;
                if (bz < 0 && bz % chunkSize != 0) cz--;
                long idx = sapi.WorldManager.MapChunkIndex2D(cx, cz);
                if (columns.Add(idx)) toLoad.Add(new Vec2i(cx, cz));
            }

            foreach (var key in journal.TouchOrder)
            {
                // Mini-dimension columns are not addressable through LoadChunkColumnPriority;
                // they are loaded with their parent and need no preload.
                if (key.Dim == 0) Want(key.X, key.Z);
            }
            foreach (var e in journal.EntityDeltas.Values) Want((int)e.X, (int)e.Z);
            foreach (var es in rp.Entities) Want((int)es.X, (int)es.Z);
            foreach (var ps in rp.Players.Values) if (ps.Dimension == 0) Want((int)ps.X, (int)ps.Z);

            preloadStartMs = watch.ElapsedMilliseconds;

            if (toLoad.Count == 0) { preloadDone = true; StartRestoreTicks(); return; }

            pendingChunkLoads = toLoad.Count;
            foreach (var c in toLoad)
            {
                try
                {
                    sapi.WorldManager.LoadChunkColumnPriority(c.X, c.Y, new ChunkLoadOptions
                    {
                        KeepLoaded = false,
                        OnLoaded = () => sapi.Event.EnqueueMainThreadTask(OneChunkLoaded, "sm-chunkloaded")
                    });
                }
                catch (Exception e)
                {
                    onWarn($"chunk preload failed for {c.X},{c.Y}: {e.Message}");
                    OneChunkLoaded();
                }
            }

            // The restore ticker also supervises the preload, so a chunk callback that
            // never arrives cannot hang the return forever.
            StartRestoreTicks();
        }

        private void OneChunkLoaded()
        {
            if (--pendingChunkLoads <= 0) preloadDone = true;
        }

        private void StartRestoreTicks()
        {
            if (tickListener != -1) return;
            tickListener = sapi.Event.RegisterGameTickListener(OnRestoreTick, 1);
        }

        private void StopRestoreTicks()
        {
            if (tickListener == -1) return;
            sapi.Event.UnregisterGameTickListener(tickListener);
            tickListener = -1;
        }

        private void OnRestoreTick(float dt)
        {
            try
            {
                if (!preloadDone)
                {
                    // Half the budget is the most the preload may consume before we
                    // press on with whatever is loaded.
                    if (watch.ElapsedMilliseconds - preloadStartMs > cfg.Anchors.RewindBudgetMs / 2)
                    {
                        onWarn($"chunk preload timed out with {pendingChunkLoads} column(s) outstanding; continuing");
                        preloadDone = true;
                    }
                    else return;
                }

                if (blockCursor == 0)
                {
                    RestoreEntities();
                }

                bool blocksDone = RestoreBlockBatch();
                if (!blocksDone) return;

                ReconcileBlocks();
                RestoreCalendar();
                RestorePlayers();
                Verify();

                Finish(RewindOutcome.Success);
            }
            catch (Exception e)
            {
                onWarn($"rewind threw mid-flight: {e}");
                StopRestoreTicks();
                RunSafeMode("exception during restore: " + e.Message);
            }
        }

        // ----------------------------------------------------------------- 3. ENTITIES

        private void RestoreEntities()
        {
            var world = sapi.World;

            // (a)+(d) Remove everything that came into existence since the anchor.
            // EnumDespawnReason.Removed drops nothing and fires no death event.
            foreach (var d in journal.EntityDeltas.Values)
            {
                if (d.Kind != EntityDeltaKind.Spawned && d.Kind != EntityDeltaKind.ItemSpawned) continue;
                var e = world.GetEntityById(d.EntityId);
                if (e == null || !e.Alive) continue;
                if (e is EntityPlayer) continue;                  // never remove a player
                try
                {
                    e.Die(EnumDespawnReason.Removed, null);
                    if (d.Kind == EntityDeltaKind.ItemSpawned) result.ItemEntitiesRemoved++;
                    else result.EntitiesRemoved++;
                }
                catch (Exception ex) { onWarn($"could not remove entity {d.EntityId}: {ex.Message}"); }
            }

            // Any item entity the journal never saw but that post-dates the anchor is
            // still a duplication risk (§19), so sweep by creation time as a backstop.
            SweepStrayItemEntities();

            // (b) Bring back everything that died.
            foreach (var d in journal.EntityDeltas.Values)
            {
                if (d.Kind != EntityDeltaKind.Died || d.Data == null) continue;
                try
                {
                    if (RespawnFromBlob(d.EntityCode, d.Data)) result.EntitiesRespawned++;
                }
                catch (Exception ex) { onWarn($"could not respawn {d.EntityCode}: {ex.Message}"); }
            }

            // (c) Put drifted entities back where, and how, they were.
            foreach (var d in journal.EntityDeltas.Values)
            {
                if (d.Kind != EntityDeltaKind.Drifted || d.Data == null) continue;
                var e = world.GetEntityById(d.EntityId);
                if (e == null)
                {
                    // It drifted out of existence between sampling and now; recreate it.
                    try { if (RespawnFromBlob(d.EntityCode, d.Data)) result.EntitiesRespawned++; }
                    catch (Exception ex) { onWarn($"could not recreate drifted {d.EntityCode}: {ex.Message}"); }
                    continue;
                }
                if (e is EntityPlayer) continue;
                try
                {
                    RestoreEntityFromBlob(e, d.Data);
                    result.EntitiesRestored++;
                }
                catch (Exception ex) { onWarn($"could not restore entity {d.EntityId}: {ex.Message}"); }
            }
        }

        private void SweepStrayItemEntities()
        {
            double anchorHours = rp.TotalHours;
            var doomed = new List<Entity>();
            foreach (var kv in sapi.World.LoadedEntities)
            {
                var e = kv.Value;
                if (!(e is EntityItem) || !e.Alive) continue;
                if (journal.Knows(e.EntityId)) continue;      // already handled above
                // EntityItem stamps its creation time; anything younger than the anchor
                // did not exist then and must not survive the rewind.
                double born = e.WatchedAttributes.GetDouble("createdTotalHours", double.MinValue);
                if (born > anchorHours) doomed.Add(e);
            }
            foreach (var e in doomed)
            {
                try { e.Die(EnumDespawnReason.Removed, null); result.ItemEntitiesRemoved++; }
                catch (Exception ex) { onWarn($"stray item sweep failed: {ex.Message}"); }
            }
        }

        private bool RespawnFromBlob(string entityCode, byte[] blob)
        {
            if (string.IsNullOrEmpty(entityCode)) return false;
            var type = sapi.World.GetEntityType(new AssetLocation(entityCode));
            if (type == null) { onWarn($"no entity type for '{entityCode}'; not respawning"); return false; }

            var entity = sapi.ClassRegistry.CreateEntity(type);
            if (entity == null) return false;

            using (var ms = new MemoryStream(blob))
            using (var r = new BinaryReader(ms))
            {
                entity.FromBytes(r, false);
            }
            // SpawnEntity assigns a fresh EntityId and runs Initialize/AfterInitialized.
            entity.Pos.SetFrom(entity.ServerPos);
            sapi.World.SpawnEntity(entity);
            return true;
        }

        private void RestoreEntityFromBlob(Entity e, byte[] blob)
        {
            long keepId = e.EntityId;
            using (var ms = new MemoryStream(blob))
            using (var r = new BinaryReader(ms))
            {
                e.FromBytes(r, false);
            }
            e.EntityId = keepId;
            e.Pos.SetFrom(e.ServerPos);
            e.ServerPos.Motion.Set(0, 0, 0);
            e.Pos.Motion.Set(0, 0, 0);
        }

        // ------------------------------------------------------------------- 4. BLOCKS

        /// <summary>Restores one batch of blocks. Returns true when every block is done.</summary>
        private bool RestoreBlockBatch()
        {
            if (reverseOrder == null || reverseOrder.Count == 0) return true;

            int budget = cfg.Anchors.RewindBlocksPerTick;
            int end = Math.Min(blockCursor + budget, reverseOrder.Count);
            if (blockCursor >= reverseOrder.Count) return true;

            var bulk = sapi.World.GetBlockAccessorBulkUpdate(true, true);
            var withBlockEntities = new List<BlockDelta>();
            var pos = new BlockPos();

            for (int i = blockCursor; i < end; i++)
            {
                var d = journal.Blocks[reverseOrder[i]];
                pos.Set(d.X, d.Y, d.Z);
                pos.dimension = d.Dim;
                try
                {
                    bulk.SetBlock(d.OldSolidId, pos, BlockLayersAccess.Solid);
                    bulk.SetBlock(d.OldFluidId, pos, BlockLayersAccess.Fluid);
                    result.BlocksRestored++;
                    if (d.OldBeClass != null) withBlockEntities.Add(d);
                }
                catch (Exception e) { onWarn($"SetBlock failed at {d.X},{d.Y},{d.Z}: {e.Message}"); }
            }

            try { bulk.Commit(); }
            catch (Exception e) { onWarn($"bulk commit failed: {e.Message}"); }

            // Block entities only exist after the commit that created their block.
            foreach (var d in withBlockEntities)
            {
                try { RestoreBlockEntity(d); }
                catch (Exception e) { onWarn($"BE restore failed at {d.X},{d.Y},{d.Z}: {e.Message}"); }
            }

            blockCursor = end;
            return blockCursor >= reverseOrder.Count;
        }

        /// <summary>
        /// Second pass over everything the block pass wrote, fixing whatever did not
        /// stick.
        ///
        /// A block that needs support — grass, a flower, a torch — is broken again by
        /// the neighbour update that follows its own commit, because the block holding
        /// it up is still queued in a later batch. Writing the survivors once more,
        /// lowest first, settles those chains. Two sweeps is enough for any real
        /// support chain; a third would mean something else is wrong, and silently
        /// looping on it would hide that.
        /// </summary>
        private void ReconcileBlocks()
        {
            if (reverseOrder == null || reverseOrder.Count == 0) return;

            var acc = sapi.World.BlockAccessor;
            var pos = new BlockPos();
            var pending = new List<PosKey>();

            for (int sweep = 0; sweep < 2; sweep++)
            {
                pending.Clear();

                foreach (var key in reverseOrder)
                {
                    var d = journal.Blocks[key];
                    pos.Set(d.X, d.Y, d.Z);
                    pos.dimension = d.Dim;
                    try
                    {
                        var solid = acc.GetBlock(pos, BlockLayersAccess.Solid);
                        var fluid = acc.GetBlock(pos, BlockLayersAccess.Fluid);
                        if ((solid?.BlockId ?? 0) != d.OldSolidId || (fluid?.BlockId ?? 0) != d.OldFluidId)
                            pending.Add(key);
                    }
                    catch { }
                }

                if (pending.Count == 0) return;

                // Lowest first, so whatever holds a block up is in place before it is.
                pending.Sort((a, b) => a.Y.CompareTo(b.Y));

                var bulk = sapi.World.GetBlockAccessorBulkUpdate(true, true);
                foreach (var key in pending)
                {
                    var d = journal.Blocks[key];
                    pos.Set(d.X, d.Y, d.Z);
                    pos.dimension = d.Dim;
                    try
                    {
                        bulk.SetBlock(d.OldSolidId, pos, BlockLayersAccess.Solid);
                        bulk.SetBlock(d.OldFluidId, pos, BlockLayersAccess.Fluid);
                        result.BlocksReconciled++;
                    }
                    catch (Exception e) { onWarn($"reconcile failed at {d.X},{d.Y},{d.Z}: {e.Message}"); }
                }
                try { bulk.Commit(); }
                catch (Exception e) { onWarn($"reconcile commit failed: {e.Message}"); }
            }

            if (pending.Count > 0)
                onWarn($"{pending.Count} position(s) would not settle after two reconcile sweeps");
        }

        private void RestoreBlockEntity(BlockDelta d)
        {
            var pos = new BlockPos(d.X, d.Y, d.Z, d.Dim);
            var acc = sapi.World.BlockAccessor;

            var be = acc.GetBlockEntity(pos);
            if (be == null)
            {
                acc.SpawnBlockEntity(d.OldBeClass, pos);
                be = acc.GetBlockEntity(pos);
                if (be == null) { onWarn($"could not recreate block entity '{d.OldBeClass}' at {pos}"); return; }
            }

            if (d.OldBeTree != null && d.OldBeTree.Length > 0)
            {
                var tree = new TreeAttribute();
                tree.FromBytes(d.OldBeTree);
                be.FromTreeAttributes(tree, sapi.World);
            }
            be.MarkDirty(true);
            result.BlockEntitiesRestored++;
        }

        // ----------------------------------------------------------------- 5. CALENDAR

        private void RestoreCalendar()
        {
            try
            {
                double now = sapi.World.Calendar.TotalHours;
                double delta = now - rp.TotalHours;
                // IGameCalendar.Add() is a plain TimeSpan add, so negatives rewind (verified,
                // see API_VERIFIED.md §0). Using the delta keeps float precision usable.
                if (Math.Abs(delta) > 0.0001) sapi.World.Calendar.Add((float)(-delta));

                // Keep the persisted clock in step so a save mid-loop reloads correctly.
                if (rp.TotalGameSeconds > 0) sapi.WorldManager.SaveGame.TotalGameSeconds = rp.TotalGameSeconds;
            }
            catch (Exception e)
            {
                onWarn($"calendar restore failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ 6. PLAYERS

        private void RestorePlayers()
        {
            foreach (var kv in rp.Players)
            {
                var plr = sapi.World.PlayerByUid(kv.Key) as IServerPlayer;
                if (plr?.Entity == null) continue;
                try
                {
                    PlayerStateIO.Restore(sapi, plr, kv.Value);
                    result.PlayersRestored++;
                }
                catch (Exception e)
                {
                    onWarn($"player restore failed for {plr.PlayerName}: {e.Message}");
                }
            }
        }

        // ------------------------------------------------------------------- 10. VERIFY

        private void Verify()
        {
            int n = cfg.Anchors.VerifySampleCount;
            if (n <= 0 || journal.TouchOrder.Count == 0) return;

            var rand = new Random(unchecked((int)rp.RealTimeCreatedMs));
            int checks = Math.Min(n, journal.TouchOrder.Count);
            int mismatches = 0;
            var acc = sapi.World.BlockAccessor;
            var pos = new BlockPos();

            for (int i = 0; i < checks; i++)
            {
                var key = journal.TouchOrder[rand.Next(journal.TouchOrder.Count)];
                var d = journal.Blocks[key];
                pos.Set(d.X, d.Y, d.Z);
                pos.dimension = d.Dim;
                try
                {
                    var b = acc.GetBlock(pos, BlockLayersAccess.Solid);
                    if (b == null || b.BlockId != d.OldSolidId) mismatches++;
                }
                catch { mismatches++; }
            }

            result.MismatchFraction = checks == 0 ? 0f : (float)mismatches / checks;
            if (result.MismatchFraction > cfg.Anchors.VerifyMismatchTolerance)
            {
                rp.Degraded = true;
                onWarn($"post-rewind verification: {mismatches}/{checks} sampled blocks mismatched " +
                       $"({result.MismatchFraction:P1}); anchor flagged degraded");
            }
        }

        // ----------------------------------------------------------------- SAFE MODE

        /// <summary>
        /// The seatbelt (§4.8): restore the players and the clock, touch nothing else,
        /// and never surface an error to the player. Must always be reachable.
        /// </summary>
        private void RunSafeMode(string why)
        {
            result ??= new RewindResult();
            result.Note = why;
            try
            {
                RestoreCalendar();
                RestorePlayers();
            }
            catch (Exception e)
            {
                onWarn($"SafeMode itself failed — this should be impossible: {e}");
            }
            Finish(RewindOutcome.SafeMode);
        }

        private void Finish(RewindOutcome outcome)
        {
            StopRestoreTicks();
            watch?.Stop();
            result.Outcome = outcome;
            result.ElapsedMs = watch?.ElapsedMilliseconds ?? 0;

            var cb = onComplete;
            // Clear run state before the callback: it may start another return.
            IsBusy = false;
            onComplete = null;
            rp = null;
            journal = null;
            reverseOrder = null;

            try { cb?.Invoke(result); }
            catch (Exception e) { onWarn($"rewind completion handler threw: {e}"); }
        }
    }
}
