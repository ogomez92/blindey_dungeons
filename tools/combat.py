"""Dev aid: print combat gamepad state; optional button presses first."""
import sys, time
ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad
from bridge import UNIT
def batch(xs): return pad.b.request("batch", UNIT.join(xs)).split(UNIT)
C = "{states.Combat}"
W = ["state", "turn", "gamepad_dicemode", "gamepad_buttonmode", "gamepad_selectedslot", "gamepad_pendingaction",
     "gamepad_dicerequesting", "gamepad_deckcolumn", "gamepad_preferredrow",
     "gamepad_selectedequipment.name", "gamepad_selecteddice.basevalue", "gamepad_hoverdice.basevalue", "gamepad_lastequipment.name",
     "showmessage", "preventfurtheractions"]
def show(tag=""):
    r = batch([f"{C}.{w}" for w in W])
    print(tag, {w.replace("gamepad_", "g_"): (v.split(":", 1)[1] if ":" in v and not v.startswith("!") else v) for w, v in zip(W, r)})
def dice():
    rows = pad.b.request("pluck", "{Game}.player.dicepool" + UNIT + UNIT.join(["basevalue", "modifier", "locked", "assigned.name", "x", "y", "available()"]))
    return [r.split(UNIT) for r in rows.split("")] if rows else []
def texts():
    return [t.split(",", 4)[4] for t in pad.b.request("stagetext", "{Screen}.stage").split(UNIT) if t.count(",") >= 4]
show("start")
for a in ARGS:
    if a == "text": print("   text:", texts()); continue
    if a == "dice": print("   dice:", dice()); continue
    if a.startswith("wait"): time.sleep(float(a[4:] or 1)); continue
    pad.press(pad.BUTTONS[a] if a in pad.BUTTONS else int(a), settle=0.5)
    show(a)
