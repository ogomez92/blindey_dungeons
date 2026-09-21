"""Dev aid: keep pressing a button until the scene changes, logging stage text changes."""
import sys, time
btn = sys.argv[1] if len(sys.argv) > 1 else "a"
maxp = int(sys.argv[2]) if len(sys.argv) > 2 else 40
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
def texts():
    return [t.split(",", 4)[4] for t in pad.b.request("stagetext", "{Screen}.stage").split(UNIT) if t.count(",") >= 4]
start = pad.scene(); last = None
for step in range(maxp):
    for i in range(5):
        t = texts()
        if t != last and t: print("   ", t); last = t
        time.sleep(0.2)
    if pad.scene() != start: break
    pad.press(pad.BUTTONS[btn], settle=0.1)
print("scene now:", pad.scene())
