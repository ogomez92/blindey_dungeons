# One-shot: build the launcher, install the mod, and optionally start playing.
#
#   .\tools\build_and_deploy.ps1
#   .\tools\build_and_deploy.ps1 -Run
param(
  [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$PrismDir = "C:\Users\Nitropc\code\libs\prism\prism-windows-x64",
  [switch]$Run
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "== Building launcher =="
dotnet build (Join-Path $root "companion\DiceyA11y.csproj") -c Release -v minimal "-p:PrismDir=$PrismDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "== Installing mod =="
& (Join-Path $PSScriptRoot "deploy_mod.ps1") -GameDir $GameDir

$exe = Join-Path $root "companion\bin\Release\net9.0-windows\win-x64\DiceyA11y.exe"
Write-Host "== Ready =="
Write-Host "Launcher: $exe"

if ($Run) { & $exe }
