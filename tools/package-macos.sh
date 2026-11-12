#!/usr/bin/env bash
# Packages the published macOS Desktop output into a NodeX.app bundle with Info.plist
# (including NSMicrophoneUsageDescription so macOS TCC prompts for microphone access).
set -euo pipefail

RID="${1:-osx-arm64}"
CONFIG="${2:-Release}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${3:-$ROOT_DIR/artifacts/publish/$RID}"
APP_BUNDLE="${4:-$ROOT_DIR/artifacts/NodeX.app}"

dotnet publish "$ROOT_DIR/src/NodeX.UI/NodeX.UI.Desktop/NodeX.UI.Desktop.csproj" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -o "$PUBLISH_DIR"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

cp "$ROOT_DIR/src/NodeX.UI/NodeX.UI.Desktop/macos/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/NodeX.UI.Desktop"

echo "Packaged macOS bundle at: $APP_BUNDLE"
