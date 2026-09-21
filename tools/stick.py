"""Dev aid: push the virtual left stick toward (dx,dy) for a moment and report the move."""
import sys, time, math
DX, DY, HOLD = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) > 3 else 0.35
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
P = "{states.Dungeon}.playerposition"
def pos():
    r = pad.b.request("batch", UNIT.join([P + ".x", P + ".y", P + ".type"])).split(UNIT)
    return r
n = math.hypot(DX, DY); ux, uy = DX / n, DY / n
g = f"{{GamepadManager}}.gamepads[{pad.pad()}]"
print("before", pos())
print(pad.b.request("batch", UNIT.join([f"{g}.onAxisMove(0, {ux:.4f})", f"{g}.onAxisMove(1, {uy:.4f})"])))
time.sleep(HOLD)
print(pad.b.request("batch", UNIT.join([f"{g}.onAxisMove(0, 0.0)", f"{g}.onAxisMove(1, 0.0)"])))
time.sleep(0.8)
print("after ", pos())
