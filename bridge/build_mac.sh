#!/bin/bash
# Builds the bridge (our replacement lime.ndll) for the macOS game.
#
# The Mac game is x86_64 (it runs under Rosetta on Apple silicon), so the bridge is too.
# Windows forwards every Lime export with linker pragmas; Mach-O has a better tool: the
# bridge re-exports lime_orig.dll whole (-reexport_library), and dyld finds our three
# intercepted exports in the bridge before it looks in the re-exported library.
#
# The linker only needs names, so it links against a stub generated from the committed
# lime_exports_mac.txt: a build never reads the game. With --refresh-exports GAME_APP the
# list is re-read from the game's real Lime first (commit it when it changes).
#
#   bridge/build_mac.sh
#   bridge/build_mac.sh --refresh-exports "/Applications/Dicey Dungeons/diceydungeons.app"
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/bin/mac"
list="$here/lime_exports_mac.txt"
intercepted="hx_set_loader lime_application_event_manager_register__prime lime_window_context_flip__prime"

if [[ "${1:-}" == "--refresh-exports" ]]; then
  macos="$2/Contents/MacOS"
  original="$macos/lime_orig.dll"
  [[ -f "$original" ]] || original="$macos/lime.ndll"
  names="$(nm -gU "$original" | awk '{print $3}' | sed 's/^_//' | sort)"
  if grep -qx diceya11y_bridge_marker <<<"$names"; then
    echo "$original is the bridge itself, not the game's Lime." >&2; exit 1
  fi
  printf '%s\n' "$names" > "$list"
  echo "lime_exports_mac.txt refreshed from $original"
fi

count=$(grep -c . "$list")
if (( count < 100 )); then echo "Only $count exports in $list - refusing to build a broken proxy." >&2; exit 1; fi
for i in $intercepted; do
  grep -qx "$i" "$list" || { echo "The game's Lime does not export $i; the hook design no longer fits." >&2; exit 1; }
done

mkdir -p "$out/stub"
stub_c="$out/stub/lime_orig_stub.c"
grep -vx -e hx_set_loader -e lime_application_event_manager_register__prime -e lime_window_context_flip__prime "$list" |
  awk '{print "void " $0 "(void) {}"}' > "$stub_c"

flags=(-arch x86_64 -mmacosx-version-min=11.0)
clang "${flags[@]}" -dynamiclib -w "$stub_c" -install_name @loader_path/lime_orig.dll -o "$out/stub/lime_orig.dll"

clang++ "${flags[@]}" -std=c++17 -O2 -fvisibility=hidden -dynamiclib "$here/bridge.cpp" \
  -install_name @loader_path/lime.ndll \
  -Wl,-reexport_library,"$out/stub/lime_orig.dll" \
  -o "$out/lime.ndll"
codesign --force --sign - "$out/lime.ndll" >/dev/null 2>&1 || true

echo "Built $out/lime.ndll ($((count - 3)) re-exported, 3 intercepted)"
