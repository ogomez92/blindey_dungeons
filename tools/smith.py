"""Dev aid: blacksmith cursor state after optional button presses."""
import sys
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
def batch(xs): return pad.b.request("batch", UNIT.join(xs)).split(UNIT)
B = "{states.Blacksmith}"
def show(tag):
    print(tag, batch([B + ".gamepad_column", B + ".gamepad_row", B + ".upgradefrom", B + ".regularversion.name", B + ".upgradedversion.name",
                      B + ".upgradedversion.namemodifier", B + ".numbought", B + ".upgradecost()", B + ".type"]), pad.scene())
show("start")
for a in ARGS:
    pad.press(pad.BUTTONS[a], settle=0.6); show(a)
