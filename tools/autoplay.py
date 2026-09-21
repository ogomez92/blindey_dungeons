"""Dev aid: play far enough into a run to reach screens that need progress (shops, level-ups).

    python tools/autoplay.py goto STAIRS      walk to the nearest node of that type, fighting on the way
    python tools/autoplay.py fight            play out the current battle
    python tools/autoplay.py state            scene + focus + text

Walking uses the companion's own map keys through the dev pipe, so it exercises the real
navigation code; battles are played through the bridge's virtual pad.
"""
import io
import sys
import time

ARGS = sys.argv[1:]
sys.argv = ["pad.py", "scene"]
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
import pad  # noqa: E402
from bridge import UNIT  # noqa: E402

DEV = open(r"\\.\pipe\DiceyA11yDev", "r+b", buffering=0)


def dev(cmd):
    DEV.write((cmd + "\n").encode("utf-8"))
    reply = b""
    while not reply.endswith(b"\n"):
        reply += DEV.read(4096)
    return reply.decode("utf-8", "replace").strip()


def batch(xs):
    return pad.b.request("batch", UNIT.join(xs)).split(UNIT)


def fight():
    for _ in range(40):
        if pad.scene() != "states.Combat":
            return
        s = batch(["{states.Combat}.state", "{Game}.player.dicepool.length"])
        if s[0] != "s:playerturn:allocatedice":
            pad.press(0, settle=0.7)   # dismiss dialogue / wait
            continue
        free = [r for r in pad.b.request("pluck", "{Game}.player.dicepool" + UNIT + "assigned.name" + UNIT + "available()").split("\x1e") if r.startswith("!") or r.startswith("n")]
        if not free:
            pad.press(2, settle=3.0)   # X: end turn
            continue
        pad.press(0, settle=0.6)       # pick the highlighted card
        pad.press(0, settle=1.6)       # play the highlighted dice
        still = [r for r in pad.b.request("pluck", "{Game}.player.dicepool" + UNIT + "assigned.name" + UNIT + "available()").split("\x1e") if r.startswith("!") or r.startswith("n")]
        if len(still) == len(free):    # nothing was placed: this card cannot take a dice
            pad.press(1, settle=0.4)
            pad.press(2, settle=3.0)


def goto(kind):
    for _ in range(30):
        scene = pad.scene()
        if scene == "states.Combat":
            fight(); continue
        if scene != "states.Dungeon":
            pad.press(0, settle=1.2); continue    # rewards, level up, pickups: accept
        here = batch(["{states.Dungeon}.playerposition.type"])[0].split(":")[-1]
        if here == kind:
            return True
        if here == "ENEMY":
            pad.press(0, settle=6); continue
        # choose the target of this kind, then step
        for _ in range(12):
            dev("key Right"); time.sleep(0.2)
            ctx = batch(["{states.Dungeon}.playerposition.x"])
            last = open(LOG, encoding="utf-8", errors="replace").read().rsplit("Spoke: ", 1)[-1].lower()
            if WORDS[kind] in last:
                break
        dev("key Up"); time.sleep(1.6)
    return False


import os
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "companion", "bin", "Release", "net9.0-windows", "win-x64", "diceya11y.log")
WORDS = {"STAIRS": "salida", "SHOP": "tienda", "ITEM": "cofre", "ENEMY": "enemigo", "UPGRADE": "yunque", "HEALTH": "manzana"}

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace") if not isinstance(sys.stdout, io.TextIOWrapper) or sys.stdout.encoding.lower() != "utf-8" else sys.stdout
if ARGS[0] == "fight":
    fight()
elif ARGS[0] == "goto":
    print("reached" if goto(ARGS[1]) else "gave up", pad.scene())
elif ARGS[0] == "state":
    pad.state()
