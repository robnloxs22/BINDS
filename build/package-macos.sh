#!/usr/bin/env bash
# Builds "Doink Binds.app" (and optionally a .dmg) for macOS.
#
#   ./build/package-macos.sh            # Apple Silicon (osx-arm64)
#   ./build/package-macos.sh osx-x64    # Intel
#
# Output: publish/<rid>/Doink Binds.app  (+ publish/DoinkBinds-<rid>.dmg when run on macOS)
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/DoinkBinds/DoinkBinds.csproj"
OUT="$ROOT/publish/$RID"
APP="$OUT/Doink Binds.app"

echo "▶ Publishing $RID…"
dotnet publish "$PROJECT" -p:PublishProfile="$RID"

echo "▶ Creating app bundle…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ROOT/build/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/build/macos/doink.icns" "$APP/Contents/Resources/doink.icns"

# Executable + native libraries (Avalonia/Skia dylibs) + sample macros all live next to each other.
cp "$OUT/DoinkBinds" "$APP/Contents/MacOS/"
cp "$OUT"/*.dylib "$APP/Contents/MacOS/" 2>/dev/null || true
cp -R "$OUT/Samples" "$APP/Contents/MacOS/Samples"
chmod +x "$APP/Contents/MacOS/DoinkBinds"

if [[ "$(uname)" == "Darwin" ]]; then
    # Ad-hoc signature: required on Apple Silicon, and gives macOS a stable identity for the
    # Accessibility / Input Monitoring permissions. Replace "-" with your Developer ID to distribute.
    echo "▶ Signing (ad-hoc)…"
    codesign --force --deep --sign - "$APP"

    echo "▶ Building DMG…"
    DMG="$ROOT/publish/DoinkBinds-$RID.dmg"
    rm -f "$DMG"
    hdiutil create -volname "Doink Binds" -srcfolder "$APP" -ov -format UDZO "$DMG" >/dev/null
    echo "✔ $DMG"
else
    echo "ℹ Not on macOS: skipping codesign + DMG (copy the .app to a Mac and run 'codesign --force --deep --sign - \"Doink Binds.app\"')."
fi

echo "✔ $APP"
