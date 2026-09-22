"""Dev aid: walk the battle card list through the companion's real key path and check it.

    python tools/cardlist.py            walk Right to the end, then Left to the start
    python tools/cardlist.py Right Right Down     press just these keys

After each key it prints the card the game's cursor is on, gamepad_preferredrow, and whether
the companion had to correct the game ("Card list:" lines in its log). Needs DiceyA11y --dev.
"""
import io
import sys
import time

sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
from bridge import Bridge  # noqa: E402

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
LOG = r"S:\steam\steamapps\common\Dicey Dungeons\DiceyA11y\diceya11y.log"
b = Bridge()


def ev(x):
    return b.request("eval", x)


def cards():
    n = int(ev("{Game}.player.equipment.length")[2:])
    out = []
    for i in range(n):
        p = f"{{Game}}.player.equipment[{i}]"
        out.append([ev(f"{p}.{f}") for f in ("name", "tx", "ty", "ready", "show", "skillcard", "displayrow", "displaycolumn")])
    return out


def cursor():
    s = "{states.Combat}.gamepad_selectedequipment"
    return f"{ev(s + '.name')} @{ev(s + '.tx')},{ev(s + '.ty')} row={ev('{states.Combat}.gamepad_preferredrow')} dicemode={ev('{states.Combat}.gamepad_dicemode')}"


def key(name):
    pipe = open(r"\\.\pipe\DiceyA11yDev", "r+b", buffering=0)
    pipe.write(f"key {name}\n".encode())
    pipe.readline()
    pipe.close()


def log_size():
    with open(LOG, "rb") as f:
        f.seek(0, 2)
        return f.tell()


def log_since(pos):
    with open(LOG, "rb") as f:
        f.seek(pos)
        text = f.read().decode("utf-8", "replace")
    return [l[:170] for l in text.splitlines() if "Slow poll" not in l]


print("layout", ev("{Game}.player.layout"))
for c in cards():
    print("  ", c)
print("start :", cursor())
keys = sys.argv[1:] or ["Right"] * 8 + ["Left"] * 8
for k in keys:
    pos = log_size()
    key(k)
    time.sleep(0.9)
    print(f"{k:6}:", cursor())
    for line in log_since(pos):
        print("        ", line)
