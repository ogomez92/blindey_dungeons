"""Measure key-to-speech latency through the companion's dev pipe (needs DiceyA11y.exe --dev).

    python tools/latency.py Right Left Right Confirm Right Back

For each key: when it was sent, and every line the mod spoke in the following 1.5 s with
the delay since the key. The arrow-key lag in battle was found and fixed with this.
"""
import datetime
import io
import os
import sys
import time

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
LOG = os.environ.get("DICEY_LOG") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "companion", "bin", "Release",
                   "net9.0-windows", "win-x64", "diceya11y.log")


def stamp(line):
    try:
        h, m, s = line[:12].split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)
    except ValueError:
        return None


pipe = open(r"\\.\pipe\DiceyA11yDev", "r+b", buffering=0)
for key in sys.argv[1:]:
    with open(LOG, encoding="utf-8", errors="replace") as f:
        seen = sum(1 for _ in f)
    now = datetime.datetime.now()
    sent = now.hour * 3600 + now.minute * 60 + now.second + now.microsecond / 1e6
    pipe.write(f"key {key}\n".encode("utf-8"))
    reply = b""
    while not reply.endswith(b"\n"):
        reply += pipe.read(4096)
    time.sleep(1.5)
    print(f"{key}:")
    with open(LOG, encoding="utf-8", errors="replace") as f:
        for line in list(f)[seen:]:
            t = stamp(line)
            if t is not None and "Spoke:" in line:
                print(f"   +{(t - sent) * 1000:5.0f} ms  {line[14:].rstrip()}")
