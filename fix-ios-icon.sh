#!/bin/bash
# Fallback: manually compile Assets.car for iOS builds
# Run from the repo root: ./fix-ios-icon.sh [Debug|Release]
# The MSBuild target _FixAssetCatalog in .csproj handles this automatically.
set -euo pipefail
cd "$(dirname "$0")/src/VegaBridgeApp"

CONFIG="${1:-Debug}"
PLATFORM="iphoneos"
[[ "$CONFIG" == "Debug" ]] && PLATFORM="iphonesimulator"

for base in "obj/$CONFIG/net10.0-ios" "obj/$CONFIG/net10.0-ios/ios-arm64"; do
    ASSETS="$base/actool/cloned-assets/Assets.xcassets"
    BUNDLE="$base/actool/bundle"
    APP="bin/$CONFIG/net10.0-ios/ios-arm64/VegaBridgeApp.app"
    if [ -d "$ASSETS" ]; then
        echo "Compiling Assets.car from $ASSETS ($PLATFORM)..."
        xcrun actool --compile "$BUNDLE" "$ASSETS" \
            --platform "$PLATFORM" \
            --minimum-deployment-target 16.0 \
            --app-icon appicon \
            --output-partial-info-plist "$base/actool/partial.plist" > /dev/null
        cp "$BUNDLE/Assets.car" "$APP/"
        echo "  → $APP/Assets.car ($(stat -f%z "$APP/Assets.car") bytes)"
        exit 0
    fi
done
echo "No cloned-assets found. Run dotnet build first."
exit 1
