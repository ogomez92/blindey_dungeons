#!/bin/sh
# Dev: stop everything, rebuild the companion, start the game on its own (so restarting the
# companion never takes the game with it), then start the companion, which attaches.
#   sh tools/relaunch.sh "character=Robot"
cd "$(dirname "$0")/.."
taskkill //F //IM DiceyA11y.exe >/dev/null 2>&1
taskkill //F //IM diceydungeons.exe >/dev/null 2>&1
sleep 2
dotnet build companion/DiceyA11y.csproj -c Release 2>&1 | grep -E "error|Errores" | head -10
if [ -n "$1" ]; then
  DICEYA11Y_NO_AUTOSTART=1 powershell -NoProfile -Command "Start-Process 'S:\steam\steamapps\common\Dicey Dungeons\diceydungeons.exe' -WorkingDirectory 'S:\steam\steamapps\common\Dicey Dungeons' -ArgumentList '$1'"
else
  DICEYA11Y_NO_AUTOSTART=1 powershell -NoProfile -Command "Start-Process 'S:\steam\steamapps\common\Dicey Dungeons\diceydungeons.exe' -WorkingDirectory 'S:\steam\steamapps\common\Dicey Dungeons'"
fi
sleep 9
cd companion/bin/Release/net9.0-windows/win-x64 && (./DiceyA11y.exe --dev >/dev/null 2>&1 &)
sleep 5
tasklist | grep -i -E "DiceyA11y|diceydungeons"
