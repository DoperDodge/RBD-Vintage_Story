using System;
using System.Collections.Generic;
using Shinimodori.Config;
using Shinimodori.Core.Deltas;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori.Core
{
    /// <summary>
    /// Feeds the <see cref="WorldJournal"/> from the live world.
    ///
    /// Player-driven changes come from server events; everything else — fluid flow,
    /// crop growth, fire spread, another mod's bulk edit — arrives through the Harmony
    /// bridge in <see cref="Compat.HarmonyPatches"/>, because those paths never raise
    /// a player event. Block writes can come off the main thread, so every mutation of
    /// the journal is taken under one lock (§4.4).
    /// </summary>
    public class JournalRecorder
    {
        private readonly ICoreServerAPI sapi;
        private readonly ShinimodoriConfig cfg;
        private readonly object gate = new object();
        private readonly Random fuzzRand = new Random();

        public WorldJournal Journal { get; private set; }

        /// <summary>No anchor yet, or the mod is off: record nothing.</summary>
        public bool Active { get; set; }

        /// <summary>True while a rewind is executing — its own writes must not be journaled.</summary>
        public bool Suspended { get; set; }

        private long driftListener = -1;

        public JournalRecorder(ICoreServerAPI sapi, ShinimodoriConfig cfg)
        {
            this.sapi = sapi;
            this.cfg = cfg;
            Journal = new WorldJournal(Guid.Empty);
        }

        private bool Recording => Active && !Suspended && cfg.Core.Enabled;
        private long MaxBytes => (long)cfg.Anchors.MaxJournalMB * 1024 * 1024;

        // ------------------------------------------------------------------- wiring

        public void Register()
        {
            sapi.Event.BreakBlock += OnBreakBlock;
            sapi.Event.DidPlaceBlock += OnDidPlaceBlock;
            sapi.Event.CanUseBlock += OnCanUseBlock;
            sapi.Event.OnEntitySpawn += OnEntitySpawn;
            sapi.Event.OnEntityDeath += OnEntityDeath;

            driftListener = sapi.Event.RegisterGameTickListener(
                OnDriftSample, cfg.Anchors.EntityDriftSampleSeconds * 1000);
        }

        public void Unregister()
        {
            sapi.Event.BreakBlock -= OnBreakBlock;
            sapi.Event.DidPlaceBlock -= OnDidPlaceBlock;
            sapi.Event.CanUseBlock -= OnCanUseBlock;
            sapi.Event.OnEntitySpawn -= OnEntitySpawn;
            sapi.Event.OnEntityDeath -= OnEntityDeath;
            if (driftListener != -1) { sapi.Event.UnregisterGameTickListener(driftListener); driftListener = -1; }
        }

        /// <summary>
        /// Every journal handler runs inside this. A throwing handler must never break
        /// the game loop, but it must cost the journal its integrity (§4.4).
        /// </summary>
        private void Guarded(string what, Action body)
        {
            try
            {
                if (cfg.Debug.FuzzJournalFailures && fuzzRand.NextDouble() < cfg.Debug.FuzzFailureChance)
                    throw new Exception("injected journal fuzz failure");
                body();
            }
            catch (Exception e)
            {
                lock (gate) Journal.MarkIntegrityLost($"{what}: {e.Message}");
                sapi.Logger.Warning("[shinimodori] journal handler '{0}' threw, SafeMode armed: {1}", what, e.Message);
            }
        }

        // -------------------------------------------------------------- block events

        private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel,
                                  ref float dropQuantityMultiplier, ref EnumHandling handling)
        {
            if (!Recording || blockSel?.Position == null) return;
            var pos = blockSel.Position.Copy();
            Guarded("BreakBlock", () => CaptureBlock(pos));
        }

        private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
        {
            // The Harmony bridge already captured the pre-state before the write landed.
            // This handler exists only so a placement into an unloaded-then-loaded chunk
            // still registers if the bridge was unavailable.
            if (!Recording || blockSel?.Position == null) return;
            var pos = blockSel.Position.Copy();
            Guarded("DidPlaceBlock", () => CaptureBlockIfUnknown(pos, oldblockId));
        }

        private bool OnCanUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
        {
            // Fires *before* the interaction, which is the only moment a chest's
            // pre-state is still observable. Always returns true: this is a spy, not a veto.
            if (Recording && blockSel?.Position != null)
            {
                var pos = blockSel.Position.Copy();
                Guarded("CanUseBlock", () => CaptureBlock(pos));
            }
            return true;
        }

        // ------------------------------------------------------- Harmony bridge entry

        /// <summary>A single block write is about to happen at <paramref name="pos"/>.</summary>
        public void OnBlockWillChange(BlockPos pos)
        {
            if (!Recording || pos == null) return;
            Guarded("SetBlock", () => CaptureBlock(pos));
        }

        /// <summary>A bulk accessor is about to commit; capture every staged position.</summary>
        public void OnBulkWillCommit(IBulkBlockAccessor acc)
        {
            if (!Recording || acc == null) return;
            Guarded("BulkCommit", () =>
            {
                var staged = acc.StagedBlocks;
                if (staged == null || staged.Count == 0) return;
                foreach (var kv in staged) CaptureBlock(kv.Key);
            });
        }

        // ------------------------------------------------------------- block capture

        private void CaptureBlock(BlockPos pos)
        {
            var key = new PosKey(pos);
            lock (gate)
            {
                if (Journal.Blocks.ContainsKey(key)) return;   // copy-on-FIRST-touch
                var snapshot = pos.Copy();
                Journal.RecordBlock(cfg.Anchors.MaxDeltas, MaxBytes, key, () => BuildBlockDelta(snapshot));
            }
        }

        private void CaptureBlockIfUnknown(BlockPos pos, int knownOldBlockId)
        {
            var key = new PosKey(pos);
            lock (gate)
            {
                if (Journal.Blocks.ContainsKey(key)) return;
                var snapshot = pos.Copy();
                Journal.RecordBlock(cfg.Anchors.MaxDeltas, MaxBytes, key, () =>
                {
                    var d = BuildBlockDelta(snapshot);
                    // The event knows the truth about the solid layer; trust it over a
                    // read that now returns the *new* block.
                    if (d != null) d.OldSolidId = knownOldBlockId;
                    return d;
                });
            }
        }

        /// <summary>Reads the current state of a position — both layers plus any block entity.</summary>
        private BlockDelta BuildBlockDelta(BlockPos pos)
        {
            var acc = sapi.World.BlockAccessor;

            var solid = acc.GetBlock(pos, BlockLayersAccess.Solid);
            var fluid = acc.GetBlock(pos, BlockLayersAccess.Fluid);

            var d = new BlockDelta
            {
                X = pos.X, Y = pos.Y, Z = pos.Z, Dim = pos.dimension,
                OldSolidId = solid?.BlockId ?? 0,
                OldFluidId = fluid?.BlockId ?? 0,
            };

            var be = acc.GetBlockEntity(pos);
            if (be != null)
            {
                var tree = new TreeAttribute();
                be.ToTreeAttributes(tree);
                d.OldBeTree = tree.ToBytes();
                d.OldBeClass = sapi.ClassRegistry.GetBlockEntityClass(be.GetType());
            }

            return d;
        }

        /// <summary>
        /// Pre-seeds the journal with the anchor-time state of every block entity near
        /// the anchor. Ticking block entities — farmland, firepits, barrels — change
        /// with no event and no SetBlock, so the only honest way to undo them is to
        /// have written down what they looked like when the anchor was set.
        /// </summary>
        public int SeedNearbyBlockEntities(BlockPos origin, int radius, int maxCount)
        {
            int seeded = 0;
            try
            {
                int r = radius;
                var min = new BlockPos(origin.X - r, Math.Max(0, origin.Y - r), origin.Z - r, origin.dimension);
                var max = new BlockPos(origin.X + r, Math.Min(sapi.WorldManager.MapSizeY - 1, origin.Y + r), origin.Z + r, origin.dimension);

                sapi.World.BlockAccessor.WalkBlocks(min, max, (block, x, y, z) =>
                {
                    if (seeded >= maxCount) return;
                    var pos = new BlockPos(x, y, z, origin.dimension);
                    var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
                    if (be == null) return;
                    var key = new PosKey(pos);
                    lock (gate)
                    {
                        if (Journal.Blocks.ContainsKey(key)) return;
                        if (Journal.RecordBlock(cfg.Anchors.MaxDeltas, MaxBytes, key, () => BuildBlockDelta(pos)))
                            seeded++;
                    }
                }, true);
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[shinimodori] block-entity seeding failed: {0}", e.Message);
            }
            return seeded;
        }

        // ------------------------------------------------------------ entity events

        private void OnEntitySpawn(Entity entity)
        {
            if (!Recording || entity == null || entity is EntityPlayer) return;
            Guarded("OnEntitySpawn", () =>
            {
                long id = entity.EntityId;
                bool isItem = entity is EntityItem;
                lock (gate)
                {
                    if (Journal.Knows(id)) return;
                    Journal.RecordEntity(cfg.Anchors.MaxDeltas, MaxBytes, id, () => new EntityDelta
                    {
                        Kind = isItem ? EntityDeltaKind.ItemSpawned : EntityDeltaKind.Spawned,
                        EntityId = id,
                        EntityCode = entity.Code?.ToString() ?? "",
                        X = entity.ServerPos.X, Y = entity.ServerPos.Y, Z = entity.ServerPos.Z,
                        Data = null,   // undo is "remove"; no state needed
                    });
                }
            });
        }

        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            if (!Recording || entity == null || entity is EntityPlayer) return;
            Guarded("OnEntityDeath", () =>
            {
                long id = entity.EntityId;
                lock (gate)
                {
                    if (Journal.WasSpawnedSinceAnchor(id)) return;  // born and died inside the loop: undo is still "remove"
                    if (Journal.Knows(id)) { Journal.PromoteToDied(id); return; }

                    Journal.RecordEntity(cfg.Anchors.MaxDeltas, MaxBytes, id, () => new EntityDelta
                    {
                        Kind = EntityDeltaKind.Died,
                        EntityId = id,
                        EntityCode = entity.Code?.ToString() ?? "",
                        X = entity.ServerPos.X, Y = entity.ServerPos.Y, Z = entity.ServerPos.Z,
                        Data = SerializeEntity(entity),
                    });
                }
            });
        }

        /// <summary>
        /// Periodic drift sampling (§4.5). Cheap, and it covers "a wolf wandered 200
        /// blocks and ate my chicken" without journaling every position update.
        /// </summary>
        private void OnDriftSample(float dt)
        {
            if (!Recording) return;
            Guarded("driftSample", () =>
            {
                var origin = CurrentOrigin;
                if (origin == null) return;
                float r = cfg.Anchors.CaptureRadius;
                var centre = new Vec3d(origin.X, origin.Y, origin.Z);

                var around = sapi.World.GetEntitiesAround(centre, r, r,
                    e => e != null && e.Alive && !(e is EntityPlayer) && !(e is EntityItem));

                foreach (var e in around)
                {
                    long id = e.EntityId;
                    lock (gate)
                    {
                        if (Journal.Knows(id)) continue;
                        var captured = e;
                        Journal.RecordEntity(cfg.Anchors.MaxDeltas, MaxBytes, id, () => new EntityDelta
                        {
                            Kind = EntityDeltaKind.Drifted,
                            EntityId = id,
                            EntityCode = captured.Code?.ToString() ?? "",
                            X = captured.ServerPos.X, Y = captured.ServerPos.Y, Z = captured.ServerPos.Z,
                            Data = SerializeEntity(captured),
                        });
                    }
                }
            });
        }

        /// <summary>Where the current anchor is, so drift sampling knows what "nearby" means.</summary>
        public BlockPos CurrentOrigin { get; set; }

        internal static byte[] SerializeEntity(Entity e)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                e.ToBytes(w, false);
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------ lifecycle

        public void ResetTo(Guid anchorId)
        {
            lock (gate) Journal.Clear(anchorId);
        }

        public void ReplaceJournal(WorldJournal j)
        {
            lock (gate) Journal = j ?? new WorldJournal(Guid.Empty);
        }

        public JournalStats Stats()
        {
            lock (gate)
            {
                return new JournalStats
                {
                    Blocks = Journal.BlockDeltaCount,
                    Entities = Journal.EntityDeltaCount,
                    ApproximateBytes = Journal.ApproximateBytes,
                    IntegrityOk = Journal.IntegrityOk,
                    IntegrityFailReason = Journal.IntegrityFailReason,
                    Overflowed = Journal.Overflowed,
                };
            }
        }
    }

    public class JournalStats
    {
        public int Blocks;
        public int Entities;
        public long ApproximateBytes;
        public bool IntegrityOk;
        public string IntegrityFailReason;
        public bool Overflowed;
    }
}
