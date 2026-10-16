# Run Stats for Slay the Spire 2

Tracks what each player did over a whole run and shows it live in your browser:
Damage, Block, Prevented (enemy damage that never happened because you lowered their
Strength), Weak and Vulnerable, plus a chart per stat by floor. Works in singleplayer and
multiplayer. Only one person in the lobby needs it; everyone else plays unmodded and the
lobby stays compatible.

## Install

1. Copy the `RunStats` folder (`RunStats.dll` + `RunStats.json`) into the game's `mods` folder:
   - Windows: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`
   - macOS: `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`
2. Launch the game. The first time, go to Settings > Mods, enable mod loading, and restart.
3. During a run, open `http://localhost:8765` in any browser.

The mod only reads game state. It never changes gameplay and cannot cause desyncs.

## Your modded profile looks empty

Mods run on a separate copy of your save. If that copy shows no progression, close the
game and run the script for your OS, then relaunch:

- Windows: `powershell -ExecutionPolicy Bypass -File restore-modded-progress.ps1`
- macOS: `./restore-modded-progress.sh`

It copies your real progression into the modded profile. It never touches your real save,
only replaces the modded one when yours has more progress, and keeps the file it replaced
next to it. Running it when nothing is wrong does nothing.

## Where the data is

- Windows: `%APPDATA%\SlayTheSpire2\RunStats\`
- macOS: `~/Library/Application Support/SlayTheSpire2/RunStats/`

`current.json` is the live run; `runs/` keeps one file per run. Delete a run's file to
reset its totals.
