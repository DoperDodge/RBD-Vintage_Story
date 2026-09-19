# Return by Death

**死に戻り — Shinimodori**
A Re:Zero-inspired mod for **Vintage Story 1.22.x**.

---

You are the only person in the world who cannot die.

Every death drags you back to a checkpoint you did not choose, with your memories
intact and nothing else. Your inventory is gone. The blocks you broke are whole.
The animals you killed are grazing. The sun is back where it was.

The world does not remember.

You do.

---

## What it actually does

When something kills you, you never see the death screen. Lethal damage is
intercepted before the death state exists, the world is rolled back to an
invisible anchor, and you wake up there — hours of progress earlier, alive, and
the only one who knows it happened.

You do not place the anchor. It advances on its own terms, quietly, when you are
safe and have made real progress. Losing four hours to a bad decision is the
intended feeling.

Coming back is not free. Something clings to you afterwards, and it gets worse
the more you do it. Animals notice you sooner. Then they notice you from further
away. Then they come looking. Traders stop dealing with you. Eventually things
that are not wolves start arriving at night, in numbers, from directions you
were not watching.

The only way out is forward. Making progress advances the anchor, and advancing
the anchor cleans you off. Hope is, mechanically, the escape.

There is more. You will find it.

---

## Install

1. Vintage Story **1.22.0 or newer**.
2. Drop `shinimodori.zip` (or the built folder) into `VintagestoryData/Mods/`.
3. Start a world. Do not read anything else first.

The mod is **Universal** — required on both client and server. On a server,
every connected player needs it installed.

## Building from source

Needs the **.NET 10 SDK** and a Vintage Story installation.

```bash
export VINTAGE_STORY=/path/to/vintagestory      # folder with VintagestoryAPI.dll
dotnet build -c Release
```

The single output assembly is `bin/Release/Shinimodori.dll`. A mod folder is
that DLL plus `modinfo.json` and `assets/`.

To regenerate the procedural assets (all of them are generated, none are
shipped from anywhere else):

```bash
pip install numpy soundfile
python3 scripts/generate_sounds.py      # the audio manifest
python3 scripts/generate_textures.py    # the textures
```

To run the checks:

```bash
dotnet tests/TabooTests/bin/Release/net10.0/TabooTests.dll tests/TabooCorpus.json
dotnet tools/AssetCheck/bin/Release/net10.0/AssetCheck.dll assets
scripts/server_test.sh                  # boots a real dedicated server with the mod
```

---

## Configuration

`ModConfig/Shinimodori.json`, created on first run. Every number in the mod is in
there, grouped by system.

Four presets, set with `"Preset"` or `/rbd preset <name>`:

| Preset | For |
|---|---|
| `balanced` | The default. What the mod was tuned for. |
| `anime` | Maximum fidelity to the source. Unkind on purpose. |
| `forgiving` | Shorter anchor timeouts, gentler costs, softer consequences. |
| `cinematic` | All effects, almost no penalties. For recording. |

Setting `"Preset": "custom"` stops the preset bundle being re-applied and leaves
your edits alone.

### Multiplayer

Three modes, under `Multiplayer.Mode`:

- **`soloReturner`** (default) — exactly one blessed player per world. When they
  return, the whole world does. Everyone else gets a few seconds of black and no
  explanation, and their own state is rolled back with it. In fiction, nothing
  happened to them.
- **`personalLoop`** — only the returner is rolled back. Lower fidelity, far
  safer on a public server. **Recommended for anything above four players.**
- **`everyoneReturns`** — several blessed players, all of whom keep their
  memories. Chaotic. Very good with friends.

---

## Commands

```
/rbd status                 your anchor, your deaths, and what is on you
/rbd ledger [page]          every death you have ever died
/rbd anchor                 [admin] force an anchor here
/rbd return                 [admin] force a return
/rbd journal stats|clear    [admin] rollback diagnostics
/rbd verify                 [admin] how much a return would currently undo
/rbd miasma get|set <n>     [admin]
/rbd despair set <n>        [admin]
/rbd taboo test "<text>"    [admin] score text without triggering anything
/rbd taboo trigger <stage>  [admin]
/rbd teaparty               [admin]
/rbd bless <player> [bool]  [admin]
/rbd mabeasts               [admin] send a pack after yourself
/rbd fx <effect> [seconds]  [admin] play any client effect in isolation
/rbd preset <name>          [admin]
```

---

## Compatibility and limits

**Other mods.** Deaths that bypass the damage system are caught by a Harmony
prefix on `Entity.Die`, which covers `/kill`, the void, and other mods calling
`Die()` directly. Block changes are journaled at the engine's own write path, so
changes made by *any* mod are rolled back, not just vanilla ones.

**If a Harmony patch fails to apply**, the mod says so loudly in the log and
keeps running in a degraded mode rather than refusing to load. If the mod cannot
start at all, it tears itself down and the world behaves normally — a
half-initialised death-interception mod is more dangerous than a missing one.

**Known limits, stated plainly:**

- Block entities are captured within `CaptureRadius` of the anchor (96 blocks by
  default) plus anywhere you touched. A chest you never went near, 400 blocks
  away, that changed on its own, is not restored.
- The screen effects are drawn entirely with composited quads rather than
  framebuffer shaders. This is the plan's mandated no-shader path, promoted to
  being the only path: nothing can fail to compile, and it behaves identically on
  every GPU. The `Visuals.UseCustomShaders` config field is reserved for a future
  shader pass and currently does nothing.
- The mabeast uses the vanilla wolf rig at runtime with an original texture over
  it. No art from the base game is redistributed.
- Rendering and audio have not been verified on a running client in the
  environment this was built in — the server side has. See the pull request for
  exactly what was and was not tested.

**Performance.** Journaling is copy-on-first-touch: mining and replacing the same
block forty times costs one entry. Entity drift is sampled on a ten-second timer,
not per tick. Memory is bounded by `MaxDeltas` and `MaxJournalMB`; overflowing
either forces a new anchor rather than degrading the rollback.

**Your save.** The rollback has a verified fallback (`SafeMode`) that restores
only the player, never the world, and is reachable from every failure path. It
is the seatbelt. It has never been optional.

---

## What has actually been tested

Run against a real Vintage Story 1.22.7 dedicated server, not a mock:

```
/rbd selftest 20000
  engine reported 20040 block write(s); journal kept 20000 distinct position(s)
  rewind Success in 231ms — 20000 blocks (6 reconciled), 6 entities removed
  blocks: 20000/20000 restored exactly
  clock:  back to 872.00 (anchor 872.00, error 0.000h)
  PASS — the world is byte-identical at every touched position.

/rbd selftest 500 true          (journal handlers forced to throw)
  journal: integrity=LOST
  rewind SafeMode — player restored, world untouched
  PASS — forced journal failures landed in SafeMode, as designed
```

The 40-write gap between "engine reported" and "journal kept" is the test
deliberately touching 40 positions twice: copy-on-first-touch collapses them, as
it should.

The taboo corpus passes 80/80, and the server log is clean of mod errors at boot.

**Not tested:** anything needing a GPU or a sound device. The renderers, the GUI
dialogs, the hotkeys and the audio playback compile and are wired, but no client
has run them. Treat the presentation layer as unproven until you have played it.

---

## Credits

Built by DoperDodge, from a design document written for the purpose.

Inspired by *Re:Zero − Starting Life in Another World* by Tappei Nagatsuki. This
is an unofficial fan work and is not affiliated with or endorsed by the author,
Kadokawa, White Fox, or Anichi Studios. **No assets from the series are used.**
Every sound and texture in this mod is generated procedurally from the scripts in
`scripts/` — you can delete them all and rebuild them from nothing.

Vintage Story is by Anego Studios.

---

## License

MIT. See `LICENSE`.
