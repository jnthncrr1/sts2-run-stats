# Restores progression into the modded save profile after Steam Cloud replaced it with an
# older, empty modded profile. Run with the game closed, then relaunch with mods.
#
# Safety rules:
#   - The unmodded source is never modified.
#   - The modded file is replaced only when the source has strictly more progress (more
#     floors climbed, or equal floors and more playtime). Pass -Force to override.
#   - Whatever gets replaced is kept as progress.save.before-restore-<timestamp>.
#
# The game's cloud sync keeps the local file only when its modification time equals the
# cloud's, so the restored file is stamped with the time the replaced file already had.
#
# Run from PowerShell:  powershell -ExecutionPolicy Bypass -File .\restore-modded-progress.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = "Stop"

if (Get-Process -Name "Slay the Spire 2" -ErrorAction SilentlyContinue) {
    Write-Host "Slay the Spire 2 is running. Quit it first, then rerun this script."; exit 1
}

function Read-Save($path) {
    try { $j = Get-Content -Raw $path | ConvertFrom-Json } catch { return $null }
    if ($null -eq $j.schema_version) { return $null }
    return $j
}
function Field($save, $name) { $v = $save.$name; if ($null -eq $v) { 0 } else { [long]$v } }

$base = Join-Path $env:APPDATA "SlayTheSpire2\steam"
$restored = 0; $skipped = 0
foreach ($userDir in Get-ChildItem -Directory $base) {
    foreach ($unmodded in Get-ChildItem -Path $userDir.FullName -Filter "progress.save" -Recurse -File |
             Where-Object { $_.FullName -notmatch "\\modded\\" }) {
        $profile = $unmodded.Directory.Parent.Name
        $modded = Join-Path $userDir.FullName "modded\$profile\saves\progress.save"

        $src = Read-Save $unmodded.FullName
        if ($null -eq $src) { Write-Host "${profile}: unmodded progress.save is empty or unreadable, leaving everything alone"; $skipped++; continue }
        if (-not (Test-Path $modded)) { Write-Host "${profile}: no modded copy yet; the game will create one on the next modded launch"; continue }
        if ((Get-FileHash $unmodded.FullName).Hash -eq (Get-FileHash $modded).Hash) { Write-Host "${profile}: modded progress already matches"; continue }

        $dst = Read-Save $modded
        $srcFloors = Field $src floors_climbed; $srcTime = Field $src total_playtime
        $dstFloors = if ($dst) { Field $dst floors_climbed } else { 0 }
        $dstTime   = if ($dst) { Field $dst total_playtime } else { 0 }
        Write-Host "${profile}: unmodded has $srcFloors floors / ${srcTime}s playtime, modded has $dstFloors floors / ${dstTime}s playtime"
        if (-not $Force -and ($srcFloors -lt $dstFloors -or ($srcFloors -eq $dstFloors -and $srcTime -le $dstTime))) {
            Write-Host "${profile}: modded profile has as much or more progress; NOT replacing it (rerun with -Force to override)"; $skipped++; continue
        }

        $stampName = "$modded.before-restore-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item $modded $stampName -Force
        $mtime = (Get-Item $modded).LastWriteTimeUtc
        Copy-Item $unmodded.FullName "$modded.tmp" -Force
        Move-Item "$modded.tmp" $modded -Force
        (Get-Item $modded).LastWriteTimeUtc = $mtime
        if ((Get-FileHash $unmodded.FullName).Hash -ne (Get-FileHash $modded).Hash) {
            Write-Host "${profile}: copy verification failed, restoring previous modded file"; Copy-Item $stampName $modded -Force; exit 1
        }
        Write-Host "${profile}: restored $($unmodded.Length) bytes into modded profile; previous file kept as $(Split-Path -Leaf $stampName)"
        $restored++
    }
}
Write-Host "done: $restored restored, $skipped left alone. Relaunch the game with mods; the next progress save uploads the full version to the cloud."
