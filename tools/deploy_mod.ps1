# Installs the DiceyA11y Polymod mod into the game's mods folder.
#
# flexible_generator.hx is a REPLACEMENT of a game file, so it is rebuilt here as
# "our prelude + the game's current copy". A game update to the generator is then
# picked up automatically instead of being silently reverted by the mod.
param(
  [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$ModId   = "diceya11y"
)
$ErrorActionPreference = "Stop"

$src = Join-Path (Split-Path -Parent $PSScriptRoot) "mod"
if (-not (Test-Path (Join-Path $GameDir "data\text"))) {
  throw "Dicey Dungeons data folder not found under '$GameDir'. Pass -GameDir with the right path."
}

$dest = Join-Path $GameDir "mods\$ModId"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Path $dest -Force | Out-Null

Copy-Item (Join-Path $src "_polymod_meta.json") $dest

$scriptDest = Join-Path $dest "data\text\scripts\diceydungeons\a11y"
New-Item -ItemType Directory -Path $scriptDest -Force | Out-Null
Copy-Item (Join-Path $src "data\text\scripts\diceydungeons\a11y\*.hx") $scriptDest

$orig = Join-Path $GameDir "data\text\scripts\diceydungeons\flexible_generator.hx"
if (-not (Test-Path $orig)) { throw "Missing game file: $orig" }

$prelude = Get-Content (Join-Path $src "_prelude_flexible_generator.hx") -Raw
$body    = Get-Content $orig -Raw
Set-Content -Path (Join-Path $dest "data\text\scripts\diceydungeons\flexible_generator.hx") `
            -Value ($prelude + "`n" + $body) -Encoding UTF8

Write-Host "Deployed mod '$ModId' to $dest"
Get-ChildItem $dest -Recurse -File | ForEach-Object { "  " + $_.FullName.Substring($dest.Length) }
