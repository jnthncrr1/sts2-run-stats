#!/bin/bash
# Restores the modded-profile progress.save from the .backup the game left behind,
# keeping the mtime Steam Cloud expects so the startup sync keeps the local file.
set -e
S=~/Library/Application\ Support/SlayTheSpire2/steam/76561198025004890/modded/profile1/saves
while pgrep -f "Slay the Spire 2/SlayTheSpire2.app" >/dev/null; do sleep 2; done
echo "game exited at $(date +%H:%M:%S)"
REF=$(mktemp); touch -r "$S/progress.save" "$REF"
cp "$S/progress.save" "$S/progress.save.stale-cloud-copy"
cp "$S/progress.save.backup" "$S/progress.save"
touch -r "$REF" "$S/progress.save"; rm -f "$REF"
stat -f "%N  %z bytes  mtime %Sm" -t "%Y-%m-%d %H:%M:%S" "$S/progress.save" "$S/progress.save.stale-cloud-copy"
