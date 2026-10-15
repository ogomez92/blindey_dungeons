"""Dev: a fake controller inside the game, driven the way SDL's events drive a real one.

    python tools/fakepad.py connect            # Lime's Gamepad.__connect: the game adds a pad
    python tools/fakepad.py press A Down RT    # buttons, d-pad, triggers, stick directions
    python tools/fakepad.py chord Back A       # hold the first, tap the others, release
    python tools/fakepad.py disconnect

Names: A B X Y Back Start LS RS LB RB Up Down Left Right (buttons), LT RT (triggers),
LUp LDown LLeft LRight RUp RDown RLeft RRight (sticks). Each press is held 80 ms and followed
by a pause (--gap ms, default 500) so the mod can speak. The companion (with --dev or not)
takes the pad from the game as it would a real one; read diceya11y.log for what was said.
"""
import sys
import time
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
from bridge import Bridge

PAD_ID = 99
DEV = "{lime.ui.Gamepad}.devices.get(%d)" % PAD_ID
BUTTONS = {"A": 0, "B": 1, "X": 2, "Y": 3, "Back": 4, "Start": 6, "LS": 7, "RS": 8, "LB": 9, "RB": 10,
           "Up": 11, "Down": 12, "Left": 13, "Right": 14}
AXES = {"LT": (4, 1.0), "RT": (5, 1.0), "LUp": (1, -1.0), "LDown": (1, 1.0), "LLeft": (0, -1.0), "LRight": (0, 1.0),
        "RUp": (3, -1.0), "RDown": (3, 1.0), "RLeft": (2, -1.0), "RRight": (2, 1.0)}


def down(b, name):
    if name in BUTTONS:
        b.request("eval", f"{DEV}.onButtonDown.dispatch({BUTTONS[name]})")
    else:
        axis, value = AXES[name]
        b.request("eval", f"{DEV}.onAxisMove.dispatch({axis}, {value})")


def up(b, name):
    if name in BUTTONS:
        b.request("eval", f"{DEV}.onButtonUp.dispatch({BUTTONS[name]})")
    else:
        b.request("eval", f"{DEV}.onAxisMove.dispatch({AXES[name][0]}, 0.0)")


def main():
    args = sys.argv[1:]
    gap = 0.5
    if "--gap" in args:
        i = args.index("--gap")
        gap = int(args[i + 1]) / 1000
        del args[i:i + 2]
    b = Bridge()
    cmd, names = args[0], args[1:]
    if cmd == "connect":
        print(b.request("eval", "{lime.ui.Gamepad}.__connect(%d)" % PAD_ID))
    elif cmd == "disconnect":
        print(b.request("eval", "{lime.ui.Gamepad}.__disconnect(%d)" % PAD_ID))
    elif cmd == "press":
        for n in names:
            down(b, n)
            time.sleep(0.08)
            up(b, n)
            time.sleep(gap)
    elif cmd == "chord":
        down(b, names[0])
        time.sleep(0.1)
        for n in names[1:]:
            down(b, n)
            time.sleep(0.08)
            up(b, n)
            time.sleep(gap)
        up(b, names[0])
        time.sleep(gap)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
