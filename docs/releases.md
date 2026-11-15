# CI and beta release operations

`ChannelWave pipeline` validates every branch push and pull request. Automatic
installer releases are enabled for `alizeraatkar/channelwave`: each successful push
to `main` publishes a new beta for Android ARM64, Windows x64 and Linux x64 after
same-commit validation and every enabled package job succeeds.

Other repositories must configure the required credentials and set
`CHANNELWAVE_ENABLE_RELEASES=true` to enable publishing. Set it to `false` to pause
publishing while keeping validation active. Tag pushes do not trigger a second
pipeline. The stable validation
check is `checks / Required checks`; require it on main where repository branch
protection is available. A protection API permission/plan restriction must be
reported rather than changing repository visibility.

## Policy and versions

`.github/release.json` owns the initial version floor, toolchain, platform
settings and Android build floor. Until the first stable release, pushes continue
its beta cycle (`0.1.0-beta.4`, `0.1.0-beta.5`, ...).

After a stable release, the pipeline reads every commit since its tag and chooses
the next version using [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

- `fix: ...` ? patch (`0.1.0` ? `0.1.1-beta.1`).
- `feat: ...` ? minor (`0.1.0` ? `0.2.0-beta.1`).
- `feat!: ...` or a `BREAKING CHANGE:` footer ? major (`0.1.0` ? `1.0.0-beta.1`).
- Documentation, maintenance and untyped commits default to patch so every push
  still produces a downloadable beta.

The highest-impact change since the last stable release wins. Further pushes
increment the beta counter for that calculated base. A feature added during a
patch beta cycle starts a minor beta cycle. The configuration's version floor can
be raised to target a particular milestone such as `1.0.0`.

To publish a stable release, open **Actions ? ChannelWave pipeline ? Run workflow**,
select `main`, set **release-channel** to **stable**, and leave **recovery-tag**
empty. Main HEAD must already have a complete published beta. The pipeline verifies
that beta's manifest and uploaded asset digests, reruns validation, and rebuilds
installers from that exact commit with a higher internal build number. It then
publishes the stable version (for example `0.1.0`) and marks it latest. Stable
publication is an explicit readiness decision; successful CI alone keeps a beta.
This automation setup does not itself publish a stable release.

The next push automatically moves to the next semantic version; no version-file
edit is needed. Android uses an always-increasing internal build number across
both channels, and Windows keeps numeric package metadata. Published files and
tags are immutable. Failed reservations leave intentional gaps. Reruns reuse the
original reservation for the same commit and channel, including older betas after
a stable release has been published. A manual beta dispatch with no recovery tag
continues to validate only.

Main runs serialize with an expanded concurrency queue (100 pending runs maximum).
PR/branch runs cancel superseded validation. The queue does not promise commit-time
ordering; reservation order determines version numbers. If a run is canceled or the
queue fills, rerun the original main run after capacity returns. Do not dispatch a
new unreserved release for a different commit to disguise the missed run.

## Required credentials

Android keeps the existing four signing secrets: ANDROID_KEYSTORE_BASE64,
ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS and ANDROID_KEY_PASSWORD. Never rotate
the app signing identity casually: an incompatible key prevents installed APKs from
upgrading. All release hosts use TELEGRAM_API_ID and TELEGRAM_API_HASH, application
registration identifiers that are embedded in distributed binaries. Account login
codes, passwords, session databases and local proxy configuration are never inputs.

Apple distribution is disabled pending onboarding. Configure these Actions secrets:
APPLE_CERTIFICATES_BASE64 (a PKCS#12 containing the needed distribution identities),
APPLE_CERTIFICATES_PASSWORD, APPLE_PROFILES_BASE64 (ZIP of provisioning profiles),
ASC_KEY_BASE64 (App Store Connect .p8 key), ASC_KEY_ID and ASC_ISSUER_ID. Configure
repository variables APPLE_SIGNING_IDENTITY_MACOS, APPLE_PROFILE_MACOS,
APPLE_SIGNING_IDENTITY_IOS, APPLE_PROFILE_IOS, ASC_APP_ID and ASC_INTERNAL_GROUP_ID.
The group must be internal. Developer ID signing/notarization and App Store
distribution provisioning require the corresponding Apple Developer setup.
Complete the app's export-compliance declarations in App Store Connect during
onboarding. The pipeline does not invent an encryption exemption; a compliance hold
fails delivery until Apple reports the build available for internal beta testing.

Before enabling either Apple target, generate/review its lockfile, pass its unsigned
build/native checks, validate signing/provisioning, run the signed package on a
supported device, and verify notarization or internal TestFlight assignment. Enable
the target through a reviewed policy change. Once enabled, missing/expired credentials,
processing failures and signing failures fail the release rather than skipping it.

Temporary signing keys live on dedicated ephemeral runners and are removed in
`always()` cleanup. Do not add signing or release permissions to pull-request jobs,
use `pull_request_target` to execute PR code, or broaden workflow-token permissions.
Credential rotation must first pass a validate/build run and preserve signing identity.

## Recovery

If a job fails before runner assignment with no steps, read the check annotations.
GitHub can refuse new jobs because account payments failed or the Actions spending
limit was reached. The account owner must restore billing/budget or provide suitably
isolated runners. Do not bypass the release gate or mark these checks successful.
After service is restored, rerun validation and the original release workflow.

For a transient failed job, use GitHub's rerun failed jobs on the original main run.
Artifact names remain stable within a run; reservations are read from immutable
tags. For an expired artifact, rerun validation and rebuild before publication.
For a reserved older main commit, dispatch the pipeline on main with `recovery-tag`
set to that reserved beta or stable tag. The resolver verifies main ancestry and validates/builds the
reserved SHA, never the current branch head in its place.

Draft uploads resume only when existing assets have the exact expected sizes and
SHA256 digests. A different rebuilt binary, unexpected asset or tag mismatch stops
publication. Investigate and retain evidence; never overwrite a public asset or move
a tag. If an unpublished draft must be discarded, delete only that draft after
review and rebuild the complete reserved release. Published releases are never
amended; corrections ship as a new beta. TestFlight reruns reuse a matching uploaded
build and wait for processing before assigning the internal group.

Rollback means distributing a previous verified release package. Preserve user data,
application identity and signing keys; do not delete sessions or downgrade storage
formats blindly. A future data migration requires separate rollback design.

Android normally rejects an APK with a lower `versionCode` ([Android versioning](https://developer.android.com/studio/publish/versioning)).
For an installed Android regression, restore the previous application behavior in
a new reviewed main commit and let the pipeline publish a higher build number
with the original signing identity. Do not uninstall the app to bypass downgrade
protection: that can erase its local account data. Desktop rollback can replace
application binaries with the previous verified package while retaining profile
directories and secure-storage entries; verify storage compatibility first.

## Dependency and workflow changes

SDK, Node, workload set and Xcode are pinned. Application NuGet caches are separate
from workload caches and restore only from nuget.org with lock validation. A raw
signed nupkg ZIP SHA512 is different from NuGet's signature-independent content hash;
compare `dotnet nuget verify --all` content hashes, not ZIP hashes, with lockfiles.
Never disable NU1403 checks or regenerate hashes to hide a mismatch.

Use `Native lockfile bootstrap` to generate new platform/RID graphs on the correct
host. Review and commit only intended lockfiles; ordinary builds use locked mode.
Native cache keys include source/toolchain/build scripts and cache hits undergo
checksum, revision, architecture and notices checks. Invalidate the cache when any
native toolchain input changes. Native dependencies must match the supported OS
baseline; Linux binaries are built on Ubuntu 24.04 rather than a newer glibc image.

Linux dependency verification checks every shipped `.so` and fails on unresolved
libraries. The single recorded exception is `liblttng-ust.so.0` in the portable
runtime's `libcoreclrtraceptprovider.so`: .NET explicitly tolerates this optional
OS tracing provider failing to load, while Ubuntu 24.04 provides the incompatible
LTTng `.so.1` ABI. See the [upstream runtime loader](https://github.com/dotnet/runtime/blob/v10.0.6/src/coreclr/pal/src/misc/tracepointprovider.cpp).
Do not symlink incompatible ABIs or exempt TDLib, audio or WebKit dependencies.
The sanitized library report records this exception; all other missing libraries
remain fatal. Normal .NET EventPipe diagnostics and application playback are
unaffected by the absence of optional LTTng tracing.

Run `python -m unittest discover -s tests/ci -v`, shared/core/schema checks and the
browser suite before merging. Workflow validation uses actionlint; version 1.7.12
does not yet recognize GitHub's documented concurrency `queue` key, so its one known
schema warning requires checking against GitHub server validation. Do not suppress
other workflow errors. Retain synthetic failure traces/reports for 14 days and release
artifacts for 30 days. Never upload developer profiles or credentials as diagnostics.

Browser suites use synthetic isolated Telegram fixtures. The separately selected
WebKit job covers the shared Apple UI at four widths, large bilingual text, picker
selection/dismissal/focus, favorites, listening lists and metadata editing. It blocks
browser service workers because Apple hosts use native storage/audio; browser
service-worker/streaming adapters retain full Linux Chromium and Windows Edge suites.
Native Apple linking and signed-device playback are separate requirements. Suites
validate unsigned iOS ARM64 and Mac Catalyst ARM64 Release/native linking on every
pipeline; these are compile checks, not simulator or physical-device execution.
Browser suites
stop on the first failure for fast diagnostics; a green full-suite job still executes
every selected test, with no retries. Tests start their own server; use
CHANNELWAVE_TEST_PORT to avoid a local development server. Rebuild assets after any
JS/CSS change so service-worker integrity hashes match the files being served.

The separately selected
`CHANNELWAVE_VERIFY_TDWEB=1` integration test requires freshly built/audited pinned
tdweb assets; ordinary suite results do not claim real TDLib/browser authentication.
Native initialization/audio diagnostics perform no login. Android package signature,
identity and version checks do not establish physical-device upgrade/data safety.
Record actual device, signing and authenticated-Telegram limits in verification.md.

## Windows audio on hosted runners

The Windows package smoke test needs a render endpoint. Its disposable hosted
runner installs Media Foundation and a checksum-pinned, signature-verified
[Scream virtual audio driver](https://github.com/duncanthrax/scream). The driver is
used only by CI and is not included in ChannelWave packages. Provisioning fails
if no endpoint appears; decoding and playback checks remain required.
