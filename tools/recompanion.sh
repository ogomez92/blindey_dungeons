#!/bin/sh
# Dev loop for the companion: stop it, rebuild, start it again with --dev (it attaches to a running game).
cd "$(dirname "$0")/.."
taskkill //F //IM DiceyA11y.exe >/dev/null 2>&1
sleep 1
dotnet build companion/DiceyA11y.csproj -c Release 2>&1 | grep -E "error|Errores|Error\(s\)" | head -20
cd companion/bin/Release/net9.0-windows/win-x64 && (./DiceyA11y.exe --dev "$@" >/dev/null 2>&1 &)
sleep 4
tasklist | grep -i -E "DiceyA11y|diceydungeons"
