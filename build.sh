#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="随手问"
BUNDLE="dist/${APP_NAME}.app"
BIN="QuickAsk"

rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"

cat > "$BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>随手问</string>
    <key>CFBundleDisplayName</key><string>随手问</string>
    <key>CFBundleIdentifier</key><string>com.liab.quickask</string>
    <key>CFBundleExecutable</key><string>QuickAsk</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSSupportsAutomaticTermination</key><false/>
    <key>NSSupportsSuddenTermination</key><false/>
</dict>
</plist>
PLIST

mkdir -p .build/mc .build/tmp
export TMPDIR="$PWD/.build/tmp"

if [ ! -f .build/AppIcon.icns ]; then
  swiftc -O -swift-version 5 -disable-sandbox -module-cache-path .build/mc -Xcc -fmodules-cache-path=.build/mc tools/make_icon.swift -o .build/make_icon
  rm -rf .build/AppIcon.iconset
  .build/make_icon .build/AppIcon.iconset
  iconutil -c icns .build/AppIcon.iconset -o .build/AppIcon.icns
fi
cp .build/AppIcon.icns "$BUNDLE/Contents/Resources/AppIcon.icns"

swiftc -O -swift-version 5 \
  -target arm64-apple-macos14.0 \
  -disable-sandbox \
  -module-cache-path .build/mc \
  -Xcc -fmodules-cache-path=.build/mc \
  -framework SwiftUI -framework AppKit -framework Carbon \
  Sources/*.swift \
  -o "$BUNDLE/Contents/MacOS/$BIN"

codesign --force --sign - "$BUNDLE" >/dev/null 2>&1 || true
echo "built: $BUNDLE"
