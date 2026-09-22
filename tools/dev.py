"""Drive the companion the way the keyboard does (needs DiceyA11y.exe --dev).

    python tools/dev.py key Right key Right key Up wait1.5 read context
    python tools/dev.py spoken 15          last 15 lines the mod spoke

Windows drops synthetic keystrokes to an unfocused window, so tests talk to the
companion's dev pipe instead; from there on it is the real key path.
"""
import io
import os
import sys
import time

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "companion", "bin", "Release",
                   "net9.0-windows", "win-x64", "diceya11y.log")


def spoken(n):
    with open(LOG, encoding="utf-8", errors="replace") as f:
        lines = [l.rstrip() for l in f if "Spoke:" in l or "Screen:" in l or "rror" in l]
    for l in lines[-n:]:
        print("   ", l[:12], l[14:])


def main():
    args = sys.argv[1:]
    pipe = None
    i = 0
    while i < len(args):
        a = args[i]
        if a.startswith("wait"):
            time.sleep(float(a[4:] or 1)); i += 1; continue
        if a == "spoken":
            n = int(args[i + 1]) if i + 1 < len(args) and args[i + 1].isdigit() else 12
            spoken(n); i += 2 if i + 1 < len(args) and args[i + 1].isdigit() else 1; continue
        single = a in ("peek",)
        if pipe is None:
            pipe = open(r"\\.\pipe\DiceyA11yDev", "r+b", buffering=0)
        pipe.write((a + "\n" if single else f"{a} {args[i + 1]}\n").encode("utf-8"))
        reply = b""
        while not reply.endswith(b"\n"):
            reply += pipe.read(4096)
        if a in ("read", "buffer", "cursor", "status", "review"):
            print(f"[{args[i + 1]}]", reply.decode("utf-8", "replace").strip())
        i += 1 if single else 2
        time.sleep(0.25)


main()
