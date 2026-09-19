using System;
using System.Collections.Generic;
using Shinimodori.Authority;
using Shinimodori.Compat;
using Shinimodori.Config;
using Shinimodori.Core;
using Shinimodori.Death;
using Shinimodori.Miasma;
using Shinimodori.Net;
using Shinimodori.Taboo;
using Shinimodori.Trauma;
using Shinimodori.Witches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Shinimodori
{
    /// <summary>
    /// Server-side root. Owns every authoritative piece of the mod and wires them to
    /// each other; the subsystems talk through this rather than through statics, so a
    /// second world in the same process cannot bleed into the first.
    /// </summary>
    public class ShinimodoriServer
    {
        public ICoreServerAPI Api { get; private set; }
        public ShinimodoriConfig Cfg { get; private set; }

        public WorldState State { get; private set; } = new WorldState();
        public JournalRecorder Recorder { get; private set; }
        public RewindEngine Engine { get; private set; }
        public WorldFreezer Freezer { get; private set; }
        public AnchorManager Anchors { get; private set; }
        public ReturnSequenceServer Returns { get; private set; }
        public MiasmaSystem Miasma { get; private set; }
        public TabooSystem TabooSystem { get; private set; }
        public TraumaSystem Trauma { get; private set; }
        public TeaPartySystem TeaParty { get; private set; }
        public AuthoritySystem AuthoritySystem { get; private set; }

        public IServerNetworkChannel StateChannel { get; private set; }
        public IServerNetworkChannel FxChannel { get; private set; }

        private long saveTickListener = -1;

        public void Start(ICoreServerAPI api, ShinimodoriConfig cfg)
        {
            Api = api;
            Cfg = cfg;

            Recorder = new JournalRecorder(api, cfg);
            Engine = new RewindEngine(api, cfg, Warn);
            Freezer = new WorldFreezer(api);
            Anchors = new AnchorManager(this);
            Returns = new ReturnSequenceServer(this);
            Miasma = new MiasmaSystem(this);
            TabooSystem = new TabooSystem(this);
            Trauma = new TraumaSystem(this);
            TeaParty = new TeaPartySystem(this);
            AuthoritySystem = new AuthoritySystem(this);

            RegisterChannels();

            ShinimodoriBridge.Recorder = Recorder;
            ShinimodoriBridge.InterceptDie = Returns.OnEntityWillDie;

            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.GameWorldSave += OnGameWorldSave;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Event.PlayerDisconnect += OnPlayerDisconnect;

            Recorder.Register();
            Anchors.Register();
            Miasma.Register();
            TabooSystem.Register();
            Trauma.Register();
            TeaParty.Register();
            AuthoritySystem.Register();

            // Cheap periodic save of the small state; the journal rides the world save.
            saveTickListener = api.Event.RegisterGameTickListener(_ => { }, 60000);

            api.Logger.Notification("[shinimodori] Server side ready. Death is not an ending.");
        }

        private void RegisterChannels()
        {
            StateChannel = Api.Network.RegisterChannel(ChannelNames.State)
                .RegisterMessageType<PktBlessing>()
                .RegisterMessageType<PktReturnBegin>()
                .RegisterMessageType<PktStageAdvance>()
                .RegisterMessageType<PktReturnComplete>()
                .RegisterMessageType<PktStateSync>()
                .RegisterMessageType<PktTabooStage>()
                .RegisterMessageType<PktTeaParty>()
                .RegisterMessageType<PktDialogNode>()
                .RegisterMessageType<PktDialogChoice>()
                .RegisterMessageType<PktAuthorityCast>()
                .RegisterMessageType<PktAuthorityState>()
                .RegisterMessageType<PktVoluntaryReturn>()
                .RegisterMessageType<PktLedger>()
                .RegisterMessageType<PktLedgerRequest>()
                .SetMessageHandler<PktDialogChoice>((plr, msg) => TeaParty.OnDialogChoice(plr, msg))
                .SetMessageHandler<PktAuthorityCast>((plr, msg) => AuthoritySystem.OnCast(plr, msg))
                .SetMessageHandler<PktVoluntaryReturn>((plr, msg) => Returns.OnVoluntaryRequest(plr, msg))
                .SetMessageHandler<PktLedgerRequest>((plr, msg) => SendLedger(plr));

            FxChannel = Api.Network.RegisterChannel(ChannelNames.Fx)
                .RegisterMessageType<PktAmbientCue>();
        }

        // ------------------------------------------------------------- persistence

        private void OnSaveGameLoaded()
        {
            State = StatePersistence.Load(Api);

            var journal = StatePersistence.LoadJournal(Api, State);
            if (journal != null && State.Anchor != null && journal.AnchorId == State.Anchor.Id)
            {
                Recorder.ReplaceJournal(journal);
            }
            else if (State.Anchor != null)
            {
                // Anchor without a trustworthy journal: keep the anchor (the player's
                // return point is still meaningful) but arm SafeMode.
                var fresh = new WorldJournal(State.Anchor.Id);
                fresh.MarkIntegrityLost("journal missing or mismatched at load");
                Recorder.ReplaceJournal(fresh);
            }

            if (State.Anchor != null)
            {
                Recorder.Active = true;
                Recorder.CurrentOrigin = State.Anchor.Origin;
            }

            Api.Logger.Notification("[shinimodori] Loaded: anchor={0}, journal={1} deltas, {2} player record(s).",
                State.Anchor?.AnchorReason ?? "none", Recorder.Stats().Blocks + Recorder.Stats().Entities, State.Players.Count);
        }

        private void OnGameWorldSave()
        {
            StatePersistence.Save(Api, State, Recorder.Journal);
        }

        // ------------------------------------------------------------- player hooks

        private void OnPlayerNowPlaying(IServerPlayer plr)
        {
            var ps = State.GetOrCreate(plr.PlayerUID);

            // A player who disconnected mid-return owes one; finish it before handing
            // over control, or they resume standing in the moment that killed them (§19).
            if (State.MidReturn.Contains(plr.PlayerUID))
            {
                State.MidReturn.Remove(plr.PlayerUID);
                Api.Logger.Notification("[shinimodori] {0} rejoined mid-return; completing it now.", plr.PlayerName);
                Returns.CompleteInterruptedReturn(plr, ps);
                return;
            }

            Blessings.OnPlayerJoin(this, plr, ps);
            SyncState(plr);

            if (ps.Blessed && State.Anchor == null)
            {
                // First blessed player in a fresh world: they need a point to come back to.
                Anchors.SetAnchor(plr, "initial");
            }
        }

        private void OnPlayerDisconnect(IServerPlayer plr)
        {
            if (Returns.IsReturning(plr.PlayerUID)) State.MidReturn.Add(plr.PlayerUID);
        }

        // ------------------------------------------------------------------ helpers

        public PlayerState StateOf(IServerPlayer plr) => State.GetOrCreate(plr.PlayerUID);

        public bool IsBlessed(IServerPlayer plr) => plr != null && StateOf(plr).Blessed;

        public IEnumerable<IServerPlayer> BlessedPlayers()
        {
            foreach (var p in Api.World.AllOnlinePlayers)
                if (p is IServerPlayer sp && StateOf(sp).Blessed) yield return sp;
        }

        public void SyncState(IServerPlayer plr)
        {
            var ps = StateOf(plr);
            double nowHours = Api.World.Calendar.TotalHours;
            StateChannel.SendPacket(new PktStateSync
            {
                Miasma = ps.Miasma,
                MiasmaTier = Miasma.TierIndexFor(plr, ps),
                Despair = ps.Despair,
                TotalDeaths = ps.TotalDeaths,
                DeathsAtAnchor = ps.DeathsAtAnchor,
                Blessed = ps.Blessed,
                AuthorityUnlocked = ps.AuthorityUnlocked,
                PhantomPainFraction = Trauma.CurrentPhantomPain(ps, nowHours),
                AnchorAgeHours = State.Anchor == null ? 0 : Math.Max(0, nowHours - State.Anchor.TotalHours),
                AnchorReason = State.Anchor?.AnchorReason ?? "",
                WearingPeltCloak = Miasma.IsWearingPeltCloak(plr),
            }, plr);
        }

        public void SendCue(IServerPlayer plr, string cue, float intensity = 1f, float seconds = 1f)
        {
            FxChannel.SendPacket(new PktAmbientCue { Cue = cue, Intensity = intensity, Seconds = seconds }, plr);
        }

        private void SendLedger(IServerPlayer plr)
        {
            var ps = StateOf(plr);
            var pkt = new PktLedger();
            foreach (var d in ps.Ledger)
            {
                pkt.Rows.Add(new LedgerRow
                {
                    Index = d.Index, Cause = d.Cause, KillerName = d.KillerName,
                    X = d.X, Y = d.Y, Z = d.Z, TotalHours = d.TotalHours,
                    MiasmaAtDeath = d.MiasmaAtDeath, AnchorId = d.AnchorId,
                });
            }
            StateChannel.SendPacket(pkt, plr);
        }

        public void Warn(string message)
        {
            Api.Logger.Warning("[shinimodori] {0}", message);
        }

        public void Debug(string message)
        {
            if (Cfg.Debug.Verbose) Api.Logger.Notification("[shinimodori] {0}", message);
        }

        public void Dispose()
        {
            if (saveTickListener != -1) Api.Event.UnregisterGameTickListener(saveTickListener);
            Recorder?.Unregister();
            Anchors?.Unregister();
            Miasma?.Unregister();
            TabooSystem?.Unregister();
            Trauma?.Unregister();
            TeaParty?.Unregister();
            AuthoritySystem?.Unregister();
            ShinimodoriBridge.Clear();
        }
    }
}
