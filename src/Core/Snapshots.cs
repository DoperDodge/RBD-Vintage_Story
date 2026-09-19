using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.MathTools;

namespace Shinimodori.Core
{
    /// <summary>
    /// "The world as it was at time T". Player state and nearby entities are held by
    /// value; everything else is reconstructed by replaying <see cref="WorldJournal"/>
    /// backwards (§4.1).
    ///
    /// Serialized by hand rather than with protobuf-net: the payload embeds Vintage
    /// Story's own tree/entity blobs, which are already versioned binary, and a
    /// hand-rolled format lets the schema version travel with the data. See §0 of
    /// API_VERIFIED.md.
    /// </summary>
    public class ReturnPoint
    {
        public const int Schema = 1;

        public Guid Id = Guid.NewGuid();
        public long RealTimeCreatedMs;

        // Calendar
        public double TotalHours;
        public long TotalGameSeconds;

        // Origin of the capture sphere
        public int OriginX, OriginY, OriginZ, OriginDim;
        public int CaptureRadius = 96;

        public Dictionary<string, PlayerSnapshot> Players = new Dictionary<string, PlayerSnapshot>();
        public List<EntitySnapshot> Entities = new List<EntitySnapshot>();

        /// <summary>"sleep", "milestone:firstIron", "region:12_-7", "timeout", "journalOverflow", "manual".</summary>
        public string AnchorReason = "initial";
        public int DeathsAtThisAnchor;
        /// <summary>Set when post-rewind verification found too many mismatches (§4.6 step 10).</summary>
        public bool Degraded;

        public BlockPos Origin => new BlockPos(OriginX, OriginY, OriginZ, OriginDim);

        public void SetOrigin(BlockPos pos)
        {
            OriginX = pos.X; OriginY = pos.Y; OriginZ = pos.Z; OriginDim = pos.dimension;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Schema);
            w.Write(Id.ToByteArray());
            w.Write(RealTimeCreatedMs);
            w.Write(TotalHours);
            w.Write(TotalGameSeconds);
            w.Write(OriginX); w.Write(OriginY); w.Write(OriginZ); w.Write(OriginDim);
            w.Write(CaptureRadius);
            w.Write(AnchorReason ?? "");
            w.Write(DeathsAtThisAnchor);
            w.Write(Degraded);

            w.Write(Players.Count);
            foreach (var kv in Players) { w.Write(kv.Key); kv.Value.Write(w); }

            w.Write(Entities.Count);
            foreach (var e in Entities) e.Write(w);
        }

        public static ReturnPoint Read(BinaryReader r)
        {
            int schema = r.ReadInt32();
            if (schema != Schema) throw new InvalidDataException($"ReturnPoint schema {schema} != {Schema}");

            var rp = new ReturnPoint
            {
                Id = new Guid(r.ReadBytes(16)),
                RealTimeCreatedMs = r.ReadInt64(),
                TotalHours = r.ReadDouble(),
                TotalGameSeconds = r.ReadInt64(),
                OriginX = r.ReadInt32(),
                OriginY = r.ReadInt32(),
                OriginZ = r.ReadInt32(),
                OriginDim = r.ReadInt32(),
                CaptureRadius = r.ReadInt32(),
                AnchorReason = r.ReadString(),
                DeathsAtThisAnchor = r.ReadInt32(),
                Degraded = r.ReadBoolean(),
            };

            int pc = r.ReadInt32();
            for (int i = 0; i < pc; i++)
            {
                string uid = r.ReadString();
                rp.Players[uid] = PlayerSnapshot.Read(r);
            }

            int ec = r.ReadInt32();
            for (int i = 0; i < ec; i++) rp.Entities.Add(EntitySnapshot.Read(r));

            return rp;
        }
    }

    /// <summary>
    /// Everything about a player that a return undoes. What is deliberately absent —
    /// waypoints, handbook discoveries, the mod's own ledger — is the whole mod (§4.3).
    /// </summary>
    public class PlayerSnapshot
    {
        public string PlayerUID = "";
        public double X, Y, Z;
        public float Yaw, Pitch;
        public int Dimension;
        public byte[] WatchedAttributes = Array.Empty<byte>();
        public byte[] EntityAttributes = Array.Empty<byte>();
        /// <summary>A tree of sub-trees, one per inventory id.</summary>
        public byte[] Inventories = Array.Empty<byte>();

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerUID ?? "");
            w.Write(X); w.Write(Y); w.Write(Z);
            w.Write(Yaw); w.Write(Pitch);
            w.Write(Dimension);
            WriteBlob(w, WatchedAttributes);
            WriteBlob(w, EntityAttributes);
            WriteBlob(w, Inventories);
        }

        public static PlayerSnapshot Read(BinaryReader r) => new PlayerSnapshot
        {
            PlayerUID = r.ReadString(),
            X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(),
            Yaw = r.ReadSingle(), Pitch = r.ReadSingle(),
            Dimension = r.ReadInt32(),
            WatchedAttributes = ReadBlob(r),
            EntityAttributes = ReadBlob(r),
            Inventories = ReadBlob(r),
        };

        internal static void WriteBlob(BinaryWriter w, byte[] b)
        {
            if (b == null) { w.Write(0); return; }
            w.Write(b.Length); w.Write(b);
        }

        internal static byte[] ReadBlob(BinaryReader r)
        {
            int len = r.ReadInt32();
            return len <= 0 ? Array.Empty<byte>() : r.ReadBytes(len);
        }
    }

    /// <summary>A non-player entity captured by value, via its own ToBytes/FromBytes.</summary>
    public class EntitySnapshot
    {
        public long EntityId;
        public string EntityCode = "";
        public double X, Y, Z;
        public byte[] Data = Array.Empty<byte>();

        public void Write(BinaryWriter w)
        {
            w.Write(EntityId);
            w.Write(EntityCode ?? "");
            w.Write(X); w.Write(Y); w.Write(Z);
            PlayerSnapshot.WriteBlob(w, Data);
        }

        public static EntitySnapshot Read(BinaryReader r) => new EntitySnapshot
        {
            EntityId = r.ReadInt64(),
            EntityCode = r.ReadString(),
            X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(),
            Data = PlayerSnapshot.ReadBlob(r),
        };
    }
}
