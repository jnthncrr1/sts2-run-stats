# Restores progression into the modded save profile after Steam Cloud replaced it with an
# older, empty modded profile. Run with the game closed, then relaunch with mods.
#
# The game's cloud sync keeps the local file only when its modification time equals the
# cloud's, so the restored file is stamped with the time the stale file already had.
#
# Run from PowerShell:  powershell -ExecutionPolicy Bypass -File .\restore-modded-progress.ps1
$ErrorActionPreference = "Stop"

if (Get-Process -Name "Slay the Spire 2" -ErrorAction SilentlyContinue) {
    Write-Host "Slay the Spire 2 is running. Quit it first, then rerun this script."; exit 1
}

$base = Join-Path $env:APPDATA "SlayTheSpire2\steam"
$fixed = 0
foreach ($userDir in Get-ChildItem -Directory $base) {
    foreach ($unmodded in Get-ChildItem -Path $userDir.FullName -Filter "progress.save" -Recurse -File |
             Where-Object { $_.FullName -notmatch "\\modded\\" }) {
        $profile = $unmodded.Directory.Parent.Name
        $modded = Join-Path $userDir.FullName "modded\$profile\saves\progress.save"
        if (-not (Test-Path $modded)) {
            Write-Host "${profile}: no modded copy yet; the game will create one on the next modded launch"; continue
        }
        if ((Get-FileHash $unmodded.FullName).Hash -eq (Get-FileHash $modded).Hash) {
            Write-Host "${profile}: modded progress already matches"; continue
        }
        $stamp = (Get-Item $modded).LastWriteTimeUtc
        Copy-Item $modded "$modded.stale-cloud-copy" -Force
        Copy-Item $unmodded.FullName $modded -Force
        (Get-Item $modded).LastWriteTimeUtc = $stamp
        Write-Host "${profile}: restored $($unmodded.Length) bytes into modded profile (stale copy kept as progress.save.stale-cloud-copy)"
        $fixed++
    }
}
Write-Host "done, $fixed profile(s) restored. Relaunch the game with mods; the next progress save uploads the full version to the cloud."
