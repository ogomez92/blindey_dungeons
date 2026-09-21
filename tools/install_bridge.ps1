# Installs (or removes) the bridge in the game folder.
#
# The bridge takes the place of the game's lime.ndll and forwards to the original,
# which is kept next to it as lime_orig.dll. Nothing else in the game is modified.
#
#   .\tools\install_bridge.ps1
#   .\tools\install_bridge.ps1 -Uninstall
#
# After a game update or a Steam "verify files", Steam puts its own lime.ndll back.
# Re-running this script notices that, refreshes lime_orig.dll from it and reinstalls.
param(
  [string]$GameDir = "",
  [string]$Bridge  = "",
  [switch]$Uninstall
)
$ErrorActionPreference = "Stop"

if (-not $GameDir) {
  # A release sits in <game>\DiceyA11y\tools; a checkout is somewhere else entirely.
  $installed = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
  $GameDir = if (Test-Path (Join-Path $installed "diceydungeons.exe")) { $installed }
             else { "S:\steam\steamapps\common\Dicey Dungeons" }
}

$lime = Join-Path $GameDir "lime.ndll"
$orig = Join-Path $GameDir "lime_orig.dll"
if (-not (Test-Path $lime)) { throw "lime.ndll not found under '$GameDir'. Pass -GameDir with the right path." }
if (Get-Process diceydungeons -ErrorAction SilentlyContinue) { throw "Dicey Dungeons is running. Close it first." }

# The bridge exports a marker symbol that no real Lime build has. Export names are
# plain ASCII in the file, so a byte search is enough. (An earlier version of this check
# looked for a string that was not actually in the binary, mistook the bridge for the
# original, and overwrote lime_orig.dll with the bridge. Hence the marker, and the
# refusal below to ever save something bridge-sized as "the original".)
function Test-IsBridge([string]$path) {
  $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($path))
  return $text.Contains("diceya11y_bridge_marker")
}

if ($Uninstall) {
  if ((Test-Path $orig) -and (Test-IsBridge $lime)) {
    Copy-Item $orig $lime -Force
    Remove-Item $orig
    Write-Host "Bridge removed; the original lime.ndll is back in place."
  } else {
    Write-Host "Bridge is not installed; nothing to do."
  }
  return
}

if (-not $Bridge) {
  $candidates = @(
    (Join-Path $PSScriptRoot "..\bridge\bin\lime.ndll"),
    (Join-Path $PSScriptRoot "..\bridge\lime.ndll")
  )
  $Bridge = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Bridge -or -not (Test-Path $Bridge)) { throw "Bridge build not found. Run bridge\build.ps1 first, or pass -Bridge." }

if (-not (Test-IsBridge $Bridge)) { throw "'$Bridge' is not a DiceyA11y bridge build (marker export missing)." }

if (-not (Test-IsBridge $lime)) {
  # What is in place is a genuine Lime (first install, or Steam restored it): keep it.
  if ((Get-Item $lime).Length -lt 3MB) { throw "lime.ndll is neither the bridge nor plausibly the real Lime (too small). Not touching anything; verify the game files in Steam." }
  Copy-Item $lime $orig -Force
  Write-Host "Saved the game's lime.ndll as lime_orig.dll"
} elseif (-not (Test-Path $orig)) {
  throw "lime.ndll is already the bridge but lime_orig.dll is missing. Use Steam's 'Verify integrity of game files', then run this again."
}

Copy-Item $Bridge $lime -Force
Write-Host "Bridge installed into $GameDir"
