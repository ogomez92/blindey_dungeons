"""Drive the game through the bridge with a virtual gamepad and print what is on screen.

    python tools/pad.py state            scene, focus, registered buttons, all stage text
    python tools/pad.py down a state     press buttons in order, then print the state
    python tools/pad.py buttons          just the registered navigation buttons

Button names: up down left right a b x y back start ls rs lb rb, or a number.
This is a developer aid for mapping screens; the player-facing logic is in companion/.
"""
import io
import sys
import time

sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
from bridge import Bridge, UNIT  # noqa: E402

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

BUTTONS = {"a": 0, "b": 1, "x": 2, "y": 3, "back": 4, "guide": 5, "start": 6, "ls": 7, "rs": 8,
           "lb": 9, "rb": 10, "up": 11, "down": 12, "left": 13, "right": 14}

b = Bridge()


def ev(x):
    return b.request("eval", x)


def pad():
    """Index of our virtual pad (the one with no Lime gamepad behind it), created on demand."""
    n = int(ev("{GamepadManager}.gamepads.length")[2:])
    for i in range(n):
        if ev(f"{{GamepadManager}}.gamepads[{i}].gamepad") == "n":
            return i
    ev("{GamepadManager}.gamepads.push({Type}.createInstance({GamepadState}, []))")
    return n


def press(btn, hold=0.06, settle=0.35):
    p = f"{{GamepadManager}}.gamepads[{pad()}]"
    ev(f"{p}.onButtonDown({btn})")
    time.sleep(hold)
    ev(f"{p}.onButtonUp({btn})")
    time.sleep(settle)


def scene():
    i = ev("{haxegon.Scene}.currentscene")[2:]
    return ev(f"{{haxegon.Scene}}.scenelist[{i}]").split(":", 2)[-1]


def buttons():
    n = int(ev("{ButtonGamepadNavigation}.buttonrecords.length")[2:])
    frame = ev("{ButtonGamepadNavigation}.frame")
    rows = []
    for i in range(n):
        r = b.request("batch", UNIT.join(f"{{ButtonGamepadNavigation}}.buttonrecords[{i}].{f}"
                                          for f in ("label", "x", "y", "frame", "priority"))).split(UNIT)
        rows.append(r)
    return frame, rows


def state():
    print("scene :", scene(), "| mode:", ev("{ControlMode}.mode").split(":")[-1])
    print("focus :", ev("{ButtonGamepadNavigation}.activebutton.label"))
    frame, rows = buttons()
    live = [r for r in rows if r[3] == frame or r[3] == "i:%d" % (int(frame[2:]) - 1)]
    print(f"buttons: {len(rows)} records, {len(live)} live this frame (nav frame {frame})")
    for r in live:
        print("   ", r[0][2:], r[1], r[2], "prio", r[4])
    print("text  :")
    items = []
    for it in b.request("stagetext", "{Screen}.stage").split(UNIT):
        if it.count(",") >= 4:
            x, y, _w, _h, t = it.split(",", 4)
            items.append((int(y), int(x), t))
    for y, x, t in sorted(items):
        print(f"    {x:5d},{y:5d}  {t}")


def main():
    for a in sys.argv[1:] or ["state"]:
        if a == "state":
            state()
        elif a == "buttons":
            frame, rows = buttons()
            for r in rows:
                print(r)
        elif a == "scene":
            print(scene())
        elif a.startswith("wait"):
            time.sleep(float(a[4:] or 1))
        else:
            press(BUTTONS[a] if a in BUTTONS else int(a))


main()
