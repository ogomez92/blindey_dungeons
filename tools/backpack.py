"""Dev aid: backpack cursor state after optional button presses."""
import sys, time
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
def batch(xs): return pad.b.request("batch", UNIT.join(xs)).split(UNIT)
B = "{states.Backpack}"
def show(tag):
    r = batch([B + ".gamepad_column", B + ".gamepad_row", B + ".gamepad_inbackpack", "{Game}.active_collectible.equipment.name", "{Game}.hot_collectible.equipment.name",
               B + ".hotslotx", B + ".hotsloty", B + ".dropcolumn", B + ".droprow"])
    print(tag, r)
show("start")
for a in ARGS:
    if a == "grid":
        print(pad.ev("{Inventory}.equipmentslots.toString()")); print(pad.ev("{Inventory}.backpack.toString()")); continue
    pad.press(pad.BUTTONS[a], settle=0.5); show(a)
