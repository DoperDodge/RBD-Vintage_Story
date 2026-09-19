using System;
using System.Collections.Generic;
using System.IO;
using Shinimodori.Core.Deltas;
using Vintagestory.API.MathTools;

namespace Shinimodori.Core
{
    /// <summary>Allocation-free dictionary key for a world position (§4.5 hot path).</summary>
    public readonly struct PosKey : IEquatable<PosKey>
    {
        public readonly int X, Y, Z, Dim;

        public PosKey(int x, int y, int z, int dim) { X = x; Y = y; Z = z; Dim = dim; }
        public PosKey(BlockPos p) { X = p.X; Y = p.Y; Z = p.Z; Dim = p.dimension; }

        public bool Equals(PosKey o) => X == o.X && Y == o.Y && Z == o.Z && Dim == o.Dim;
        public override bool Equals(object o) => o is PosKey k && Equals(k);

        public override int GetHashCode()
        {
            // Cheap, well-spread mix. Positions cluster on all three axes, so xor-shift
            // each component rather than relying on multiplication alone.
            unchecked
            {
                int h = X * 73856093;
                h ^= Y * 19349663;
                h ^= Z * 83492791;
                h ^= Dim * 51438721;
                return h;
            }
        }

        public BlockPos ToBlockPos() => new BlockPos(X, Y, Z, Dim);
        public override string ToString() => $"{X},{Y},{Z}" + (Dim != 0 ? $"@{Dim}" : "");
    }

    /// <summary>
    /// The append-only log of everything reversible that happened since the anchor.
    ///
    /// Copy-on-first-touch (§4.5): for each position or entity only the *first*
    /// observed pre-state is kept, so mining and replacing one block forty times
    /// costs one delta. Memory is bounded by distinct touched objects, not by actions.
    /// </summary>
    public class WorldJournal
    {
        private readonly Dictionary<PosKey, BlockDelta> touchedBlocks = new Dictionary<PosKey, BlockDelta>();
        /// <summary>Insertion order, replayed in reverse so support/fluid chains unwind correctly.</summary>
        private readonly List<PosKey> touchOrder = new List<PosKey>();
        private readonly Dictionary<long, EntityDelta> touchedEntities = new Dictionary<long, EntityDelta>();

        /// <summary>Cleared only by a new anchor. False forces SafeMode on the next return (§4.8).</summary>
        public bool IntegrityOk = true;
        /// <summary>Why integrity was lost, for the log and for /rbd journal stats.</summary>
        public string IntegrityFailReason = "";
        public bool Overflowed { get; private set; }

        public Guid AnchorId;

        public int BlockDeltaCount => touchedBlocks.Count;
        public int EntityDeltaCount => touchedEntities.Count;
        public int TotalDeltas => touchedBlocks.Count + touchedEntities.Count;

        /// <summary>Rough live byte cost, used against maxJournalMB without serializing.</summary>
        public long ApproximateBytes { get; private set; }

        public IReadOnlyList<PosKey> TouchOrder => touchOrder;
        public IReadOnlyDictionary<PosKey, BlockDelta> Blocks => touchedBlocks;
        public IReadOnlyDictionary<long, EntityDelta> EntityDeltas => touchedEntities;

        public WorldJournal(Guid anchorId) { AnchorId = anchorId; }

        public void MarkIntegrityLost(string reason)
        {
            if (IntegrityOk)
            {
                IntegrityOk = false;
                IntegrityFailReason = reason ?? "unknown";
            }
        }

        /// <summary>
        /// Records a block's pre-state the first time it is touched. Later touches of
        /// the same position are no-ops — that is the whole point.
        /// Returns false when the journal is full.
        /// </summary>
        public bool RecordBlock(int maxDeltas, long maxBytes, PosKey key, Func<BlockDelta> capture)
        {
            if (touchedBlocks.ContainsKey(key)) return true;
            if (TotalDeltas >= maxDeltas || ApproximateBytes >= maxBytes) { Overflowed = true; return false; }

            BlockDelta d;
            try { d = capture(); }
            catch (Exception) { MarkIntegrityLost("block capture threw"); return true; }
            if (d == null) return true;

            touchedBlocks[key] = d;
            touchOrder.Add(key);
            ApproximateBytes += 40 + (d.OldBeTree?.Length ?? 0) + (d.OldBeClass?.Length ?? 0) * 2;
            return true;
        }

        /// <summary>Records an entity's pre-state on first touch. Same contract as RecordBlock.</summary>
        public bool RecordEntity(int maxDeltas, long maxBytes, long entityId, Func<EntityDelta> capture)
        {
            if (touchedEntities.ContainsKey(entityId)) return true;
            if (TotalDeltas >= maxDeltas || ApproximateBytes >= maxBytes) { Overflowed = true; return false; }

            EntityDelta d;
            try { d = capture(); }
            catch (Exception) { MarkIntegrityLost("entity capture threw"); return true; }
            if (d == null) return true;

            touchedEntities[entityId] = d;
            ApproximateBytes += 48 + (d.Data?.Length ?? 0) + (d.EntityCode?.Length ?? 0) * 2;
            return true;
        }

        /// <summary>
        /// Upgrades a record when an entity we already knew about dies. A Drifted record
        /// holds a live snapshot, which is exactly what respawning it needs — but the
        /// Died kind tells the rewind to recreate rather than merely reposition.
        /// </summary>
        public void PromoteToDied(long entityId)
        {
            if (touchedEntities.TryGetValue(entityId, out var d) && d.Kind == EntityDeltaKind.Drifted)
                d.Kind = EntityDeltaKind.Died;
        }

        /// <summary>True if this entity was spawned after the anchor, so undo means "remove".</summary>
        public bool WasSpawnedSinceAnchor(long entityId) =>
            touchedEntities.TryGetValue(entityId, out var d) &&
            (d.Kind == EntityDeltaKind.Spawned || d.Kind == EntityDeltaKind.ItemSpawned);

        public bool Knows(long entityId) => touchedEntities.ContainsKey(entityId);

        public void Clear(Guid newAnchorId)
        {
            touchedBlocks.Clear();
            touchOrder.Clear();
            touchedEntities.Clear();
            IntegrityOk = true;
            IntegrityFailReason = "";
            Overflowed = false;
            ApproximateBytes = 0;
            AnchorId = newAnchorId;
        }

        // ---------------------------------------------------------------- persistence

        public const int Schema = 1;

        public void Write(BinaryWriter w)
        {
            w.Write(Schema);
            w.Write(AnchorId.ToByteArray());
            w.Write(IntegrityOk);
            w.Write(IntegrityFailReason ?? "");
            w.Write(Overflowed);

            w.Write(touchOrder.Count);
            foreach (var key in touchOrder)
            {
                // touchOrder and touchedBlocks are written together so order survives.
                touchedBlocks[key].Write(w);
            }

            w.Write(touchedEntities.Count);
            foreach (var kv in touchedEntities) kv.Value.Write(w);
        }

        public static WorldJournal Read(BinaryReader r)
        {
            int schema = r.ReadInt32();
            if (schema != Schema) throw new InvalidDataException($"WorldJournal schema {schema} != {Schema}");

            var j = new WorldJournal(new Guid(r.ReadBytes(16)))
            {
                IntegrityOk = r.ReadBoolean(),
                IntegrityFailReason = r.ReadString(),
            };
            j.Overflowed = r.ReadBoolean();

            int bc = r.ReadInt32();
            for (int i = 0; i < bc; i++)
            {
                var d = BlockDelta.Read(r);
                var key = new PosKey(d.X, d.Y, d.Z, d.Dim);
                if (!j.touchedBlocks.ContainsKey(key))
                {
                    j.touchedBlocks[key] = d;
                    j.touchOrder.Add(key);
                    j.ApproximateBytes += 40 + (d.OldBeTree?.Length ?? 0);
                }
            }

            int ec = r.ReadInt32();
            for (int i = 0; i < ec; i++)
            {
                var d = EntityDelta.Read(r);
                j.touchedEntities[d.EntityId] = d;
                j.ApproximateBytes += 48 + (d.Data?.Length ?? 0);
            }

            return j;
        }
    }
}
