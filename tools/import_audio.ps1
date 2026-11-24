# Turns downloaded sounds into the files this game plays.
#
# The game reads StreamingAssets/audio/<id>.wav at runtime (see docs/STATE.md, "Audio"). Downloaded packs
# are mp3/ogg/flac/aiff and often several per sound, so this converts each one to the one format the game
# reads - 16 bit PCM, 44100 Hz, mono - and names it after the sound it replaces.
#
#   .\import_audio.ps1 -Source C:\Users\me\Downloads\kenney-ui\Audio
#   .\import_audio.ps1 -Source .\picked -Map .\map.txt
#
# Without -Map every file is converted under its own name (knife-slice-01.mp3 -> knife-slice-01.wav).
# With -Map only the listed files are converted, renamed to the id on the right:
#
#   click   = ui_click_01.wav
#   equip   = gear_clank_02.flac
#   menu    = dark_ambient_loop.ogg
#
# Ids the game knows: click error move equip trash case heal shot death extract menu raid

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Map,
    [string]$Target,
    [string]$Ffmpeg = "ffmpeg"
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
if (-not $Target) { $Target = Join-Path $project "Assets\StreamingAssets\audio" }
$known = @("click", "error", "move", "equip", "trash", "case", "heal", "shot", "death", "extract", "menu", "raid")
$extensions = @(".wav", ".mp3", ".ogg", ".flac", ".aif", ".aiff", ".m4a")

if (-not (Test-Path $Source)) { throw "No such folder: $Source" }
New-Item -ItemType Directory -Force -Path $Target | Out-Null

function Convert-One([string]$file, [string]$id) {
    $destination = Join-Path $Target "$id.wav"
    # -vn: some packs carry a video thumbnail track. Mono because AudioDirector builds mono clips.
    & $Ffmpeg -hide_banner -loglevel error -y -i $file -vn -ac 1 -ar 44100 -c:a pcm_s16le $destination
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $file" }
    "  $([System.IO.Path]::GetFileName($file))  ->  $id.wav"
}

$done = @()
if ($Map) {
    if (-not (Test-Path $Map)) { throw "No such map file: $Map" }
    foreach ($line in Get-Content $Map) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) { continue }
        $parts = $trimmed -split "=", 2
        if ($parts.Count -ne 2) { throw "Each map line is '<id> = <file>': $line" }
        $id = $parts[0].Trim().ToLowerInvariant()
        $name = $parts[1].Trim()
        if ($known -notcontains $id) { throw "$id is not a sound the game knows ($($known -join ', '))" }
        $file = Join-Path $Source $name
        if (-not (Test-Path $file)) { throw "The map names a file that is not there: $name" }
        Convert-One $file $id
        $done += $id
    }
}
else {
    foreach ($file in Get-ChildItem $Source -File | Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() }) {
        $id = [System.IO.Path]::GetFileNameWithoutExtension($file.Name).ToLowerInvariant() -replace "[^a-z0-9]+", "-"
        Convert-One $file.FullName $id
        $done += $id
    }
}

$missing = $known | Where-Object { $done -notcontains $_ }
""
"Converted $($done.Count) file(s) into $Target"
if ($missing.Count -gt 0) {
    "Still on the built-in synth: $($missing -join ', ')"
}
else {
    "Every sound the game knows now has a file."
}
