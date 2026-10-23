# sts2-run-stats

A Slay the Spire 2 mod that tracks per-player totals across a whole run and shows them
as a live table in your browser, with a cumulative chart per stat by floor underneath.
Works in singleplayer and multiplayer, credits every player (not just the host), and only
one person in the lobby needs it installed.
Combat events are also recorded locally as append-only JSONL for later analysis.

| Column | What it counts |
|---|---|
| Damage | Damage dealt to enemies by the player or their pet (Osty), including what enemy block absorbed but not overkill; includes relics, potions, Thorns and similar. Self-damage (Offering) and damage to allies never count. Poison, Demise, Strangle and Haunt deal their damage with no dealer recorded; see below for how they are credited. |
| Block | Block gained, plus Osty HP from summons and heals. Credited to the player whose card or potion did it (so shielding or summoning for an ally counts for the giver); block from Plating is credited to whoever applied the Plating (so a Plating potion thrown on an ally counts for the thrower); other block or summons from a power or relic outside a card or potion go to the receiver or summoner. |
| Prevented | Enemy attack damage that did not happen because the player lowered the attacker's Strength (Piercing Wail, Dark Shackles, permanent Strength-down effects). Giving an enemy Strength counts negative, even if someone later cancels it. |
| Weak / Vulnerable | Stacks applied to enemies. |

Dealer-less damage is credited like this:

- **Strangle** is one instance per applier and fires when that applier plays a card, so it is
  credited exactly.
- **Haunt** sits on the player and fires on their own Soul play, so it is credited exactly.
- **Doom** kills outright (on its own or through End of Days), so there is no damage event at
  all. A read-only Harmony prefix on the Doom kill records the victim's HP just before it dies,
  and that HP is counted as damage split between the Doom owners in proportion to their stacks.
- **Poison and Demise** stacks from several players merge into one counter on the enemy. The
  mod keeps a per-fight ledger of how many stacks each player owns. A full tick pays every
  owner their stacks; a short tick (the enemy had less HP than stacks) is split in proportion
  to ownership. Poison's one-per-turn decrement is taken from whoever owns the most stacks
  (ties broken by lowest net ID). Stacks applied by monsters are not owned by anyone and
  their share of a tick is uncredited.

Prevented damage: every Strength change a player puts on an enemy goes into an ordered ledger
for that enemy (reductions positive, buffs negative), and temporary ones leave it when they
expire. For each enemy attack the
mod re-runs the game's damage pipeline with the reductions restored one at a time, oldest
first, and credits each player with the difference their reduction made. Once an attack is
already at zero, later reductions earn nothing. Weak and Vulnerable are not tracked this way
because their multiplicative effect has no equally clean attribution.

## Layout

| Path | Purpose |
|---|---|
| `mod/RunStats/` | The mod: .NET 9 class library, manifest, and the embedded viewer page |
| `mod/RunStats/src/RunStatsTracker.cs` | Subscribes to the game's combat history and aggregates per player |
| `mod/RunStats/src/StatsHttpServer.cs` | Serves the viewer on `http://localhost:8765` from inside the game |
| `mod/RunStats/src/StatsFile.cs` | Writes `RunStats/current.json` and per-run files under the game's user data dir |
| `mod/RunStats/src/` | Includes structured combat-event recording and an append-only journal independent of totals |
| `mod/RunStats/index.html` | The browser table and per-floor charts (polls `/stats.json` once a second) |
| `tools/` | `restore-modded-progress.sh` / `.ps1`: repair the modded save profile after a Steam Cloud collision |
| `dist/` | `README.md` for friends; `RunStats.zip` (gitignored) bundles it with the DLL, manifest and scripts |
| `notes/` | Research notes from the game's XML docs |
| `tests/RunStats.Tests/` | Offline checks for event payload coverage and journal storage |
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

Open `http://localhost:8765` in any browser while the game is running. The table shows
run totals; below it, once two combats have finished, one chart per column plots each
player's cumulative total by floor (hover for the floor, encounter and values). Colors
follow the character: Ironclad red, Silent green, Defect blue, Necrobinder purple, Regent
orange, with a different shade for each copy of the same character. The JSON behind it is
also on disk if you want to do anything else with it:

- macOS: `~/Library/Application Support/SlayTheSpire2/RunStats/current.json`
- Windows: `%APPDATA%\SlayTheSpire2\RunStats\current.json`

Per-run files live next to it under `RunStats/runs/`, keyed by seed, game mode and the
players' net IDs. That file is the only place totals are kept: the mod re-reads it before
every update, so deleting it resets the run (even while the game is running), and
continuing a saved run picks up where the file left off. A restarted or reloaded fight
discards the in-progress portion and resumes from the last finished combat. Each finished
combat also appends a snapshot of the totals to the file's `floors` list, which is what the
charts read.

## Event recordings

Combat observations are also saved under `RunStats/events/<runKey>/<sessionId>.jsonl`
in the same user data directory. Each line is a standalone JSON object, written immediately.
There is one file per run per mod session; continuing a run after restarting the game creates
another file in the same run directory. No event files are deleted automatically. Deleting a
run's aggregate JSON resets the dashboard totals but leaves its event recordings intact.

The log keeps one `CardPlayed` action per card-play resolution, with the player, card,
target and resource cost. A `PotionUsed` action provides the same source context for a potion.
Their effects are separate records linked by `actionId`: damage, block, power changes,
summons, card generation, discards, exhausts, afflictions, orb channeling and star changes.
Monster moves and targets are also recorded. Draws, play-start/play-finish records,
standalone card energy-spending records and duplicate attack summaries are omitted.
The mod's Doom and pet-healing observations, Strength-attribution credit on actual hits
and temporary Strength expiry are retained.
Actual damage results omitted by the game's history are observed before after-damage
triggers, so the fight-ending hit is included even when the game already considers combat
to be ending. A result already present in history is not recorded or counted again.
Run/combat/turn boundaries and creature-list changes include roster or creature snapshots,
with decks, relics, potions, HP, block, powers, hands, energy and stars for context.

Every line contains:

| Field | Meaning |
|---|---|
| `schemaVersion` | Event format version, currently `3` |
| `sessionId`, `sequence`, `recordedAt` | Recording session, increasing write order within that session, and UTC observation time |
| `runKey` | The same seed/mode/player key used for aggregate files |
| `combatAttemptId` | Unique ID for a combat attempt; absent outside combat |
| `context` | Floor, act, act floor, encounter, round and side at observation time |
| `type`, `source` | Event name and whether it is an `action`, `history` effect, `lifecycle` marker or supplemental `hook` |
| `actionId` | On an action and its linked effects; absent when no resolving action is known |
| `parentActionId` | On an action triggered inside another action, such as a nested auto-play |
| `data` | Structured event fields; history rows contain `historyIndex` and `payload` |

Player net IDs are strings to preserve all Steam ID digits in JavaScript and analysis tools.
Creatures carry combat IDs and model IDs; cards carry model IDs, upgrade levels, owner IDs
and a recording-local `instanceId` to distinguish copies. Card instance IDs are scoped to
the combat attempt (or the run-start snapshot outside combat), not portable deck IDs.
Damage payloads retain blocked, unblocked and overkill values separately, including
self-damage, friendly damage and dealer-less damage that the dashboard may exclude.
Power-change payloads retain the applied delta as `amount` and the total at observation
time as `observedPowerAmount`. The game's initial application is logged after setting the
total, while changes to an existing power are logged before updating it; the observed total
is therefore not uniformly a before- or after-value. In schema `1` recordings, the field
`amountBefore` holds this same observed value and must not be treated as a reliable pre-change total.

Count card plays using `type == "CardPlayed"`, not by counting every row. Join an action to
its effects by `actionId`. For example, Bash has one action row, a damage effect and a
Vulnerable effect sharing the same ID. Multi-hit and multi-target cards keep one effect
per damage instance, not duplicate play actions. Automatic plays and repeated resolutions
each have their own action ID; `isAutoPlay`, `playIndex` and `playCount` identify them.
Resource values belong to the resolution's play metadata; `resourceCostPaid` is true only
for the first resolution of a repeated play, so its cost must not be summed again for
later repetitions. Star-change effects are observed deltas, not additional resource costs.

Action rows are emitted as resolution begins, before before-play or before-potion triggers,
and are not confirmation that resolution completed. The execution context stays active
through after-play triggers and card cleanup, restores an enclosing action after nested
plays, and clears on completion, failure or cancellation. Explicit block-to-play references
can link to a completed action; a later Poison tick or other delayed effect without an explicit
source is not guessed to belong to the last card played. An `actionId` identifies resolving
execution context, not necessarily ownership of every triggered effect. State snapshots
still contain hands even though individual draw events are omitted.
Damage effects use `type == "DamageReceivedEntry"` with either `source == "history"` or
`source == "hook"`. Both contain `data.payload.result`; the hook form has
`data.captureMethod == "afterDamageGiven"` instead of a history index. Sum by event type,
not only by `source == "history"`, to include killing blows.

Retries and aborted fights remain in the log with distinct attempt IDs. A `CombatEnded`
marker identifies an ended attempt; `CombatWon` identifies a victory. An attempt without
an end marker is incomplete, rather than evidence that its events should be added to a
completed retry. History indices refer to the original game history, so intentional filtering
leaves gaps; they can also restart after a `HistoryCleared` marker. Use session/sequence to
order and identify individual records. Schema `1` and `2` logs can contain both
`CreatureAttackedEntry` and `DamageReceivedEntry`: these describe the same attacks and must
not be summed together as independent damage. Recordings from mod versions
`0.2.0` and `0.2.1` can contain `EnemyAttackDamageComputed` and `StrengthMitigationComputed`;
these include repeated intent-display calculations, not just executed attacks, and must be
excluded from action counts and damage totals. `StrengthMitigationCredited` reports only
the attribution consumed by an actual damage-history entry.
Recordings and totals from mod versions through `0.3.0` may omit the hit that ends a fight:
the game suppresses its history entry once the last primary enemy reaches zero HP. Agreement
between those logs and the dashboard does not establish complete damage capture. A final
card action without a damage effect in such a recording must not be assumed to have dealt zero.

This is an observation log, not a complete game replay. It does not record shop purchases,
map decisions, rewards, or every internal state mutation. For example, ordinary healing
and power removals are not combat-history entries; the supplemental heal observation covers
pets only. Hook observations such as pet healing and Doom kills run before the underlying
operation, not as confirmation that it completed. Creature state reflects observation time.
If a payload cannot be captured, its row contains `captureError`; unknown history types retain
their name and actor with `unsupportedType`. Disk errors are logged as warnings and may leave
gaps; logging failures do not prevent the existing totals tracker from running.

Offline checks (requires the local game DLL references under `game-refs/`):

```sh
dotnet run --project tests/RunStats.Tests/RunStats.Tests.csproj -c Release
```

## How it works

Every client in a multiplayer run executes the full deterministic combat simulation,
and the game records each action in `CombatManager.Instance.History` (damage received,
block gained, power received, each tagged with the responsible creature). The mod
subscribes to that history's `Changed` event, with supplemental Harmony hooks for Doom
kills, pet healing, potion use, the damage pipeline, temporary Strength expiry and
card-play execution context, plus actual damage results missing from history.
Card and potion wrapper tasks are observed through completion
to keep effect links active through their triggered effects; original failures and cancellation
propagate unchanged. Recording callbacks catch mod failures and log warnings.
Nothing the mod does changes game state, so it cannot affect gameplay or
cause desyncs. `affects_gameplay` is false in the manifest, which
also keeps the game from treating it as a mod mismatch between players.

## Regenerating the decompiled reference

```sh
export DOTNET_ROOT=~/.dotnet
dotnet tool install -g ilspycmd --version 9.1.0.7988
DOTNET_ROLL_FORWARD=Major ilspycmd -p -o decompiled game-refs/sts2.dll
```
