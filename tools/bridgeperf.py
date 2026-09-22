"""How long the bridge takes to answer: 30 requests one after another, then 30 sent together.

The bridge answers on the game's frame tick, so sequential requests cost a frame each and
pipelined ones share a frame. An unfocused or minimised game ticks slower.
"""
import time

pipe = open(r"\\.\pipe\DiceyA11yBridge", "r+b", buffering=0)


def read_lines(n):
    data = b""
    while data.count(b"\n") < n:
        data += pipe.read(65536)


start = time.time()
for i in range(30):
    pipe.write(f"{i}\teval\t{{states.Combat}}.state\n".encode())
    read_lines(1)
print("30 sequential evals: %.0f ms" % ((time.time() - start) * 1000))

start = time.time()
for i in range(100, 130):
    pipe.write(f"{i}\teval\t{{states.Combat}}.state\n".encode())
read_lines(30)
print("30 pipelined evals:  %.0f ms" % ((time.time() - start) * 1000))
