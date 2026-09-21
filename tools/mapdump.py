"""Dev aid: list the current floor's graph as the game holds it."""
import sys
IDX = int(sys.argv[1]) if len(sys.argv) > 1 else 0
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
b = pad.b
def batch(xs): return b.request("batch", UNIT.join(xs)).split(UNIT)
F = "{states.Dungeon}.playerposition"
print("player at", batch([F + ".x", F + ".y", F + ".type"]))
fl = None
for i in range(6):
    n = batch([f"{{states.Dungeon}}.floor[{i}].nodes.length", f"{{states.Dungeon}}.floor[{i}].depth"])
    print("floor", i, n)
cur = batch(["{Game}.getcurrentfloor()", "{Rules}.floor", "{states.Dungeon}.currentfloor"])
print("current floor guesses:", cur)
idx = IDX
N = f"{{states.Dungeon}}.floor[{idx}].nodes"
cnt = int(batch([N + ".length"])[0][2:])
for i in range(cnt):
    r = batch([f"{N}[{i}].{f}" for f in ("x", "y", "type", "visited", "connections.length", "item", "enemytemplate.name", "graphnodedata.type")])
    if r[2].endswith(":EMPTY") and r[4] == "i:0": continue
    print(i, [x.split(":", 2)[-1] if x[:2] in ("e:", "o:") else x for x in r])
