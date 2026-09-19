using System.Collections.Generic;

namespace Shinimodori.Config
{
    /// <summary>
    /// The whole mod's tuning surface. Every number named in PLAN.md lives here,
    /// grouped exactly as §14 requires. Loaded via api.LoadModConfig, written back
    /// with defaults when absent.
    /// </summary>
    public class ShinimodoriConfig
    {
        /// <summary>Bumped when the shape of this file changes incompatibly.</summary>
        public int SchemaVersion = 1;

        /// <summary>
        /// "balanced" (default), "anime", "forgiving", "cinematic", or "custom".
        /// Anything but "custom" re-applies that preset's bundle over the file on load,
        /// so a preset world stays a preset world even after the mod updates.
        /// </summary>
        public string Preset = "balanced";

        public CoreConfig Core = new CoreConfig();
        public AnchorConfig Anchors = new AnchorConfig();
        public MiasmaConfig Miasma = new MiasmaConfig();
        public TabooConfig Taboo = new TabooConfig();
        public TraumaConfig Trauma = new TraumaConfig();
        public TeaPartyConfig TeaParty = new TeaPartyConfig();
        public AuthorityConfig Authority = new AuthorityConfig();
        public VisualsConfig Visuals = new VisualsConfig();
        public AudioConfig Audio = new AudioConfig();
        public MultiplayerConfig Multiplayer = new MultiplayerConfig();
        public DebugConfig Debug = new DebugConfig();
    }

    public class CoreConfig
    {
        /// <summary>"auto" (cold open on first join), "item" (Shard of Envy), "command" (/rbd bless).</summary>
        public string BlessingMode = "auto";
        /// <summary>Master switch. False leaves the world completely vanilla.</summary>
        public bool Enabled = true;
        /// <summary>Knowledge (waypoints, handbook) survives a return. True makes the mod merciless.</summary>
        public bool RewindKnowledge = false;
        /// <summary>Permanent rifts at repeated death sites (§8).</summary>
        public bool DeathScars = true;
        /// <summary>Returns at one anchor before a death scar opens.</summary>
        public int DeathScarThreshold = 5;
        /// <summary>Ctrl+Shift+K voluntary return (§5.4).</summary>
        public bool AllowVoluntaryReturn = true;
        /// <summary>Real seconds the voluntary-return key must be held.</summary>
        public float VoluntaryHoldSeconds = 4.0f;
        /// <summary>Real minutes between voluntary returns.</summary>
        public float VoluntaryCooldownMinutes = 15f;
        /// <summary>Voluntary return requires a blade in hand.</summary>
        public bool VoluntaryRequiresBlade = true;

        // Return sequence stage durations, in real seconds (§5.2). Total default 7.5s.
        public float DyingSeconds = 0.4f;
        public float VoidSeconds = 3.0f;
        public float RewindSeconds = 1.6f;
        public float ArrivalSeconds = 2.5f;
        /// <summary>Seconds into ARRIVAL before input is handed back. The helplessness matters.</summary>
        public float ArrivalInputUnlockSeconds = 0.8f;
    }

    public class AnchorConfig
    {
        /// <summary>"none", "subtle" (default), "explicit" (§4.9).</summary>
        public string AnchorFeedback = "subtle";
        /// <summary>In-game hours that must pass before a new anchor may be set.</summary>
        public double MinAnchorSpacingHours = 6.0;
        /// <summary>Fallback: anchor after this many in-game hours if preconditions hold.</summary>
        public double AnchorTimeoutHours = 18.0;
        /// <summary>No anchor while a hostile is within this radius.</summary>
        public int NoHostilesWithin = 30;
        public float MinTemporalStability = 0.65f;
        public float MinHealthFraction = 0.5f;
        /// <summary>Blocks captured around the anchor origin for the entity snapshot.</summary>
        public int CaptureRadius = 96;
        /// <summary>Side of a region cell, in blocks, for the "entered a new region" milestone.</summary>
        public int RegionCellSize = 512;
        /// <summary>In-game minutes a player must survive in a new region for it to count.</summary>
        public double RegionDwellMinutes = 3.0;
        /// <summary>Anchors cannot be set during a temporal storm.</summary>
        public bool StormsBlockAnchors = true;
        /// <summary>Seconds between entity drift samples (§4.5).</summary>
        public int EntityDriftSampleSeconds = 10;
        /// <summary>Hard caps before the journal overflows and forces a new anchor (§19).</summary>
        public int MaxDeltas = 200_000;
        public int MaxJournalMB = 64;
        /// <summary>Milliseconds a rewind may take before SafeMode takes over.</summary>
        public int RewindBudgetMs = 8000;
        /// <summary>Block writes per tick when a rewind must be chunked across ticks.</summary>
        public int RewindBlocksPerTick = 4000;
        /// <summary>Random positions sampled after a rewind to verify it landed (§4.6 step 10).</summary>
        public int VerifySampleCount = 64;
        /// <summary>Mismatch fraction above which the anchor is flagged degraded.</summary>
        public float VerifyMismatchTolerance = 0.02f;
    }

    public class MiasmaConfig
    {
        public bool Enabled = true;
        public float GainPerReturn = 10f;
        public float VoluntaryMultiplier = 1.5f;
        public float TabooMultiplier = 2.0f;
        /// <summary>Decay per in-game hour.</summary>
        public float DecayPerHour = 0.75f;
        /// <summary>Decay is multiplied by this at or above ClingThreshold — it clings.</summary>
        public float ClingDecayMultiplier = 0.5f;
        public float ClingThreshold = 60f;
        /// <summary>Decay multiplier for a while after an anchor advances — progress cleanses.</summary>
        public float AnchorCleanseMultiplier = 2.0f;
        public double AnchorCleanseHours = 24.0;
        /// <summary>Miasma burned by consuming a temporal gear at tier >= 3.</summary>
        public float TemporalGearBurn = 20f;
        /// <summary>Drifters notice the scent more keenly than animals do.</summary>
        public float DrifterSensitivity = 1.5f;
        /// <summary>Tiers are evaluated in order; the last whose Min is met wins.</summary>
        public List<MiasmaTierConfig> Tiers = new List<MiasmaTierConfig>
        {
            new MiasmaTierConfig { Min =  0, DetectionMultiplier = 1.00f, DrifterSpawnBonus = 0f,    StabilityDrainBonus = 0f,    TraderPriceMultiplier = 1.00f, TradersRefuse = false, MabeastPacks = false, MabeastsByDay = false, Rifts = false, AuraOpacity = 0f    },
            new MiasmaTierConfig { Min = 20, DetectionMultiplier = 1.25f, DrifterSpawnBonus = 0f,    StabilityDrainBonus = 0f,    TraderPriceMultiplier = 1.00f, TradersRefuse = false, MabeastPacks = false, MabeastsByDay = false, Rifts = false, AuraOpacity = 0f    },
            new MiasmaTierConfig { Min = 40, DetectionMultiplier = 1.50f, DrifterSpawnBonus = 0.30f, StabilityDrainBonus = 0f,    TraderPriceMultiplier = 1.12f, TradersRefuse = false, MabeastPacks = false, MabeastsByDay = false, Rifts = false, AuraOpacity = 0.15f },
            new MiasmaTierConfig { Min = 60, DetectionMultiplier = 1.90f, DrifterSpawnBonus = 0.30f, StabilityDrainBonus = 0.35f, TraderPriceMultiplier = 1.12f, TradersRefuse = true,  MabeastPacks = true,  MabeastsByDay = false, Rifts = false, AuraOpacity = 0.35f },
            new MiasmaTierConfig { Min = 80, DetectionMultiplier = 2.40f, DrifterSpawnBonus = 0.30f, StabilityDrainBonus = 0.70f, TraderPriceMultiplier = 1.12f, TradersRefuse = true,  MabeastPacks = true,  MabeastsByDay = true,  Rifts = true,  AuraOpacity = 0.60f },
        };

        // Mabeast pack events (§6.4)
        public int MabeastPackMin = 3;
        public int MabeastPackMax = 6;
        public int MabeastSpawnMinDistance = 40;
        public int MabeastSpawnMaxDistance = 70;
        /// <summary>Chance per in-game hour of darkness that a pack is rolled.</summary>
        public float MabeastPackChancePerHour = 0.35f;
        /// <summary>Hard ceiling on simultaneously living mabeasts per player.</summary>
        public int MabeastMaxAlive = 12;
        /// <summary>Tiers reduced while the mabeast-pelt cloak is worn.</summary>
        public int PeltCloakTierReduction = 1;
    }

    public class MiasmaTierConfig
    {
        public float Min;
        public float DetectionMultiplier = 1f;
        public float DrifterSpawnBonus;
        public float StabilityDrainBonus;
        public float TraderPriceMultiplier = 1f;
        public bool TradersRefuse;
        public bool MabeastPacks;
        public bool MabeastsByDay;
        public bool Rifts;
        public float AuraOpacity;
    }

    public class TabooConfig
    {
        public bool Enabled = true;
        /// <summary>Score at or above which the grip triggers (§7.2).</summary>
        public int TriggerScore = 3;
        /// <summary>Messages starting with this are treated as out-of-character and scored down.</summary>
        public string OocPrefix = "((";
        /// <summary>Written text fades away instead of gripping (§7.2). The quiet horror.</summary>
        public bool ErasesWriting = true;
        /// <summary>Stage 3 kills non-blessed NPCs/traders nearby.</summary>
        public bool KillsListeners = false;
        /// <summary>Stage 3 extends the listener kill to players. Leave off.</summary>
        public bool KillsPlayers = false;
        public int ListenerKillRadius = 12;
        /// <summary>Seconds within which a further trigger advances a stage.</summary>
        public float StageAdvanceWindowSeconds = 60f;
        /// <summary>Seconds of silence that decay one stage.</summary>
        public float StageDecaySeconds = 120f;
        public float Stage1Seconds = 3.0f;
        public float Stage2Seconds = 5.0f;
        public float Stage3Seconds = 2.5f;
        /// <summary>Fraction of max health drained across stage 2.</summary>
        public float Stage2HealthDrainFraction = 0.30f;
        /// <summary>Real minutes between stage-3 grips.</summary>
        public float Stage3CooldownMinutes = 10f;
        /// <summary>Stage 3 plays in full but does not end in a return. Set by the forgiving preset.</summary>
        public bool ForgivingStage3 = false;
    }

    public class TraumaConfig
    {
        public bool Enabled = true;
        /// <summary>Max-health fraction lost per arrival (§9.1).</summary>
        public float PhantomPainPerReturn = 0.15f;
        /// <summary>Floor on the accumulated max-health penalty.</summary>
        public float PhantomPainFloor = 0.50f;
        /// <summary>In-game hours over which one stack decays linearly.</summary>
        public double PhantomPainDecayHours = 3.0;
        /// <summary>Real seconds of the sensory afterimage (shiver / haze / muffle).</summary>
        public float SensoryAfterimageSeconds = 20f;

        public bool DespairEnabled = true;
        public float DespairPerDeathAtAnchor = 18f;
        /// <summary>Despair at which the Breakdown fires.</summary>
        public float BreakdownThreshold = 100f;
        public float BreakdownSeconds = 6f;
        /// <summary>Despair left after a Breakdown resolves.</summary>
        public float BreakdownResolveDespair = 40f;
        /// <summary>Damage bonus granted by Resolve.</summary>
        public float ResolveDamageBonus = 0.25f;
        /// <summary>In-game minutes Resolve lasts.</summary>
        public double ResolveMinutes = 10.0;

        /// <summary>Blocks within which a past death site triggers Death Recall.</summary>
        public int DeathRecallRadius = 10;
        public float DeathRecallSwayBonus = 0.15f;
        public float DeathRecallSwaySeconds = 20f;
    }

    public class TeaPartyConfig
    {
        public bool Enabled = true;
        /// <summary>Deaths at one anchor before Echidna may appear.</summary>
        public int DeathThreshold = 4;
        /// <summary>Despair that also opens the door.</summary>
        public float DespairThreshold = 80f;
        /// <summary>Chance the first time it is eligible at an anchor.</summary>
        public float FirstChance = 1.0f;
        /// <summary>Chance on subsequent eligibility.</summary>
        public float RepeatChance = 0.35f;
        /// <summary>Real minutes between tea parties.</summary>
        public float CooldownMinutes = 20f;
        /// <summary>Miasma added by drinking the tea.</summary>
        public float TeaMiasmaCost = 8f;
        /// <summary>Favor at which refusing earns the better gift.</summary>
        public int FavorGiftThreshold = 3;
        /// <summary>In-game minutes of Foresight (see hostiles through walls).</summary>
        public double ForesightMinutes = 10.0;
        /// <summary>Total deaths at which each other Witch unlocks (§10.4, stretch).</summary>
        public bool OtherWitchesEnabled = true;
        public int MinervaDeaths = 25;
        public int TyphonDeaths = 40;
        public int DaphneDeaths = 55;
        public int SekhmetDeaths = 70;
        public int CarmillaDeaths = 85;
    }

    public class AuthorityConfig
    {
        public bool Enabled = true;
        /// <summary>Total deaths that unlock Invisible Providence.</summary>
        public int DeathThreshold = 50;
        public int GrabRange = 12;
        public int RetrieveRange = 16;
        public float GrabStunSeconds = 1.5f;
        public float MiasmaPerUse = 5f;
        public float StabilityCostPerUse = 0.08f;
        /// <summary>Chance a use draws a free stage-1 grip.</summary>
        public float GripChancePerUse = 0.12f;
        /// <summary>Uses within OveruseHours that force a stage-2 grip.</summary>
        public int OveruseCount = 5;
        public double OveruseHours = 2.0;
    }

    public class VisualsConfig
    {
        /// <summary>"diegetic" (no HUD at all, default), "minimal", "full".</summary>
        public string HudMode = "diegetic";
        /// <summary>Master intensity for every screen effect, 0..1. 0 disables all overlays.</summary>
        public float EffectIntensity = 1.0f;
        /// <summary>Try to compile the custom GLSL. False forces the quad-only fallback path.</summary>
        public bool UseCustomShaders = true;
        /// <summary>Show the whisper as on-screen text as well, for players without audio.</summary>
        public bool WhisperSubtitles = false;
        public bool ScreenShake = true;
        /// <summary>Peripheral silhouettes at high miasma (§12.5).</summary>
        public bool PeripheralSilhouettes = true;
        /// <summary>In-game minutes between silhouette chances.</summary>
        public double SilhouetteIntervalMinutes = 20.0;
        public int VoidMoteCount = 300;
        /// <summary>Hands converging in taboo stage 2.</summary>
        public int TabooHandCount = 8;
    }

    public class AudioConfig
    {
        public float MasterVolume = 1.0f;
        /// <summary>Volume world audio is ducked to during a grip or the Void.</summary>
        public float DuckedVolume = 0.01f;
        public float HeartbeatVolume = 0.9f;
        public float WhisperVolume = 0.5f;
        /// <summary>Ambient whisper bed at miasma tier 4.</summary>
        public bool MiasmaAmbience = true;
    }

    public class MultiplayerConfig
    {
        /// <summary>"soloReturner" (default), "personalLoop", "everyoneReturns".</summary>
        public string Mode = "soloReturner";
        /// <summary>Optional single line shown to non-returners. Empty for nothing at all.</summary>
        public string NonReturnerMessage = "";
        /// <summary>Non-returners see the Void visuals without the whisper or the silhouette.</summary>
        public bool FreezeNonReturners = true;
    }

    public class DebugConfig
    {
        public bool Verbose = false;
        /// <summary>Log the score breakdown of every taboo evaluation, not just triggers.</summary>
        public bool LogTabooScores = false;
        /// <summary>Log per-rewind timing and delta counts.</summary>
        public bool LogRewindTimings = true;
        /// <summary>Randomly throw inside journal handlers to prove SafeMode holds (§16 Phase 1).</summary>
        public bool FuzzJournalFailures = false;
        public float FuzzFailureChance = 0.01f;
    }
}
