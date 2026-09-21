"""Developer client for the DiceyA11y bridge pipe.

    python tools/bridge.py eval "{haxegon.Scene}.currentscene"
    python tools/bridge.py dump "#0.parent"
    python tools/bridge.py statics "{states.Title}"
    python tools/bridge.py members "{displayobjects.Button}"
    python tools/bridge.py iter "#12.keys()"
    python tools/bridge.py tree "#0.parent" 2        # recursive dump, depth 2
    python tools/bridge.py probe "{states.Title}" words.txt   # find statics by trying names

Several commands may be chained with "--":  eval X -- dump Y
Handles (#n) are only valid while the game keeps running.
"""
import os
import sys
import time

PIPE = r"\\.\pipe\DiceyA11yBridge"
UNIT = "\x1f"


class Bridge:
    def __init__(self, timeout=10.0):
        deadline = time.time() + timeout
        while True:
            try:
                self.f = open(PIPE, "r+b", buffering=0)
                break
            except OSError:
                if time.time() > deadline:
                    raise SystemExit("bridge pipe not available - is the game running with the bridge installed?")
                time.sleep(0.25)
        self.n = 0
        self.buf = b""

    def request(self, cmd, arg="", extra=None):
        self.n += 1
        tail = "" if extra is None else "\t" + extra
        self.f.write(f"{self.n}\t{cmd}\t{arg}{tail}\n".encode("utf-8"))
        while b"\n" not in self.buf:
            chunk = self.f.read(65536)
            if not chunk:
                raise SystemExit("bridge closed the pipe")
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        _id, _, result = line.decode("utf-8", "replace").partition("\t")
        return result


def show(result):
    for part in result.split(UNIT):
        print(part)


def handle_of(repr_):
    if repr_[:2] in ("o:", "a:", "c:"):
        return repr_.split(":")[1]
    return None


def tree(b, expr, depth, indent=0, seen=None):
    seen = seen if seen is not None else set()
    parts = b.request("dump", expr).split(UNIT)
    print(" " * indent + parts[0])
    if depth <= 0:
        return
    for p in parts[1:]:
        name, _, val = p.partition("=")
        print(" " * (indent + 2) + p)
        h = handle_of(val)
        cls = val.split(":", 2)[2] if val.startswith("o:") else ""
        if h and depth > 1 and cls not in seen:
            seen.add(cls)
            tree(b, h, depth - 1, indent + 4, seen)


def main():
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    b = Bridge()
    group = []
    for a in args + ["--"]:
        if a != "--":
            group.append(a)
            continue
        if not group:
            continue
        cmd, rest = group[0], group[1:]
        if cmd == "probe":
            # probe <expr> [wordlist]  - statics cannot be listed in this build; try names.
            words = rest[1] if len(rest) > 1 else os.environ.get("DICEY_WORDS", "words.txt")
            parts = b.request("probe", rest[0], os.path.abspath(words)).split(UNIT)
            fns = [x[:-3] for x in parts[1:] if x.endswith("=fn")]
            for x in parts:
                if not x.endswith("=fn"):
                    print(x)
            if fns:
                print("functions:", " ".join(fns))
        elif cmd == "tree":
            tree(b, rest[0], int(rest[1]) if len(rest) > 1 else 2)
        else:
            show(b.request(cmd, rest[0] if rest else ""))
        print("--")
        group = []


if __name__ == "__main__":
    main()
