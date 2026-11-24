# Cuts a seamless loop out of a long track and puts it where the game plays it.
#
# Post-rock and most AI-generated tracks are two to four minutes with a build; a game needs a lift that can
# repeat for an hour without a click, so this takes one steady stretch (start + length), blends its own head
# back over its tail, and writes 16 bit PCM mono 44100 Hz - the only thing AudioDirector reads.
#
#   .\make_loop.ps1 -Track "$HOME\Music\long-build.wav" -Start 92 -Length 75 -Id menu
#   .\make_loop.ps1 -Track .\raid.mp3 -Start 40 -Length 60 -Id raid -Fade 4
#
# -Start is where the steady part begins, -Length is what one pass of the loop should be, and -Fade is how
# much of that is spent blending the tail into the head. A longer fade hides a steadier pulse; two to four
# seconds suits a sustained post-rock section.

param(
    [Parameter(Mandatory = $true)][string]$Track,
    [Parameter(Mandatory = $true)][double]$Start,
    [double]$Length = 75,
    [double]$Fade = 3,
    [Parameter(Mandatory = $true)]
    [ValidateSet("click", "telegraph", "error", "move", "equip", "trash", "case", "heal", "shot", "death", "extract", "menu", "raid", "heartbeat")][string]$Id,
    [string]$Target,
    [string]$Ffmpeg = "ffmpeg"
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
if (-not $Target) { $Target = Join-Path $project "Assets\StreamingAssets\audio" }
if (-not (Test-Path $Track)) { throw "No such file: $Track" }
if ($Length -le $Fade) { throw "-Length has to be longer than -Fade." }
if ($Start -lt $Fade) { throw "-Start has to be at least -Fade seconds in, so there is music to blend from." }
New-Item -ItemType Directory -Force -Path $Target | Out-Null

$destination = Join-Path $Target "$Id.wav"
$end = $Start + $Length

# The loop is [Start, Start+Length]. Its tail is blended with the seconds *just before* Start, which is
# what makes the seam inaudible: the fade ends on the very material the loop begins with, so the file's
# last sample and its first are the same moment of the performance.
$filter = "[0:a]atrim=start=$Start`:end=$end,asetpts=N/SR/TB[body];" +
          "[0:a]atrim=start=$($Start - $Fade)`:end=$Start,asetpts=N/SR/TB[preroll];" +
          "[body][preroll]acrossfade=d=$Fade`:c1=tri:c2=tri[out]"

& $Ffmpeg -hide_banner -loglevel error -y -i $Track -filter_complex $filter -map "[out]" `
    -ac 1 -ar 44100 -c:a pcm_s16le $destination
if ($LASTEXITCODE -ne 0) { throw "ffmpeg could not make the loop." }

$size = [math]::Round((Get-Item $destination).Length / 1MB, 2)
"Wrote $destination ($size MB, about $Length s, last $Fade s blended into the head)"
"Set the bed's level on the SETTINGS screen; the built-in synth for $Id is not used any more."
