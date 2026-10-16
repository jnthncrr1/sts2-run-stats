#!/bin/bash
# Restores progression into the modded save profile after Steam Cloud replaced it with an
# older, empty modded profile. Run with the game closed, then relaunch with mods.
#
# The game's cloud sync keeps the local file only when its modification time equals the
# cloud's, so the restored file is stamped with the time the stale file already had.
set -euo pipefail

if pgrep -f "SlayTheSpire2.app/Contents/MacOS" >/dev/null; then
  echo "Slay the Spire 2 is running. Quit it first, then rerun this script."; exit 1
fi

BASE="$HOME/Library/Application Support/SlayTheSpire2/steam"
fixed=0
for user_dir in "$BASE"/*/; do
  for unmodded in "$user_dir"profile*/saves/progress.save; do
    [ -f "$unmodded" ] || continue
    profile=$(basename "$(dirname "$(dirname "$unmodded")")")
    modded="$user_dir""modded/$profile/saves/progress.save"
    if [ ! -f "$modded" ]; then
      echo "$profile: no modded copy yet; the game will create one on the next modded launch"; continue
    fi
    if cmp -s "$unmodded" "$modded"; then
      echo "$profile: modded progress already matches"; continue
    fi
    ref=$(mktemp); touch -r "$modded" "$ref"
    cp "$modded" "$modded.stale-cloud-copy"
    cp "$unmodded" "$modded"
    touch -r "$ref" "$modded"; rm -f "$ref"
    echo "$profile: restored $(stat -f %z "$unmodded") bytes into modded profile (stale copy kept as progress.save.stale-cloud-copy)"
    fixed=$((fixed+1))
  done
done
echo "done, $fixed profile(s) restored. Relaunch the game with mods; the next progress save uploads the full version to the cloud."
