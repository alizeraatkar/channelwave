#!/usr/bin/env bash
set -euo pipefail
platform=${1:?ios or macos}
: "${RELEASE_VERSION:?}" "${BASE_VERSION:?}" "${RELEASE_BUILD_NUMBER:?}"
native=maccatalyst; tfm=net10.0-maccatalyst; rid=maccatalyst-arm64
identity=${APPLE_SIGNING_IDENTITY_MACOS:-}; profile=${APPLE_PROFILE_MACOS:-}
if [[ $platform = ios ]]; then
  native=ios; tfm=net10.0-ios; rid=ios-arm64
  identity=${APPLE_SIGNING_IDENTITY_IOS:-}; profile=${APPLE_PROFILE_IOS:-}
  : "${ASC_APP_ID:?}" "${ASC_INTERNAL_GROUP_ID:?}"
fi
test -n "$identity" && test -n "$profile"
python3 scripts/verify-native.py "artifacts/tdlib/$native" "$native"
project=src/ChannelWave.Host.Maui
properties=(-p:ChannelWaveRelease=true "-p:ChannelWaveTarget=$tfm" "-p:ChannelWaveReleaseRid=$rid" "-p:ChannelWaveTdlibAppleArchive=$PWD/artifacts/tdlib/$native/libtdjson-combined.a"
  "-p:CodesignKey=$identity" "-p:CodesignProvision=$profile" "-p:CodesignKeychain=$RUNNER_TEMP/channelwave-apple/signing.keychain-db"
  "-p:ApplicationDisplayVersion=$BASE_VERSION" "-p:ApplicationVersion=$RELEASE_BUILD_NUMBER" -p:ArchiveOnBuild=true -p:UseHardenedRuntime=true)
dotnet restore "$project" --locked-mode -p:Configuration=Release "${properties[@]}"
dotnet publish "$project" -c Release -f "$tfm" --no-restore "${properties[@]}" -p:BuildIpa=true -o "artifacts/apple-publish-$platform"
out="artifacts/release-$platform"
mkdir -p "$out"
if [[ $platform = macos ]]; then
  app=$(find "artifacts/apple-publish-$platform" -maxdepth 1 -name '*.app' -type d)
  test -n "$app"
  python3 scripts/security-check.py artifacts "$app"
  codesign --verify --deep --strict "$app"
  hdiutil create -volname ChannelWave -srcfolder "$app" -ov -format UDZO "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg"
  codesign --force --sign "$identity" --timestamp "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg"
  xcrun notarytool submit "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg" --key "$RUNNER_TEMP/channelwave-apple/AuthKey.p8" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" --wait --timeout 30m
  xcrun stapler staple "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg"
  xcrun stapler validate "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg"
  spctl --assess --type open --context context:primary-signature --verbose "$out/ChannelWave-$RELEASE_VERSION-macos-arm64.dmg"
else
  ipa=$(find "artifacts/apple-publish-$platform" -maxdepth 1 -name '*.ipa' -type f)
  test -n "$ipa"
  python3 scripts/security-check.py artifacts "$ipa"
  # Check for an existing build before upload to support reruns after processing.
  if node scripts/testflight.mjs lookup; then
    :
  else
    status=$?
    if [[ $status != 3 ]]; then exit "$status"; fi
    xcrun altool --upload-app -f "$ipa" -t ios --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
  fi
  node scripts/testflight.mjs deliver > "$out/TestFlight-$RELEASE_VERSION.json"
fi
tar -czf "$out/TDLib-$platform-notices.tar.gz" -C "artifacts/tdlib/$native" licenses channelwave-build.json SHA256SUMS
python3 scripts/release.py package --platform "$platform" --folder "$out"
