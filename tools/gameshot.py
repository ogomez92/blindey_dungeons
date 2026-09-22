"""Save what the game is drawing as a PNG, from inside the game.

    python tools/gameshot.py [path.png]

Screen capture does not work on this game: CopyFromScreen needs the window uncovered, and
PrintWindow (even with PW_RENDERFULLCONTENT) and ordinary screenshot tools get a plain white
rectangle from its OpenGL surface. Lime can read its own frame back, though, so the bridge
asks the game to do it: Window.readPixels() -> Image.encode() (PNG) -> sys.io.File.saveBytes.
Works with the window covered or unfocused, and captures nothing but the game.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bridge import Bridge  # noqa: E402

path = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "gameshot.png").replace("\\", "/")
reply = Bridge().request("eval", '{sys.io.File}.saveBytes("%s", #0.parent.window.readPixels(null).encode(null, 90))' % path)
print(path if os.path.exists(path) else "failed: " + reply)
