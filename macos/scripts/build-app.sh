#!/usr/bin/env bash
# Builds "Monitor Switcher.app" (menu bar app + monitorswitcher CLI) and zips it.
#
#   macos/scripts/build-app.sh
#
# Output: macos/dist/Monitor Switcher.app and macos/dist/MonitorSwitcher-macOS.zip
#
# Signing: ad-hoc by default. For distribution set
#   DEVELOPER_ID="Developer ID Application: Your Name (TEAMID)"   -> hardened-runtime signature
#   NOTARY_PROFILE=<keychain profile from `xcrun notarytool store-credentials`> -> notarize + staple
set -euo pipefail

MACOS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_DIR="$(cd "$MACOS_DIR/.." && pwd)"
VERSION="${VERSION:-0.1.0}"
APP_NAME="Monitor Switcher"
DIST="$MACOS_DIR/dist"
APP="$DIST/$APP_NAME.app"

"$MACOS_DIR/scripts/sync-shared.sh"

cd "$MACOS_DIR"
swift build -c release --product MonitorSwitcherApp
swift build -c release --product monitorswitcher
BIN="$(swift build -c release --show-bin-path)"

rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/MonitorSwitcherApp" "$APP/Contents/MacOS/MonitorSwitcherApp"
cp "$BIN/monitorswitcher" "$APP/Contents/MacOS/monitorswitcher"

# Bundled monitor profiles. They are copied as a plain folder rather than as SwiftPM's
# MonitorSwitcher_MonitorSwitcherCore.bundle: a nested bundle without the standard macOS layout can
# make codesign fail. CoreResources.profilesDirectory() looks in Contents/Resources/profiles first.
cp -R "$MACOS_DIR/Sources/MonitorSwitcherCore/Resources/profiles" "$APP/Contents/Resources/profiles"
rm -f "$APP/Contents/Resources/profiles/README.md"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>io.github.ashrash.monitorswitcher</string>
    <key>CFBundleExecutable</key><string>MonitorSwitcherApp</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSLocalNetworkUsageDescription</key><string>Monitor Switcher talks to Monitor Switcher on your other computer, so either computer can switch the monitor you share.</string>
    <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 ashrash and contributors. MIT License.</string>
</dict>
</plist>
PLIST

# App icon from the shared PNGs (assets/icon/icon-<size>.png).
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC="$REPO_DIR/assets/icon/icon-1024.png"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

if [[ -n "${DEVELOPER_ID:-}" ]]; then
    # Sign inner binaries first, then the bundle, with the hardened runtime (required for notarization).
    codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP/Contents/MacOS/monitorswitcher"
    codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP"
else
    codesign --force -s - "$APP/Contents/MacOS/monitorswitcher"
    codesign --force --deep -s - "$APP"
fi

ZIP="$DIST/MonitorSwitcher-macOS.zip"
ditto -c -k --keepParent "$APP" "$ZIP"

if [[ -n "${DEVELOPER_ID:-}" && -n "${NOTARY_PROFILE:-}" ]]; then
    xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP"
    rm -f "$ZIP"
    ditto -c -k --keepParent "$APP" "$ZIP"
fi

echo "Built $APP"
echo "Zipped $ZIP"
