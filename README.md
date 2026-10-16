# sts2-run-stats

A Slay the Spire 2 mod that tracks per-player totals across a whole run and shows them
as a live table in your browser. Works in singleplayer and multiplayer, credits every
player (not just the host), and only one person in the lobby needs it installed.

| Column | What it counts |
|---|---|
| Damage | Unblocked damage dealt to enemies by the player or their pet (Osty). Poison and other sourceless ticks are not credited to anyone. |
| Block | Block gained from cards the player played (credited to the card's owner, so block given to an ally counts for the giver). |
| Weak / Vulnerable | Stacks applied to enemies. |
| Other debuffs | Stacks of any other debuff-type power applied to enemies (Poison, Frail, Strength down, ...). |

## Layout

| Path | Purpose |
|---|---|
| `mod/RunStats/` | The mod: .NET 9 class library, manifest, and the embedded viewer page |
| `mod/RunStats/src/RunStatsTracker.cs` | Subscribes to the game's combat history and aggregates per player |
| `mod/RunStats/src/StatsHttpServer.cs` | Serves the viewer on `http://localhost:8765` from inside the game |
| `mod/RunStats/src/StatsFile.cs` | Writes `RunStats/current.json` and per-run files under the game's user data dir |
| `mod/RunStats/index.html` | The browser table (polls `/stats.json` once a second) |
| `tools/` | `restore-modded-progress.sh` / `.ps1`: repair the modded save profile after a Steam Cloud collision |
| `notes/` | Research notes from the game's XML docs |
| `decompiled/`, `game-refs/`, `reference-mods/` | Local only, gitignored: decompiled game source, game DLLs, other mods studied |

## Build and install

Requires the .NET 9 SDK (`https://dot.net`). The build copies the mod straight into the
game's `mods/` folder.

```sh
cd mod/RunStats
dotnet build -c Release
```

If Steam is not in the default location, copy `local.props.example` to `local.props`
and set `STS2GamePath`.

Install locations the build writes to:

- macOS: `.../Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/RunStats/`
- Windows: `...\Slay the Spire 2\mods\RunStats\`

To install on a friend's machine without building, copy that `RunStats/` folder
(`RunStats.dll` + `RunStats.json`) into their `mods/` directory.

## First launch

The game refuses to load any mod until you accept the mods warning once: open
Settings, go to Mods, enable mod loading, then restart the game. The log should
then contain `RUNNING MODDED` and `[RunStats] viewer at http://localhost:8765/`.

## Modded profile shows no progression

The game keeps a separate `modded/` save profile and copies your real saves into it the
first time you play modded. If Steam Cloud already holds a modded profile from an earlier
attempt at mods, its sync overwrites that copy with the old, empty one (the cloud wins
whenever timestamps differ). Your unmodded saves are untouched; only the modded view
looks fresh. Fix it with the game closed:

- macOS: `tools/restore-modded-progress.sh`
- Windows: `powershell -ExecutionPolicy Bypass -File tools\restore-modded-progress.ps1`

Then relaunch with mods. The script copies your progression into the modded profile with
the timestamp the cloud expects, so the sync keeps it; the next save uploads it.

It will not destroy progress on either side: the unmodded file is never modified, the
modded file is only replaced when the unmodded one has strictly more progress (floors
climbed, then playtime), and whatever it replaces is kept next to it as
`progress.save.before-restore-<timestamp>`. If the modded profile is the one with more
progress, it says so and stops; `--force` (`-Force` on Windows) overrides that.

## Viewing

Open `http://localhost:8765` in any browser while the game is running. The JSON
behind it is also on disk if you want to do anything else with it:

- macOS: `~/Library/Application Support/SlayTheSpire2/RunStats/current.json`
- Windows: `%APPDATA%\SlayTheSpire2\RunStats\current.json`

Per-run files live next to it under `RunStats/runs/`, keyed by seed, game mode and the
players' net IDs. That file is the only place totals are kept: the mod re-reads it before
every update, so deleting it resets the run (even while the game is running), and
continuing a saved run picks up where the file left off. A restarted or reloaded fight
discards the in-progress portion and resumes from the last finished combat.

## How it works

Every client in a multiplayer run executes the full deterministic combat simulation,
and the game records each action in `CombatManager.Instance.History` (damage received,
block gained, power received, each tagged with the responsible creature). The mod only
subscribes to that history's `Changed` event, so it needs no Harmony patches and cannot
affect gameplay or cause desyncs. `affects_gameplay` is false in the manifest, which
also keeps the game from treating it as a mod mismatch between players.

## Regenerating the decompiled reference

```sh
export DOTNET_ROOT=~/.dotnet
dotnet tool install -g ilspycmd --version 9.1.0.7988
DOTNET_ROLL_FORWARD=Major ilspycmd -p -o decompiled game-refs/sts2.dll
```
