FROM emscripten/emsdk:7.0.0@sha256:6bd7aef93408b10bf7b36642cb7c91119b302f720b9ec2d20439f73cf07fe894 AS build
ARG TDLIB_REVISION=c15d3f5a5de6e3ba5839822c451152e5e18bb700
ARG BUILD_JOBS=2
ENV TDLIB_REVISION=${TDLIB_REVISION}
RUN apt-get update && apt-get install -y --no-install-recommends gperf libssl-dev zlib1g-dev git cmake ninja-build curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /td
RUN git init && git remote add origin https://github.com/tdlib/td.git && git fetch --depth 1 origin ${TDLIB_REVISION} && git checkout --detach FETCH_HEAD
COPY scripts/tdweb-patch.cjs /tdweb-patch.cjs
RUN node /tdweb-patch.cjs
WORKDIR /opt
RUN curl --fail --location --retry 3 https://www.openssl.org/source/openssl-3.5.8.tar.gz -o openssl.tar.gz \
    && echo 'a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2  openssl.tar.gz' | sha256sum -c - \
    && tar -xzf openssl.tar.gz && rm openssl.tar.gz
WORKDIR /opt/openssl-3.5.8
RUN emconfigure ./Configure linux-generic32 no-shared no-threads no-dso no-engine no-tests no-ui-console no-module no-asm --prefix=/opt/openssl-wasm --libdir=lib \
    && sed -i 's/^CROSS_COMPILE=.*/CROSS_COMPILE=/' Makefile \
    && emmake make -j${BUILD_JOBS} build_libs && emmake make install_dev
WORKDIR /td
RUN cmake -S . -B build-generate -G Ninja -DTD_GENERATE_SOURCE_FILES=ON \
    && cmake --build build-generate -j${BUILD_JOBS}
RUN emcmake cmake -S . -B build-wasm -G Ninja -DCMAKE_BUILD_TYPE=MinSizeRel \
    -DOPENSSL_FOUND=1 -DOPENSSL_ROOT_DIR=/opt/openssl-wasm -DOPENSSL_INCLUDE_DIR=/opt/openssl-wasm/include \
    -DOPENSSL_CRYPTO_LIBRARY=/opt/openssl-wasm/lib/libcrypto.a -DOPENSSL_SSL_LIBRARY=/opt/openssl-wasm/lib/libssl.a \
    '-DOPENSSL_LIBRARIES=/opt/openssl-wasm/lib/libssl.a;/opt/openssl-wasm/lib/libcrypto.a' -DOPENSSL_VERSION=3.5.8 \
    && cmake --build build-wasm --target td_wasm -j${BUILD_JOBS}
# Runtime filesystem helpers are link-only exports. Keep them out of compiler
# flags so changing the wrapper's export surface never recompiles TDLib objects.
RUN emcmake cmake -S . -B build-wasm "-DCMAKE_EXE_LINKER_FLAGS=-sEXPORTED_RUNTIME_METHODS=['FS','cwrap','IDBFS','WORKERFS']" \
    && cmake --build build-wasm --target td_wasm -j${BUILD_JOBS}
WORKDIR /td/example/web/tdweb
COPY vendor/tdweb-build/ ./
RUN mkdir -p src/prebuilt/release && cp /td/build-wasm/td_wasm.js /td/build-wasm/td_wasm.wasm src/prebuilt/release/ \
    && npm ci --ignore-scripts && npm audit --audit-level=high && npm run build \
    && mkdir -p dist/licenses && cp /td/LICENSE_1_0.txt dist/licenses/TDLib.txt \
    && cp /opt/openssl-3.5.8/LICENSE.txt dist/licenses/OpenSSL.txt \
    && cp node_modules/localforage/LICENSE dist/licenses/localforage.txt \
    && cp node_modules/lie/license.md dist/licenses/lie.txt \
    && cp node_modules/immediate/LICENSE.txt dist/licenses/immediate.txt
FROM scratch AS artifact
COPY --from=build /td/example/web/tdweb/dist/ /
