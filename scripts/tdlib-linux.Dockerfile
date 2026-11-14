FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac AS build
ARG TDLIB_REVISION=c15d3f5a5de6e3ba5839822c451152e5e18bb700
ARG BUILD_JOBS=2
RUN apt-get update && apt-get install -y --no-install-recommends cmake ninja-build g++ gperf libssl-dev zlib1g-dev git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /td
RUN git init && git remote add origin https://github.com/tdlib/td.git && git fetch --depth 1 origin ${TDLIB_REVISION} && git checkout --detach FETCH_HEAD
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS_RELEASE='-O2 -DNDEBUG' -DTD_ENABLE_JNI=OFF -DTD_ENABLE_LTO=OFF \
    && cmake --build build --target tdjson -j${BUILD_JOBS}
RUN mkdir -p /out/lib /out/licenses && cp -a build/libtdjson.so* /out/lib/ \
    && cp LICENSE_1_0.txt /out/licenses/TDLib.txt \
    && cp /usr/share/doc/libssl-dev/copyright /out/licenses/OpenSSL.txt \
    && cp /usr/share/doc/zlib1g-dev/copyright /out/licenses/zlib.txt \
    && printf '{"revision":"%s","platform":"linux","architecture":"x64","baseline":"ubuntu-24.04","gcc":"%s","openssl":"%s","zlib":"%s"}\n' "$TDLIB_REVISION" "$(g++ -dumpfullversion)" "$(dpkg-query -W -f='${Version}' libssl-dev)" "$(dpkg-query -W -f='${Version}' zlib1g-dev)" > /out/channelwave-build.json \
    && cd /out && sha256sum lib/libtdjson.so.* > SHA256SUMS
FROM scratch AS artifact
COPY --from=build /out/ /
