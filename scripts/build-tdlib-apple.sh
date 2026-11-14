#!/usr/bin/env bash
set -euo pipefail
platform=${1:?ios or maccatalyst}
case "$platform" in ios) sdk=iphoneos; target=arm64-apple-ios17.0 ;; maccatalyst) sdk=macosx; target=arm64-apple-ios17.0-macabi ;; *) exit 2 ;; esac
root=$(cd "$(dirname "$0")/.." && pwd)
revision=$(python3 -c 'import json; print(json.load(open("vendor/tdlib.lock.json"))["revision"])')
tools="$root/.tools/native-apple-$platform"
out="$root/artifacts/tdlib/$platform"
mkdir -p "$tools" "$out/licenses"
if [[ ! -d "$tools/td/.git" ]]; then git clone --filter=blob:none --no-checkout https://github.com/tdlib/td.git "$tools/td"; fi
git -C "$tools/td" checkout --detach "$revision"
cmake -S "$tools/td" -B "$tools/host" -DCMAKE_BUILD_TYPE=Release -DTD_ENABLE_JNI=OFF
cmake --build "$tools/host" --target prepare_cross_compiling -j2
curl --fail --location --retry 3 https://www.openssl.org/source/openssl-3.5.8.tar.gz -o "$tools/openssl.tar.gz"
echo "a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2  $tools/openssl.tar.gz" | shasum -a256 -c -
tar -xzf "$tools/openssl.tar.gz" -C "$tools"
sdkroot=$(xcrun --sdk "$sdk" --show-sdk-path)
flags="-target $target -isysroot $sdkroot"
if [[ $platform = maccatalyst ]]; then
  # The bundled SQLite's platform detection predates Catalyst and incorrectly
  # enables gethostuuid. Use its documented override for the unavailable API.
  flags="$flags -DHAVE_GETHOSTUUID=0 -isystem $sdkroot/System/iOSSupport/usr/include -iframework $sdkroot/System/iOSSupport/System/Library/Frameworks"
fi
(
  cd "$tools/openssl-3.5.8"
  export CC="$(xcrun --find clang)" CFLAGS="$flags" LDFLAGS="$flags"
  ./Configure darwin64-arm64-cc no-shared no-tests no-module --prefix="$tools/openssl" --libdir=lib
  make -j2 build_libs
  make install_dev
)
cmake -S "$tools/td" -B "$tools/build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Darwin \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_SYSROOT="$sdkroot" \
  -DCMAKE_C_FLAGS="$flags" -DCMAKE_CXX_FLAGS="$flags" -DCMAKE_CXX_FLAGS_RELEASE='-O2 -DNDEBUG' \
  -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY -DTD_ENABLE_JNI=OFF -DTD_ENABLE_LTO=OFF \
  -DOPENSSL_ROOT_DIR="$tools/openssl" -DOPENSSL_USE_STATIC_LIBS=TRUE \
  -DOPENSSL_INCLUDE_DIR="$tools/openssl/include" -DOPENSSL_CRYPTO_LIBRARY="$tools/openssl/lib/libcrypto.a" \
  -DOPENSSL_SSL_LIBRARY="$tools/openssl/lib/libssl.a"
cmake --build "$tools/build" --target tdjson_static -j2
find "$tools/build" -name '*.a' -type f > "$tools/archives.txt"
printf '%s\n' "$tools/openssl/lib/libssl.a" "$tools/openssl/lib/libcrypto.a" >> "$tools/archives.txt"
libtool -static -o "$out/libtdjson-combined.a" -filelist "$tools/archives.txt"
lipo "$out/libtdjson-combined.a" -verify_arch arm64
cp "$tools/td/LICENSE_1_0.txt" "$out/licenses/TDLib.txt"
cp "$tools/openssl-3.5.8/LICENSE.txt" "$out/licenses/OpenSSL.txt"
printf '{"revision":"%s","platform":"%s","architecture":"arm64","openssl":"3.5.8","xcode":"26.6"}\n' "$revision" "$platform" > "$out/channelwave-build.json"
(cd "$out" && shasum -a256 libtdjson-combined.a > SHA256SUMS)
