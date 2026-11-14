# Android releases

The [Android release workflow](../.github/workflows/android-release.yml) builds a signed **ARM64 APK for Android 10+**, including TDLib at the repository's pinned revision. It runs the core/Telegram/SQLite tests and schema verification before building, verifies the native checksum and ABI, checks that the APK contains the exact native library, and verifies the APK signature and [16 KB ZIP alignment](https://developer.android.com/tools/zipalign) before upload.

## One-time repository setup

In GitHub **Settings → Secrets and variables → Actions**, add these repository secrets:

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64 encoding of your release keystore file |
| `ANDROID_KEY_ALIAS` | Alias of the signing key inside that keystore |
| `ANDROID_KEY_PASSWORD` | Signing key password |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `TELEGRAM_API_ID` | Your Telegram application's positive numeric API ID |
| `TELEGRAM_API_HASH` | Your Telegram application's 32-character API hash |

Use the existing signing key if the app has already been distributed. Otherwise, create a key once with JDK 21's `keytool` (password prompts are interactive):

```powershell
keytool -genkeypair -keystore channelwave.keystore -alias channelwave -keyalg RSA -keysize 3072 -validity 10000
```

Back up the keystore and passwords securely; updates must use the same signing identity. Keystore files are ignored by Git. Do not commit signing material or real Telegram configuration.

With the authenticated GitHub CLI, upload the keystore directly from PowerShell without printing it:

```powershell
gh secret set ANDROID_KEYSTORE_BASE64 --body ([Convert]::ToBase64String([IO.File]::ReadAllBytes("$PWD/channelwave.keystore")))
gh secret set ANDROID_KEY_ALIAS --body channelwave
gh secret set ANDROID_KEY_PASSWORD
gh secret set ANDROID_KEYSTORE_PASSWORD
gh secret set TELEGRAM_API_ID
gh secret set TELEGRAM_API_HASH
```

The workflow writes Telegram app credentials into the APK's `telegram.json` resource. These identify the distributed application and can be extracted from the APK; never use account passwords, login codes, session data, or a private local proxy configuration here. Signing material is kept in the runner's temporary directory and removed even if publishing fails. Only APKs, dependency notices/build metadata, and checksums are uploaded.

The repository or organization must permit GitHub Actions and allow this workflow's release job to request `contents: write`. The build jobs request only `contents: read`. No personal access token is needed.

## Build and release

Commit and push the workflow, Android lockfile, project changes and native build scripts together. Test the setup first through **Actions → Android release → Run workflow**, using a version such as `0.1.0-beta.1`. Download `android-release` from that run's artifacts. Manual runs build and sign the APK without creating a GitHub release.

Push a version tag on the commit to distribute:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Tags use `vMAJOR.MINOR.PATCH`, optionally followed by a prerelease suffix such as `v0.2.0-beta.1`. Successful tag runs create a GitHub release with generated notes and attach:

- `ChannelWave-<version>-android-arm64.apk`
- `TDLib-Android-notices.zip` (dependency licenses, native build manifest and native checksum)
- `SHA256SUMS` (APK and notices checksums)

Tags with a prerelease suffix create a GitHub prerelease. The workflow fails if that release already exists, preserving existing release assets; use a new tag for a new release. APK version names come from the tag/manual input. Android version codes use `github.run_number`, which increases for each new run of this workflow; rerunning a run keeps its version code. If migrating an already distributed app, ensure this counter exceeds its previous version code before distributing an update.

The pinned native library is cached by the native build inputs. Native compilation runs on a dedicated runner that frees unused preinstalled toolchains and checks for at least 24 GiB of free disk space. APK packaging runs on a separate runner and downloads the verified native artifact, so the Docker build and MAUI/Android SDK installations do not compete for disk space. A successful native job saves its cache even if later APK packaging fails.

The first run or a native dependency change rebuilds TDLib and OpenSSL from source and can take substantially longer. The host stage builds only TDLib's `prepare_cross_compiling` target, then removes its temporary objects; it does not build a Linux TDLib distribution before the Android build. No native binaries are committed.

## Local ARM64 reproduction

Use SDK 10.0.302, JDK 21, Android SDK and Docker. The native build script works in PowerShell 7 on Windows and Linux:

```powershell
./scripts/build-tdlib-android.ps1 -Abi arm64-v8a
dotnet restore src/ChannelWave.Host.Maui -p:ChannelWaveTarget=net10.0-android -p:ChannelWaveAndroidRelease=true --locked-mode
```

`ChannelWaveAndroidRelease=true` selects `android-arm64` and the checked-in `packages.android-release.lock.json`; the Windows lockfile remains separate. Publish the MAUI project with `-c Release -f net10.0-android`, the same Android properties, `ChannelWaveTdlibAndroidRoot`, and your own signing properties. Password properties support environment-variable references for APK publishing; see [Microsoft's Android command-line publishing guide](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0). The existing native build script still defaults to the x64 emulator ABI when `-Abi` is omitted. Each native export's root manifest and checksums describe its most recently built ABI.

This workflow distributes APKs through GitHub Releases. Device installation, authenticated Telegram use and playback still require device validation. See [verification status](verification.md) for the recorded evidence and remaining platform limits.
