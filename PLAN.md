# PLAN.md — **SHINIMODORI** (死に戻り)

### A Re:Zero "Return by Death" mod for Vintage Story

**Mod ID:** `shinimodori`
**Display name:** Return by Death
**Target game:** Vintage Story **1.21.x / 1.22.x** (verify exact current version before starting — see §17)
**Language:** C# (Code mod) + JSON assets + GLSL shaders
**Implementer:** Opus 5 via Claude Code
**Audience for this doc:** the implementing agent. Everything here is a directive unless marked *(optional)* or *(stretch)*.

---

## 0. READ THIS FIRST — How to use this plan

1. **Do not trust my API names blindly.** I wrote this from knowledge of the VS modding API, but signatures shift between versions. Before writing code, clone/read the official sources (§17) and build a `API_VERIFIED.md` that maps every hook in §17's checklist to its real signature in the installed version. Fix this plan's code sketches against reality, then proceed.
2. **Build in the phase order in §16.** Each phase has acceptance criteria. Do not start Phase N+1 until Phase N's criteria pass in a live world.
3. **The rollback engine (§4) is the whole mod.** If it is unreliable, nothing else matters. Over-engineer it, test it with fuzzing, and give it a safe-mode fallback.
4. **Ship zero copyrighted assets.** No ripped anime audio, no official art, no character models traced from the show. All sounds synthesized/recorded original, all textures hand-made in VS's palette. Names of canon characters/concepts are fine (fan mods use them routinely); *assets* are not.
5. **When canon and fun conflict, canon wins on flavor, fun wins on tuning.** Every canon rule in §15 must be *represented*; its numbers may be softened via config.

---

## 1. Design goals

**The pitch:** You are the only person in the world who cannot die. Every death drags you back to an invisible checkpoint you did not choose, with your memories intact and nothing else. The world does not remember. The Witch does. And every time you come back, you stink a little more of her.

**Five pillars:**

| Pillar | Meaning |
|---|---|
| **P1 — Death is not a menu** | The player must never see Vintage Story's death/respawn screen. Lethal damage is intercepted *before* the death state. The transition from "about to die" to "back at the checkpoint" is a single unbroken cinematic. |
| **P2 — Memory is the only thing you keep** | Inventory, world state, time of day, crops, mobs, block changes: all rewound. Knowledge (map waypoints, handbook discoveries, what you learned) persists. This asymmetry *is* the mod. |
| **P3 — The checkpoint is not yours** | The player never places a save point. Anchors advance on the mod's terms, silently, on progress beats. Losing 4 hours to a bad death is the intended feeling. |
| **P4 — Coming back has a cost that compounds** | Miasma → world hostility → more deaths → more miasma. The death spiral is a feature. Escaping it requires *advancing the anchor*, i.e. making progress, i.e. hope. |
| **P5 — The Witch is a character, not a UI element** | Satella is never explained by a tooltip. She is felt: a whisper at the wrong moment, a silhouette at the edge of vision, and an absolute, screaming, screen-eating punishment the moment you try to tell anyone. |

**Explicit non-goals:** a quest chain retelling the anime's plot; NPC dialogue trees beyond the Witches; anything requiring a server-side database.

---

## 2. Environment & build

### 2.1 Toolchain

- .NET SDK matching the game's runtime (**verify**: check `Vintagestory.dll`'s target framework; 1.20 used .NET 7, later versions may be .NET 8).
- Base the project on `anegostudios/vsmodtemplate` (official) rather than hand-rolling the csproj.
- `VINTAGE_STORY` env var → game install dir. Reference `VintagestoryAPI.dll`, `VintagestoryLib.dll`, `VSSurvivalMod.dll`, `VSEssentials.dll`, `VSCreativeMod.dll`, `protobuf-net.dll`, `0Harmony.dll` with `<Private>false</Private>`.
- Post-build: copy mod folder to `%APPDATA%/VintagestoryData/Mods/shinimodori/`.
- `dotnet build -c Release` must produce a loadable mod with **exactly one** assembly containing a `ModSystem`.

### 2.2 File tree

```
shinimodori/
├── modinfo.json
├── modicon.png
├── src/
│   ├── ShinimodoriModSystem.cs          # root ModSystem, wires everything
│   ├── Config/
│   │   ├── ShinimodoriConfig.cs         # POCO, loaded via api.LoadModConfig
│   │   └── ConfigDefaults.cs
│   ├── Core/
│   │   ├── ReturnPoint.cs               # anchor snapshot data model
│   │   ├── AnchorManager.cs             # when/where anchors are set
│   │   ├── WorldJournal.cs              # append-only delta log
│   │   ├── Deltas/
│   │   │   ├── IWorldDelta.cs
│   │   │   ├── BlockDelta.cs
│   │   │   ├── BlockEntityDelta.cs
│   │   │   ├── EntityDelta.cs
│   │   │   ├── ItemStackDelta.cs
│   │   │   └── CalendarDelta.cs
│   │   ├── RewindEngine.cs              # the undo executor
│   │   ├── SnapshotSerializer.cs
│   │   └── SafeMode.cs                  # degraded fallback rollback
│   ├── Death/
│   │   ├── DeathInterceptor.cs          # EntityBehavior, preempts lethal damage
│   │   ├── ReturnSequenceServer.cs      # server-side orchestration/state machine
│   │   └── VoluntaryReturn.cs           # self-inflicted return ritual
│   ├── Miasma/
│   │   ├── MiasmaSystem.cs
│   │   ├── MiasmaTiers.cs
│   │   ├── ScentAggroBehavior.cs        # attached to hostiles
│   │   └── MabeastSpawner.cs
│   ├── Taboo/
│   │   ├── TabooDetector.cs             # chat/sign/book/trade scanning
│   │   ├── TabooGrip.cs                 # 3-stage escalation state machine
│   │   └── TabooWordlist.cs
│   ├── Trauma/
│   │   ├── TraumaSystem.cs              # phantom pain, despair, death recall
│   │   └── DeathLedger.cs               # every death ever: where, what, when
│   ├── Witches/
│   │   ├── TeaPartySystem.cs
│   │   ├── TeaPartyDimension.cs
│   │   ├── EntityEchidna.cs
│   │   ├── TeaPartyDialog.cs
│   │   └── OtherWitches.cs              # stretch
│   ├── Authority/
│   │   └── InvisibleProvidence.cs       # late-game Authority of Envy
│   ├── Client/
│   │   ├── ShinimodoriClientSystem.cs
│   │   ├── Renderers/
│   │   │   ├── VoidRenderer.cs          # the black between deaths
│   │   │   ├── RewindRenderer.cs        # reverse-montage effect
│   │   │   ├── TabooRenderer.cs         # grayscale + black hands + heart grip
│   │   │   ├── MiasmaRenderer.cs        # aura, trailing wisps, peripheral figure
│   │   │   ├── DespairRenderer.cs       # vignette, tunnel vision, shake
│   │   │   └── UnseenHandsRenderer.cs
│   │   ├── ScreenEffectStack.cs         # composition/priority of overlapping FX
│   │   ├── AudioDirector.cs             # ducking, heartbeat, whispers
│   │   └── Hud/
│   │       ├── HudDeathCounter.cs       # optional, off by default
│   │       └── HudMiasma.cs
│   ├── Net/
│   │   ├── Packets.cs                   # ProtoContract DTOs
│   │   └── ChannelNames.cs
│   ├── Commands/
│   │   └── ShinimodoriCommands.cs
│   └── Compat/
│       └── HarmonyPatches.cs
└── assets/shinimodori/
    ├── lang/en.json
    ├── shaders/
    │   ├── sm_desaturate.fsh / .vsh
    │   ├── sm_void.fsh
    │   ├── sm_rewind.fsh
    │   └── sm_hands.fsh
    ├── sounds/          # see §12.3
    ├── textures/
    │   ├── gui/, entity/, block/, particle/
    ├── entities/land/mabeast-ulgarm.json
    ├── entities/humanoid/echidna.json
    ├── entities/humanoid/cultist.json
    ├── itemtypes/shard-of-envy.json
    ├── blocktypes/witch-tea-table.json
    ├── shapes/...
    └── patches/         # JSON patches to vanilla entities (aggro ranges etc.)
```

### 2.3 modinfo.json

```json
{
  "type": "code",
  "modid": "shinimodori",
  "name": "Return by Death",
  "authors": ["DoperDodge"],
  "description": "Death is not an ending. It is a checkpoint you did not choose.",
  "version": "0.1.0",
  "dependencies": { "game": "1.21.0" },
  "side": "Universal",
  "requiredOnClient": true,
  "requiredOnServer": true
}
```

---

## 3. Acquiring the Blessing (the isekai moment)

Canon: Subaru doesn't earn Return by Death; he wakes up with it and no explanation.

**Default (`blessingMode: "auto"`):** on a player's **first ever join** to a world with the mod active, before control is handed over:

1. Screen holds black for 2.0s (no HUD).
2. A single white four-fingered hand-silhouette fades in at 8% opacity, centered, and closes.
3. A whisper — unintelligible, reversed, female — plays at −18 dB.
4. Text, centered, serif, fades in over 1.5s then out: *"…I love you."*
5. Hard cut to the world. No tutorial. No further explanation ever.

**`blessingMode: "item"`:** the player must consume **Shard of Envy** (`shard-of-envy`, an obsidian-black crystal, worldgen-rare in deep ruins / traders' curiosity slot). Use this for existing worlds and multiplayer servers where only one player should hold it.

**`blessingMode: "command"`:** admin grants via `/rbd bless <player>`.

Blessing state is stored on the player entity: `entity.WatchedAttributes.GetBool("sm:blessed")`, mirrored into savegame data. **Removing the blessing is not possible in-game.** (Config can force-revoke for admins.)

---

## 4. CORE SYSTEM 1 — Anchors & the Rewind Engine

This is the hardest part of the mod. Read this section twice.

### 4.1 Concept

- An **Anchor** (`ReturnPoint`) is a full snapshot of "the world as it was at time T" — *partially* by value (player state, calendar, nearby entities) and *mostly* by reference-plus-journal (block changes are recorded as deltas and undone in reverse).
- A **Journal** is the append-only list of every reversible change made to the world since the anchor was set.
- A **Return** = stop the world, replay the journal in reverse, restore the snapshots, resume.

Snapshot-everything is impossible (chunk data is huge). Journal-everything is tractable because a player in a survival game touches a bounded number of blocks per session.

### 4.2 `ReturnPoint` data model

```csharp
[ProtoContract]
public class ReturnPoint
{
    [ProtoMember(1)] public Guid Id;
    [ProtoMember(2)] public long RealTimeCreatedMs;

    // Calendar / weather
    [ProtoMember(3)] public double TotalHours;         // world.Calendar.TotalHours
    [ProtoMember(4)] public byte[] WeatherStateBlob;   // serialized weather sim state (verify serializer)
    [ProtoMember(5)] public int WeatherSeedSnapshot;

    // Players (keyed by PlayerUID)
    [ProtoMember(6)] public Dictionary<string, PlayerSnapshot> Players;

    // Entities within CaptureRadius of the anchor origin
    [ProtoMember(7)] public List<EntitySnapshot> Entities;
    [ProtoMember(8)] public BlockPos Origin;
    [ProtoMember(9)] public int CaptureRadius;         // default 96 blocks

    // Bookkeeping
    [ProtoMember(10)] public string AnchorReason;      // "sleep", "milestone:firstIron", "region:12_-7", "timeout", "journalOverflow"
    [ProtoMember(11)] public int DeathsAtThisAnchor;
}

[ProtoContract]
public class PlayerSnapshot
{
    [ProtoMember(1)] public string PlayerUID;
    [ProtoMember(2)] public double X, Y, Z;
    [ProtoMember(3)] public float Yaw, Pitch;
    [ProtoMember(4)] public byte[] WatchedAttributesBlob;   // health, hunger, temporal stability, bodytemp...
    [ProtoMember(5)] public byte[] InventoryBlob;           // ALL inventories: hotbar, backpack, craft grid, character slots
    [ProtoMember(6)] public byte[] EntityAttributesBlob;
    // NOT captured (deliberately persistent across returns — "memory"):
    //   map waypoints, revealed map chunks, handbook/journal discoveries,
    //   mod's own DeathLedger, Miasma, Trauma, ability unlocks.
}
```

### 4.3 What is captured vs. what persists (P2)

| Category | Rewound | Rationale |
|---|---|---|
| Inventory, equipment, hotbar | ✅ | Subaru returns empty-handed |
| Health, hunger, body temp, temporal stability | ✅ | Physical state reset |
| Position, dimension | ✅ | |
| World blocks, containers, crops, fire, fluids | ✅ | |
| Entity positions/health/aggro/tame state | ✅ (within radius) | |
| Calendar time, weather | ✅ | Nothing is gained by looping into daylight |
| **Map waypoints & explored map** | ❌ persists | Knowledge is memory |
| **Handbook discoveries / known recipes** | ❌ persists | Knowledge is memory |
| **Death ledger, death count, miasma, trauma, Authority unlocks** | ❌ persists | The Witch's ledger is not a world object |
| **Chat log, screenshots, the player's brain** | ❌ | The point of the mod |

> Config `rewindKnowledge: false` by default. Setting it true makes the mod merciless and is explicitly supported.

### 4.4 Journaling — what to hook

Register on the server, all guarded by "is the world in a rewindable session":

| Event | Delta produced | Notes |
|---|---|---|
| `sapi.Event.DidBreakBlock` / `BreakBlock` | `BlockDelta(pos, oldId, oldBlockEntityTree, 0)` | Capture the BlockEntity tree **before** removal — hook the *pre* event or read in `BreakBlock`. |
| `sapi.Event.DidPlaceBlock` | `BlockDelta(pos, oldId, oldBETree, newId)` | |
| `sapi.Event.DidUseBlock` | `BlockEntityDelta(pos, treeBefore)` | Chests, firepits, querns, barrels. Snapshot-on-first-touch (see 4.5). |
| `sapi.Event.ChunkColumnLoaded/Unloaded` | — | Force-load handling during rewind. |
| `sapi.Event.OnEntitySpawn` | `EntityDelta.Spawned(entityId)` | Undo = remove without drops. |
| `sapi.Event.OnEntityDeath` | `EntityDelta.Died(type, fullTree, pos)` | Undo = respawn from tree. |
| `sapi.Event.OnEntityDespawn` | `EntityDelta.Despawned(...)` | Distinguish unload-despawn (ignore) from real despawn. |
| Entity position/health drift | periodic `EntityDelta.State` sampling | See 4.5. |
| `Calendar` progression | implicit | Restored from snapshot, not journaled. |

**Blanket safety:** wrap every handler in try/catch; a throwing journal handler must never break the game loop, but it **must** set `journalIntegrity = false`, which forces SafeMode on the next return (§4.8).

### 4.5 Copy-on-first-touch, not copy-on-every-change

Naive journaling records every change. Instead, for each `BlockPos` / `entityId`, record **only the first observed pre-state since the anchor**, in a `Dictionary<key, firstState>`. Rewind then writes that one value back. This turns "player mined and replaced the same block 40 times" into one delta and bounds memory by *distinct touched objects*, not by actions.

Keep an ordered list too, for objects whose undo order matters (fluid/support chains). Recommended:

```csharp
// O(1) journaling, O(n distinct) rewind
Dictionary<BlockPos, BlockDelta> touchedBlocks;   // first-touch pre-state
List<BlockPos> touchOrder;                        // for reverse-order replay
Dictionary<long, EntityDelta> touchedEntities;
```

**Entity drift sampling:** every 10s (configurable), for each entity within `CaptureRadius` not already in `touchedEntities`, store its full tree. Cheap, and covers "a wolf wandered 200 blocks and ate my chicken."

### 4.6 The rewind execution

`RewindEngine.Execute(ReturnPoint rp, WorldJournal j, ReturnCause cause)` — runs on the server main thread, in a *frozen world* (§4.7):

```
 1. FREEZE      -> pause AI, stop player input, send PktReturnBegin to all clients
 2. PRELOAD     -> force-load every chunk column touched by the journal
                   (sapi.WorldManager.LoadChunkColumnPriority, await callbacks)
 3. ENTITIES    -> reverse pass:
                     a) despawn all entities spawned since anchor (no drops, no death events)
                     b) respawn all entities that died (restore tree, then re-add to world)
                     c) restore state of drifted entities
                     d) despawn ALL EntityItem created since anchor
 4. BLOCKS      -> IBulkBlockAccessor, iterate touchOrder in REVERSE:
                     SetBlock(oldId, pos); if oldBETree != null -> recreate BlockEntity, FromTreeAttributes
                   then Commit() once, then MarkChunkDirty + relight
 5. CALENDAR    -> set world time back to rp.TotalHours; restore weather blob
 6. PLAYERS     -> for each snapshot: clear all inventories, deserialize, teleport, restore attributes
 7. PERSISTENT  -> re-apply NON-rewound state (miasma, trauma, ledger, unlocks) ON TOP
 8. JOURNAL     -> clear, restart from rp
 9. THAW        -> resume AI, send PktReturnComplete with the client-side arrival cinematic cue
10. VERIFY      -> sample 64 random touched positions; log mismatches; if >2% mismatch, log a warning
                   and flag the anchor as degraded
```

Target budget: **< 2000 ms** for a 20k-delta journal. If it exceeds `rewindBudgetMs` (default 8000), chunk the block pass across ticks with the world still frozen and keep the client in the Void sequence (§12.1) — which is *why* the Void exists: it is a load screen that is also the best scene in the show.

### 4.7 Freezing the world

There is no real "pause" in VS. Approximate it:

- Server: set a global `IsRewinding` flag checked by all mod behaviors; for every loaded entity, suspend AI (clear/suspend `EntityBehaviorTaskAI` tasks — **verify the right way**: either `entity.GetBehavior<EntityBehaviorTaskAI>().TaskManager` suspension, or set `entity.State = EnumEntityState.Inactive`), zero `entity.ServerPos.Motion`.
- Clients: `PktReturnBegin` puts them in the Void renderer, which swallows input and hides the world entirely — so imperfect server-side freezing is invisible.
- Other players online (multiplayer): see §13.

### 4.8 SafeMode (degraded rollback)

If `journalIntegrity == false`, the journal overflowed, or the rewind throws:

- Restore **player snapshots only** (inventory, position, stats, calendar).
- Do **not** touch world blocks/entities.
- Force a **new anchor immediately** after arrival.
- Lore-wrap it, never show an error to the player: log to console, and in-game show the whisper line *"…the thread frayed."* once.

SafeMode must always be reachable and must never crash. It is the seatbelt.

### 4.9 Anchor advancement rules (P3)

An anchor is set when the player is **stable and has progressed**. Never on player command.

```csharp
bool ShouldSetAnchor() =>
    NoHostilesWithin(30) &&
    TemporalStability > 0.65f &&
    HealthFraction > 0.5f &&
    (MilestoneHit() || SleptFullNight() || EnteredNewRegion() || TimeoutElapsed())
    && HoursSinceLastAnchor >= cfg.minAnchorSpacingHours;   // default 6
```

**Milestones** (fire once each, persisted):
- First smelted metal of each tier (copper, bronze, iron, steel)
- First crafted tool head of a new tier
- Building a bed and sleeping in it (also a repeatable trigger)
- First translocator repaired / first ruin entered
- Surviving a temporal storm without dying
- Descending past y-levels 0 / −40 / −100 for the first time
- First trade with a trader
- Domesticating an animal

**Region entry:** world is diced into 512×512 cells; entering a never-visited cell (and surviving 3 in-game minutes there) is a milestone.

**Timeout fallback:** `anchorTimeoutHours` (default 18 in-game hours) with the safety preconditions met. Prevents pathological 4-hour loops.

**Anchor advancement is the only escape from a death spiral.** This must be communicated implicitly by the miasma decay curve, never by a tutorial.

**Anchor feedback** (config `anchorFeedback`):
- `"none"` (hardcore, default for the `anime` preset): nothing at all.
- `"subtle"` (**default**): 6 tiny white motes drift upward around the player + a 0.8s single low bell tone at −24 dB. Easy to miss.
- `"explicit"`: chat message *"Something settles. A point you could return to."*

### 4.10 Persistence

- On `GameWorldSave`: serialize `{ currentAnchor, journal, perPlayerState, globalState }`.
- Anchor + small state → `sapi.WorldManager.SaveGame.StoreData("shinimodori:state", blob)`.
- The journal can be megabytes — write it to `sapi.GetOrCreateDataPath("ModData/" + worldIdentifier + "/shinimodori")/journal.bin` (compressed), and store only its hash+path in savegame data.
- On `SaveGameLoaded`: load; if the journal is missing or its hash mismatches, keep the anchor and enter SafeMode-on-next-return.
- Version the blob (`schemaVersion` int) and write migrations from day one.

---

## 5. CORE SYSTEM 2 — Death interception & the Return sequence

### 5.1 Never let death happen (P1)

Do not hook "on death and respawn." Hook **lethal damage** and prevent the death state entirely.

Implementation: `EntityBehaviorReturner : EntityBehavior`, attached to blessed `EntityPlayer`s, overriding `OnEntityReceiveDamage`:

```csharp
public override void OnEntityReceiveDamage(DamageSource src, ref float damage)
{
    if (!IsBlessed || api.Side != EnumAppSide.Server) return;
    if (returnState != ReturnState.Idle) { damage = 0; return; }   // already returning: invulnerable

    float hp = entity.GetBehavior<EntityBehaviorHealth>().Health;
    if (damage < hp) return;                                       // survivable, carry on

    damage = 0;                                                    // <- death never occurs
    ReturnSequence.Begin(player, DeathCause.From(src));
}
```

**Also intercept:**
- Non-damage kills: `/kill`, void fall, `entity.Die()` calls from other mods → **Harmony prefix on `EntityPlayer.Die` / `Entity.Die`** that redirects to the return sequence when blessed and the cause isn't `EnumDespawnReason.Death`-exempt.
- Starvation, drowning, hypothermia, temporal instability death — all arrive as damage, covered above, but **test each explicitly**.
- Damage arriving while `returnState != Idle` is nulled.

**Harmony safety:** every patch in `Compat/HarmonyPatches.cs` must be wrapped and logged; if patching fails, fall back to the post-death path (hook `sapi.Event.PlayerDeath` + instantly suppress the death GUI client-side) and log loudly.

### 5.2 The Return state machine

```
Idle
 └─(lethal damage)→ DYING            0.4s : world freeze-frame, desaturation ramp, audio cut
      └→ VOID                        3.0s : black, starfield, reversed whisper, "…I love you."
           ├→ (tea party roll passes) TEAPARTY → returns to VOID when the dream ends
           └→ REWIND                 1.6s : server executes §4.6 while client plays reverse-montage
                └→ ARRIVAL           2.5s : gasp, heartbeat, tunnel vision fade-in, hands shake
                     └→ Idle         + apply miasma, trauma, ledger entry
```

Durations configurable; total default **7.5s**, and the rewind is *hidden inside* it. If the server needs longer, the VOID stage extends — never the ARRIVAL.

Server owns the state machine and drives clients via packets. Client renderers own the visuals. **Never let a client-side timer decide when control returns** — always a server `PktReturnComplete`.

### 5.3 Death cause classification

Build a `DeathCause` enum + description, because several systems consume it (trauma flashbacks, Echidna's dialogue, the death ledger, and the arrival's chosen sound):

`Wolf, Bear, Drifter, Locust, Bell, Fall, Drowning, Starvation, Hypothermia, Heat, TemporalInstability, Fire, Suffocation, Explosion, Player, Taboo, Voluntary, Unknown`

Store the killing entity's name, the exact position, the total-hours timestamp, and the depth. The ledger (§9.3) is used to make the world feel like it is mocking you.

### 5.4 Voluntary return

Canon: Subaru chooses death in the Sanctuary arc; it is one of the show's heaviest beats. Support it, do not make it cheap.

- Requires a blade in hand + `Ctrl+Shift+K` held for **4.0 real seconds** with a progress vignette closing in and the heartbeat rising.
- Adds **+50% miasma** compared to a normal death (the Witch notices eagerness) and **+1 despair stack immediately**.
- 15-minute real-time cooldown, `allowVoluntaryReturn` config (default **true**).
- Distinct arrival: no gasp; silence for 1.2s before ambience returns.

---

## 6. CORE SYSTEM 3 — The Witch's Miasma (魔女の残り香)

Canon: each use of Return by Death leaves the Witch's scent on Subaru. Beasts hunt it. Cultists worship it. Sensitives recoil from it.

### 6.1 The stat

- `Miasma` ∈ [0, 100], per player, persistent across returns (never rewound).
- **+10** per return (× 1.5 for voluntary, × 2.0 for a taboo-caused death).
- **Decay −0.75 per in-game hour** while not returning; decay is **halved** while miasma ≥ 60 (it clings).
- Decay is **doubled** for 24 in-game hours after an anchor advances (progress cleanses).

### 6.2 Tiers & effects

| Tier | Range | Name (lang key) | Effects |
|---|---|---|---|
| 0 | 0–19 | *Faint* | Occasional single black mote particle. Nothing else. |
| 1 | 20–39 | *Lingering* | Hostile detection range ×1.25. Wolves growl at you from farther away. Tamed/herded animals shy 1 block. |
| 2 | 40–59 | *Clinging* | Detection ×1.5. Drifter spawn weight +30% within 64 blocks of you. Traders' prices +12%. Visible dark wisp trail behind the player (seen by all players). |
| 3 | 60–79 | *Reeking* | Detection ×1.9. **Mabeast pack events** at night (§6.4). Temporal stability drains +35% faster. Traders refuse to trade (*"You smell of something I want no part of."*). Rare peripheral-vision silhouette (§12.5). |
| 4 | 80–100 | *Devoured* | Detection ×2.4. Mabeast packs day and night. Stability drain +70%. **Rifts open near you** on a timer. **Witch Cultists** spawn during temporal storms (§6.5, stretch). Constant low whisper ambience. A dark aura renders around the player. |

All numbers in config under `miasma.tiers[]`.

### 6.3 Aggro implementation

Prefer a **behavior injection** over patching every entity JSON:
- On entity spawn, if the entity is hostile/predatory, attach `ScentAggroBehavior`, which multiplies the effective detection range by querying the nearest blessed player's miasma.
- Concretely: the behavior runs a scan each `n` ticks; if a blessed player is within `baseRange × multiplier`, force the AI's seek-entity target (**verify** how to inject a target into `EntityBehaviorTaskAI` / `AiTaskSeekEntity` — likely by setting the task's target or by calling `TaskManager.ExecuteTask<AiTaskSeekEntity>()`).
- JSON patches in `assets/patches/` as a fallback for entities where behavior injection is awkward.

Design note: aggro must feel like being *hunted*, not like a bigger number. At tier 3+, wolves should path toward the player from off-screen and arrive in packs from multiple directions, rather than merely noticing sooner.

### 6.4 Mabeasts (魔獣) — **Ulgarm**

New entity `shinimodori:mabeast-ulgarm`:
- Base: vanilla wolf AI, heavily retuned. Black fur, faint red rim-light on the eyes (emissive texture), slightly larger, unnaturally smooth run animation.
- **Pack spawner:** at miasma tier ≥ 3, roll every in-game hour of darkness; on success spawn 3–6 Ulgarm at 40–70 blocks from the player, out of line of sight, with an immediate seek order on the player.
- They **do not despawn** by distance while the player is above tier 3; they track.
- **They ignore non-blessed players** unless attacked. The scent is yours alone. (This is a brutal, beautiful multiplayer detail.)
- Drops: `mabeast-pelt` (crafts into a cloak that reduces effective miasma tier by 1 while worn — the only real counterplay item).
- Audio: layered wolf howl pitched down 30% with a reversed tail.

### 6.5 Witch Cult *(stretch, Phase 7)*

At tier 4 during a temporal storm, spawn 2–4 `shinimodori:cultist`: ragged white-robed humanoids that **do not attack**. They kneel, chant, and follow at a distance, muttering about the fragrance. If the player attacks them, they fight, and killing one spikes miasma by +15. A rare named variant (**"the Sloth"**) uses shadow-hand ranged attacks and is a genuine boss fight, dropping a `witch-factor` item that unlocks the Authority early.

---

## 7. CORE SYSTEM 4 — The Taboo & Satella's Grip

The single most iconic mechanic in Re:Zero. **Get this perfect.**

### 7.1 Canon rules to encode

1. Acting on foreknowledge is allowed. *Describing the ability* is not.
2. Punishment is instant, escalating, and silent to everyone else.
3. The world goes gray; time stops; black hands emerge; one closes on his heart.
4. If he persists, **the listener dies** — killed by the Witch, not by him.
5. Nobody else perceives any of it. From outside, he simply seized up.

### 7.2 Detection

`TabooDetector` scans, for blessed players only:
- Outgoing **chat messages** (server-side, before broadcast).
- Text written to **signs** and **book/scroll/parchment** items.
- Renaming items, map waypoint names.
- *(optional)* A dedicated `/tell-them` roleplay command that always triggers.

**Scoring, not keyword matching** — false positives are a disaster:

```
score = 0
+3  strong phrase:  "return by death", "shinimodori", "死に戻り", "i can't die", "i come back when i die",
                    "i respawn", "i reset when i die", "checkpoint", "save point", "time loop"
+2  paired signals:  (death word) within 8 tokens of (repeat word)
                      death words: die, died, death, killed, dying
                      repeat words: again, back, loop, rewind, restart, over, before, redo, repeat
+2  "i already died", "last time i died", "this already happened", "i've seen this before"
+1  "i know what happens next", "trust me, i've been here"
-2  message is in a #ooc channel / starts with the configured OOC prefix (default "((")
-3  no blessed-player first person ("i", "my", "me") present

TRIGGER at score >= 3.
```

**Important nuance:** *"a wolf is going to attack at dusk"* must **not** trigger. Foreknowledge is permitted; that is the whole tragedy. Include a regression corpus of ~80 phrases (40 trigger / 40 must-not) as a unit test in `tests/TabooCorpus.json`.

**Book/sign special case (do this, it's gorgeous):** the write is *allowed* — then, 0.5s later, the letters on the page **fade out one by one** and the page is blank. No grip, no damage. Just the quiet horror of being erased. (Add a `tabooErasesWriting` config, default true.)

### 7.3 The Grip — 3-stage state machine

Triggering sets `GripStage = 1`. Each further trigger within 60s advances a stage. Stages decay one step per 120s of silence.

**Stage 1 — "She hears you." (3.0s)**
- Client: saturation → 15% over 0.4s; **all in-world audio ducked to −40 dB**; a single low sub-bass swell; frame rate of entity animations visibly drops to ~2 fps (freeze illusion).
- Server: player input locked (movement, attack, interact all suppressed). No damage.
- Two black tendrils slide in from the lower screen corners, reaching toward the center, then withdraw.
- The chat message is **never sent.** It is eaten. Other players see nothing.

**Stage 2 — "She reaches." (5.0s)**
- Everything from Stage 1, plus: saturation → 0% with **red retained** (color-key the red channel — a direct nod to the show's grayscale-with-red framing).
- Six to nine black hands emerge from the screen edges, fingers splayed, moving at different speeds, converging.
- One hand-shaped shadow settles over the center of the screen; the heartbeat SFX slows and *deepens*; the camera FOV pulls in 8°.
- Server: **30% of max health drained over the duration**, uncancellable. Full input lock. Player is a statue in the world; other players see them standing perfectly still.
- Blood-like droplets slide down from the top edge of the screen.

**Stage 3 — "She takes." (2.5s, then death)**
- The central hand **closes.** Chromatic aberration spikes, screen crushes inward, a single wet crunch SFX, hard cut to black.
- Server: the player's return sequence fires with `DeathCause.Taboo` (miasma ×2.0 on this return).
- **If `tabooKillsListeners` is enabled** (default **false**; true in the `anime` preset): every non-blessed **NPC/trader** within 12 blocks dies instantly and silently, with no damage source and no drops, one second before the player's death. In multiplayer, `tabooKillsPlayers` (default **false**) extends this to players — leave this off unless the server owner is doing a themed run.

### 7.4 Safeties

- The grip never triggers **inside the Tea Party dimension** (canon: witches already know).
- Grip triggers are rate-limited: at most one Stage-3 per 10 real minutes.
- `/rbd taboo off` for admins; `tabooEnabled: true` by default.
- Log every trigger with the matched score breakdown so false positives can be tuned from user reports.

---

## 8. CORE SYSTEM 5 — Temporal integration (Vintage Story's own lore)

Vintage Story already has time-anomaly lore: temporal stability, rifts, temporal storms, Drifters that come from somewhere else. **Marry Return by Death to it instead of bolting it on.** This is where the mod stops feeling like a reskin.

- Returning **tears the local temporal fabric**: on arrival, temporal stability in a 32-block radius is depressed by 15% for 2 in-game hours. Dying repeatedly in one place makes that place *wrong*.
- After **5 returns at the same anchor**, a **permanent rift** opens at the last death site. It does not close. It is a monument to your failure, and it spawns Drifters. (Config `deathScars`, default true.)
- **Temporal storms are anchor-hostile:** anchors cannot be set during a storm, and miasma decay stops. Storms become dread events rather than combat events.
- **Temporal gears** (the game's anomaly item): at miasma tier ≥ 3, a consumed temporal gear also burns **20 miasma**. Gives the player one lever, and it costs a precious resource.
- The Drifters **notice** the miasma more than animals do (they're from the same kind of elsewhere): their multiplier is `1 + (tierMultiplier − 1) × 1.5`.

---

## 9. CORE SYSTEM 6 — Trauma, Despair, and the Ledger

Canon: Subaru keeps every death. The physical reset is not a psychological one.

### 9.1 Phantom Pain

Applied on every arrival:
- **−15% max health, −20% max stamina/satiety drain rate penalty**, decaying linearly over 3 in-game hours.
- Stacks additively up to a **−50%** floor.
- If the death cause was cold/burning/drowning, add a matching sensory afterimage: 20s of shivering camera / heat haze / muffled underwater audio. **Cosmetic, not mechanical.** It sells the idea better than any number.

### 9.2 Despair

`Despair` ∈ [0, 100], **+18 per death at the same anchor**, **reset to 0 when the anchor advances**. This is the mod's hope-meter, inverted.

| Despair | Effects |
|---|---|
| ≥ 25 | Weapon sway +20%. Breathing audible in quiet moments. |
| ≥ 50 | Screen edges desaturate 25%. Occasional 1-frame flash of the last death's freeze-frame. Random 0.5s heartbeat spikes. |
| ≥ 75 | Tunnel vignette. Whispers with no source. Rare hallucinated entity at 30+ blocks that vanishes when looked at directly. |
| = 100 | "Breakdown": once per anchor, a 6s forced sequence — the player crouches, screen shakes, layered audio of every death cause they've suffered, then ends. Grants **Resolve**: despair → 40, phantom pain cleared, +25% damage for 10 in-game minutes. Rock bottom is where he gets up. |

Despair **must** be escapable through play, not through waiting. The Breakdown is the release valve and is the emotional climax of a bad run.

### 9.3 Death Ledger

Every death recorded forever: `{ index, cause, killerName, pos, totalHours, depth, anchorId, miasmaAtDeath }`.

Used by:
- **Death Recall:** entering within 10 blocks of a past death site → 2s desaturated flashback overlay of the cause, +15% weapon sway for 20s. The world becomes a map of your failures.
- **Echidna's dialogue** (she reads it aloud, and she is *delighted*).
- `/rbd ledger` — a paginated GUI listing every death. Purely for the player's own horror.
- Achievement-flavored whispers at 10 / 25 / 50 / 100 deaths.

---

## 10. CORE SYSTEM 7 — Echidna's Tea Party

Canon: the Witch of Greed pulls Subaru into a dream between deaths, in a white field, with tea brewed from his experiences, and she is the only being he can speak to freely.

### 10.1 Trigger

- During the VOID stage, if `deathsAtThisAnchor >= cfg.teaPartyDeathThreshold` (default **4**) **or** `despair >= 80`, and not already used at this anchor → roll `teaPartyChance` (default 1.0 the first time, 0.35 thereafter).
- Hard limit: once per anchor, minimum 20 real minutes between occurrences.

### 10.2 The dimension

Preferred: a real custom dimension (**verify** VS's dimension support for the target version; 1.20+ has multi-dimension support used for micro-blocks/blueprints). Fallback: a reserved coordinate island at `y = 240` several hundred thousand blocks from spawn, generated once on first use, with the player's previous position stashed.

**Contents:**
- A flat field of white flowers to the horizon (custom block, emissive, gentle sway), infinite white fog beyond 60 blocks, no sky, no sun — just a flat bright void.
- A single small round table with two chairs, a teapot, two cups.
- Gravity, hunger, temperature, damage, and the clock are all **disabled**. Time does not pass outside.
- The player's inventory is **hidden and inaccessible** (cosmetic; restored on exit).
- Ambient: a single sustained high string note, wind with no trees.

### 10.3 Echidna

Entity `shinimodori:echidna`: seated, white-haired, black dress, pale — an original model, not a traced one. Idle animation of pouring tea. She is never hostile and cannot be damaged (attacking her → she laughs; a line plays; nothing happens).

**Dialog GUI** (`TeaPartyDialog`), a custom `GuiDialog` styled as parchment with a black border. Nodes:

1. **Greeting** — varies by whether this is her first appearance, and she *always* quotes the ledger: *"Four times at this same point. Drowning, then the cold, then the wolves — twice. You are becoming a very good vintage."*
2. **Freedom to speak** — an explicit dialog line that tells the player, in-fiction, that the taboo has no power here. Then a free-text input box where the player can type anything about Return by Death, and she responds with a generated-from-templates reply referencing their real stats. **No grip. No punishment.** The relief must be palpable.
3. **The tea** — she offers a cup brewed from their deaths.
   - **Drink:** grants **"Witch's Knowledge"** — choose one:
     - *Clarity:* reveals the exact cause and position of the last death as a permanent map waypoint.
     - *Foresight:* the next 10 in-game minutes after arrival show hostile entities through walls (outline render).
     - *Endurance:* phantom pain skipped on the next return.
     - Cost: **+8 miasma** and a permanent `EchidnaDebt++`.
   - **Refuse:** she is *more* pleased. `EchidnaFavor++`. No mechanical reward, but at favor ≥ 3 she offers a genuinely better gift (reveals the *next* anchor's milestone condition).
4. **Ask about the Witch** — lore fragments unlocked progressively by death count. Never a full explanation.
5. **Leave** — she says something quietly devastating, the screen whites out, and the VOID resumes.

**Write her well.** Curious, amused, affectionate toward suffering, never cruel for cruelty's sake. She is interested in you the way a scientist is interested in an experiment she has grown fond of. At least **40 unique lines**, selected by ledger/stat context.

### 10.4 Other Witches *(stretch)*

Unlocked at total-death thresholds; each appears once, rarely, in place of Echidna:
- **Minerva** (Wrath, 25 deaths): furious that you're hurt, punches you, full heal + phantom pain cleared, launches you 15 blocks in the VOID. Absurd and perfect.
- **Typhon** (Pride, 40): asks *"Are you a bad person?"* — a yes/no prompt. Answering "yes" costs 30% max health for a day. Answering "no" costs nothing. She believes you either way.
- **Daphne** (Gluttony, 55): offers "food" that is definitely alive. Eating it grants immunity to hunger for a day and +20 miasma.
- **Sekhmet** (Sloth, 70): does nothing. Says almost nothing. Grants a passive that slows stability drain 15% permanently.
- **Carmilla** (Lust, 85): gives a "gift" that appears to be a buff and is a curse (double drops, double damage taken, 2 days).

---

## 11. CORE SYSTEM 8 — Authority of Envy: Invisible Providence

Late-game, canon-accurate, and deliberately terrifying to use.

- **Unlock:** total deaths ≥ `authorityDeathThreshold` (default **50**) *or* obtain a `witch-factor` from the Sloth boss.
- **Bind:** hold `V`. Shadow arms extend from the player's back — **rendered only on the owner's client** (canon: no one else can see them; other players see nothing at all).
- **Abilities** (single button, contextual by what you're aiming at):
  - Aiming at an entity ≤ 12 blocks: **grab & yank** — pulls it to you, 1.5s stun.
  - Aiming at a block/item ≤ 16 blocks: **retrieve** — teleports the item stack or breaks-and-collects the block.
  - Aiming at nothing while taking damage: **guard** — negates the next hit entirely.
- **Costs per use:** +5 miasma, −8% temporal stability, and a **12% chance of a free Taboo Stage-1 grip** (using her power draws her eye).
- **Overuse:** 5 uses within 2 in-game hours → forced Stage-2 grip. Do not let this become a normal tool.
- Visuals: 4–6 segmented black arms, purple-black gradient, subtle chromatic fringe, and an audio bed of muffled voices.

---

## 12. Presentation — visuals & audio

This is what "go all out" means. Each effect below is a spec, not a suggestion.

### 12.1 The five stage-effects

**(A) DYING — 0.4s**
Freeze the last rendered frame of the world. Desaturate to 20% over 0.4s. All audio cuts to silence in 120ms except a single 60 Hz sub-drop. Slight inward zoom (FOV −5°). No UI.

**(B) THE VOID — 3.0s (extendable)**
- Pure black. World rendering off entirely (draw a fullscreen opaque quad at the top render stage, or hide via a renderer at `EnumRenderStage.AfterPostProcessing` — **verify** the correct stage).
- 200–400 white motes, varying size, drifting **upward and slightly inward**, parallaxed in three depth layers, slowly. They are stars, and they are also her.
- At t=1.0s, a faint pale silhouette fades in at 6% opacity, center-left — female, indistinct, only readable as a shape. It never resolves. It fades at t=2.2s.
- Audio: a reversed whisper bed (an original recording of a phrase, reversed and pitch-shifted), a very slow heartbeat, and at t=2.0s, barely audible, a whispered *"…I love you."* (`sm_whisper_love.ogg`, −26 dB). Text version optional via config for players without audio.
- Input fully swallowed. Escape does nothing.

**(C) REWIND — 1.6s**
- The world fades back in **playing backwards**: implement as a shader pass with heavy horizontal scanline displacement, per-channel RGB offset oscillating, and 6–8 hard frame-tears per second.
- Overlay: a faint analog clock face, hands sweeping counterclockwise at increasing speed, at 12% opacity, centered.
- Flashes of the player's *own* death images from the ledger (the captured freeze-frames from stage A), 2 frames each, at random intervals.
- Audio: a rising reversed sweep terminating in a hard downbeat.

**(D) ARRIVAL — 2.5s**
- Hard cut to the world at the anchor. Screen at 180% brightness, blooming, decaying to normal over 1.2s.
- A gasp SFX (sharp intake of breath). Heartbeat at 150 bpm decaying to 70 over the full 2.5s.
- Heavy vignette closing to 60% then opening. Camera sway (a 2 Hz, 1.5° wobble) decaying to zero — the hands shaking.
- Muffled world audio (lowpass at 800 Hz) opening up over 1.5s.
- Input unlocked at t=0.8s, not at t=0 — the moment of helplessness matters.

**(E) TABOO GRIP** — see §7.3. This is the most expensive effect; give it the most polish.

### 12.2 Shader approach & fallbacks

Register a custom shader program via `capi.Shader.NewShaderProgram()` + `ShaderRegistry`, and a renderer via `capi.Event.RegisterRenderer(this, EnumRenderStage.AfterPostProcessing)` (**verify** stage names and how to sample the primary framebuffer in the current version).

Effects needing real shaders: desaturation, red-channel keying, chromatic aberration, scanline displacement, radial blur.

**Mandatory fallback path:** if shader compilation fails (log it, don't crash), degrade to composited fullscreen textured quads:
- desaturate → a gray quad at partial alpha with `blendmode: multiply`-ish approximation,
- chromatic aberration → skip,
- black hands → still work (they're just textured quads with animation),
- the Void → still works (it's a black quad + particles).

**The mod must remain fully playable with zero custom shaders.** Test this path by forcing failure.

### 12.3 Effect composition

`ScreenEffectStack` resolves overlapping effects by priority; higher priority fully suppresses lower:

```
100  Taboo Grip
 90  Return sequence (Void/Rewind/Arrival)
 70  Tea Party transition
 50  Breakdown (despair 100)
 30  Death Recall flashback
 20  Despair ambient (vignette/desaturation)
 10  Miasma ambient (aura, wisps)
```

### 12.4 Audio manifest (all original)

```
sm_void_whisper_bed.ogg        sm_whisper_love.ogg        sm_heartbeat_slow.ogg
sm_heartbeat_fast.ogg          sm_heartbeat_crush.ogg     sm_gasp.ogg
sm_rewind_sweep.ogg            sm_rewind_impact.ogg       sm_anchor_bell.ogg
sm_taboo_swell.ogg             sm_taboo_hands.ogg         sm_taboo_crunch.ogg
sm_miasma_ambient_loop.ogg     sm_mabeast_howl.ogg        sm_mabeast_growl.ogg
sm_teaparty_string.ogg         sm_teaparty_pour.ogg       sm_echidna_laugh.ogg
sm_despair_breath.ogg          sm_despair_whispers.ogg    sm_hands_extend.ogg
```

Production notes: heartbeats from a synthesized sine kick with a body thump; whispers recorded, reversed, pitch −4 semitones, heavy reverb, then reversed again for the "almost-language" effect; the crunch is celery. Duck all world audio via `AudioDirector` rather than stopping sounds individually.

### 12.5 Ambient dread (the small things that sell it)

- **Peripheral silhouette** (miasma ≥ 60): every ~20 in-game minutes, a 2-second, 10%-opacity figure at the far edge of the screen, in the direction the player is *not* facing. It is gone by the time the camera turns. No sound. No mechanical effect. Ever.
- **The whisper that isn't there** (despair ≥ 50): a single word of the player's own name at −30 dB, positioned behind them.
- **Reflections:** in still water, at high miasma, occasionally render a second figure standing behind the player's reflection *(stretch — check whether VS's water reflection pass permits it; skip if expensive).*
- **Candles/firepits gutter** when the player walks past at tier ≥ 3.

### 12.6 HUD (minimal by design)

Default `hudMode: "diegetic"` — **no HUD at all.** The player checks `/rbd status` or infers from the world.

`hudMode: "minimal"`: a small black hand glyph beside the temporal stability meter, which fills with darkness as miasma rises; a death counter appears only for 3 seconds after each arrival.

`hudMode: "full"`: explicit bars + counters + an anchor-age readout. For streamers and debugging.

---

## 13. Multiplayer

Three modes, `multiplayerMode`:

**`"soloReturner"` (default)** — Exactly one blessed player per world (enforced; additional blessings rejected). On their return, the **entire world** rewinds. Other players:
- are frozen with a 7.5s **black screen with no explanation** (they get the Void visuals *without* the whisper, the silhouette, or the text),
- have their own inventories/positions/stats rewound from the anchor snapshot,
- receive **no message** and have no memory of it. In-fiction, nothing happened.
- Config `nonReturnerMessage` can add a single line: *"You feel like you've lost your train of thought."*

**`"personalLoop"`** — Only the returner's own state and the blocks *they* changed are rewound. Other players and entities are untouched. Lower fidelity, drastically safer on public servers. Recommended default for any server with >4 players.

**`"everyoneReturns"`** — Like soloReturner but multiple blessed players are allowed; any blessed death rewinds the world; every blessed player keeps their memory. Chaotic, extremely fun with friends, and canon-adjacent (multiple Witch Factors exist).

**Implementation requirements:**
- All authority server-side. Clients render; they never decide.
- All packets `[ProtoContract]` over two channels: `shinimodori.state` (state sync, low frequency) and `shinimodori.fx` (effect cues, fire-and-forget).
- Packet list: `PktBlessing`, `PktReturnBegin{stage, durations, deathCause, seed}`, `PktStageAdvance`, `PktReturnComplete`, `PktMiasmaSync`, `PktDespairSync`, `PktTabooStage{stage}`, `PktTeaPartyEnter/Exit`, `PktDialogNode`, `PktDialogChoice` (C→S), `PktAuthorityCast` (C→S), `PktAmbientCue`.
- Never trust the client for: return triggering, taboo detection, ability costs, dialog outcomes.
- A returning player must be **immune to all damage** and unable to be interacted with for the duration.

---

## 14. Config

`ModConfig/Shinimodori.json`, loaded with `api.LoadModConfig<ShinimodoriConfig>()`, written back with defaults if absent. Ship **presets** that set whole bundles:

- **`"anime"`** — maximum fidelity: anchorFeedback `none`, tabooKillsListeners `true`, rewindKnowledge `false`, deathScars `true`, hudMode `diegetic`, miasma decay halved.
- **`"balanced"`** (default) — as documented throughout this plan.
- **`"forgiving"`** — anchor timeout 8h, miasma gain 5, phantom pain halved, taboo stage 3 does not kill, anchorFeedback `explicit`.
- **`"cinematic"`** — all FX at max, all penalties at minimum. For video capture.

Every single number named in this document must be a config field. Group them: `core`, `anchors`, `miasma`, `taboo`, `trauma`, `teaParty`, `authority`, `visuals`, `audio`, `multiplayer`, `debug`.

---

## 15. Canon fidelity matrix

Checklist. Every row must be demonstrably present in the shipped mod.

| # | Canon fact | Mechanic | §|
|---|---|---|---|
| 1 | Death returns Subaru to a checkpoint | Rewind engine | 4 |
| 2 | He does not choose the checkpoint | Anchor rules, no player control | 4.9 |
| 3 | Only he retains memory | Knowledge persists, world resets, other players wiped | 4.3, 13 |
| 4 | The checkpoint advances on story progress | Milestone-driven anchors | 4.9 |
| 5 | Physical state fully resets | Inventory/health/position rewound | 4.3 |
| 6 | He hears "I love you" as he dies | Void whisper | 12.1B |
| 7 | The void / floating sensation between | The Void stage | 12.1B |
| 8 | The Witch's scent clings and grows | Miasma stat | 6.1 |
| 9 | Mabeasts hunt the scent | Ulgarm packs, aggro multipliers | 6.3–6.4 |
| 10 | Sensitives recoil; some refuse him | Traders refuse at tier 3 | 6.2 |
| 11 | The Witch Cult worships the scent | Cultist spawns | 6.5 |
| 12 | Telling anyone is forbidden | Taboo detection | 7.2 |
| 13 | Time stops and the world goes gray | Grip stages 1–2 | 7.3 |
| 14 | Black hands emerge | Hands renderer | 7.3 |
| 15 | A hand crushes his heart | Health drain + crush effect | 7.3 |
| 16 | Persisting kills the listener | `tabooKillsListeners` | 7.3 |
| 17 | Nobody else witnesses any of it | Client-local FX, no broadcast | 7.3, 13 |
| 18 | Writing it down doesn't work either | Letters erase themselves | 7.2 |
| 19 | He keeps the trauma of every death | Phantom pain, despair, ledger | 9 |
| 20 | He breaks down, then gets back up | Breakdown → Resolve | 9.2 |
| 21 | He can choose to die | Voluntary return | 5.4 |
| 22 | Echidna's tea party between deaths | Tea Party dimension | 10 |
| 23 | Tea is brewed from his experiences | Death-count-driven buffs | 10.3 |
| 24 | He can speak freely to a Witch | No grip inside the dream | 10.3 |
| 25 | Other Witches appear | Minerva/Typhon/Daphne/Sekhmet/Carmilla | 10.4 |
| 26 | He briefly wields the Authority of Envy | Invisible Providence | 11 |
| 27 | Using her power draws her attention | Authority grip chance | 11 |
| 28 | Foreknowledge itself is permitted | Detector must not fire on predictions | 7.2 |
| 29 | Repeated death warps him and the world | Death scars / permanent rifts | 8 |
| 30 | He arrives with no explanation | The isekai cold open | 3 |

---

## 16. Implementation phases

### Phase 0 — Skeleton (½ day)
Project builds, loads, logs. `ShinimodoriModSystem` registers client/server systems, config loads/saves, network channels open, `/rbd status` prints stub data, lang file wired.
**Accept:** mod appears in the mod list; `/rbd status` works in SP and on a dedicated server.

### Phase 1 — Rewind engine, headless (2–3 days) ⚠️ hardest
`ReturnPoint`, journaling with copy-on-first-touch, persistence, `RewindEngine`, SafeMode, `/rbd anchor`, `/rbd return`, `/rbd journal stats`, `/rbd verify`.
**Accept:**
- Set anchor → mine 500 blocks, place 300, empty a chest into your inventory, kill 5 animals, let a crop grow, sleep to change the time → `/rbd return` → world is *byte-identical* on a 64-sample verification, inventory restored, time restored, animals alive, chest refilled.
- Works after a server restart mid-session.
- Rewind of a 20k-delta journal completes under 2s.
- Forced-exception fuzz run always lands in SafeMode, never a crash.

### Phase 2 — Death interception & the return loop (1–2 days)
`EntityBehaviorReturner`, the Harmony `Die` patch, the state machine, placeholder FX (black screen + timers), death cause classification, ledger, blessing acquisition.
**Accept:** every death type (wolf, fall, drown, starve, freeze, lava, `/kill`, void) routes to a return; the vanilla death screen is **never** seen, not once, in 30 consecutive test deaths.

### Phase 3 — Presentation pass 1 (2–3 days)
Void, Rewind, Arrival renderers with real shaders + fallbacks; the full audio manifest; `ScreenEffectStack`; `AudioDirector`; the isekai cold open.
**Accept:** a fresh viewer who doesn't know the mod can watch a death and understand, without text, that something took them and put them back.

### Phase 4 — Miasma & world hostility (2 days)
Miasma stat & tiers, `ScentAggroBehavior`, trader refusal, Ulgarm entity + pack spawner, mabeast pelt cloak, temporal integration (§8), particles and aura.
**Accept:** at tier 4 a player is visibly hunted; at tier 0 the world behaves exactly like vanilla; the cloak measurably helps.

### Phase 5 — The Taboo (2 days)
Detector + scoring + regression corpus, three grip stages with full FX, book/sign erasure, listener death option.
**Accept:** the 80-phrase corpus passes 100%; stage 2 is genuinely unpleasant to sit through; other players observe absolutely nothing.

### Phase 6 — Trauma, despair, ledger (1–2 days)
Phantom pain, despair tiers, Breakdown/Resolve, Death Recall, ledger GUI, ambient dread effects.
**Accept:** a 10-death run at one anchor produces a visibly deteriorating experience that resolves cleanly on anchor advance.

### Phase 7 — Echidna & the Tea Party (2–3 days)
Dimension/fallback island, Echidna entity + model + animation, dialog GUI, 40+ context lines, tea buffs, favor/debt, free-speech input box.
**Accept:** triggering after 4 deaths at one anchor feels like relief; she correctly quotes real ledger data every time.

### Phase 8 — Authority of Envy (1–2 days)
Unlock, keybind, three contextual abilities, owner-only rendering, costs and overuse punishment.

### Phase 9 — Polish, compat, ship (2 days)
Multiplayer modes end-to-end on a dedicated server; presets; `README.md`; mod DB page copy; performance profiling (journaling must cost < 0.2 ms/tick); shader-fallback verification; 1-hour unattended stability soak.

### Stretch (post-1.0)
Witch Cult + Sloth boss; other Witches; Gluttony memory-eating (wipes revealed map regions / temporarily locks handbook entries); Rem-style NPC that recoils from the player; a "Gospel" book item that predicts one future event (an incoming storm, a mabeast pack) and then goes blank.

---

## 17. API verification checklist

**Sources to read before writing any code** (all official and public):
- `anegostudios/vsapi` — the API itself
- `anegostudios/vssurvivalmod` — how vanilla does behaviors, AI tasks, temporal stability, traders
- `anegostudios/vsessentialsmod` — entity AI, weather, particles
- `anegostudios/vsmodtemplate` — project scaffolding
- The official wiki (`wiki.vintagestory.at/Modding:*`) — noting that some pages are verified only against older versions and may lie.
- The `VintageStoryAPI` XML docs shipped with the game.

Confirm each of these against the installed version and record the real signature in `API_VERIFIED.md`:

```
[ ] ModSystem lifecycle: Start / StartPre / StartClientSide / StartServerSide / ExecuteOrder / Dispose
[ ] sapi.Event.PlayerDeath / PlayerRespawn / PlayerJoin / PlayerNowPlaying / PlayerDisconnect
[ ] sapi.Event.DidBreakBlock / BreakBlock / DidPlaceBlock / DidUseBlock / OnTrySpawnEntity
[ ] sapi.Event.OnEntitySpawn / OnEntityDeath / OnEntityDespawn / OnEntityLoaded
[ ] sapi.Event.SaveGameLoaded / GameWorldSave / ChunkColumnLoaded / ChunkColumnUnloaded
[ ] sapi.Event.EnqueueMainThreadTask / RegisterGameTickListener / RegisterCallback
[ ] sapi.WorldManager.SaveGame.StoreData / GetData / <world identifier property name>
[ ] sapi.WorldManager.LoadChunkColumnPriority(+callback) and its completion signal
[ ] sapi.GetOrCreateDataPath
[ ] IBlockAccessor vs IBulkBlockAccessor: SetBlock, Commit, MarkBlockDirty, relight behavior
[ ] BlockEntity.ToTreeAttributes / FromTreeAttributes; how to recreate a BE at a pos
[ ] Entity.ToBytes / FromBytes / WatchedAttributes / Attributes; spawning from a serialized tree
[ ] EntityBehavior.OnEntityReceiveDamage signature (ref float? DamageSource shape?)
[ ] EntityBehaviorHealth: Health/MaxHealth setters, how to modify max health safely
[ ] EntityPlayer.Die / Entity.Die signature for the Harmony prefix
[ ] How to suspend AI: EntityBehaviorTaskAI / AiTaskManager / EnumEntityState
[ ] How to inject an aggro target into AiTaskSeekEntity
[ ] IGameCalendar: TotalHours/TotalDays, and the SERVER-side method to set time backwards
[ ] Weather system: how to serialize/restore state (WeatherSystemServer?)
[ ] SystemTemporalStability: GetTemporalStability(pos), how to modify a player's stability
[ ] Rift system: ModSystemRifts? how to spawn/persist a rift at a position
[ ] Custom dimensions: are they available/usable for the Tea Party? If not, fallback island.
[ ] capi.Event.RegisterRenderer + EnumRenderStage values; which stage can sample the final frame
[ ] capi.Shader.NewShaderProgram / ShaderRegistry registration and asset path conventions
[ ] capi.Render primitives: RenderTexture, Render2DTexture, mesh upload for a fullscreen quad
[ ] GuiDialog: custom composer, text input, how to open/close and capture keyboard
[ ] capi.Input.RegisterHotKey / SetHotKeyHandler
[ ] IServerNetworkChannel / IClientNetworkChannel registration + ProtoContract conventions
[ ] SimpleParticleProperties / AdvancedParticleProperties; spawning server-side vs client-side
[ ] api.World.PlaySoundAt overloads; per-sound volume/pitch control; how to duck ambient audio
[ ] api.LoadModConfig / StoreModConfig
[ ] Command registration API (the fluent chat-command builder in 1.19+)
[ ] Trader entity: how to block/deny trading conditionally
[ ] Player inventory enumeration: all inventory classes on IServerPlayer.InventoryManager
[ ] Waypoint/map systems (WorldMapManager, waypoint layer) — to EXCLUDE from rewind
```

If any of these does not exist as described, **adapt the design and note the change at the top of the relevant section of this file**, rather than silently working around it.

---

## 18. Commands

```
/rbd status                 current anchor age/reason, deaths (total & at anchor), miasma, despair, journal size
/rbd ledger [page]          opens the death ledger GUI
/rbd anchor                 [admin] force-set an anchor here
/rbd return                 [admin] force a return
/rbd journal stats|clear    [admin] journal diagnostics
/rbd verify                 [admin] run the post-rewind sampling verification now
/rbd miasma get|set <n>     [admin]
/rbd despair set <n>        [admin]
/rbd taboo test "<text>"    [admin] prints the scoring breakdown without triggering
/rbd taboo trigger <stage>  [admin] force a grip stage
/rbd teaparty               [admin] force the Tea Party
/rbd bless <player>         [admin]
/rbd fx <effectname>        [admin] play any client effect in isolation — build this in Phase 0, it pays for itself
/rbd preset <name>          [admin] apply a config preset
```

---

## 19. Risks & mitigations

| Risk | Mitigation |
|---|---|
| **Rewind desyncs the world** (ghost blocks, duped items, corrupted BEs) | Post-rewind verification sampling; SafeMode; never ship without the Phase-1 acceptance test passing 50 consecutive times |
| **Journal memory blowup** on long anchors | Copy-on-first-touch; hard caps (`maxDeltas` 200k, `maxJournalMB` 64); overflow forces a new anchor (lore-justified) |
| **Rewind takes too long → server timeout** | Chunked block pass across ticks while frozen; Void stage extends to cover; hard `rewindBudgetMs` beyond which SafeMode takes over |
| **Shader API changes between versions** | Full non-shader fallback path, tested |
| **Harmony patch breaks on update** | Patches isolated in one file, wrapped, with a non-patched fallback path and a loud log |
| **Taboo false positives ruin normal chat** | Scoring not keywords; regression corpus; OOC prefix; `tabooEnabled` toggle; log every trigger |
| **Multiplayer grief** (one player rewinding a shared base) | `personalLoop` mode; `soloReturner` enforcement; server owners get clear docs |
| **Item duplication across the rewind** | Despawn *all* EntityItems created since the anchor; clear inventories fully before deserializing; never merge |
| **Player quits mid-return** | On disconnect during a return, mark the player "mid-return" in savegame; complete the return on rejoin before control is handed over |
| **Other mods' custom deaths** | The `Die` prefix catches most; document a small public API (`IShinimodoriApi.TriggerReturn(player, cause)`) for mod compat |
| **Performance** | Journaling ≤ 0.2 ms/tick; entity drift sampling on a 10s timer, not per-tick; all renderers early-out when inactive |

---

## 20. Definition of done (v1.0)

- [ ] 100 consecutive deaths of assorted causes, in one world, with zero crashes, zero vanilla death screens, and zero world-state corruption.
- [ ] Every row of §15 demonstrably present.
- [ ] All four config presets load and visibly change the experience.
- [ ] Dedicated-server test with 3 clients in each of the three multiplayer modes.
- [ ] Runs with custom shaders forcibly disabled.
- [ ] Journal survives a server restart mid-anchor.
- [ ] A 60-second capture of a death → Void → Rewind → Arrival that looks like a cutscene, not a loading screen.
- [ ] `README.md` explaining nothing about the taboo. Let them find out.
