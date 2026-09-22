"""Print every change of a few battle values for some seconds (default 12).

    python tools/watchstate.py 15 "{states.Combat}.state" "{states.Combat}.turn"
"""
import sys
import time

pipe = open(r"\\.\pipe\DiceyA11yBridge", "r+b", buffering=0)
seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 12
exprs = sys.argv[2:] or ["{states.Combat}.state", "{states.Combat}.turn"]
last = None
n = 0
start = time.time()
while time.time() - start < seconds:
    n += 1
    pipe.write((f"{n}\tbatch\t" + "\x1f".join(exprs) + "\n").encode("utf-8"))
    data = b""
    while not data.endswith(b"\n"):
        data += pipe.read(65536)
    value = data.decode("utf-8", "replace").strip().split("\t", 1)[1].replace("\x1f", " | ")
    if value != last:
        print(f"{time.time() - start:6.2f}  {value}")
        last = value
