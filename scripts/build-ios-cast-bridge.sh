#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
  echo "Usage: bash scripts/build-ios-cast-bridge.sh /path/to/extracted/static-sdk" >&2
  exit 2
fi

sdk="$(cd "$1" && pwd)"
project="$(cd "$(dirname "$0")/.." && pwd)"
framework="$sdk/GoogleCast.xcframework/ios-arm64"
if [[ ! -d "$framework/GoogleCast.framework" ]]; then
  echo "Expected the static Google Cast 4.8.6 device framework under $framework." >&2
  exit 1
fi

build="$(mktemp -d)"
trap 'rm -f "$build/CastBridge.o"; rmdir "$build"' EXIT
xcrun --sdk iphoneos clang -target arm64-apple-ios17.0 -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
  -Os -fobjc-arc -F "$framework" -c "$project/src/ChannelWave.Host.Maui/Platforms/iOS/CastBridge.m" \
  -o "$build/CastBridge.o"
xcrun libtool -static -o "$sdk/libChannelWaveCastBridge.a" "$build/CastBridge.o"
printf 'Build net10.0-ios for ios-arm64 with -p:ChannelWaveIosCastSdkRoot=%s\n' "$sdk"
