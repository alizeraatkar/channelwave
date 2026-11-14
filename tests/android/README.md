# Android playback checks

`PlaybackDeviceTests.cs` is an opt-in Debug activity. It plays the original synthetic MP3 fixture, without connecting to Telegram or changing account data. It checks the foreground media notification, system metadata, background playback, song restoration after activity destruction, media-session Pause/Play/Seek/Next, and notification removal on Stop. Competing audio-focus requests check that duckable notifications keep playback and the media session playing, position advances, and the latest user volume is preserved when focus returns. Exclusive temporary interruptions still pause and resume automatically; manual pause, already-paused playback, permanent focus loss and Stop suppress automatic resume. These checks use Android's audio-focus service rather than an actual notification sound or phone call. They do not measure audible attenuation; verify the lowered volume and its restoration on a device with a notification sound.

Build the MAUI Android project with `ChannelWavePlaybackTest=true`, `RuntimeIdentifier=android-x64`, `EmbedAssembliesIntoApk=true`, and `AndroidUseAssemblyStore=false`, plus the usual SDK, JDK and TDLib paths. Install the signed APK with `adb install --no-incremental -r`, wake/unlock the emulator, then run:

```powershell
adb shell am start -n dev.channelwave.app/dev.channelwave.app.PlaybackTests
adb shell run-as dev.channelwave.app cat cache/channelwave-playback-test-result.txt
```

The report ends with `RESULT: PASS` or an exception and `RESULT: FAIL`. Run it on an idle development emulator because it replaces the current playback selection. Rebuild with `ChannelWavePlaybackTest=false` and reinstall afterwards. Ordinary and Release builds exclude the activity and fixture. Preserve app data when installing so developer credentials and sessions remain intact.

On 2026-10-16, these checks passed on the Android 16 x86_64 emulator. This establishes native playback and UI lifecycle behavior with synthetic audio; authenticated Telegram downloads and physical-device interruption behavior need separate validation. Assembly-store packaging caused a Mono metadata parsing crash in this emulator's Debug test build; the standard individual-assembly layout passed.

For isolated playback regression runs, enable both `ChannelWaveLibraryPerformance`
and `ChannelWavePlaybackTest`, install the fixture APK, and run:

```powershell
node tests/android/run-playback-checks.mjs artifacts/android-playback
```

This runner only targets `dev.channelwave.app.performance`. It explicitly launches
Main after the activity-destruction checkpoint to model the user reopening the
app; Android 16 may block the test activity's own background launch. It records
whether that foreground launch was needed alongside the unmodified device report.

## Library and search performance

Use a dedicated Android 16 x64 emulator in portrait. Build the opt-in Debug fixture with the usual Android SDK and JDK properties. The fixture installs as `dev.channelwave.app.performance` and seeds 10,000 tracks into its own SQLite database on every process launch. It uses the regular MAUI library and metadata scanner with synthetic Telegram and media inputs.

```powershell
dotnet build ChannelWave.slnx --no-restore
dotnet restore src/ChannelWave.Host.Maui --locked-mode -p:ChannelWaveTarget=net10.0-android -p:ChannelWaveLibraryPerformance=true
dotnet build src/ChannelWave.Host.Maui -c Debug -f net10.0-android --no-restore `
    -p:BuildProjectReferences=false -p:ChannelWaveTarget=net10.0-android -p:ChannelWaveLibraryPerformance=true `
    -p:RuntimeIdentifier=android-x64 -p:EmbedAssembliesIntoApk=true -p:AndroidUseAssemblyStore=false `
    "-p:ChannelWaveTdlibAndroidRoot=$PWD/artifacts/tdlib/android"
adb -s emulator-5554 install --no-incremental -r <fixture-apk>
node tests/android/measure-library-performance.mjs artifacts/android-library-performance/after
```

Build the baseline in a separate checkout with the same fixture and trace scopes, direct SQLite calls, and full search-cache invalidation on writes. Install that APK and run the same command with `artifacts/android-library-performance/before`. Keep the emulator image, configuration, and host load comparable across the two builds. The script defaults to five trials per condition and accepts `CHANNELWAVE_EMULATOR` and `CHANNELWAVE_PERF_RUNS`.

The runner launches and seeds the app once per metadata condition. Each trial reopens Tracks at the first page and records Android app frame deadlines during scroll and paging, WebView keydown and query submission to the next painted state, WebView layout and paint traces, and managed SQLite and background-refresh timings. The runner types a short prefix, then fills and submits the complete query in one browser task so query timing excludes the 250 ms debounce. Compare results separately with the metadata scanner idle and active. Keep the raw samples under ignored `artifacts/`.

## Home session regression checks

The same isolated 10,000-track fixture can measure Home tab changes with the metadata scanner idle and active:

```powershell
node tests/android/measure-home-performance.mjs artifacts/home-session/android-after
```

Use `CHANNELWAVE_PERF_RUNS` (default 5) and `CHANNELWAVE_EMULATOR` to select the sample count and dedicated emulator. To compare an older fixture APK, set `CHANNELWAVE_HOME_BASELINE=1` and choose a separate output directory; that mode records the old reset behavior instead of asserting preserved state. Keep the APK, emulator configuration and host load comparable. The runner records tab-to-paint latency and scroll/selection restoration, then checks Home-key backgrounding and screen sleep/wake on the updated build. Managed traces and screenshots are written beside the samples; a failed run attempts to save its synthetic page and logs.

These measurements are separate from the existing Tracks/search benchmark. Activity recreation, physical-device behavior and authenticated Telegram acceptance require separate checks. Do not interpret an interrupted ADB/WebView run as passing any lifecycle checks.

## Scrolling and navigation regression checks

`measure-browsing-performance.mjs` uses the same isolated fixture and real ADB
swipes. It records five trials per metadata condition: Home list/grid scrolling,
Settings/Home paint latency and restored scroll, Tracks scrolling/paging, and
Android Back. Android frame samples, WebView traces, managed logs, screenshots,
and environment details are saved alongside `samples.json`.

```powershell
$env:CHANNELWAVE_PERF_BASELINE = '1'
node tests/android/measure-browsing-performance.mjs artifacts/android-lag/before
Remove-Item Env:CHANNELWAVE_PERF_BASELINE
# Install the updated fixture before the comparison run.
$env:CHANNELWAVE_SOAK_SECONDS = '600'
node tests/android/measure-browsing-performance.mjs artifacts/android-lag/after
Remove-Item Env:CHANNELWAVE_SOAK_SECONDS
```

Baseline mode records a failed Back check as `backRestored: false` and explicitly
recovers the test UI so later measurements can proceed; this is a failure, not a
pass. Failed scroll restoration is recorded as `restoredCorrectly: false`.
Updated runs fail immediately on either condition. The optional soak repeats
scrolling, Settings/Home navigation, tab changes, scroll restoration and bounded
row checks with metadata enabled. Both runs also check background/foreground and
sleep/wake. Warm measurements begin after the initial collection load; startup
work is excluded. Keep emulator configuration and host load comparable and do
not run builds or other test suites during measurements.

History saves, scroll clamping after Home is removed, delayed observer disposal,
and the managed viewport render acknowledgment are covered by
`tests/browser/scroll-persistence.spec.mjs`. These tests do not require Telegram
credentials.

## Natural completion and lost EOF notifications

Enable both `ChannelWaveLibraryPerformance=true` and `ChannelWavePlaybackTest=true`
when building the isolated Debug APK, with embedded assemblies, the native TDLib
directory, and `AndroidUseAssemblyStore=false`. Install it on `emulator-5554`, then run:

```powershell
python tests/android/run-completion-matrix.py
```

The matrix runs 15 cases serially against the real Android MediaPlayer and the
shared coordinator: Repeat All in foreground/background/screen-off states, Repeat
Off/One, shuffle, delayed preparation, MP3 EOF after seeking, Pause/Resume, very
short WAVs, the real offline store, and simulated Telegram download responses
through the real media resolver. Screen-off cases wait for Android to report
`mWakefulness=Asleep`; sending the sleep key alone is not counted as verification.

The `missing` cases deliberately unsubscribe the native Completion handler while
leaving the real player running to EOF. They verify recovery from a lost event;
they do not establish that Android spontaneously loses callbacks on a physical
device. Startup, paused/interrupted playback, unknown duration and a stop in the
middle of a recording must not be mistaken for EOF. Those guards, duplicate/stale
signals, and failure of the final position sample are covered in the C# suite.

Reports, bounded process logcat, power state and `summary.json` are written under
ignored `artifacts/android-completion/after`. Use `run-completion-checks.py` with
`--scenario`, `--screen` and `--output` for a single case. The activities and fake
transport hooks are excluded from normal and Release builds. These tests use only
the separate performance app and synthetic account 700; they do not log in to a
real Telegram account or modify the ordinary app's account data.
