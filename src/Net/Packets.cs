using System.Collections.Generic;
using ProtoBuf;

namespace Shinimodori.Net
{
    /// <summary>Stage of the return cinematic. The server owns this; clients render it.</summary>
    public enum ReturnStage
    {
        Idle = 0,
        Dying = 1,
        Void = 2,
        TeaParty = 3,
        Rewind = 4,
        Arrival = 5
    }

    /// <summary>Sent once on join and whenever the blessing changes.</summary>
    [ProtoContract]
    public class PktBlessing
    {
        [ProtoMember(1)] public bool Blessed;
        /// <summary>True for the isekai cold open (§3) — the client plays the hand and the whisper.</summary>
        [ProtoMember(2)] public bool PlayColdOpen;
    }

    /// <summary>Opens the return cinematic. Carries every duration so the client never guesses.</summary>
    [ProtoContract]
    public class PktReturnBegin
    {
        [ProtoMember(1)] public int DeathCause;
        [ProtoMember(2)] public float DyingSeconds;
        [ProtoMember(3)] public float VoidSeconds;
        [ProtoMember(4)] public float RewindSeconds;
        [ProtoMember(5)] public float ArrivalSeconds;
        [ProtoMember(6)] public int Seed;
        /// <summary>A non-blessed player caught in someone else's rewind: Void visuals, no whisper.</summary>
        [ProtoMember(7)] public bool Bystander;
        /// <summary>Voluntary returns arrive in silence instead of with a gasp (§5.4).</summary>
        [ProtoMember(8)] public bool Voluntary;
        [ProtoMember(9)] public string KillerName = "";
        /// <summary>Total deaths so far — used for the ledger whisper at 10/25/50/100.</summary>
        [ProtoMember(10)] public int DeathIndex;
    }

    /// <summary>Server-driven stage transition. Never let a client timer decide this (§5.2).</summary>
    [ProtoContract]
    public class PktStageAdvance
    {
        [ProtoMember(1)] public ReturnStage Stage;
        [ProtoMember(2)] public float Seconds;
    }

    /// <summary>The world is restored and control is coming back.</summary>
    [ProtoContract]
    public class PktReturnComplete
    {
        [ProtoMember(1)] public float ArrivalSeconds;
        [ProtoMember(2)] public float InputUnlockSeconds;
        [ProtoMember(3)] public int DeathCause;
        [ProtoMember(4)] public bool Voluntary;
        /// <summary>The rewind degraded to SafeMode: play the "the thread frayed" line once.</summary>
        [ProtoMember(5)] public bool SafeMode;
        /// <summary>Sensory afterimage to play on arrival: 0 none, 1 cold, 2 heat, 3 drowning.</summary>
        [ProtoMember(6)] public int Afterimage;
    }

    /// <summary>Per-player persistent state, pushed when it changes.</summary>
    [ProtoContract]
    public class PktStateSync
    {
        [ProtoMember(1)] public float Miasma;
        [ProtoMember(2)] public int MiasmaTier;
        [ProtoMember(3)] public float Despair;
        [ProtoMember(4)] public int TotalDeaths;
        [ProtoMember(5)] public int DeathsAtAnchor;
        [ProtoMember(6)] public bool Blessed;
        [ProtoMember(7)] public bool AuthorityUnlocked;
        [ProtoMember(8)] public float PhantomPainFraction;
        [ProtoMember(9)] public double AnchorAgeHours;
        [ProtoMember(10)] public string AnchorReason = "";
        [ProtoMember(11)] public bool WearingPeltCloak;
    }

    /// <summary>Taboo grip stage. Client-local visuals only — nobody else sees anything (§7.3).</summary>
    [ProtoContract]
    public class PktTabooStage
    {
        [ProtoMember(1)] public int Stage;
        [ProtoMember(2)] public float Seconds;
        /// <summary>The written-text erasure instead of a grip (§7.2).</summary>
        [ProtoMember(3)] public bool WritingErased;
    }

    /// <summary>Enter/leave Echidna's dream.</summary>
    [ProtoContract]
    public class PktTeaParty
    {
        [ProtoMember(1)] public bool Entering;
        [ProtoMember(2)] public string WitchCode = "echidna";
    }

    /// <summary>One node of tea-party dialog, server-authored, with her real lines.</summary>
    [ProtoContract]
    public class PktDialogNode
    {
        [ProtoMember(1)] public string NodeId = "";
        [ProtoMember(2)] public string Speaker = "";
        [ProtoMember(3)] public string Text = "";
        [ProtoMember(4)] public List<DialogChoice> Choices = new List<DialogChoice>();
        /// <summary>Shows the free-text box where the player may say the unsayable (§10.3).</summary>
        [ProtoMember(5)] public bool AllowFreeText;
        /// <summary>Closes the dialog and resumes the Void.</summary>
        [ProtoMember(6)] public bool Closing;
    }

    [ProtoContract]
    public class DialogChoice
    {
        [ProtoMember(1)] public string Id = "";
        [ProtoMember(2)] public string Label = "";
    }

    /// <summary>C→S. Never trusted for outcomes; the server decides what a choice does.</summary>
    [ProtoContract]
    public class PktDialogChoice
    {
        [ProtoMember(1)] public string NodeId = "";
        [ProtoMember(2)] public string ChoiceId = "";
        [ProtoMember(3)] public string FreeText = "";
    }

    /// <summary>C→S. A request to use the Authority; the server validates range, cost and cooldown.</summary>
    [ProtoContract]
    public class PktAuthorityCast
    {
        [ProtoMember(1)] public long TargetEntityId;
        [ProtoMember(2)] public int TargetX, TargetY, TargetZ;
        [ProtoMember(3)] public bool HasBlockTarget;
        [ProtoMember(4)] public bool Guarding;
    }

    /// <summary>S→C. Authority arms are rendered only on the owner's client (§11).</summary>
    [ProtoContract]
    public class PktAuthorityState
    {
        [ProtoMember(1)] public bool Extended;
        [ProtoMember(2)] public bool Unlocked;
        [ProtoMember(3)] public int Kind; // 0 idle, 1 grab, 2 retrieve, 3 guard
    }

    /// <summary>C→S. Voluntary-return ritual progress and commit (§5.4).</summary>
    [ProtoContract]
    public class PktVoluntaryReturn
    {
        [ProtoMember(1)] public bool Commit;
    }

    /// <summary>Fire-and-forget client effect cue. Used by ambient dread and /rbd fx.</summary>
    [ProtoContract]
    public class PktAmbientCue
    {
        [ProtoMember(1)] public string Cue = "";
        [ProtoMember(2)] public float Intensity = 1f;
        [ProtoMember(3)] public float Seconds = 1f;
    }

    /// <summary>The death ledger, sent on request for the /rbd ledger GUI.</summary>
    [ProtoContract]
    public class PktLedger
    {
        [ProtoMember(1)] public List<LedgerRow> Rows = new List<LedgerRow>();
    }

    [ProtoContract]
    public class LedgerRow
    {
        [ProtoMember(1)] public int Index;
        [ProtoMember(2)] public int Cause;
        [ProtoMember(3)] public string KillerName = "";
        [ProtoMember(4)] public int X, Y, Z;
        [ProtoMember(5)] public double TotalHours;
        [ProtoMember(6)] public float MiasmaAtDeath;
        [ProtoMember(7)] public string AnchorId = "";
    }

    /// <summary>C→S. Asks for the ledger.</summary>
    [ProtoContract]
    public class PktLedgerRequest { }
}
