#!/usr/bin/env bash
# Builds MachineVoice.app with Native AOT and an ad-hoc signature.
# Usage: ./build-app.sh [--install]   (--install copies the bundle to ~/Applications)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/artifacts/stage0"
publish="$out/publish"
app="$out/MachineVoice.app"

rm -rf "$out"
dotnet publish "$here/MachineVoice.Stage0.csproj" -c Release -r osx-arm64 -o "$publish" -nologo -v q

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$publish/MachineVoice" "$app/Contents/MacOS/"
cp "$publish"/*.dylib "$app/Contents/MacOS/"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>dev.machinevoice.app</string>
  <key>CFBundleName</key><string>MachineVoice</string>
  <key>CFBundleDisplayName</key><string>MachineVoice</string>
  <key>CFBundleExecutable</key><string>MachineVoice</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.0.1</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --sign - --timestamp=none "$app/Contents/MacOS/"*.dylib
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict "$app"

echo "Собрано: $app ($(du -sh "$app" | cut -f1))"

if [[ "${1:-}" == "--install" ]]; then
  mkdir -p "$HOME/Applications"
  rm -rf "$HOME/Applications/MachineVoice.app"
  cp -R "$app" "$HOME/Applications/"
  echo "Установлено: $HOME/Applications/MachineVoice.app"
fi
