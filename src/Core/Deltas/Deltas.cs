using System;
using System.IO;

namespace Shinimodori.Core.Deltas
{
    /// <summary>
    /// A change to the world that a return must undo. Deltas store the state
    /// *before* the first touch since the anchor — not every change (§4.5).
    /// </summary>
    public interface IWorldDelta
    {
        void Write(BinaryWriter w);
    }

    /// <summary>
    /// The pre-state of one block position: both layers, plus the block entity's
    /// serialized tree if it had one. Undo writes exactly this back.
    /// </summary>
    public class BlockDelta : IWorldDelta
    {
        public int X, Y, Z, Dim;
        public int OldSolidId;
        public int OldFluidId;
        /// <summary>Block entity class name, or null/empty if the position had no BE.</summary>
        public string OldBeClass;
        public byte[] OldBeTree;

        public void Write(BinaryWriter w)
        {
            w.Write(X); w.Write(Y); w.Write(Z); w.Write(Dim);
            w.Write(OldSolidId); w.Write(OldFluidId);
            w.Write(OldBeClass ?? "");
            if (OldBeTree == null) { w.Write(0); } else { w.Write(OldBeTree.Length); w.Write(OldBeTree); }
        }

        public static BlockDelta Read(BinaryReader r)
        {
            var d = new BlockDelta
            {
                X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), Dim = r.ReadInt32(),
                OldSolidId = r.ReadInt32(), OldFluidId = r.ReadInt32(),
                OldBeClass = r.ReadString(),
            };
            int len = r.ReadInt32();
            d.OldBeTree = len > 0 ? r.ReadBytes(len) : null;
            if (d.OldBeClass.Length == 0) d.OldBeClass = null;
            return d;
        }
    }

    public enum EntityDeltaKind
    {
        /// <summary>Spawned since the anchor. Undo: remove, no drops, no death event.</summary>
        Spawned = 0,
        /// <summary>Died since the anchor. Undo: recreate from the captured tree.</summary>
        Died = 1,
        /// <summary>Existed at the anchor and has since drifted. Undo: restore captured state.</summary>
        Drifted = 2,
        /// <summary>An item entity created since the anchor. Undo: remove (§19, no dupes).</summary>
        ItemSpawned = 3
    }

    /// <summary>The pre-state of one entity, keyed by its runtime id.</summary>
    public class EntityDelta : IWorldDelta
    {
        public EntityDeltaKind Kind;
        public long EntityId;
        public string EntityCode = "";
        public double X, Y, Z;
        public byte[] Data;

        public void Write(BinaryWriter w)
        {
            w.Write((int)Kind);
            w.Write(EntityId);
            w.Write(EntityCode ?? "");
            w.Write(X); w.Write(Y); w.Write(Z);
            if (Data == null) { w.Write(0); } else { w.Write(Data.Length); w.Write(Data); }
        }

        public static EntityDelta Read(BinaryReader r)
        {
            var d = new EntityDelta
            {
                Kind = (EntityDeltaKind)r.ReadInt32(),
                EntityId = r.ReadInt64(),
                EntityCode = r.ReadString(),
                X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(),
            };
            int len = r.ReadInt32();
            d.Data = len > 0 ? r.ReadBytes(len) : null;
            return d;
        }
    }
}
