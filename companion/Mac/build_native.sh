#!/bin/bash
# Builds libDiceyMac.dylib (speech, key tap, focus) for both Mac architectures.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/bin}"
mkdir -p "$out"
for arch in arm64 x86_64; do
  swiftc -O -emit-library -module-name DiceyMac -target "$arch-apple-macos11.0" \
    "$here/Native/DiceyMac.swift" -o "$out/libDiceyMac-$arch.dylib"
done
lipo -create "$out/libDiceyMac-arm64.dylib" "$out/libDiceyMac-x86_64.dylib" -output "$out/libDiceyMac.dylib"
rm "$out/libDiceyMac-arm64.dylib" "$out/libDiceyMac-x86_64.dylib"
echo "Built $out/libDiceyMac.dylib"
