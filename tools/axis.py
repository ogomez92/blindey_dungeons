"""Dev aid: push pad axes. python tools/axis.py 4=1 5=1 hold0.3  (sets, holds, then zeroes)"""
import sys, time
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
p = f"{{GamepadManager}}.gamepads[{pad.pad()}]"
hold = 0.3
sets = []
for a in ARGS:
    if a.startswith("hold"): hold = float(a[4:])
    else:
        k, v = a.split("="); sets.append((int(k), float(v)))
pad.b.request("batch", UNIT.join(f"{p}.onAxisMove({k}, {v})" for k, v in sets))
time.sleep(hold)
pad.b.request("batch", UNIT.join(f"{p}.onAxisMove({k}, 0.0)" for k, v in sets))
time.sleep(0.5)
