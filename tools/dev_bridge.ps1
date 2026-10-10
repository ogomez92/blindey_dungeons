# Developer loop for the bridge: stop the game, rebuild, install, relaunch.
#
#   .\tools\dev_bridge.ps1                 rebuild + install + launch
#   .\tools\dev_bridge.ps1 -NoBuild        just relaunch
#   .\tools\dev_bridge.ps1 -GameArgs "mod=diceya11y character=Warrior enemy=Frog"
param(
  [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$GameArgs = "",
  [switch]$NoBuild,
  [switch]$NoLaunch
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Get-Process diceydungeons -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

if (-not $NoBuild) {
  & (Join-Path $root "bridge\build.ps1") -GameDir $GameDir | Select-Object -Last 3
  & (Join-Path $PSScriptRoot "install_bridge.ps1") -GameDir $GameDir
}

if (-not $NoLaunch) {
  $env:DICEYA11Y_NO_AUTOSTART = '1'   # the game's bridge must not start a companion of its own
  if ($GameArgs) { Start-Process (Join-Path $GameDir "diceydungeons.exe") -WorkingDirectory $GameDir -ArgumentList $GameArgs }
  else           { Start-Process (Join-Path $GameDir "diceydungeons.exe") -WorkingDirectory $GameDir }
  Write-Host "Game launched."
}
