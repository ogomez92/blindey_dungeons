#!/bin/bash
# Builds "Dicey Dungeons Accessibility.app": the companion as a background app (no Dock
# icon, no window, no Terminal) that starts the game and quits when the game quits.
#
#   companion/Mac/package_mac.sh                 # Apple silicon
#   companion/Mac/package_mac.sh --arch x64      # Intel Macs
#   companion/Mac/package_mac.sh --install       # also copy it into the game folder
#
# Inside the bundle, Contents/MacOS/bridge/lime.ndll is the bridge payload; the app
# installs it into the game on start (Launcher/BridgeInstaller.cs), as on Windows.
set -euo pipefail

arch=arm64
install=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) arch="$2"; shift 2 ;;
    --install) install=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$repo/release/mac-$arch"
app="$out/Dicey Dungeons Accessibility.app"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/companion/DiceyA11y.csproj")

"$here/build_native.sh"
"$repo/bridge/build_mac.sh"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS/bridge" "$app/Contents/Resources"
dotnet publish "$repo/companion/DiceyA11y.csproj" -c Release -r "osx-$arch" --self-contained true \
  -o "$app/Contents/MacOS" -nologo -v quiet
cp "$repo/bridge/bin/mac/lime.ndll" "$app/Contents/MacOS/bridge/lime.ndll"
rm -f "$app/Contents/MacOS/"*.pdb

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Dicey Dungeons Accessibility</string>
  <key>CFBundleDisplayName</key><string>Dicey Dungeons Accessibility</string>
  <key>CFBundleIdentifier</key><string>io.github.ogomez92.diceya11y</string>
  <key>CFBundleExecutable</key><string>DiceyA11y</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <!-- A background app: no Dock icon, no menu bar, no window. -->
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

# Ad-hoc signature: enough for Apple silicon and for the Accessibility permission, which
# macOS ties to this signature (so a rebuilt app has to be allowed again).
codesign --force --deep --sign - "$app" >/dev/null
echo "Built $app"

if (( install )); then
  game="/Applications/Dicey Dungeons"
  [[ -d "$game/diceydungeons.app" ]] || { echo "No game at $game; copy the app next to diceydungeons.app yourself." >&2; exit 1; }
  rm -rf "$game/Dicey Dungeons Accessibility.app"
  cp -R "$app" "$game/"
  echo "Installed into $game"
fi
