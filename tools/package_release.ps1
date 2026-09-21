# Builds the release folder and zip. Run locally or by .github/workflows/release.yml.
#
# The zip extracts into the GAME folder and adds exactly one directory there:
#
#   DiceyA11y\DiceyA11y.exe ...     the launcher, prism.dll and its licences
#   DiceyA11y\bridge\lime.ndll      the bridge; the installer (or the launcher itself, on
#                                   its next start) swaps it in for the game's lime.ndll
#   DiceyA11y\tools\install_bridge.ps1   manual install / uninstall
#
# It contains no game code or assets: the bridge forwards to the Lime already on the
# player's machine. Without the game on this machine the bridge is built from the
# committed bridge\lime_exports.txt (see bridge\build.ps1).
param(
  [string]$GameDir  = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$PrismDir = "C:\Users\Nitropc\code\libs\prism\prism-windows-x64",
  [string]$OutDir   = "",
  [string]$Version  = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ($OutDir -eq "") { $OutDir = Join-Path $root "release" }

$csproj = Join-Path $root "companion\DiceyA11y.csproj"
if ($Version -eq "") {
  $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "No <Version> in $csproj and no -Version given." }

$prismDll = Join-Path $PrismDir "dynamic\release\bin\prism.dll"
if (-not (Test-Path $prismDll)) { throw "prism.dll not found at $prismDll. Pass -PrismDir (the prism-windows-x64 folder of a Prism release)." }

Write-Host "== Building launcher $Version =="
dotnet publish $csproj -c Release -v minimal "-p:PrismDir=$PrismDir" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$publish = Join-Path $root "companion\bin\Release\net9.0-windows\win-x64\publish"
if (-not (Test-Path $publish)) { throw "Publish output not found at $publish" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$modDir = Join-Path $OutDir "DiceyA11y"
New-Item -ItemType Directory -Path $modDir -Force | Out-Null

Write-Host "== Collecting launcher =="
Copy-Item (Join-Path $publish "*") $modDir -Recurse -Force
# Logs and a previous config must never ship.
Get-ChildItem $modDir -Include *.log, DiceyA11y.cfg, *.pdb -Recurse -File |
  Remove-Item -Force -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $modDir "prism.dll"))) { throw "prism.dll did not reach the publish output." }

Write-Host "== Collecting Prism licences =="
$thirdParty = Join-Path $modDir "licenses\prism"
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
Copy-Item (Join-Path $PrismDir "NOTICE") $thirdParty -Force
Copy-Item (Join-Path $PrismDir "LICENSES") $thirdParty -Recurse -Force

Write-Host "== Building and collecting the bridge =="
& (Join-Path $root "bridge\build.ps1") -GameDir $GameDir | Select-Object -Last 2
$bridgeOut = Join-Path $modDir "bridge"
New-Item -ItemType Directory -Path $bridgeOut -Force | Out-Null
Copy-Item (Join-Path $root "bridge\bin\lime.ndll") $bridgeOut -Force

Write-Host "== Collecting docs and tools =="
Copy-Item (Join-Path $root "README.md") $modDir -Force
Copy-Item (Join-Path $root "LICENSE") $modDir -Force
if (Get-Command pandoc -ErrorAction SilentlyContinue) {
  pandoc -f gfm -t html -s --metadata title="Dicey Dungeons Accessibility Mod" (Join-Path $root "README.md") -o (Join-Path $modDir "readme.html")
  if ($LASTEXITCODE -ne 0) { throw "pandoc failed." }
} else {
  Write-Warning "pandoc not found: the zip will carry README.md but no readme.html."
}
New-Item -ItemType Directory -Path (Join-Path $modDir "tools") -Force | Out-Null
Copy-Item (Join-Path $root "tools\install_bridge.ps1") (Join-Path $modDir "tools") -Force

# ZipFile, not Compress-Archive: entry names must use forward slashes for the installer.
$zip = Join-Path $root "DiceyA11y-v$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $zip)

Write-Host "== Done =="
Write-Host "Folder: $OutDir"
Write-Host "Zip:    $zip"
