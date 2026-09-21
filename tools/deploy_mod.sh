#!/bin/bash
# Deploys the Polymod mod into the game's mods folder.
#
# flexible_generator.hx is a REPLACEMENT of a game file, so it is rebuilt here as
# "our prelude + the game's current copy". That way a game update to the generator
# is picked up automatically instead of being silently reverted by the mod.
set -e
GAME="${DICEY_GAME_DIR:-/s/steam/steamapps/common/Dicey Dungeons}"
SRC="$(cd "$(dirname "$0")/.." && pwd)/mod"
ID="${DICEY_MOD_ID:-diceya11y}"
DEST="$GAME/mods/$ID"

[ -d "$GAME/data/text" ] || { echo "ERROR: game data not found at $GAME" >&2; exit 1; }

rm -rf "$DEST"
mkdir -p "$DEST"
cp "$SRC/_polymod_meta.json" "$DEST/"
mkdir -p "$DEST/data/text/scripts/diceydungeons/a11y"
cp "$SRC/data/text/scripts/diceydungeons/a11y/"*.hx "$DEST/data/text/scripts/diceydungeons/a11y/"

ORIG="$GAME/data/text/scripts/diceydungeons/flexible_generator.hx"
[ -f "$ORIG" ] || { echo "ERROR: $ORIG missing" >&2; exit 1; }
cat "$SRC/_prelude_flexible_generator.hx" "$ORIG" \
  > "$DEST/data/text/scripts/diceydungeons/flexible_generator.hx"

echo "Deployed mod '$ID' to $DEST"
find "$DEST" -type f | sed "s|$DEST|  .|"
