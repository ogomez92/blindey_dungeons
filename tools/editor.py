"""Dev aid: progress editor state."""
import sys
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
P = "{states.ProgressEditor}"
def show(tag):
    r = pad.b.request("pluck", P + ".scorecard_diceicons" + UNIT + UNIT.join(["created", "_x", "_y", "_alpha"]))
    rows = [x.split(UNIT) for x in r.split("\x1e")]
    print(tag, "focus", pad.ev("{ButtonGamepadNavigation}.activebutton.label"), "confirm", pad.ev(P + ".resetconfirm"))
    for c in range(6):
        print("   ", c, [("%s@%s,%s" % (rows[c * 6 + e][0][2:], rows[c * 6 + e][1][2:], rows[c * 6 + e][2][2:])) for e in range(6)])
show("start")
for a in ARGS:
    pad.press(pad.BUTTONS[a], settle=0.8); show(a)
