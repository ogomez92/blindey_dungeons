#!/bin/sh
# Dev: from anywhere in a run, go to the title and move <n> contestants from the remembered one (negative = left) and pick episode <index> (0-based),
# checking focus by name before each Enter.  sh tools/newrun.sh 2 4
cd "$(dirname "$0")/.."
export PYTHONIOENCODING=utf-8
d() { timeout 60 python tools/dev.py "$@"; }
scene() { timeout 20 python tools/pad.py scene | tail -1; }
if [ "$(scene)" != "states.Title" ]; then
  d key Start wait2.5 >/dev/null
  for i in 1 2 3 4; do f=$(d read focus); case "$f" in *"Menú principal"*|*"Main menu"*) break;; esac; d key Down wait0.8 >/dev/null; done
  case "$f" in *"Menú principal"*|*"Main menu"*) d key Confirm wait6 >/dev/null;; *) echo "could not find Main menu: $f"; exit 1;; esac
fi
f=$(d read focus); case "$f" in *Jugar*|*Play*) d key Confirm wait6 >/dev/null;; *) echo "not on Play: $f"; exit 1;; esac
if [ "$(scene)" = "states.DoYouWantToContinue" ]; then d key Down wait1 >/dev/null; f=$(d read focus); case "$f" in *nueva*|*New*) d key Confirm wait6 >/dev/null;; *) echo "not on New game: $f"; exit 1;; esac; fi
n=$1; key=Right; if [ $n -lt 0 ]; then key=Left; n=$((0-n)); fi
i=0; while [ $i -lt $n ]; do d key $key wait2.2 >/dev/null; i=$((i+1)); done
d read focus
d key Confirm wait5 >/dev/null
i=0; while [ $i -lt $2 ]; do d key Right wait1.6 >/dev/null; i=$((i+1)); done
timeout 20 python tools/bridge.py eval "{states.Episodestate}.activeepisode.episodename" | head -1
d key Confirm wait7 >/dev/null
for i in $(seq 1 60); do [ "$(scene)" = "states.Dungeon" ] && break; d key Confirm wait1.1 >/dev/null; done
scene
