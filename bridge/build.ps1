# Builds the bridge (our replacement lime.ndll) with MSVC.
#
# The export forwarders are regenerated from the game's real lime.ndll on every build
# that can see the game, so a game update that adds or removes Lime primitives is picked
# up instead of breaking startup. The names are also written to lime_exports.txt, which
# is committed: a machine without the game (the release workflow) builds from that list.
# It holds export names only, no game code.
param(
  [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons",
  [string]$OutDir  = (Join-Path $PSScriptRoot "bin")
)
$ErrorActionPreference = "Stop"

# The exports we implement ourselves; everything else is forwarded.
$intercepted = @(
  "hx_set_loader",
  "lime_application_event_manager_register__prime",
  "lime_window_context_flip__prime"
)

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vcvars = $null
if (Test-Path $vswhere) {
  $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
  if ($vs) { $vcvars = Get-Item (Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat") -ErrorAction SilentlyContinue }
}
if (-not $vcvars) {
  $vcvars = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter vcvars64.bat -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $vcvars) { throw "MSVC (vcvars64.bat) not found." }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$exportList = Join-Path $PSScriptRoot "lime_exports.txt"

# The untouched original: lime_orig.dll once installed, lime.ndll before that.
# (Path.Combine, not Join-Path: Join-Path throws when the drive does not exist.)
$original = [System.IO.Path]::Combine($GameDir, "lime_orig.dll")
if (-not (Test-Path $original)) { $original = [System.IO.Path]::Combine($GameDir, "lime.ndll") }

if (Test-Path $original) {
  $exportsTxt = Join-Path $OutDir "exports.txt"
  cmd /c "`"$($vcvars.FullName)`" >nul && dumpbin /nologo /exports `"$original`" > `"$exportsTxt`""
  if ($LASTEXITCODE -ne 0) { throw "dumpbin failed." }

  $names = Get-Content $exportsTxt |
    ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]{8}\s+(\S+)') { $Matches[1] } }
  if ($names -contains "diceya11y_bridge_marker") { throw "'$original' is the bridge itself, not the game's Lime. Verify the game files in Steam." }
  $source = $original
} else {
  if (-not (Test-Path $exportList)) { throw "No lime.ndll found under '$GameDir' and no lime_exports.txt to build from." }
  $names = Get-Content $exportList | Where-Object { $_ -ne "" }
  $source = $exportList
  Write-Host "Game not found under '$GameDir'; using the committed export list."
}

if ($names.Count -lt 100) { throw "Only $($names.Count) exports read from $source - refusing to build a broken proxy." }
foreach ($i in $intercepted) { if ($names -notcontains $i) { throw "Original does not export '$i'; the hook design no longer fits this game version." } }

if ($source -eq $original) {
  # Only rewritten on a real change, so a rebuild does not dirty the working tree.
  $text = ($names -join "`n") + "`n"
  if (-not (Test-Path $exportList) -or [System.IO.File]::ReadAllText($exportList) -ne $text) {
    [System.IO.File]::WriteAllText($exportList, $text)
    Write-Host "lime_exports.txt updated - commit it."
  }
}

# Forwarders are emitted as linker pragmas in a generated header. Measured: link.exe
# rejects the same forwarders from a .def file, a response file and the command line
# ("unresolved external"), and accepts them only in this form.
$fwd = $names | Where-Object { $intercepted -notcontains $_ } |
       ForEach-Object { "#pragma comment(linker, `"/export:$_=lime_orig.$_`")" }
Set-Content (Join-Path $OutDir "forwards.h") $fwd -Encoding ASCII
Write-Host "forwards.h: $($fwd.Count) forwarded, $($intercepted.Count) intercepted"

$src = Join-Path $PSScriptRoot "bridge.cpp"
$dll = Join-Path $OutDir "lime.ndll"
$cl  = "cl /nologo /LD /O2 /MT /EHa /W3 /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /I`"$OutDir`" /Fo`"$OutDir\\`" `"$src`" /link /OUT:`"$dll`" /IMPLIB:`"$OutDir\bridge.lib`" kernel32.lib"
cmd /c "`"$($vcvars.FullName)`" >nul 2>&1 && $cl"
if ($LASTEXITCODE -ne 0) { throw "Compile failed." }
Write-Host "Built $dll"
