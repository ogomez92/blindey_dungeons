# Puts the dev build of the companion into the installed copy in the game folder
# (<game>\DiceyA11y), the one the player actually starts. Settings and logs there are kept.
#
#   .\tools\deploy_companion.ps1              publish + copy
#   .\tools\deploy_companion.ps1 -Bridge      also hand the launcher the dev bridge; it swaps
#                                             lime.ndll itself at its next start
#   .\tools\deploy_companion.ps1 -Run         start the installed launcher afterwards
param(
  [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$PrismDir = "C:\Users\Nitropc\code\libs\prism\prism-windows-x64",
  [switch]$Bridge,
  [switch]$KeepGame,
  [switch]$Run
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $GameDir "DiceyA11y"
if (-not (Test-Path (Join-Path $dest "DiceyA11y.exe"))) {
  throw "No installed mod at '$dest'. Install a release first; this script only refreshes one."
}

# The exe is locked while it runs, and it would take the game down with it anyway.
# -KeepGame leaves a game in progress alone: a killed launcher does not take it along, and the
# new one attaches to it (a player's run is not lost to a deploy).
$stop = if ($KeepGame) { @("DiceyA11y") } else { @("DiceyA11y", "diceydungeons") }
Get-Process $stop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

dotnet publish (Join-Path $root "companion\DiceyA11y.csproj") -c Release -v minimal "-p:PrismDir=$PrismDir" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
$publish = Join-Path $root "companion\bin\Release\net9.0-windows\win-x64\publish"

Get-ChildItem $publish -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
  Copy-Item $_.FullName $dest -Force
  "  " + $_.Name
}
Copy-Item (Join-Path $root "README.md") $dest -Force

if ($Bridge) {
  $built = Join-Path $root "bridge\bin\lime.ndll"
  if (-not (Test-Path $built)) { throw "No built bridge at $built. Run bridge\build.ps1 first." }
  Copy-Item $built (Join-Path $dest "bridge\lime.ndll") -Force
  "  bridge\lime.ndll"
}

Write-Host "Deployed to $dest"
if ($Run) { Start-Process (Join-Path $dest "DiceyA11y.exe") -WorkingDirectory $dest }
