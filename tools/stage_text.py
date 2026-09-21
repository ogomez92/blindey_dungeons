"""Walks the game's Starling stage and prints every visible text field (dev aid)."""
import sys
sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from bridge import Bridge, UNIT

b = Bridge()

def fields(h):
    parts = b.request("dump", h).split(UNIT)
    d = {}
    for p in parts[1:]:
        k, _, v = p.partition("=")
        d[k] = v
    return parts[0], d

def walk(h, depth, ox, oy, out, stats):
    head, d = fields(h)
    cls = head.split(":", 2)[2] if head.count(":") >= 2 else head
    stats[cls] = stats.get(cls, 0) + 1
    if d.get("__visible", "b:1") == "b:0":
        return
    x = float(d.get("__x", "f:0")[2:] or 0); y = float(d.get("__y", "f:0")[2:] or 0)
    if d.get("__alpha", "i:1")[2:] in ("0", "0.0"):
        return
    if "__text" in d and d["__text"].startswith("s:"):
        out.append((oy + y, ox + x, cls, d["__text"][2:], d.get("__alpha")))
    kids = d.get("__children")
    if kids and kids.startswith("o:"):      # openfl Vector wrapping a plain array
        kids = b.request("eval", kids.split(":")[1] + ".__array")
    if kids and kids.startswith("a:"):
        kh = kids.split(":")[1]
        n = int(kids.split(":")[2])
        for i in range(n):
            r = b.request("eval", f"{kh}[{i}]")
            if r.startswith("o:"):
                walk(r.split(":")[1], depth + 1, ox + x, oy + y, out, stats)

out, stats = [], {}
root = b.request("eval", "{Screen}.stage")
walk(root.split(":")[1], 0, 0, 0, out, stats)
for y, x, cls, text, alpha in sorted(out):
    print(f"{y:7.0f} {x:7.0f}  {text}")
print(stats)
