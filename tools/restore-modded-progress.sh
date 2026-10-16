#!/bin/bash
# Restores progression into the modded save profile after Steam Cloud replaced it with an
# older, empty modded profile. Run with the game closed, then relaunch with mods.
#
# Safety rules:
#   - The unmodded source is never modified.
#   - The modded file is replaced only when the source has strictly more progress (more
#     floors climbed, or equal floors and more playtime). Pass --force to override.
#   - Whatever gets replaced is kept as progress.save.before-restore-<timestamp>.
#
# The game's cloud sync keeps the local file only when its modification time equals the
# cloud's, so the restored file is stamped with the time the replaced file already had.
set -euo pipefail

FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

if pgrep -f "SlayTheSpire2.app/Contents/MacOS" >/dev/null; then
  echo "Slay the Spire 2 is running. Quit it first, then rerun this script."; exit 1
fi

BASE="${STS2_SAVE_BASE:-$HOME/Library/Application Support/SlayTheSpire2/steam}"

field() { # field <file> <name>  -> integer value or 0
  grep -o "\"$2\": *[0-9]*" "$1" 2>/dev/null | head -1 | grep -o '[0-9]*$' || echo 0
}
valid_save() { [ -s "$1" ] && grep -q '"schema_version"' "$1"; }

restored=0; skipped=0
for user_dir in "$BASE"/*/; do
  for unmodded in "$user_dir"profile*/saves/progress.save; do
    [ -f "$unmodded" ] || continue
    profile=$(basename "$(dirname "$(dirname "$unmodded")")")
    modded="$user_dir""modded/$profile/saves/progress.save"

    if ! valid_save "$unmodded"; then
      echo "$profile: unmodded progress.save is empty or unreadable, leaving everything alone"; skipped=$((skipped+1)); continue
    fi
    if [ ! -f "$modded" ]; then
      echo "$profile: no modded copy yet; the game will create one on the next modded launch"; continue
    fi
    if cmp -s "$unmodded" "$modded"; then
      echo "$profile: modded progress already matches"; continue
    fi

    src_floors=$(field "$unmodded" floors_climbed); src_time=$(field "$unmodded" total_playtime)
    dst_floors=$(field "$modded" floors_climbed);   dst_time=$(field "$modded" total_playtime)
    echo "$profile: unmodded has $src_floors floors / ${src_time}s playtime, modded has $dst_floors floors / ${dst_time}s playtime"
    if [ "$FORCE" = 0 ] && { [ "$src_floors" -lt "$dst_floors" ] || { [ "$src_floors" -eq "$dst_floors" ] && [ "$src_time" -le "$dst_time" ]; }; }; then
      echo "$profile: modded profile has as much or more progress; NOT replacing it (rerun with --force to override)"
      skipped=$((skipped+1)); continue
    fi

    stamp=$(date +%Y%m%d-%H%M%S)
    keep="$modded.before-restore-$stamp"
    cp -p "$modded" "$keep"
    ref=$(mktemp); touch -r "$modded" "$ref"
    cp "$unmodded" "$modded.tmp" && mv "$modded.tmp" "$modded"
    touch -r "$ref" "$modded"; rm -f "$ref"
    if ! cmp -s "$unmodded" "$modded"; then
      echo "$profile: copy verification failed, restoring previous modded file"; cp -p "$keep" "$modded"; exit 1
    fi
    echo "$profile: restored $(stat -f %z "$unmodded") bytes into modded profile; previous file kept as $(basename "$keep")"
    restored=$((restored+1))
  done
done
echo "done: $restored restored, $skipped left alone. Relaunch the game with mods; the next progress save uploads the full version to the cloud."
