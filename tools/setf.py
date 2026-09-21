"""Dev aid: set a field on a live object.  python tools/setf.py "{Game}.monster" hp 1"""
import sys
sys.path.insert(0, __file__.replace("\\", "/").rsplit("/", 1)[0])
from bridge import Bridge, UNIT
b = Bridge()
a = sys.argv[1:]
for i in range(0, len(a), 3):
    print(a[i], a[i + 1], "=", a[i + 2], "->", b.request("set", UNIT.join(a[i:i + 3])))
