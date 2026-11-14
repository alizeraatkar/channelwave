# Contributing to ChannelWave

Use .NET SDK **10.0.302** from `global.json`, Node **24**, and Python 3 for
release-tool checks. Native hosts also need platform workloads and TDLib
libraries. See [development setup](docs/development.md).

## Build and test

Run from the repository root:

```powershell
dotnet restore ChannelWave.slnx --locked-mode
dotnet build ChannelWave.slnx --no-restore
dotnet run --project tests/ChannelWave.Core.Tests --no-restore
node scripts/verify-td-schema.mjs
npm ci --ignore-scripts
npm run test:browser
python -m unittest discover -s tests/ci -v
node --test tests/ci/tdweb-security.test.mjs
python scripts/security-check.py secrets
```

Browser tests start their own host and default to installed Edge. To use Chromium,
run `npx playwright install --with-deps chromium` and set
`$env:CHANNELWAVE_BROWSER='chromium'`. Set `CHANNELWAVE_TEST_PORT` if port 5188 is
already in use. Rebuild after changing web assets so worker integrity hashes match.
Real-engine checks require rebuilt tdweb assets and `CHANNELWAVE_VERIFY_TDWEB=1`.

## Repository layout

- `src/ChannelWave.Core`: domain models, contracts, playback and library behavior.
- `ChannelWave.Telegram`: protocol, discovery and indexing.
- `ChannelWave.Telegram.Native`, `ChannelWave.Storage.Sqlite`, and
  `ChannelWave.Platform.Browser`: platform adapters.
- `ChannelWave.UI`: shared Razor components and web assets.
- `ChannelWave.Host.Web`, `.Linux`, and `.Maui`: application hosts.
- `tests/ChannelWave.Core.Tests`, `tests/browser`, and `tests/ci`: automated tests.
- `scripts`, `vendor`, and `tools/ChannelWave.Probe`: builds, pinned contracts,
  dependency notices and runtime verification.

`ChannelWave.slnx` covers shared, web and Linux development.
`ChannelWave.Native.slnx` separates the MAUI workloads.

## Changes

Follow `.editorconfig`: UTF-8, LF, final newline, four spaces; JSON/YAML use two.
C# uses file-scoped namespaces, nullable references, PascalCase types/members,
camelCase locals/parameters, and `Async` suffixes. Match neighboring Razor and
JavaScript conventions. Keep platform behavior in adapters and package versions
in `Directory.Packages.props`. Update lockfiles only for intentional dependency changes.

Use xUnit `[Fact]`/`[Theory]` tests in `*Tests.cs` and Playwright `*.spec.mjs` tests.
Add regression coverage for behavior changes, especially account isolation,
persistence, cancellation and playback. Keep fixtures synthetic. Record platform
limits and actual verification results in [verification](docs/verification.md).

Keep commits focused. Pull requests should explain behavior changes, link relevant
issues, list validation and platform limits, and include screenshots for UI changes.

## Credentials and dependencies

Keep real credentials, login codes, session data, databases, generated binaries and
signing keys out of Git and test logs. Use ignored `channelwave.local.json` for
local application configuration. Preserve the pinned TDLib revision, schema checks,
account-scoped storage boundaries and third-party notices. Report vulnerabilities
using [SECURITY.md](SECURITY.md).
