# Security audit and remediation — 2026-11-13

This is source/dependency analysis with synthetic regression and isolated runtime
checks, not a penetration test or a guarantee that the app is vulnerability-free.
The review covers the current working tree, including existing uncommitted work.
No account credentials, private Telegram data, production changes or history
rewrites were used. Separate authenticated/device acceptance remains required.

| Assessed severity | Source findings | Remediation status |
| --- | --- | --- |
| Critical | 0 | No demonstrated critical application exploit found. |
| High | 1 | Replacement browser artifacts rebuilt and locally validated; old deployments are unchanged. |
| Medium | 4 | Erasure, Linux permissions, published headers and pre-decode artwork budget addressed. |

## Threat model and boundaries

ChannelWave is a local-first client, not a hosted account API. The web host serves
static Blazor assets; Telegram authorization and data live on the user's device.
A Telegram session has broader account privileges than the selected music
channels. Protecting its keys, origin, native bridge and downloaded files matters
even when the UI requests only music. Native keys use platform vaults; plaintext
library metadata/media rely on OS isolation. Browser IndexedDB relies on a
dedicated trusted origin and is accessible to scripts/extensions authorized there.

Reviewed attacker inputs include channel labels, tags/artwork/audio, Telegram
links, file/range requests, competing tabs, an untrusted framing origin, local
network peers and a second OS user. A compromised OS, vault or deployment origin
is outside the isolation guarantee. Source publication and binary distribution
have separate requirements: no matched secrets in Git does not validate a binary.

## Findings and changes

### CW-SEC-001 [High] Obsolete browser engine build dependencies

Evidence: `scripts/tdweb.Dockerfile:1` formerly used Emscripten 3.1.1 and invoked
upstream's OpenSSL 1.1.0l/Node 14 build. The 2026-11-13 upstream lockfile audit
reported 51 flagged package entries: 9 critical, 27 high, 12 moderate and 3 low.
These are graph entries, including development dependencies, not 51 demonstrated
browser exploits. OWASP A03:2025, software supply chain.

Before: run upstream webpack/development dependency tree using its old lockfile.
After: `vendor/tdweb-build` is a separately committed manifest/lock overlay with
esbuild 0.28.2 and localforage 1.10.0. Browser-native BroadcastChannel and randomUUID
replace old broadcast/UUID dependency trees. The pinned TDLib protocol revision
is retained. The new build uses digest-pinned Emscripten 7.0.0, checksum-verified
OpenSSL 3.5.8, and Node 24 supplied by the pinned SDK image. Dynamic JavaScript
execution is disabled in the generated engine. Wrapper logging and secret chats
remain disabled. Source drift causes patching to fail.

The emitted manifest identifies compiler/crypto/Node versions and payload SHA256
checksums. `bundle-inputs.json` records production bundle reachability;
`dependency-lock.json` and notices accompany generated assets. The build/publish
commands verify that only the reviewed localforage/lie/immediate runtime inputs
are bundled. The new graph's current advisory scan reports zero findings.

Retest: the complete engine rebuilt successfully; checksum and bundled-input
verification passed. Under enforced CSP, the real worker returned the pinned
version/commit, initialized a synthetic empty profile and closed it, with all
external networking blocked and no phone/code/password supplied. The build used
Node 24.19.0. Emscripten 7 filesystem exports are adapted to the pinned wrapper's
former namespace without changing IndexedDB names or the Telegram protocol.
Old distributed browser artifacts must still be replaced. OpenSSL 1.1.0 has
been unsupported since September 2019.
[OpenSSL support/advisory history](https://openssl-library.org/news/vulnerabilities-1.1.0/)

### CW-SEC-002 [Medium] Interrupted sign-out could lose erasure intent

Evidence: `src/ChannelWave.UI/Diagnostic.razor`, `LogOut` and `RestoreCleanupAsync`.
Previously the first durable checkpoint followed remote shutdown. Closing/crashing
the app during preparation or shutdown could leave metadata/offline audio with
no recovery marker. This affects interrupted actions, not completed logout.
OWASP A01:2025/A04:2025, access control and private-data protection.

Before: await preparation/logout, then save `{Account, RemoteLogout}`.
After: save `{Account, RemoteLogout, Phase:"requested"}` before preparation;
advance to `"closed"` only after observed closure. An omitted phase is interpreted
as a legacy closed checkpoint. Unknown phases fail verification. Recovery blocks
normal account restoration and offline reads. `RecoverLogOutAsync` initializes
only the saved profile, suppresses identity/discovery/update consumers and waits
for actual closure before session erasure. An already erased profile is not
recreated. Initialization failures retain the checkpoint and retry safely.

Checkpoint write failures never prevent attempted logout/erasure; independent
cleanup failures stay visible and retryable. If storage rejects both the marker
and erasure, crash recovery cannot be guaranteed: the user must keep the app open
and retry as instructed. Remote revocation is claimed only when confirmed.

Retest: existing audit suite and new interruption/initialization tests pass.
The account-retry baseline also exposed a UI race: ready was published before
automatic identity resolution was marked pending. Starting resolution first fixes
the unchanged three-attempt regression without weakening its assertion.

### CW-SEC-003 [Medium, conditional] Linux profile was readable under permissive umask

Evidence: `src/ChannelWave.Host.Linux/Program.cs:19`, formerly
`Directory.CreateDirectory(root)`. With umask 022 and traversable home ancestors,
other OS users could read unencrypted library/history/media. Native vault keys
were not stored in plaintext. OWASP A01:2025/A04:2025.

After: `LinuxProfileSecurity.Prepare(root)` runs before profile lock/database/TDLib
initialization. It creates mode 0700, opens the directory with O_NOFOLLOW and
O_DIRECTORY, verifies its owner via descriptor-based statx, and applies fchmod
0700 to that descriptor. Existing profile contents are protected by the root's
traversal restriction; no recursive symlink-following chmod is performed.
Failure prevents profile startup. Exported Downloads remain outside this policy.

Retest: Linux SDK container, umask 022, a second OS user could read synthetic
metadata before protection and could not afterward. Symlink and foreign-owner
profiles were rejected without changing the target. Command:
`bash scripts/verify-linux-profile.sh /absolute/repository` in a root-owned
isolated Linux environment (the verifier needs runuser/chown).

### CW-SEC-004 [Medium] Missing web CSP and framing restrictions

Evidence: `scripts/publish-vercel.mjs`, generated Vercel routes. Passive HEAD of
the documented production URL on 2026-11-13 confirmed absent CSP and
X-Frame-Options, with HSTS/nosniff present. This was missing defense in depth;
source scanning did not demonstrate a metadata-to-script injection path.
OWASP A02:2025, security misconfiguration.

After: `scripts/web-security.mjs` supplies an enforced CSP and DENY framing header
for every published response. Scripts come from self with wasm-unsafe-eval only;
objects, child frames, form submission and parent framing are disabled. Only the
five production WebSocket DC hosts in pinned ConnectionCreator.cpp are permitted.
Workers/media/images receive necessary self/blob/data allowances. Dynamic style
attributes are allowed for Blazor/Bit; JavaScript unsafe-inline/unsafe-eval are
not. Referrers are suppressed, unused sensitive device permissions are disabled,
and HSTS/nosniff remain. The local verification server uses the same header module.
[Microsoft Blazor CSP guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0)

Retest: published page/worker headers, inline script blocking, a real cross-origin
framing rejection with an unprotected control frame, synthetic login, real WASM
and offline playback/reload passed. The live deployment has not been changed;
post-deployment header/update verification remains required.

### CW-SEC-005 [Medium] Artwork pixel budget was enforced after decoding

Evidence: `src/ChannelWave.Core/Id3Codec.cs`, `ArtworkMime`/`ValidPng`, and
`src/ChannelWave.UI/wwwroot/artwork.js`, `thumbnailArtwork`. Compressed byte limits
and per-dimension bounds alone admitted headers requesting up to a billion pixels;
the browser's 64-megapixel check happened after `image.decode()`. A malicious
selected source could request excessive decoder allocation. No destructive
out-of-memory experiment was performed. OWASP A06:2025, insecure design/resource limits.

After: shared PNG/JPEG header validation enforces the existing 64-megapixel policy
before any host invokes image decoding. Multiple supported JPEG frame headers
cannot conceal an oversized frame. The existing post-decode guard remains defense
in depth. CRC-correct oversized PNG and JPEG header regressions verify rejection
without allocating pixel buffers. Platform codec patching and broader malformed
format handling remain independent of this bounded guard.

## Six-layer results

| Layer | Result | Evidence and limits |
| --- | --- | --- |
| Dependency vulnerabilities | New locks and rebuilt runtime checks pass | 17 native/shared NuGet lockfiles, 232 unique package/version pairs; root npm and separate tdweb graph audited against current feeds. OS/native codec inventory is separate. |
| Secrets | No recognized matches | All reachable Git history, index and nonignored working files; redacted bounded patterns, Unicode text coverage, no claim about all binary/encrypted content. |
| Injection/integrity | No confirmed injection path found | Parameterized SQL values, Razor text encoding, bounded tag/container reads, restricted JPEG/PNG artwork, validated Telegram links and file/export paths. Existing malformed-media tests apply. |
| Authentication/access control | Sign-out gap fixed | Native random vault keys, pinned engine before authentication, fresh account/message checks, account-qualified stores, cleanup-only recovery. Static host has no JWT/cookie account endpoints to authorize. |
| CORS/origin policy | Restricted by actual boundary | No general application CORS API. Cast's wildcard CORS is scoped by private receiver IP plus a random per-load capability; it is not a public account API. Web origin protection adds CSP/framing rules. |
| Data protection/logging | Linux and erasure fixes implemented | Native log suppression; browser wrapper suppression; origin-local session storage; owner-only Linux profile. Native metadata/media remain unencrypted by design; device backup/vault checks remain platform gates. |

## Release safeguards

`scripts/security-check.py` scans reachable history/index/current files, platform
lockfiles and distributable artifacts. Feed failures and high/critical dependency
findings fail release checks. Diagnostics omit matched values. Archive/path and
inspection-budget failures also fail; the scanner never extracts untrusted archives.
Unicode and archive edge cases have synthetic regressions.

Artifact guards run before Android/desktop/Apple external publication and final
release packaging. Web staging excludes local configuration by default; the
existing explicit opt-in permits only validated public application apiId/apiHash,
never arbitrary developer JSON. Native registration has the same exact schema.
Existing platform signing verification remains separate from the content guard,
which checks database/session/signing material and secret patterns independently
of dependency auditing. The required security workflow retains read-only permissions
and pinned Actions. Browser rebuilding/CSP/WASM and Linux/Cast isolation checks
are required alongside the advisory/history job. No signing/account secrets are
available to those checks.

`SECURITY.md` defines reporting and maintenance limits. Enable GitHub private
vulnerability reporting in repository settings before making the source public.
No settings, deployment, signing, publication or credential rotation was performed.

## Verification and remaining gates

- Shared build and core baseline: 241 tests passed before remediation.
- Final managed core suite: 251 passed. The shared build completed with zero
  warnings/errors; Release static publishing and its 280-file artifact guard passed.
- Session remediation: 18 focused core/session checks; 14 browser audit/recovery
  checks plus the final three interruption reruns passed during implementation.
- Linux second-user/profile and actual Cast HTTP capability/receiver/range/revocation
  checks passed in isolated Linux SDK containers with synthetic data.
- Python CI suite: 65 tests passed; the Node artifact-tamper test passed. Current
  NuGet/root npm/tdweb feeds reported zero advisories. History/index/current-text
  scanning reported no recognized secrets. Published browser totals and command
  evidence are recorded in `docs/verification.md`.
- Generated browser engine rebuild, pinned runtime probe and enforced-CSP/offline
  checks passed for the new staged artifacts. Previously distributed engines are
  not upgraded by these local changes.
- Real Telegram delivery/remote revocation, actual native vaults and backup,
  native WebView navigation, physical casting/codec behavior and signed-install
  acceptance must be exercised independently on Android, iOS, Mac Catalyst,
  Windows and Linux. A Linux/container or synthetic browser pass is not that evidence.
- Recheck live headers/service-worker updates after a separately authorized deployment.
  Browser-origin compromise/extensions and device-owner access remain inherent limits.
- Native system libVLC/plugins and bundled/runtime dependencies need platform SBOM
  and current vendor-advisory review; npm/NuGet scans do not cover them.
