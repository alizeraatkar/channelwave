# ChannelWave

A local-first music client for your Telegram channels, built with .NET, Blazor
and Bit.BlazorUI. Select channels to build a searchable library; your library,
playlists and listening history stay on your device.

## Features

- Telegram sign-in, channel discovery, selective indexing and source updates.
- Search by song, artist, album or source; favorites, playlists and custom Home tabs.
- Browser streaming, native playback, an editable queue, shuffle and repeat.
- Offline saves, file exports, transfer recovery and storage controls.
- Local listening history, activity statistics and paused player restoration.
- Embedded artwork and metadata, local corrections, MP3 tag editing and export.
- A shared desktop/mobile interface with persistent playback and audio-output selection.

## Platform status

ChannelWave is in development. No target is release-validated.

| Target | Host | Current verification |
| --- | --- | --- |
| Web/PWA | Standalone Blazor WebAssembly | Automated Edge tests; local offline, worker and real-engine checks |
| Android | MAUI Blazor Hybrid | ARM64 packaging and x64 emulator checks; physical-device acceptance pending |
| Windows | MAUI Blazor Hybrid | Compilation; native playback/device acceptance pending |
| Linux | Photino.Blazor | Shared build, TDLib probe and isolated profile/Cast checks; desktop acceptance pending |
| iOS / macOS | MAUI / Mac Catalyst | Managed adapter and linking checks; signing and physical-device acceptance pending |

See [verification](docs/verification.md) for dated results and limits.

## Run locally

Install .NET SDK **10.0.302**, Node **24**, and Git. Docker is needed to build
the pinned browser TDLib runtime.

```powershell
dotnet restore ChannelWave.slnx --locked-mode
dotnet build ChannelWave.slnx --no-restore
Copy-Item src/ChannelWave.Host.Web/wwwroot/channelwave.example.json src/ChannelWave.Host.Web/wwwroot/channelwave.local.json
```

Get an API ID/hash from [Telegram's developer portal](https://my.telegram.org/apps)
and set them in the local JSON file. It is ignored by Git. Browser application
registration values are publicly inspectable; never include account passwords,
login codes or sessions.

Build the browser engine, then start the app:

```powershell
./scripts/build-tdweb.ps1
dotnet run --project src/ChannelWave.Host.Web --no-launch-profile --urls http://127.0.0.1:5188
```

Open `http://127.0.0.1:5188`, connect your account, find your channels and add
the sources you want in your library. Indexing does not download audio.

Both native and browser engines must match TDLib commit
`c15d3f5a5de6e3ba5839822c451152e5e18bb700` (1.8.68). The app rejects mismatched
engines. Builds and generated binaries stay outside source control.

## Development and documentation

- [Contributing and tests](CONTRIBUTING.md)
- [Native setup, engine builds and troubleshooting](docs/development.md)
- [Architecture](docs/architecture-decision.md) and [interface design](DESIGN.md)
- [Privacy and storage](docs/privacy.md)
- [Listening history and player recovery](docs/listening-experience.md)
- [Search and collections](docs/discovery.md)
- [Web deployment](docs/vercel.md) and [native releases](docs/releases.md)
- [Security policy](SECURITY.md) and [security audit](docs/security-audit.md)
- [Feature backlog](docs/feature-backlog.md)

## Known limitations

Indexing, reconciliation and metadata jobs run while the app is alive. Browser
storage may be reclaimed by the browser; exported copies are managed outside the
app. Native playback prepares a complete file; browser streaming needs HTTPS or
localhost and service-worker support. Platform background audio, casting, vaults,
signing and data-preserving upgrades need real-device validation.

Authenticated Telegram flows, Telegram client eligibility and sponsored-message
obligations, native dependency licensing, and store acceptance remain release
requirements. The [security audit](docs/security-audit.md) records fixes and
remaining deployment/platform checks.

Automatic binary releases are disabled by default in this repository. See
[release setup](docs/releases.md) before enabling publishing.

## License

Original project code is available under the [MIT license](LICENSE).
Third-party components retain their own licenses; see
[dependency notices](vendor/THIRD-PARTY.md).
