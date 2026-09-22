# Dev: from a character select with the wanted character focused (any pack), confirm, skip the
# cutscene, teleport onto the first enemy of floor 0 and start the fight.
# Reunion order seen: Thief, Jester, Warrior, ... Inventor last. Reaching the select: title ->
# Bonus Episodes -> Reunion -> Play (-> New Game if a run exists).
$env:PYTHONIOENCODING = 'utf-8'
$tools = $PSScriptRoot
python "$tools\dev.py" key Confirm | Out-Null; Start-Sleep 5
for ($i = 0; $i -lt 60; $i++) {
  $s = (python "$tools\bridge.py" eval "{haxegon.Scene}.scenelist[{haxegon.Scene}.currentscene]" | Select-Object -First 1)
  if ($s -match 'Dungeon') { break }
  python "$tools\dev.py" key Confirm | Out-Null; Start-Sleep -Milliseconds 900
}
"character: " + (python "$tools\bridge.py" eval "{Game}.player.name" | Select-Object -First 1)
$e = (python "$tools\mapdump.py" 0 2>&1 | Select-String "'ENEMY'" | Select-Object -First 1).Line
$n = ($e -split ' ')[0]
python "$tools\setf.py" "{states.Dungeon}" playerposition "{states.Dungeon}.floor[0].nodes[$n]" | Out-Null
Start-Sleep 7
python "$tools\dev.py" key Confirm | Out-Null
Start-Sleep 10
python "$tools\bridge.py" eval "{states.Combat}.state" | Select-Object -First 1
