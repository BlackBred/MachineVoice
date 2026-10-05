#!/usr/bin/env bash
# Builds MachineVoice.app with Native AOT and an ad-hoc signature. The MCP server is published next to the
# main binary: "Connect MCP to Cursor" in the settings copies it from there.
# Usage: ./build-app.sh [--install]   (--install copies the bundle to ~/Applications)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/artifacts/app"
publish="$out/publish"
app="$out/MachineVoice.app"

rm -rf "$out"
dotnet publish "$here/MachineVoice.App.csproj" -c Release -r osx-arm64 -o "$publish/app" -nologo -v q
dotnet publish "$root/src/MachineVoice.Mcp/MachineVoice.Mcp.csproj" -c Release -r osx-arm64 -o "$publish/mcp" -nologo -v q

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$publish/app/MachineVoice" "$app/Contents/MacOS/"
cp "$publish/app"/*.dylib "$app/Contents/MacOS/"
cp "$publish/mcp/MachineVoice.Mcp" "$app/Contents/MacOS/"
cp "$here/Assets/AppIcon.icns" "$app/Contents/Resources/"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>dev.machinevoice.app</string>
  <key>CFBundleName</key><string>MachineVoice</string>
  <key>CFBundleDisplayName</key><string>MachineVoice</string>
  <key>CFBundleExecutable</key><string>MachineVoice</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --sign - --timestamp=none "$app/Contents/MacOS/"*.dylib "$app/Contents/MacOS/MachineVoice.Mcp"
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict "$app"

echo "Собрано: $app ($(du -sh "$app" | cut -f1))"

if [[ "${1:-}" == "--install" ]]; then
  mkdir -p "$HOME/Applications"
  rm -rf "$HOME/Applications/MachineVoice.app"
  cp -R "$app" "$HOME/Applications/"
  echo "Установлено: $HOME/Applications/MachineVoice.app"
fi
