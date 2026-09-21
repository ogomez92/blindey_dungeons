"""Dev aid: ViewSpellbook cursor state after optional button presses."""
import sys
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
V = "{states.ViewSpellbook}"
def show(tag):
    r = pad.b.request("batch", UNIT.join(V + "." + f for f in ["gamepad_slot", "selectedspellindex", "selectedspell.name", "selectedequipment.name", "displayspell.name", "hotspell.name", "previewupgraded", "precastequipment[0].name", "precastequipment[1].name"])).split(UNIT)
    print(tag, r, pad.scene())
show("start")
for a in ARGS:
    pad.press(pad.BUTTONS[a], settle=0.7); show(a)
