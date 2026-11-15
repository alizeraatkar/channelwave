# Verification

Results are dated and scoped to the tested revision and environment. Synthetic
Telegram fixtures do not establish authenticated Telegram behavior. Native
compilation, isolated container checks and package inspection do not establish
physical-device acceptance. No target is release-validated.

## 2026-11-15: semantic release automation

Release regression tests cover commit-based patch/minor/major planning, real Git
history ranges, stable promotion prerequisites, stable asset integrity, independent
beta/stable reruns, legacy beta recovery, draft exclusion and prevention of stable
version rollback. Stable promotion has not been exercised against a public stable
release; this setup keeps that readiness decision manual. Existing artifact,
signing and same-commit validation gates remain required.

## 2026-11-15: hosted Windows audio

Beta.2 built all packages but its Windows smoke test failed at audio load. An
isolated runner probe reproduced error `C00D36FA` with zero render endpoints;
installing Media Foundation alone did not resolve it. With the pinned, signed
virtual CI output installed, the same MP3 decoded to 30.067 seconds and playback
advanced. The release job now provisions that endpoint before testing the actual
packaged application. No audio checks are skipped and the driver is not shipped.

## 2026-11-15: canceled tag-read fixture

The tag-read fixture waits for the replacement TDLib download callback after
cancellation. Previously the rendered Cancel button could precede the new request,
letting the test complete a stale callback and leave the current read pending.
All nine tag-editing cases passed in Linux Chromium. The cancellation case also
passed three repetitions with the new request explicitly gated until the old
callback was cleared. Existing cancellation, read-only-format and protection
assertions are retained; no retries or timeouts changed.

## 2026-11-14: first installer release test

The clean-directory packaging test exposed a Windows artifact scan that ran before
its publish folder existed. The scan now runs after publishing and before the
native probe and archive creation. Integration tests execute the packaging script
with synthetic publish output and the real artifact scanner: a first package is
created successfully, and secret-bearing output is rejected before archiving.
These tests verify packaging order and the security gate; native compilation and
startup remain separate hosted release checks. All 69 Python CI tests pass locally.
Run them with `python -m unittest discover -s tests/ci -v`; PowerShell 7 (`pwsh`)
is required for the packaging cases.

The first hosted beta completed Android and Linux packaging but exposed MAUI's
numeric Windows version requirement. Windows now uses its numeric base version and
global build number for package identity, retaining the beta in assembly
informational metadata and archive names. A fresh MAUI Release rebuild generated
identity `0.1.0.8` with beta.2 assembly metadata. Verified desktop native libraries
are cached before application packaging so later failures retain the native build.

## 2026-11-14: initial GitHub Actions fixes

The first public-repository run failed Windows MAUI restore because the SDK's
implicit library-packs source supplied package variants with different hashes.
`DisableImplicitLibraryPacksFolder` keeps application restores on configured
feeds. Existing versions, lockfile hashes and signature checks are retained.
An SDK-evaluation regression verifies exclusion and an explicit opt-in control.

Apple WebKit and Chromium/Edge library tests also assumed the pre-redesign
header and an entirely visible short list. They now verify the initial row's
player clearance and the last row's full visibility after scrolling the content
pane. Picker, focus, target-size and overflow assertions remain in place.

Local validation before the fix push: clean-cache locked Windows restore and
Release MAUI build passed (two existing PRI249 resource warnings); all six WebKit
UI checks, four Edge library-layout checks and 66 Python CI tests passed.
Hosted cross-platform status is recorded by the repository's Actions run.

The subsequent Chromium run reached the legacy listening-route reload tests and
exposed a fixture race: a pending render could replace the synthetic alias URL
between separate replaceState and reload commands. The fixture now initiates the
reload in the same browser task and asserts the new document's initial alias.
Existing canonical-route, history-index and Back/Forward assertions remain.
Both aliases passed three local repetitions each after this correction.

The next Edge run passed 242 tests before a playlist clearance assertion measured
an offscreen selection after the screenshot setup reset scrolling. The test now
scrolls the selected row into view before measuring its clearance; all four
viewport cases passed locally. Eight reconnect cases also passed after explicit
startup readiness checks. Reload tests wait for the app shell before checking
restored playlist state, preserving their existing state and navigation assertions.

The Edge job took about 37 minutes to reach 242 of 347 cases. Chromium and Edge
now each run four Playwright shards within the original 40-minute job budget;
WebKit keeps its focused Apple UI suite. Evidence artifacts include the shard
number to avoid collisions. Per-test deadlines, zero retries, assertions and
required checks are unchanged. Native builds for all four platforms, the shared OS matrix,
WebKit and both security jobs passed in the preceding hosted run.

The ID3 round-trip fixture now supplies consistent replacement audio sizes to
single-message reads, batch audits, source searches and completed-file reads.
Previously, a background source audit could overwrite the replacement's indexed
size with the original fixture size and correctly trigger the media-change guard.
The tests check that export preserves source bytes and indexed labels before
substituting the export for read-back; rereading that new source updates automatic
labels. Both ID3 versions passed three local repetitions each. The remaining
33 transfer, activity and responsive-layout cases passed locally.

Playwright test enumeration confirms that the four shards cover all 347 full-suite
cases exactly once (89, 85, 88 and 85), with no missing or duplicate cases.

The sharded run passed every native build, shared OS check, Edge shard, WebKit
and both security jobs, and exposed two Linux Chromium layout issues. On phones
up to 360 pixels wide, the status label now uses the brand column's full width.
A conflicting hover rule also removed the title button's 44-pixel minimum height,
causing a nine-pixel row-height oscillation during viewport measurements. The
touch-target rule now applies during hover; the viewport algorithm is unchanged.
The variable-row tests assert the 44-pixel target and unchanged row height after
hover. Both viewport cases and the responsive header case passed in isolated
Linux Chromium matching the pinned Playwright version.

## 2026-11-13: Android scrolling and navigation

Environment: Android 16 x64 emulator (`emulator-5554`), WebView 133, .NET SDK
10.0.302, Node 24.15.0. The isolated `dev.channelwave.app.performance` fixture
seeded 10,000 synthetic tracks. The ordinary app's data was preserved.

The investigation reproduced history IPC flooding and WebView navigation
throttling, overlapping Home observer binding/disposal, and a viewport that
announced stability before its managed rows had rendered. Scroll persistence now
waits for idle or an explicit navigation/lifecycle capture. Departing Home views
cannot save scroll clamping from their replacement screen, and viewport stability
waits for the requested managed window revision to appear in the DOM.

| Check | Result |
| --- | --- |
| Shared .NET tests | 266 passed |
| Final Home/navigation/virtualization browser run | 29 passed, including eight focused persistence/render regressions |
| Native measurements | Five idle and five metadata-active trials; Home list/grid, Tracks, Settings/Home and Android Back |
| Home scroll restoration | Baseline 5/20 failed, typically shifting about 260 CSS pixels; updated 20/20 restored exactly |
| History writes during 30 four-swipe sequences | 2,802 before; 1 after |
| Sustained browsing | 255 cycles over 601 seconds; no app errors, throttling warnings, lost positions or unbounded rendered rows |
| Background/foreground and sleep/wake | Passed in both completed updated conditions |
| Synthetic native playback | `RESULT: PASS`, including media controls, focus, background playback and activity recreation |

Pooled idle Tracks frame-deadline overrun at p95 fell from 0.533 ms to 0.130 ms
(76%), exceeding the 30% target in that scenario. Idle Home list p95 also moved
from 0.595 ms late to before the deadline. These are deadline overruns, not total
frame durations. Other pooled p95 frame changes were under 0.27 ms; this single
emulator is sensitive to host/GPU scheduling. Navigation first-paint timings did
not improve uniformly: metadata-active Grid p95 rose from 151 ms to 180 ms.
The strongest findings are elimination of per-frame history writes and restored
scroll correctness; these results do not establish physical-device performance.

The playback harness needed two corrections: wait for the system artwork update
instead of reading the notification immediately, and explicitly reopen Main from
ADB to model a user launch after destruction. Android 16 blocked the test
activity's own background launch. No background-launch exemption was granted.
The final report records that the explicit foreground launch was used.

Completed evidence is under ignored `artifacts/android-lag/`: `matched-before/`,
`matched-after-idle/`, `matched-after-metadata/`, `native-playback/` and
`verification.json`. Earlier failed/interrupted diagnostic runs are excluded from
the final acceptance results. Cold/first-use navigation is recorded separately
from warmed trials. See [Android test instructions](../tests/android/README.md).

## 2026-11-13: open-source cleanup

Tools: .NET SDK 10.0.302, Node 24.15.0, Python 3.11.15, Windows/Edge.

| Baseline check | Result |
| --- | --- |
| Locked restore and shared solution build | Passed; zero warnings/errors |
| Core executable | 265 passed, 1 failed, 266 total |
| TDLib schema | 37 signatures passed at the pinned revision |
| Python security-scanner tests | 30 passed |
| Node browser-artifact tamper regression | 1 passed |
| Secret scan | 1,362 reachable objects, 425 current files; no recognized matches |
| Locked browser tooling install | Passed; 0 advisories in the root test graph |

The core baseline failure is
`SecuritySignoutTests.Recovery_does_not_report_erasure_when_profile_initialization_fails_and_can_retry`.
It throws `IOException: Synthetic saved-profile initialization failure` during
the retry path. This predates cleanup. The final run passed all 266 tests without changing
the recovery implementation; the baseline failure remains an intermittent
regression to investigate if it recurs.

The sandbox denied Windows Service Control Manager access during .NET startup;
restore/build/tests ran with normal host access. npm initially failed DNS in the
sandbox; locked installation succeeded with network access. These restrictions
were not worked around by changing dependencies or disabling validation.

Post-cleanup validation:

- Shared solution build: zero warnings/errors.
- Core executable: **266 passed**, no failures or skips (91.319 seconds).
- Python CI suite: **65 passed**; Node artifact-tamper regression: **1 passed**.
- Pinned schema: **37 signatures passed**.
- Final reachable-history/index/current-file scan: **1,362 objects**, **430 files**,
  no recognized secret matches (`artifacts/opensource/secrets-final.json`).
- Browser baseline: **64 passed** before stopping for the final rebuild.
- Frozen full browser run: **66 passed, 1 failed, 278 not run** under CI
  fail-fast (14.2 minutes). The desktop design journey clicked before startup
  had accepted source selection, then waited for the completed-index label.
  Its setup now uses the existing connect/source helpers, which verify persisted
  selection before continuing. After that fixture correction, **all 10 focused design/redesign tests passed**,
  including the desktop/mobile journeys and 320/390/820/1440px navigation.
  The remaining full-suite cases were not rerun; a complete green browser suite
  is not established by this cleanup.
  Full-run summary: `artifacts/opensource/browser-full-summary.json`.
  Focused JUnit/HTML: `artifacts/opensource/validation-source/test-results/browser.xml`
  and `artifacts/opensource/validation-source/playwright-report/`.
- Documentation link targets, JavaScript syntax and diff whitespace checks passed.
- Desktop/phone artwork captures were visually inspected; controls and text remained
  readable and no horizontal overflow was observed.

The source cleanup removes three unused private browsing helpers, with no callers,
interop attributes, serialization or DI roles. Public interfaces and saved formats
are unchanged. Screenshot producers and CI now use `artifacts/browser-review/`.
Existing local review output was retained under ignored `artifacts/opensource/`.
Additional scroll-persistence cases appeared during validation, followed by
changes to navigation.js and rebuilt UI assemblies. Concurrent work was left
intact; the final validation copy freezes source and public engine assets without
copying ignored developer credentials or account data.
MIT covers original project code; third-party notices remain intact.

## Reproduce checks

From the repository root:

```powershell
dotnet restore ChannelWave.slnx --locked-mode
dotnet build ChannelWave.slnx --no-restore -m:1 -nr:false
dotnet run --project tests/ChannelWave.Core.Tests --no-restore
node scripts/verify-td-schema.mjs
npm ci --ignore-scripts
npm run test:browser
python -m unittest discover -s tests/ci -v
node --test tests/ci/tdweb-security.test.mjs
python scripts/security-check.py secrets --report artifacts/security/secrets.json
```

Browser tests start an isolated host and use synthetic sessions. Edge is the
default; see [contributing](../CONTRIBUTING.md) for Chromium and port selection.
Rebuild before browser tests after changing shared code or web assets. Reports
are written to `test-results/` and `playwright-report/`; visual captures use
`artifacts/browser-review/` and the feature-specific `artifacts/` directories.

To check the actual browser engine, rebuild with `./scripts/build-tdweb.ps1`,
set `CHANNELWAVE_VERIFY_TDWEB=1` and run `npm run test:browser -- tdlib.spec.mjs`.
That probe blocks external traffic and supplies no account credentials.
For enforced-header/published-output checks, follow [web deployment](vercel.md).
Advisory feeds require network access:
`python scripts/security-check.py dependencies --report artifacts/security/dependencies.json`.
An unavailable feed is not a pass.

## Previous verification

These results retain useful historical evidence; they are not reruns of this cleanup.

| Date | Area | Evidence and limits |
| --- | --- | --- |
| 2026-11-13 | Security remediation | Shared build passed, 251 core tests and 68 distinct published Edge checks passed; 65 Python CI tests and artifact tamper test passed. The 17 NuGet locks (232 package/version pairs), root npm and maintained tdweb graph reported zero advisories at that time. Full findings are in the security audit. |
| 2026-11-13 | Rebuilt browser engine | Pinned TDLib 1.8.68, Emscripten 7.0.0, OpenSSL 3.5.8 and build Node 24.19.0; checksums, bundle inputs, Release publish and actual worker version/init/close passed under enforced CSP with external networking blocked. |
| 2026-11-13 | Linux and Cast isolation | Linux SDK container checks verified owner-only profile access, symlink/foreign-owner rejection, Cast receiver/token restrictions, HTTP ranges and revocation using synthetic data. No physical receiver was exercised. |
| 2026-11-13 | Playback continuation | Shared build and 266 core tests passed in the earlier implementation run. Android packaging passed using cached official Google AARs. No attached Android device was available; actual screen-off/background continuation was not reproduced. The current core rerun above supersedes the earlier green total. |
| 2026-11-11 | PWA and deployment | 11 focused PWA/update tests, 37 schema checks and local Release publish passed. Live Edge checks verified HTTP 200, MIME/cache behavior, missing-asset 404s, responsive layouts and offline reopening on the documented Vercel deployment. This predates security-header remediation and does not verify current deployed CSP. |
| 2026-11-11 | Local Android 0.2.18 | Signed ARM64 package built and inspected; device installation, authenticated operation and upgrade safety were not established by packaging. |
| 2026-11-09 | Home/navigation | Shared build and 228 core tests passed. One focused browser run stopped after source discovery timed out; that run was not green. |
| 2026-11-07 | Home session state | 228 core tests and 100 targeted browser scenarios passed across runs; several audio cases needed isolated reruns. Matched Android performance trials were interrupted by emulator/ADB failures. |
| 2026-11-06 | Search/performance | 223 core and 23 focused Edge checks passed. Windows SQLite cold caller time fell from 41.9 ms to 0.02 ms after moving queries off the dispatcher; total query time was 416 ms vs 406 ms. A 50,000-track IndexedDB fixture measured 128/164 ms adapter-to-DOM and 430/451 ms input-to-DOM including debounce. Android matched trials were incomplete. |
| 2026-11-06 | Outputs/casting | Shared and Windows builds, Android Debug packaging and Apple managed adapters compiled. Synthetic picker/queue cases passed at 390/1440 px. The broader run stopped after streaming, sign-out and artwork failures. iOS bridge still requires Xcode/device acceptance. |
| 2026-11-06 to 2026-11-09 | Pixel 8 output routing | Earlier signed updates installed in place. Playback through Galaxy Buds2 Pro and speaker/Bluetooth switching were observed, including paused position preservation. Initial speaker switching was rejected on 0.2.10; subsequent targeted checks passed. These observations do not validate current Cast, background or sign-out behavior. |
| 2026-10-30 to 2026-11-02 | Offline/transfers/listening | Automated checks covered durable transfer intent, explicit export handoff, offline saves, storage budgets, history/statistics and paused restoration; details are in the behavior docs and tests. Low-space/device acceptance remains open. |
| 2026-10-16 | Lifecycle audit | 48 core/SQLite and 69 Edge tests passed, including a real worker probe with external networking blocked. See the application audit for defect history. |
| 2026-10-13 | Offline live check | One existing browser profile retained a saved recording after reload and played beyond 35 seconds while Telegram was disconnected. Network access was not disabled in that live profile; automated offline fixtures explicitly disable it. |

Historical local evidence is under ignored `artifacts/security/`,
`artifacts/listening/`, `artifacts/home-session/`, `artifacts/search/`,
`artifacts/android-library-performance/` and versioned Android package directories.
Those files are not supplied by a source checkout; reproduce results with the
tracked test/build scripts. Screenshots in `docs/design/` use synthetic content.

## Platform acceptance and release gates

- **Web:** check authenticated login/reconnect, protected media, actual quotas,
  cross-browser installation, accessible interactions, and worker updates on the
  deployed HTTPS origin. Local security fixes do not update old deployments.
- **Android:** validate current signed installation/upgrades, native vault/backup,
  foreground/background/screen-off continuation, notifications, focus interruptions,
  Bluetooth/Cast and disconnected restart on physical devices.
- **Windows/Linux:** validate real desktop audio, output changes, keyrings, native
  WebView navigation, offline restart, packaging and system dependency compatibility.
- **Apple:** validate static linking and iOS Cast bridge with Xcode, signing,
  provisioning, AirPlay/Cast, background transitions and physical devices.
- **Security:** retain durable sign-out failure coverage, verify remote revocation,
  enable private vulnerability reporting, and review native/system SBOMs and codec
  advisories. Pattern scans do not inspect all encrypted or arbitrary binary content.
- **Distribution:** complete Telegram client eligibility/sponsored-message review,
  third-party binary licensing, platform signing and store review. Source licensing
  is separate from binary distribution requirements.

See [security audit](security-audit.md), [privacy](privacy.md),
[release operations](releases.md) and [runtime acceptance](development.md#runtime-acceptance).

## 2026-11-13 — Native EOF recovery on the emulator

The earlier Android timing-gate correction did not establish a physical-device
fix. Ordinary native EOF scenarios passed here without spontaneously reproducing
the reported intermittent failure. Two failure paths were then verified with
controlled faults:

- Dropping only MediaPlayer's Completion notification left the real player stopped
  on the first song, without an error or preparation in progress, for the 20-second
  test deadline. The old periodic monitor depended on the same event-populated
  Ended flag and could not recover. Evidence: ignored
  `artifacts/android-completion/missing-before/missing-off.txt`.
- A failed final position read discarded an otherwise valid completion in the
  shared coordinator. `Completion_still_advances_when_the_final_position_read_fails`
  failed before the correction and passes afterwards.

Android monitoring now independently recognizes a stopped, previously observed
playing session at a known EOF (50 ms timestamp-rounding allowance). Explicit
Pause, unload and focus interruption invalidate this detection. Startup, unknown
or invalid duration, stops in the middle and obsolete playback tokens do not infer
EOF. Native Completion remains authoritative even with inaccurate timestamps.
The shared coordinator also treats the final position sample as optional when
processing a valid completion; cancellation and generation checks remain active.

Verification after the correction:

- All 276 C# tests passed. Shared/web/Linux solution build passed without warnings
  or errors with SDK 10.0.302.
- Android 16 x86_64 `emulator-5554`: all 15 completion matrix cases passed with 153
  playback starts. Eleven screen-off cases each recorded `mWakefulness=Asleep`.
  Cases cover repeat modes, shuffle, backgrounding, slow preparation, real MP3 EOF,
  Pause/Resume, short WAVs, native offline storage and simulated Telegram download
  responses through the real media resolver. Dropped-event recovery passed in
  foreground, background and screen-off cases. Raw reports, power states and
  `summary.json`: ignored `artifacts/android-completion/after/`.
- Existing native playback/lifecycle suite also returned `RESULT: PASS`, covering
  transient/exclusive/duckable focus, notification controls and artwork, Stop,
  backgrounding and Activity recreation. The harness explicitly foregrounded Main
  after recreation; evidence: `artifacts/android-completion/lifecycle-after/`.
- Standalone ARM64 Release APK `0.2.19-playback-test2` (versionCode 29) published
  successfully with isolated build outputs. Original release signer, pinned TDLib,
  embedded assemblies, Telegram API configuration, absent test activities and
  16 KB ZIP alignment were verified. Ignored output:
  `artifacts/playback-test/ChannelWave-playback-test2-arm64.apk`.

All emulator work used the separate synthetic performance app. No real Telegram
account was connected. Callback loss was deliberately injected, not observed
spontaneously; these results identify and repair recovery gaps, but do not prove
the exact cause of the user's physical-device occurrence. Unknown-duration media
still needs its native completion notification. Physical-device and authenticated
Telegram acceptance remain separate.
