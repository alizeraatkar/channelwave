FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS source
ARG TDLIB_REVISION=c15d3f5a5de6e3ba5839822c451152e5e18bb700
RUN apt-get update && apt-get install -y --no-install-recommends cmake ninja-build g++ gperf libssl-dev zlib1g-dev git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /td
RUN git init && git remote add origin https://github.com/tdlib/td.git && git fetch --depth 1 origin ${TDLIB_REVISION} && git checkout --detach FETCH_HEAD

# Android needs generated sources, not a complete Linux TDLib distribution.
FROM source AS android-source
ARG BUILD_JOBS=2
RUN cmake -S . -B build-host -G Ninja -DCMAKE_BUILD_TYPE=Release -DTD_ENABLE_JNI=OFF \
    && cmake --build build-host --target prepare_cross_compiling -j${BUILD_JOBS} \
    && rm -rf build-host

FROM source AS build
ARG TDLIB_REVISION=c15d3f5a5de6e3ba5839822c451152e5e18bb700
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/out -DTD_ENABLE_JNI=OFF && cmake --build build --target tdjson -j 2 && cmake --install build --component tdjson
RUN mkdir -p /out/lib /out/licenses && cp -a build/libtdjson.so* /out/lib/ && cp LICENSE_1_0.txt /out/licenses/TDLib.txt \
    && cp /usr/share/doc/libssl-dev/copyright /out/licenses/OpenSSL.txt \
    && cp /usr/share/doc/zlib1g-dev/copyright /out/licenses/zlib.txt \
    && printf '{"revision":"%s","platform":"linux","architecture":"x64"}\n' "$TDLIB_REVISION" > /out/channelwave-build.json \
    && cd /out && sha256sum lib/libtdjson.so.* > SHA256SUMS
FROM scratch AS artifact
COPY --from=build /out/ /
