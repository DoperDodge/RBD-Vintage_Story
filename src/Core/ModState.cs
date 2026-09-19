using System;
using System.Collections.Generic;
using System.IO;

namespace Shinimodori.Core
{
    /// <summary>One line of the ledger. Every death, forever (§9.3).</summary>
    public class DeathRecord
    {
        public int Index;
        public int Cause;
        public string KillerName = "";
        public int X, Y, Z;
        public double TotalHours;
        public int Depth;
        public string AnchorId = "";
        public float MiasmaAtDeath;

        public void Write(BinaryWriter w)
        {
            w.Write(Index); w.Write(Cause); w.Write(KillerName ?? "");
            w.Write(X); w.Write(Y); w.Write(Z);
            w.Write(TotalHours); w.Write(Depth);
            w.Write(AnchorId ?? ""); w.Write(MiasmaAtDeath);
        }

        public static DeathRecord Read(BinaryReader r) => new DeathRecord
        {
            Index = r.ReadInt32(), Cause = r.ReadInt32(), KillerName = r.ReadString(),
            X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(),
            TotalHours = r.ReadDouble(), Depth = r.ReadInt32(),
            AnchorId = r.ReadString(), MiasmaAtDeath = r.ReadSingle(),
        };
    }

    /// <summary>
    /// The Witch's ledger for one player: everything that is deliberately NOT rewound
    /// (§4.3). Persisted in savegame mod data, never in the player entity's rewindable
    /// attribute tree.
    /// </summary>
    public class PlayerState
    {
        /// <summary>
        /// 1 — initial.
        /// 2 — split the tea-party cooldown out of LastTeaPartyHours into its own
        ///     real-time field; the two were being compared against each other.
        /// </summary>
        public const int Schema = 2;

        public string PlayerUID = "";
        public bool Blessed;
        public bool ColdOpenShown;

        public float Miasma;
        public float Despair;

        public int TotalDeaths;
        public int DeathsAtAnchor;

        /// <summary>Accumulated phantom-pain stacks, each decaying over PhantomPainDecayHours.</summary>
        public List<PhantomPainStack> PhantomPain = new List<PhantomPainStack>();

        public bool AuthorityUnlocked;
        public int EchidnaFavor;
        public int EchidnaDebt;
        /// <summary>Calendar hours of the last tea party, for the once-per-anchor rule.</summary>
        public double LastTeaPartyHours = double.MinValue;
        /// <summary>Real milliseconds of the last tea party, for the real-time cooldown.</summary>
        public double LastTeaPartyRealMs = double.MinValue;
        public double LastVoluntaryReturnRealMs = double.MinValue;
        public double LastStage3RealMs = double.MinValue;
        public double AnchorAdvancedAtHours = double.MinValue;
        public double BreakdownAtAnchorHours = double.MinValue;
        public double ResolveUntilHours = double.MinValue;
        public double ForesightUntilHours = double.MinValue;
        public bool SkipNextPhantomPain;

        /// <summary>Milestones already claimed, so each fires once (§4.9).</summary>
        public HashSet<string> Milestones = new HashSet<string>();
        /// <summary>512-block cells already visited.</summary>
        public HashSet<long> VisitedRegions = new HashSet<long>();
        /// <summary>Witches already met, by code.</summary>
        public HashSet<string> WitchesMet = new HashSet<string>();

        public List<DeathRecord> Ledger = new List<DeathRecord>();

        /// <summary>Authority uses, as total-hours stamps, for the overuse rule (§11).</summary>
        public List<double> AuthorityUses = new List<double>();

        public void Write(BinaryWriter w)
        {
            w.Write(Schema);
            w.Write(PlayerUID ?? "");
            w.Write(Blessed); w.Write(ColdOpenShown);
            w.Write(Miasma); w.Write(Despair);
            w.Write(TotalDeaths); w.Write(DeathsAtAnchor);

            w.Write(PhantomPain.Count);
            foreach (var p in PhantomPain) { w.Write(p.Amount); w.Write(p.AppliedHours); }

            w.Write(AuthorityUnlocked);
            w.Write(EchidnaFavor); w.Write(EchidnaDebt);
            w.Write(LastTeaPartyHours); w.Write(LastTeaPartyRealMs);
            w.Write(LastVoluntaryReturnRealMs); w.Write(LastStage3RealMs);
            w.Write(AnchorAdvancedAtHours); w.Write(BreakdownAtAnchorHours);
            w.Write(ResolveUntilHours); w.Write(ForesightUntilHours);
            w.Write(SkipNextPhantomPain);

            w.Write(Milestones.Count); foreach (var m in Milestones) w.Write(m);
            w.Write(VisitedRegions.Count); foreach (var v in VisitedRegions) w.Write(v);
            w.Write(WitchesMet.Count); foreach (var v in WitchesMet) w.Write(v);

            w.Write(Ledger.Count); foreach (var d in Ledger) d.Write(w);
            w.Write(AuthorityUses.Count); foreach (var u in AuthorityUses) w.Write(u);
        }

        public static PlayerState Read(BinaryReader r)
        {
            int schema = r.ReadInt32();
            if (schema < 1 || schema > Schema)
                throw new InvalidDataException($"PlayerState schema {schema} is not readable by this build (max {Schema})");

            var s = new PlayerState
            {
                PlayerUID = r.ReadString(),
                Blessed = r.ReadBoolean(),
                ColdOpenShown = r.ReadBoolean(),
                Miasma = r.ReadSingle(),
                Despair = r.ReadSingle(),
                TotalDeaths = r.ReadInt32(),
                DeathsAtAnchor = r.ReadInt32(),
            };

            int pp = r.ReadInt32();
            for (int i = 0; i < pp; i++)
                s.PhantomPain.Add(new PhantomPainStack { Amount = r.ReadSingle(), AppliedHours = r.ReadDouble() });

            s.AuthorityUnlocked = r.ReadBoolean();
            s.EchidnaFavor = r.ReadInt32();
            s.EchidnaDebt = r.ReadInt32();
            s.LastTeaPartyHours = r.ReadDouble();
            // v1 had no separate real-time stamp; leaving it unset simply means the
            // first tea party after an upgrade is not held back by a stale cooldown.
            s.LastTeaPartyRealMs = schema >= 2 ? r.ReadDouble() : double.MinValue;
            s.LastVoluntaryReturnRealMs = r.ReadDouble();
            s.LastStage3RealMs = r.ReadDouble();
            s.AnchorAdvancedAtHours = r.ReadDouble();
            s.BreakdownAtAnchorHours = r.ReadDouble();
            s.ResolveUntilHours = r.ReadDouble();
            s.ForesightUntilHours = r.ReadDouble();
            s.SkipNextPhantomPain = r.ReadBoolean();

            int mc = r.ReadInt32(); for (int i = 0; i < mc; i++) s.Milestones.Add(r.ReadString());
            int vc = r.ReadInt32(); for (int i = 0; i < vc; i++) s.VisitedRegions.Add(r.ReadInt64());
            int wc = r.ReadInt32(); for (int i = 0; i < wc; i++) s.WitchesMet.Add(r.ReadString());

            int lc = r.ReadInt32(); for (int i = 0; i < lc; i++) s.Ledger.Add(DeathRecord.Read(r));
            int ac = r.ReadInt32(); for (int i = 0; i < ac; i++) s.AuthorityUses.Add(r.ReadDouble());

            return s;
        }
    }

    public class PhantomPainStack
    {
        public float Amount;
        public double AppliedHours;
    }

    /// <summary>Everything the mod persists per world.</summary>
    public class WorldState
    {
        public const int Schema = 1;
        /// <summary>Key under which this blob lives in the savegame.</summary>
        public const string SaveKey = "shinimodori:state";

        public ReturnPoint Anchor;
        public Dictionary<string, PlayerState> Players = new Dictionary<string, PlayerState>();
        /// <summary>Players who disconnected mid-return and owe one on rejoin (§19).</summary>
        public HashSet<string> MidReturn = new HashSet<string>();
        /// <summary>Death-scar rift positions that must survive (§8).</summary>
        public List<ScarSite> Scars = new List<ScarSite>();
        /// <summary>SHA-256 of the journal file, so a stale or truncated journal is detected.</summary>
        public string JournalHash = "";

        public PlayerState GetOrCreate(string uid)
        {
            if (!Players.TryGetValue(uid, out var s))
            {
                s = new PlayerState { PlayerUID = uid };
                Players[uid] = s;
            }
            return s;
        }

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Schema);
                w.Write(Anchor != null);
                Anchor?.Write(w);

                w.Write(Players.Count);
                foreach (var kv in Players) { w.Write(kv.Key); kv.Value.Write(w); }

                w.Write(MidReturn.Count); foreach (var u in MidReturn) w.Write(u);

                w.Write(Scars.Count);
                foreach (var s in Scars) { w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Returns); w.Write(s.OpenedHours); }

                w.Write(JournalHash ?? "");
                return ms.ToArray();
            }
        }

        public static WorldState FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                int schema = r.ReadInt32();
                if (schema != Schema) throw new InvalidDataException($"WorldState schema {schema} != {Schema}");

                var st = new WorldState();
                if (r.ReadBoolean()) st.Anchor = ReturnPoint.Read(r);

                int pc = r.ReadInt32();
                for (int i = 0; i < pc; i++)
                {
                    string uid = r.ReadString();
                    st.Players[uid] = PlayerState.Read(r);
                }

                int mc = r.ReadInt32(); for (int i = 0; i < mc; i++) st.MidReturn.Add(r.ReadString());

                int sc = r.ReadInt32();
                for (int i = 0; i < sc; i++)
                    st.Scars.Add(new ScarSite
                    {
                        X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(),
                        Returns = r.ReadInt32(), OpenedHours = r.ReadDouble()
                    });

                st.JournalHash = r.ReadString();
                return st;
            }
        }
    }

    /// <summary>A place made wrong by dying there too often (§8).</summary>
    public class ScarSite
    {
        public int X, Y, Z;
        public int Returns;
        public double OpenedHours;
    }
}
