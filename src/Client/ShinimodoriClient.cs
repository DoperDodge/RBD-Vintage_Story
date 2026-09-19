using System;
using Shinimodori.Client.Renderers;
using Shinimodori.Config;
using Shinimodori.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Shinimodori.Client
{
    /// <summary>
    /// Client-side root.
    ///
    /// Renders and asks; never decides. Every timing it uses came from the server in a
    /// packet, so a client that lags, cheats or disconnects cannot shorten the Void,
    /// skip a grip or dodge a cost (§13).
    /// </summary>
    public class ShinimodoriClient : IDisposable
    {
        public ICoreClientAPI Api { get; private set; }
        public ShinimodoriConfig Cfg { get; private set; }

        public ScreenEffectStack Effects { get; } = new ScreenEffectStack();
        public TextureFactory Textures { get; private set; }
        public AudioDirector Audio { get; private set; }

        public IClientNetworkChannel StateChannel { get; private set; }
        public IClientNetworkChannel FxChannel { get; private set; }

        // Mirrored server state, for the HUD and the ambient effects.
        public bool Blessed;
        public float Miasma;
        public int MiasmaTier;
        public float Despair;
        public int TotalDeaths;
        public int DeathsAtAnchor;
        public bool AuthorityUnlocked;
        public double AnchorAgeHours;

        /// <summary>True while this client is watching someone else's return (§13).</summary>
        public bool VoidIsBystander;
        public int EffectSeed;

        /// <summary>Input is locked by the server; the client just refuses to act on it too.</summary>
        public bool InputLocked { get; private set; }

        private OverlayRenderer overlay;
        private HudOverlay hud;
        private TeaPartyDialog teaDialog;
        private long tickListener = -1;

        private float voluntaryHeld;
        private bool voluntaryKeyDown;
        private float deathCounterTimer;

        private const string HotkeyVoluntary = "shinimodori.voluntaryreturn";
        private const string HotkeyAuthority = "shinimodori.authority";

        public void Start(ICoreClientAPI capi, ShinimodoriConfig cfg)
        {
            Api = capi;
            Cfg = cfg;

            Textures = new TextureFactory(capi);
            Audio = new AudioDirector(capi, cfg);

            RegisterChannels();

            overlay = new OverlayRenderer(capi, this, Textures);
            capi.Event.RegisterRenderer(overlay, EnumRenderStage.AfterPostProcessing, "shinimodori-void");
            capi.Event.RegisterRenderer(overlay, EnumRenderStage.Ortho, "shinimodori-overlay");

            hud = new HudOverlay(capi, this);
            capi.Event.RegisterRenderer(hud, EnumRenderStage.Ortho, "shinimodori-hud");

            teaDialog = new TeaPartyDialog(capi, this);

            RegisterHotkeys();
            capi.Input.InWorldAction += OnInWorldAction;

            tickListener = capi.Event.RegisterGameTickListener(OnTick, 20);
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
                .SetMessageHandler<PktBlessing>(OnBlessing)
                .SetMessageHandler<PktReturnBegin>(OnReturnBegin)
                .SetMessageHandler<PktStageAdvance>(OnStageAdvance)
                .SetMessageHandler<PktReturnComplete>(OnReturnComplete)
                .SetMessageHandler<PktStateSync>(OnStateSync)
                .SetMessageHandler<PktTabooStage>(OnTabooStage)
                .SetMessageHandler<PktTeaParty>(OnTeaParty)
                .SetMessageHandler<PktDialogNode>(OnDialogNode)
                .SetMessageHandler<PktAuthorityState>(OnAuthorityState)
                .SetMessageHandler<PktLedger>(OnLedger);

            FxChannel = Api.Network.RegisterChannel(ChannelNames.Fx)
                .RegisterMessageType<PktAmbientCue>()
                .SetMessageHandler<PktAmbientCue>(OnAmbientCue);
        }

        private void RegisterHotkeys()
        {
            Api.Input.RegisterHotKey(HotkeyVoluntary, Lang.Get("shinimodori:hotkey-voluntary"),
                GlKeys.K, HotkeyType.CharacterControls, false, true, true);
            Api.Input.SetHotKeyHandler(HotkeyVoluntary, _ => true);   // held state is polled in OnTick

            Api.Input.RegisterHotKey(HotkeyAuthority, Lang.Get("shinimodori:hotkey-authority"),
                GlKeys.V, HotkeyType.CharacterControls);
            Api.Input.SetHotKeyHandler(HotkeyAuthority, _ => { CastAuthority(); return true; });
        }

        // ---------------------------------------------------------------- packets

        private void OnBlessing(PktBlessing msg)
        {
            Blessed = msg.Blessed;
            if (!msg.PlayColdOpen) return;

            // The isekai cold open (§3). No tutorial. No explanation, now or ever.
            Effects.Begin(EffectKind.ReturnSequence, 6.5f, 1f, (int)ReturnStage.Void, "coldopen");
            VoidIsBystander = false;
            Audio.Duck(true);
            Audio.PlayOneShot("sm_void_whisper_bed", Cfg.Audio.WhisperVolume);
            Api.Event.RegisterCallback(_ => Audio.PlayOneShot("sm_whisper_love", Cfg.Audio.WhisperVolume * 0.6f), 4200);
            Api.Event.RegisterCallback(_ => Audio.Duck(false), 6200);

            if (Cfg.Visuals.WhisperSubtitles)
                Api.Event.RegisterCallback(_ => ShowWhisper(Lang.Get("shinimodori:whisper-love")), 4200);
        }

        private void OnReturnBegin(PktReturnBegin msg)
        {
            VoidIsBystander = msg.Bystander;
            EffectSeed = msg.Seed;
            InputLocked = true;

            Effects.Begin(EffectKind.ReturnSequence, msg.DyingSeconds, 1f, (int)ReturnStage.Dying);

            // (A) DYING: everything cuts to silence in 120ms except one sub-drop.
            Audio.Duck(true);
            Audio.StopAllLoops();
            Audio.PlayOneShot("sm_dying_drop", 0.9f);
        }

        private void OnStageAdvance(PktStageAdvance msg)
        {
            Effects.Begin(EffectKind.ReturnSequence, msg.Seconds, 1f, (int)msg.Stage);

            switch (msg.Stage)
            {
                case ReturnStage.Void:
                    Audio.StartLoop("sm_void_whisper_bed", Cfg.Audio.WhisperVolume);
                    Audio.StartLoop("sm_heartbeat_slow", Cfg.Audio.HeartbeatVolume * 0.7f);
                    if (!VoidIsBystander)
                    {
                        // "…I love you." at two thirds through, barely audible.
                        int at = (int)(msg.Seconds * 1000 * 0.66f);
                        Api.Event.RegisterCallback(_ =>
                        {
                            Audio.PlayOneShot("sm_whisper_love", Cfg.Audio.WhisperVolume * 0.55f);
                            if (Cfg.Visuals.WhisperSubtitles) ShowWhisper(Lang.Get("shinimodori:whisper-love"));
                        }, at);
                    }
                    break;

                case ReturnStage.Rewind:
                    Audio.StopLoop("sm_void_whisper_bed", 0.2f);
                    Audio.PlayOneShot("sm_rewind_sweep", 0.85f);
                    break;
            }
        }

        private void OnReturnComplete(PktReturnComplete msg)
        {
            Effects.Begin(EffectKind.ReturnSequence, msg.ArrivalSeconds, 1f, (int)ReturnStage.Arrival);
            deathCounterTimer = 3f;

            Audio.StopAllLoops();
            Audio.PlayOneShot("sm_rewind_impact", 0.9f);

            // (D) ARRIVAL. A voluntary return arrives in silence instead of a gasp (§5.4).
            if (msg.Voluntary)
                Api.Event.RegisterCallback(_ => Audio.Duck(false), 1200);
            else
            {
                Audio.PlayOneShot("sm_gasp", 0.95f);
                Audio.StartLoop("sm_heartbeat_fast", Cfg.Audio.HeartbeatVolume);
                Api.Event.RegisterCallback(_ => Audio.StopLoop("sm_heartbeat_fast", 2f), 2200);
                Audio.Duck(false);
            }

            if (msg.SafeMode) ShowWhisper(Lang.Get("shinimodori:whisper-frayed"));

            // Input comes back late on purpose: the moment of helplessness matters.
            Api.Event.RegisterCallback(_ => InputLocked = false, (int)(msg.InputUnlockSeconds * 1000));
        }

        private void OnStateSync(PktStateSync msg)
        {
            Blessed = msg.Blessed;
            Miasma = msg.Miasma;
            MiasmaTier = msg.MiasmaTier;
            Despair = msg.Despair;
            TotalDeaths = msg.TotalDeaths;
            DeathsAtAnchor = msg.DeathsAtAnchor;
            AuthorityUnlocked = msg.AuthorityUnlocked;
            AnchorAgeHours = msg.AnchorAgeHours;

            UpdateAmbientEffects();
        }

        private void OnTabooStage(PktTabooStage msg)
        {
            if (msg.WritingErased)
            {
                // No grip, no damage. Just the letters going.
                Effects.Begin(EffectKind.DeathRecall, msg.Seconds, 0.5f, 0, "erased");
                Audio.PlayOneShot("sm_taboo_swell", 0.35f);
                ShowWhisper(Lang.Get("shinimodori:whisper-erased"));
                return;
            }

            if (msg.Stage <= 0)
            {
                Effects.End(EffectKind.TabooGrip);
                InputLocked = false;
                Audio.Duck(false);
                Audio.StopLoop("sm_heartbeat_crush", 0.5f);
                return;
            }

            Effects.Begin(EffectKind.TabooGrip, msg.Seconds, 1f, msg.Stage);
            InputLocked = true;

            // All world audio to -40dB. The silence is most of the effect.
            Audio.Duck(true, 0.01f);
            Audio.PlayOneShot("sm_taboo_swell", 0.9f);

            if (msg.Stage >= 2)
            {
                Audio.PlayOneShot("sm_taboo_hands", 0.8f);
                Audio.StartLoop("sm_heartbeat_crush", Cfg.Audio.HeartbeatVolume);
                Audio.SetLoopPitch("sm_heartbeat_crush", 0.75f);
            }
            if (msg.Stage >= 3)
                Api.Event.RegisterCallback(_ => Audio.PlayOneShot("sm_taboo_crunch", 1f),
                    (int)(msg.Seconds * 1000 * 0.75f));
        }

        private void OnTeaParty(PktTeaParty msg)
        {
            if (msg.Entering)
            {
                Effects.Begin(EffectKind.TeaPartyTransition, 1.2f);
                Audio.StopAllLoops();
                Audio.Duck(true, 0.02f);
                Audio.StartLoop("sm_teaparty_string", 0.5f);
                Audio.PlayOneShot("sm_teaparty_pour", 0.6f);
            }
            else
            {
                Effects.End(EffectKind.TeaPartyTransition);
                Audio.StopLoop("sm_teaparty_string", 1.5f);
                teaDialog?.TryCloseSafe();
            }
        }

        private void OnDialogNode(PktDialogNode msg) => teaDialog?.Show(msg);

        private void OnAuthorityState(PktAuthorityState msg)
        {
            AuthorityUnlocked = msg.Unlocked;
            if (msg.Extended) Audio.PlayOneShot("sm_hands_extend", 0.7f);
        }

        private void OnLedger(PktLedger msg) => LedgerDialog.ShowFor(Api, msg);

        /// <summary>
        /// Fire-and-forget cues: the small things that sell it (§12.5), plus /rbd fx.
        /// Unknown cues are ignored rather than logged, so a newer server can send
        /// cues an older client has never heard of.
        /// </summary>
        private void OnAmbientCue(PktAmbientCue msg)
        {
            string cue = msg.Cue ?? "";

            switch (cue)
            {
                case "anchor":
                    Audio.PlayOneShot("sm_anchor_bell", 0.25f);
                    return;
                case "frayed":
                    ShowWhisper(Lang.Get("shinimodori:whisper-frayed"));
                    return;
                case "breakdown":
                    Effects.Begin(EffectKind.Breakdown, msg.Seconds, msg.Intensity);
                    Audio.StartLoop("sm_despair_whispers", 0.8f);
                    Api.Event.RegisterCallback(_ => Audio.StopLoop("sm_despair_whispers", 1f),
                        (int)(msg.Seconds * 1000));
                    return;
                case "resolve":
                    Effects.End(EffectKind.Breakdown);
                    Audio.PlayOneShot("sm_gasp", 0.7f);
                    return;
                case "silhouette":
                    Effects.Begin(EffectKind.MiasmaAmbient, 2f, 1f, 1, "silhouette");
                    return;
                case "mabeast-howl":
                    Audio.PlayOneShot("sm_mabeast_howl", 0.8f);
                    return;
                case "scar":
                    Audio.PlayOneShot("sm_taboo_swell", 0.4f);
                    return;
                case "authority-unlock":
                    Audio.PlayOneShot("sm_hands_extend", 1f);
                    ShowWhisper(Lang.Get("shinimodori:whisper-authority"));
                    return;
                case "miasma-burn":
                    Audio.PlayOneShot("sm_rewind_impact", 0.4f);
                    return;
            }

            if (cue.StartsWith("recall-", StringComparison.Ordinal))
            {
                Effects.Begin(EffectKind.DeathRecall, msg.Seconds, msg.Intensity);
                Audio.PlayOneShot("sm_heartbeat_slow", 0.5f);
                return;
            }

            if (cue.StartsWith("milestone-whisper-", StringComparison.Ordinal))
            {
                Audio.PlayOneShot("sm_despair_whispers", 0.5f);
                string n = cue.Substring("milestone-whisper-".Length);
                ShowWhisper(Lang.Get("shinimodori:whisper-deaths-" + n));
                return;
            }

            if (cue.StartsWith("waypoint:", StringComparison.Ordinal))
            {
                // Echidna's Clarity: the place you died, marked permanently.
                var parts = cue.Split(':');
                if (parts.Length >= 4) MarkWaypoint(parts);
                return;
            }

            // Anything else is a direct effect name from /rbd fx.
            PlayNamedEffect(cue, msg.Seconds);
        }

        private void PlayNamedEffect(string name, float seconds)
        {
            switch (name)
            {
                case "void": Effects.Begin(EffectKind.ReturnSequence, seconds, 1f, (int)ReturnStage.Void); break;
                case "rewind": Effects.Begin(EffectKind.ReturnSequence, seconds, 1f, (int)ReturnStage.Rewind); break;
                case "arrival": Effects.Begin(EffectKind.ReturnSequence, seconds, 1f, (int)ReturnStage.Arrival); break;
                case "dying": Effects.Begin(EffectKind.ReturnSequence, seconds, 1f, (int)ReturnStage.Dying); break;
                case "grip1": Effects.Begin(EffectKind.TabooGrip, seconds, 1f, 1); break;
                case "grip2": Effects.Begin(EffectKind.TabooGrip, seconds, 1f, 2); break;
                case "grip3": Effects.Begin(EffectKind.TabooGrip, seconds, 1f, 3); break;
                case "coldopen": OnBlessing(new PktBlessing { Blessed = true, PlayColdOpen = true }); break;
            }
        }

        private void MarkWaypoint(string[] parts)
        {
            try
            {
                if (!int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y)
                    || !int.TryParse(parts[3], out int z)) return;
                // Waypoints are knowledge, and knowledge is never rewound (§4.3).
                Api.SendChatMessage($"/waypoint addati circle {x} {y} {z} false #7a0f2b " +
                                    Lang.Get("shinimodori:waypoint-death"));
            }
            catch (Exception e) { Api.Logger.Debug("[shinimodori] waypoint failed: {0}", e.Message); }
        }

        private void ShowWhisper(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Api.ShowChatMessage(text);
        }

        // ------------------------------------------------------------------- tick

        private void OnTick(float dt)
        {
            Effects.Update(dt);
            Audio.OnTick(dt);

            if (deathCounterTimer > 0) deathCounterTimer -= dt;

            PollVoluntaryReturn(dt);
        }

        public bool ShowDeathCounter => deathCounterTimer > 0;

        /// <summary>
        /// The voluntary return ritual (§5.4): a blade, four real seconds, and a
        /// vignette closing the whole time. Deliberately not a keypress.
        /// </summary>
        private void PollVoluntaryReturn(float dt)
        {
            if (!Cfg.Core.AllowVoluntaryReturn || !Blessed || InputLocked)
            {
                if (voluntaryHeld > 0) CancelVoluntary();
                return;
            }

            bool down = IsHotkeyDown(HotkeyVoluntary);
            if (!down)
            {
                if (voluntaryHeld > 0) CancelVoluntary();
                voluntaryKeyDown = false;
                return;
            }

            if (!voluntaryKeyDown)
            {
                voluntaryKeyDown = true;
                Audio.StartLoop("sm_heartbeat_fast", Cfg.Audio.HeartbeatVolume * 0.6f);
            }

            voluntaryHeld += dt;
            float p = Math.Min(1f, voluntaryHeld / Cfg.Core.VoluntaryHoldSeconds);
            Effects.Begin(EffectKind.DespairAmbient, 0f, p, 0, "voluntary");
            Audio.SetLoopPitch("sm_heartbeat_fast", 0.9f + p * 0.5f);

            if (voluntaryHeld < Cfg.Core.VoluntaryHoldSeconds) return;

            CancelVoluntary();
            StateChannel.SendPacket(new PktVoluntaryReturn { Commit = true });
        }

        private void CancelVoluntary()
        {
            voluntaryHeld = 0;
            Audio.StopLoop("sm_heartbeat_fast", 0.4f);
            Effects.End(EffectKind.DespairAmbient);
            UpdateAmbientEffects();
        }

        private bool IsHotkeyDown(string code)
        {
            try
            {
                if (!Api.Input.HotKeys.TryGetValue(code, out var hk) || hk?.CurrentMapping == null) return false;
                return Api.Input.KeyboardKeyStateRaw[hk.CurrentMapping.KeyCode]
                       && (!hk.CurrentMapping.Ctrl || Api.Input.KeyboardKeyState[(int)GlKeys.ControlLeft]
                                                   || Api.Input.KeyboardKeyState[(int)GlKeys.ControlRight])
                       && (!hk.CurrentMapping.Shift || Api.Input.KeyboardKeyState[(int)GlKeys.ShiftLeft]
                                                    || Api.Input.KeyboardKeyState[(int)GlKeys.ShiftRight]);
            }
            catch { return false; }
        }

        private void CastAuthority()
        {
            if (!AuthorityUnlocked || InputLocked) return;

            var pkt = new PktAuthorityCast();
            var es = Api.World.Player?.CurrentEntitySelection;
            var bs = Api.World.Player?.CurrentBlockSelection;

            if (es?.Entity != null) pkt.TargetEntityId = es.Entity.EntityId;
            else if (bs?.Position != null)
            {
                pkt.HasBlockTarget = true;
                pkt.TargetX = bs.Position.X; pkt.TargetY = bs.Position.Y; pkt.TargetZ = bs.Position.Z;
            }
            else pkt.Guarding = true;

            StateChannel.SendPacket(pkt);
        }

        /// <summary>Ambient effects track the stats rather than being told when to start.</summary>
        private void UpdateAmbientEffects()
        {
            if (Despair >= 25)
            {
                float d = Math.Min(1f, (Despair - 25f) / 75f);
                Effects.Begin(EffectKind.DespairAmbient, 0f, d);
            }
            else Effects.End(EffectKind.DespairAmbient);

            if (MiasmaTier >= 2)
            {
                float m = Math.Min(1f, (MiasmaTier - 1) / 3f);
                Effects.Begin(EffectKind.MiasmaAmbient, 0f, m);
                if (MiasmaTier >= 4 && Cfg.Audio.MiasmaAmbience)
                    Audio.StartLoop("sm_miasma_ambient_loop", 0.35f);
                else Audio.StopLoop("sm_miasma_ambient_loop", 1f);
            }
            else
            {
                Effects.End(EffectKind.MiasmaAmbient);
                Audio.StopLoop("sm_miasma_ambient_loop", 1f);
            }
        }

        /// <summary>
        /// Swallows input while the server has the player locked. The server is already
        /// authoritative; this stops the *client* from predicting movement it will not
        /// be allowed to keep, which is what would otherwise look like rubber-banding.
        /// </summary>
        private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            if (!InputLocked) return;
            handled = EnumHandling.PreventDefault;
        }

        public void Dispose()
        {
            if (tickListener != -1) Api?.Event.UnregisterGameTickListener(tickListener);
            tickListener = -1;

            if (Api != null)
            {
                Api.Input.InWorldAction -= OnInWorldAction;
                if (overlay != null)
                {
                    Api.Event.UnregisterRenderer(overlay, EnumRenderStage.AfterPostProcessing);
                    Api.Event.UnregisterRenderer(overlay, EnumRenderStage.Ortho);
                }
                if (hud != null) Api.Event.UnregisterRenderer(hud, EnumRenderStage.Ortho);
            }

            teaDialog?.Dispose();
            Audio?.Dispose();
            Textures?.Dispose();
            Effects.Clear();
        }
    }
}
