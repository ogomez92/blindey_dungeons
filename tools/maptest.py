"""Dev aid: from the current map node, press each d-pad direction, record where it leads, walk back."""
import sys, time
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
P = "{states.Dungeon}.playerposition"
def pos():
    r = pad.b.request("batch", UNIT.join([P + ".x", P + ".y"])).split(UNIT)
    return (float(r[0][2:]), float(r[1][2:]))
def conns():
    rows = pad.b.request("pluck", P + ".connections" + UNIT + "x" + UNIT + "y" + UNIT + "type")
    return [tuple(c.split(UNIT)) for c in rows.split("\x1e")] if rows else []
here = pos()
print("at", here, "connections:", [(c[0][2:], c[1][2:], c[2].split(":")[-1]) for c in conns()])
for name in ("up", "right", "down", "left"):
    pad.press(pad.BUTTONS[name], settle=0.9)
    now = pos()
    print(f"  {name:6s} -> {now}  delta=({now[0]-here[0]:+.0f},{now[1]-here[1]:+.0f})")
    if now != here:
        # walk back: the reverse direction is unambiguous only sometimes, so try all until home
        for back in ("down", "left", "up", "right"):
            if pos() == here: break
            before = pos()
            pad.press(pad.BUTTONS[back], settle=0.9)
            if pos() != here and pos() != before:
                # wrong way: undo is not guaranteed; report and stop
                print("    could not return cleanly, now at", pos()); sys.exit()
