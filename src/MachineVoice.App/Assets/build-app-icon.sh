#!/usr/bin/env bash
# Regenerates AppIcon.icns from app-icon.svg. Run after editing the SVG and commit both files;
# build-app.sh only copies the .icns, so the app build does not need rsvg-convert.
# Requires: rsvg-convert (brew install librsvg), iconutil (ships with macOS).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
iconset="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$iconset"

for size in 16 32 128 256 512; do
  rsvg-convert -w "$size" -h "$size" "$here/app-icon.svg" -o "$iconset/icon_${size}x${size}.png"
  rsvg-convert -w $((size * 2)) -h $((size * 2)) "$here/app-icon.svg" -o "$iconset/icon_${size}x${size}@2x.png"
done

iconutil -c icns "$iconset" -o "$here/AppIcon.icns"
rm -rf "$(dirname "$iconset")"
echo "Собрано: $here/AppIcon.icns"
