"""Which directions do paths on the map take? Counts the (dx, dy) of every connection on every floor.

    python tools/mapoffsets.py

Read-only. The map's keyboard navigation is designed around the answer.
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bridge import Bridge  # noqa: E402

b = Bridge()
paths = ["x", "y", "type", "connections.length"]
for i in range(6):
    paths += [f"connections[{i}].x", f"connections[{i}].y"]
counts = collections.Counter()
degrees = collections.Counter()
for floor in range(8):
    reply = b.request("pluck", "\x1f".join([f"{{states.Dungeon}}.floor[{floor}].nodes"] + paths))
    if not reply or reply.startswith("!"):
        continue
    for record in reply.split("\x1e"):
        cells = record.split("\x1f")
        if len(cells) != len(paths) or "INVISIBLE" in cells[2]:
            continue
        num = lambda c: float(c.split(":", 1)[1]) if c[:2] in ("i:", "f:") else None
        x, y, n = num(cells[0]), num(cells[1]), num(cells[3])
        if x is None or n is None:
            continue
        degrees[int(n)] += 1
        for i in range(int(min(n, 6))):
            cx, cy = num(cells[4 + i * 2]), num(cells[5 + i * 2])
            if cx is not None:
                counts[(int(cx - x), int(cy - y))] += 1
print("connection offsets (dx, dy): count")
for offset, count in sorted(counts.items()):
    print("  ", offset, count)
print("connections per space:", dict(sorted(degrees.items())))
