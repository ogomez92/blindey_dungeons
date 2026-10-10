"""Dev aid: type arrow keys fast through the keyboard hook's own path and check where the
battle card cursor ends up (needs DiceyA11y.exe --dev).

    python tools/rapidkeys.py Right Right Right            40 ms held, 80 ms apart
    python tools/rapidkeys.py --hold 30 --gap 50 Right Right Left

Prints the card list, the card the cursor should end on, the card it did end on, and what
the mod said. "Skipped" cards (the bug players reported) show as an end card past the
expected one.
"""
import argparse
import io
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bridge import Bridge  # noqa: E402

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
LOG = os.environ.get("DICEY_LOG") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "companion", "bin",
                                                   "Release", "net9.0-windows", "win-x64", "diceya11y.log")
SEL = "{states.Combat}.gamepad_selectedequipment"


def num(v):
    return float(v.split(":", 1)[1]) if v[:2] in ("i:", "f:") else 0.0


def card_list(b):
    n = int(b.request("eval", "{Game}.player.equipment.length")[2:])
    out = []
    for i in range(n):
        p = f"{{Game}}.player.equipment[{i}]"
        name, tx, ty, ready, show = (b.request("eval", f"{p}.{f}") for f in ("name", "tx", "ty", "ready", "show"))
        if ready == "b:false" or show == "b:false":
            continue
        x, y = num(tx), num(ty)
        if -200 < x < 3840 and -200 < y < 2100:
            out.append((round(x), y, name[2:]))
    return [c[2] for c in sorted(out)], sorted(out)


def selected(b):
    return b.request("eval", SEL + ".name")[2:], num(b.request("eval", SEL + ".tx")), num(b.request("eval", SEL + ".ty"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hold", type=int, default=40)
    ap.add_argument("--gap", type=int, default=80, help="ms from one key's release to the next key's press")
    ap.add_argument("keys", nargs="+")
    a = ap.parse_args()

    b = Bridge()
    names, rows = card_list(b)
    here = selected(b)
    at = next((i for i, r in enumerate(rows) if r[2] == here[0] and abs(r[0] - here[1]) < 1 and abs(r[1] - here[2]) < 1), -1)
    want = at
    for k in a.keys:
        want = max(0, min(len(rows) - 1, want + (1 if k in ("Right", "Down") else -1)))
    print("cards:", " | ".join(names))
    print(f"start: {here[0]} ({at + 1} of {len(rows)}); expect to end on {rows[want][2]} ({want + 1})")

    with open(LOG, encoding="utf-8", errors="replace") as f:
        seen = sum(1 for _ in f)
    pipe = open(r"\\.\pipe\DiceyA11yDev", "r+b", buffering=0)
    t0 = time.time()
    for k in a.keys:
        pipe.write(f"press {k} {a.hold}\n".encode())
        reply = b""
        while not reply.endswith(b"\n"):
            reply += pipe.read(4096)
        time.sleep(a.gap / 1000)
    time.sleep(1.5 + 0.4 * len(a.keys))
    end = selected(b)
    got = next((i for i, r in enumerate(rows) if r[2] == end[0] and abs(r[0] - end[1]) < 1 and abs(r[1] - end[2]) < 1), -1)
    verdict = "OK" if got == want else "WRONG"
    print(f"end:   {end[0]} ({got + 1})  -> {verdict}   ({(time.time() - t0) * 1000:.0f} ms)")
    with open(LOG, encoding="utf-8", errors="replace") as f:
        for line in list(f)[seen:]:
            if "Spoke:" in line or "Card list" in line or "rror" in line:
                print("   ", line.rstrip()[:160])
    sys.exit(0 if verdict == "OK" else 1)


main()
