# Dev: restart the game on its own with quickstart args, then the INSTALLED companion with --dev
# (the copy the player runs; deploy_companion.ps1 puts the build there).
#   .\tools\relaunch_installed.ps1 "character=Thief enemy=Frog"
param([string]$GameArgs = "", [string]$GameDir = "S:\steam\steamapps\common\Dicey Dungeons")
Get-Process DiceyA11y, diceydungeons -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
if ($GameArgs) { Start-Process (Join-Path $GameDir "diceydungeons.exe") -WorkingDirectory $GameDir -ArgumentList $GameArgs }
else { Start-Process (Join-Path $GameDir "diceydungeons.exe") -WorkingDirectory $GameDir }
Start-Sleep 9
Start-Process (Join-Path $GameDir "DiceyA11y\DiceyA11y.exe") -ArgumentList "--dev" -WorkingDirectory (Join-Path $GameDir "DiceyA11y")
Start-Sleep 7
Get-Process DiceyA11y, diceydungeons -ErrorAction SilentlyContinue | Select-Object Name, Id
