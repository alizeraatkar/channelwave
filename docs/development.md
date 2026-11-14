# Development setup

Use .NET SDK 10.0.302 from `global.json`, Node 24 and Git. Docker is needed for
the supplied Linux/WASM TDLib builds. NuGet versions are centralized and locked.
See [contributing](../CONTRIBUTING.md) for shared build and test commands.

## Application configuration

Web: copy `src/ChannelWave.Host.Web/wwwroot/channelwave.example.json` to ignored
`channelwave.local.json` in the same directory and set your Telegram API ID/hash.
MAUI: supply developer values privately in `Resources/Raw/telegram.json` during
packaging, or use `channelwave.local.json` in the platform app-data directory.
Linux: place that JSON under `Environment.SpecialFolder.LocalApplicationData`
in the `ChannelWave` directory, normally `~/.local/share/ChannelWave`.
Native encryption keys are generated in platform vaults, not in this JSON.
Never commit real registration values, sessions, login codes or signing keys.

## Pinned TDLib

Both transports require commit `c15d3f5a5de6e3ba5839822c451152e5e18bb700` (TDLib 1.8.68). The schema, official browser reference sources, checksums and generated method-signature contract are in `vendor/`. The C# JSON adapter implements the required methods and is checked against the pinned signatures.

Build the native Linux probe library:

```powershell
docker build -f scripts/tdlib.Dockerfile --target artifact --output type=local,dest=artifacts/tdlib/linux-x64 .
```

The current Docker SDK base uses Debian 13. Its binary was initialized in that container; **it is not a portable Ubuntu 24.04 artifact**. Build on the oldest supported distro before packaging. `artifacts/tdlib/linux-x64` contains the shared library, source manifest, license and SHA256 list. On a matching Linux host:

```sh
dotnet run --project tools/ChannelWave.Probe -- /absolute/path/to/libtdjson.so.1.8.68
```

The probe checks actual loading, public version, exact commit and the initial authorization state. It does not sign in or contact an account.

Build browser assets from that same source:

```powershell
./scripts/build-tdweb.ps1
```

This exports the complete tdweb distribution to `src/ChannelWave.Host.Web/wwwroot/tdweb`, including compiler/crypto provenance, payload checksums, production bundle inputs and dependency notices. The versioned security overlay keeps secret chats and payload logging disabled. The loader requires security wrapper version 2 and the exact runtime commit; old generated assets must be rebuilt. The maintained manifest/lockfile in `vendor/tdweb-build` replaces the obsolete upstream npm graph, using esbuild and browser-native channel/UUID APIs. The build pins Emscripten 7.0.0, OpenSSL 3.5.8 and a Node 24 SDK image. The November 13 replacement npm and platform NuGet lockfile scans reported zero advisories; this does not cover every native/system component or establish authenticated/device safety. See the [security audit](security-audit.md) and [verification status](verification.md) for rebuild evidence and remaining release gates.

After building the web host and those assets, set `CHANNELWAVE_VERIFY_TDWEB=1` and run `npm run test:browser -- tdlib.spec.mjs`. This optional integration check loads the real WASM worker and verifies the runtime commit while blocking external networking. The ordinary browser suite excludes this integration check.


## Audio outputs and casting

Open **Play on…** from either player to choose an output. Google Cast is available in the Android host and in Linux builds with libVLC 3's mDNS renderer plugin. iOS and Mac Catalyst offer Apple's AirPlay and system route picker. Windows and supported browsers offer the audio devices their playback engine can access.

Google Cast uses a compatible receiver on the same local network. On Android and iOS, the app prepares a complete file before sending it and supports receivers on private IPv4 networks. A lost cast connection pauses the queue at its last known position. Reopening ChannelWave uses the system output.

To include foreground Google Cast in an iOS device build, extract Google's [static iOS SDK 4.8.6](https://developers.google.com/cast/docs/ios_sender/#downloads). The SDK archive SHA-256 is `e1fe7fd6f2bf4b58e830d378fafc435d159bd755f7ee20fd3033aa1ce313cd6e`. On a Mac with Xcode, run `bash scripts/build-ios-cast-bridge.sh /path/to/extracted/sdk`, then pass `-p:ChannelWaveIosCastSdkRoot=/path/to/extracted/sdk` when building for `ios-arm64`. Keep ChannelWave in the foreground while using Google Cast on iOS.

## Native hosts

For signed Android ARM64 APKs on GitHub Releases, see [Android release setup](android-releases.md). The workflow supports `v*` version tags and manual artifact builds, with repository secrets for signing and Telegram application configuration.

Windows needs MAUI Windows workloads, Windows SDK, WebView2, and the pinned TDLib DLL with matching crypto/runtime dependencies. Set `CHANNELWAVE_TDJSON` to its absolute path for local probing. Android needs the Android workload/JDK/SDK and TDLib shared libraries built for each ABI. Apple builds require macOS, supported Xcode, Apple signing/device provisioning and static TDLib/crypto archives for iOS or Mac Catalyst (device/simulator archives are not interchangeable).

```powershell
dotnet build src/ChannelWave.Host.Maui -p:ChannelWaveTarget=net10.0-windows10.0.19041.0
dotnet build src/ChannelWave.Host.Maui -p:ChannelWaveTarget=net10.0-android
```

On macOS, use `ChannelWaveTarget=net10.0-ios` or `net10.0-maccatalyst`. `NativeDependencies.targets` accepts `ChannelWaveTdlibWindowsRoot`, `ChannelWaveTdlibAndroidRoot` (ABI subdirectories), or `ChannelWaveTdlibAppleArchive` (matching merged static archive including dependencies). Publish fails if the required native library is missing. Windows and Apple native distributions/signatures are not supplied or validated. Android ARM64 packaging is covered by the release workflow; physical-device validation remains pending. The Android x64 emulator build is described below. Use [TDLib's build instructions](https://github.com/tdlib/td#building) for the pinned checkout, never a different API revision.

For an **x86_64 Android emulator**, build the pinned native JSON library with Docker:

```powershell
./scripts/build-tdlib-android.ps1
dotnet build src/ChannelWave.Host.Maui -p:ChannelWaveTarget=net10.0-android -p:RuntimeIdentifier=android-x64 -p:EmbedAssembliesIntoApk=true -p:NuGetLockFilePath=obj/android-emulator.packages.lock.json -p:ChannelWaveTdlibAndroidRoot="$PWD/artifacts/tdlib/android"
```

The native build verifies the NDK r28c and OpenSSL 3.5.8 archives, compiles the exact pinned TDLib revision, and exports `artifacts/tdlib/android/x86_64/libtdjson.so`, licenses, build metadata and SHA256 checksums. OpenSSL and the C++ runtime are statically linked. Pass `-ProxyUri http://host:port` to the build script if downloads need a proxy. Generated downloads and binaries stay outside source control. The default build supplies the emulator ABI; pass `-Abi arm64-v8a` for ARM64 devices. `EmbedAssembliesIntoApk=true` makes the debug APK installable through ADB without an IDE's fast-deployment step. Developer API ID/hash configuration is still required in app-private `channelwave.local.json` or the private packaged resource described above.

If native Telegram traffic needs an HTTP CONNECT proxy, MAUI's private configuration accepts an optional `"httpProxy": { "server": "127.0.0.1", "port": 10808 }` alongside `apiId` and `apiHash`. For the emulator, first run `adb -s emulator-5554 reverse tcp:10808 tcp:10808` to tunnel that port to the host's existing proxy; recreate the tunnel after restarting the emulator and keep the proxy running. Use your actual endpoint and port. TDLib enables the proxy after parameter initialization and before account sign-in. The exported `tools/tdlib-emulator-probe` verifies the runtime revision and performs a Telegram datacenter handshake through that example host proxy without application credentials or account authorization.

Linux needs a graphical desktop, Photino/WebKitGTK prerequisites, `libvlc.so.5`, and `secret-tool` with an unlocked Secret Service keyring. Set `CHANNELWAVE_TDJSON`, then `dotnet run --project src/ChannelWave.Host.Linux`. The host holds a profile lock for its lifetime. Native Linux UI/audio still require a real desktop smoke test; the container TDLib probe does not supply that evidence.


## Runtime acceptance

Use an account you control and a channel containing original or licensed audio,
including Unicode metadata, a long recording and an unsupported or corrupt file.

1. Probe the engine, connect and follow Telegram's reported delivery method.
2. Select a source, index multiple pages, restart and check cursor recovery without duplicates.
3. Play and export a track; rapidly change selection to check stale completion.
4. Favorite it and add it to a playlist. Reopen and check account data and paused restoration.
5. Test offline/reconnect, lost access, cancellation, sign-out and account isolation.
6. Repeat on each advertised target, including background/system controls,
   casting, offline playback after restart and data-preserving upgrades.

Record the exact OS/device, engine and app version in [verification](verification.md).

## Troubleshooting

- Engine missing or commit mismatch: build/package the pinned revision.
- NuGet NU1301 or connection resets: retry restore with `--disable-parallel`.
  If the official endpoint works over HTTP/1.1 only, set
  `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=false` for that process.
  Keep TLS, package signatures and lock validation enabled.
- Browser profile already open: close the owning tab and reload. Denied storage
  or private browsing can prevent safe initialization.
- Autoplay rejected: press Resume after the media is prepared.
- Linux vault unavailable: install `secret-tool` and unlock Secret Service.
- Local-only sign-out: revoke the session in Telegram Settings > Devices.
