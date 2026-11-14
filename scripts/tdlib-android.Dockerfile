# The companion PowerShell script first builds this pinned source image from tdlib.Dockerfile.
FROM channelwave-tdlib-android-source AS build
ARG BUILD_JOBS=2
ARG ANDROID_ABI=x86_64
RUN case "$ANDROID_ABI" in x86_64|arm64-v8a) ;; *) exit 1 ;; esac
RUN test "$(git -C /td rev-parse HEAD)" = c15d3f5a5de6e3ba5839822c451152e5e18bb700
RUN apt-get update && apt-get install -y --no-install-recommends make unzip && rm -rf /var/lib/apt/lists/*
COPY android-ndk-r28c-linux.zip /tmp/ndk.zip
RUN echo 'a7b54a5de87fecd125a17d54f73c446199e72a64  /tmp/ndk.zip' | sha1sum -c - && unzip -q /tmp/ndk.zip -d /opt && rm /tmp/ndk.zip
ENV ANDROID_NDK_ROOT=/opt/android-ndk-r28c
ENV PATH=/opt/android-ndk-r28c/toolchains/llvm/prebuilt/linux-x86_64/bin:$PATH
COPY openssl-3.5.8.tar.gz /tmp/openssl.tar.gz
RUN echo 'a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2  /tmp/openssl.tar.gz' | sha256sum -c - && tar -xzf /tmp/openssl.tar.gz -C /opt && rm /tmp/openssl.tar.gz
WORKDIR /opt/openssl-3.5.8
RUN case "$ANDROID_ABI" in arm64-v8a) openssl_target=android-arm64 ;; x86_64) openssl_target=android-x86_64 ;; esac \
    && ./Configure "$openssl_target" -D__ANDROID_API__=29 no-shared no-tests no-module --prefix=/opt/openssl-android --libdir=lib \
    && make -j${BUILD_JOBS} build_libs && make install_dev
WORKDIR /td
RUN cmake -S . -B build-android -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE=/opt/android-ndk-r28c/build/cmake/android.toolchain.cmake \
    -DANDROID_ABI=${ANDROID_ABI} -DANDROID_PLATFORM=android-29 -DANDROID_STL=c++_static \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS_RELEASE='-O2 -DNDEBUG' \
    -DOPENSSL_ROOT_DIR=/opt/openssl-android -DOPENSSL_USE_STATIC_LIBS=TRUE \
    -DOPENSSL_INCLUDE_DIR=/opt/openssl-android/include \
    -DOPENSSL_CRYPTO_LIBRARY=/opt/openssl-android/lib/libcrypto.a \
    -DOPENSSL_SSL_LIBRARY=/opt/openssl-android/lib/libssl.a \
    -DTD_ENABLE_JNI=OFF -DTD_ENABLE_LTO=OFF -DBUILD_TESTING=OFF \
    && cmake --build build-android --target tdjson -j${BUILD_JOBS}
RUN mkdir -p /android-out/${ANDROID_ABI} /android-out/licenses \
    && llvm-strip --strip-unneeded build-android/libtdjson.so -o /android-out/${ANDROID_ABI}/libtdjson.so \
    && llvm-readelf -d /android-out/${ANDROID_ABI}/libtdjson.so \
    && llvm-readelf -l /android-out/${ANDROID_ABI}/libtdjson.so \
    && cp LICENSE_1_0.txt /android-out/licenses/TDLib.txt \
    && cp /opt/openssl-3.5.8/LICENSE.txt /android-out/licenses/OpenSSL.txt \
    && cp /opt/android-ndk-r28c/NOTICE /android-out/licenses/AndroidNDK.txt \
    && cp /opt/android-ndk-r28c/NOTICE.toolchain /android-out/licenses/AndroidNDK-toolchain.txt \
    && printf '{"revision":"c15d3f5a5de6e3ba5839822c451152e5e18bb700","abi":"%s","androidApi":29,"ndk":"28.2.13676358","openssl":"3.5.8","stl":"c++_static"}\n' "$ANDROID_ABI" > /android-out/channelwave-build.json \
    && cd /android-out && sha256sum ${ANDROID_ABI}/libtdjson.so > SHA256SUMS
COPY tdlib-android-probe.c /tmp/tdlib-android-probe.c
RUN mkdir -p /android-out/tools \
    && case "$ANDROID_ABI" in arm64-v8a) compiler=aarch64-linux-android29-clang ;; x86_64) compiler=x86_64-linux-android29-clang ;; esac \
    && "$compiler" -O2 -Wall -Wextra /tmp/tdlib-android-probe.c -ldl -o /android-out/tools/tdlib-emulator-probe
FROM scratch AS artifact
COPY --from=build /android-out/ /
