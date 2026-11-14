# Application audit history

The October 2026 audits used Edge, synthetic Telegram transports, original test
audio and isolated IndexedDB/SQLite stores. Results describe those revisions;
current test results and release requirements are in [verification](verification.md).

## 2026-10-16: lifecycle and persistence

- **A01 / P1 — Account lookup failure permanently ends Telegram updates.** Evidence: `TelegramSession.ProcessUpdatesAsync` awaited `getMe` inside its outer reader try/catch. Injecting a 500 response left the account disconnected with Connect disabled; later updates could not be processed. Root cause: recoverable identity initialization shared the lifetime of the sole update consumer. Repair: independent bounded identity retries, explicit UI retry, per-update error isolation, and cancellation/stale-result guards. Status: fixed; core regressions verify automatic/manual recovery, malformed updates, late/superseded identities, file delivery, and closure; browser regression verifies the retry UI and subsequent sign-out.
- **A02 / P1 — Sign-out cleanup failures cannot be retried.** Evidence: `Diagnostic.LogOut` discarded its local account identifier after logout, canceled the component token, and stopped before session cleanup when library cleanup failed. The new browser regression failed because no retry action existed. Root cause: cleanup state depended on live authorization and one sequential success path. Repair: retain the cleanup account/completed stages, attempt independent library/session cleanup, hide stale collections, and expose idempotent retry before reconnecting. A checkpoint in reserved account-zero app metadata preserves recovery across reload without a schema migration; unreadable cleanup state blocks offline access until verification succeeds. Status: fixed; regressions cover both deletion failures in the open app and after reload, no false success, blocked engine startup, metadata/audio erasure, reconnect and a subsequent logout, failed-checkpoint-read recovery, and SQLite retention of the checkpoint during account erasure.
- **A03 / P2 — Canceled browser export still downloads.** Evidence: holding the final `readFile`, canceling, then releasing it exported `fixture.wav` while the UI said “Download canceled.” New regression observed one anchor click instead of zero. Root cause: canceling JS interop does not cancel the JavaScript side effect. Repair: explicit export generation invalidation during cancellation/source removal/sign-out/disposal, checked after reads and before export, with URL cleanup. Status: fixed; browser regressions verify zero export clicks/downloads after Cancel, sign-out, and source removal.
- **A04 / P3 — Privacy claims contradict offline storage.** Evidence: privacy documentation denied shell caching and persistence requests, while `audio-worker.js` caches public assets and `saveOffline` writes audio and requests persistence. Repair: document actual account-scoped audio storage, public-shell caching, cleanup, and eviction limits. Status: corrected; checked against the implementation.
- **A05 / P1 — Full checkpoint storage prevents sign-out before cleanup starts.** Evidence: default job pause writes state before remote logout; a failed write exits the sign-out handler before session revocation or erasure. Root cause: unnecessary checkpoint persistence while erasing the same account. Repair: stop jobs without writing pause checkpoints on sign-out. Status: fixed; browser regression denies checkpoint writes and verifies remote logout and local erasure still succeed.
- **A06 / P2 — Shell updates invalidate assets still needed by open tabs.** Evidence: the real service worker against two synthetic deployment manifests returned 404 for an old lazy assembly after an update; expected cached bytes were lost. Root cause: unconditional `skipWaiting` followed by old-cache deletion and `clients.claim`. Repair: keep updates waiting while old tabs exist, then activate/cache-clean normally; offline setup accepts an already active shell instead of waiting on its replacement. Status: fixed; regression verifies old cached assembly delivery, offline setup during a waiting update, and new-shell activation/cache cleanup after old tabs close.
- **A07 / P2 — Offline saves revert concurrent favorite edits.** Evidence: favorite a song while Save offline waits for download, complete the transfer, then read the saved offline row: `favorite` was false although the library row was true. Root cause: saving the stale track captured at download start. Repair: read the current account-scoped library row and persist metadata/audio in one IndexedDB transaction. Status: fixed; regression verifies the durable offline favorite and displayed state after the concurrent edit.


The final run passed 48 core/SQLite tests, 69 Edge checks and 21 schema signatures.
The shared build had no warnings/errors. Windows incremental compilation passed;
the earlier full resource build emitted two PRI249 FLUENT qualifier warnings.
Release static hosting checks covered offline reload/playback and cleanup recovery.

## Browser-engine dependency finding

The October upstream tdweb dependency graph had 53 advisory entries, including
9 critical entries. Development dependency flags did not establish bundle reachability;
no corresponding application exploit was demonstrated. The original build used
Emscripten 3.1.1, Node 14 and OpenSSL 1.1.0l.

The November 13 remediation replaced that build graph/toolchain while retaining
the pinned protocol revision. Rebuilt local assets were checked; old deployments
still require replacement. The [security audit](security-audit.md) records the
replacement recipe, findings CW-SEC-001 through CW-SEC-005 and remaining gates.

## 2026-10-13: export and library behavior

Complete-file browser reads now request the expected size and reject empty or
truncated responses. Completed files are reused only after fresh permission and
account checks. Tests exercise the pinned upstream FileManager's memory and
IndexedDB paths and compare repeated exports byte for byte. The final run passed
49 browser tests and 43 core tests. One live export reached the handoff success
message, but final disk placement was not confirmed.

## Findings fixed

| Finding | Fix and evidence |
| --- | --- |
| Favorites, channel filters, and listening lists only searched the first 100 loaded songs. | Filter the full catalog before taking a page. Browser regressions use 150 songs and a later source; SQLite checks use 10,000 songs and account isolation. |
| The listening list displayed repository order, accumulated duplicate IDs, and lacked removal. | Preserve insertion order, deduplicate membership, expose a checked removal action, and verify removal survives reload. |
| Failed listening-list writes changed the visible count and displayed success. | Publish membership after the durable write and report the failure locally. Injected full-storage failure verifies no membership or success message leaks through. |
| Searching while paused disabled Next/Previous. | Capture the loaded playback sequence when a song is selected; browsing no longer replaces it. Removing a source also prunes this sequence. |
| One transient audio-state exception permanently stopped progress polling. | Catch failures per polling tick, retain later updates, and discard results from superseded selections. Injected failure followed by a position update verifies recovery. |
| Seek and volume exceptions escaped UI handlers. | Report recoverable player errors. Injected failures verify the app remains usable; Stop is also guarded. |
| Rejected phone input was cleared before users could correct it. | Clear authentication input after successful submission only. Rejected-phone regression preserves the entered value. |
| Empty favorites offered channel setup despite an existing library. | Offer Show all tracks when sources already exist; keyboard regression verifies the recovery path and page focus. |
| Fixed mobile navigation overlapped the player at 200% text size. | Observe navigation/player heights and use them for fixed offsets and content clearance. Check control bounds, no horizontal overflow, and no navigation/player overlap. |
| Overlapping library refreshes could publish stale results. | Snapshot query/filter parameters and publish only the latest refresh for the current account. |
| A secondary refresh failure during source-removal recovery could escape the error handler. | Preserve the removal error and catch the secondary refresh error. |
| The optional real-engine test targeted a diagnostics button hidden by the redesign. | Navigate through Account → Connection details before probing. |

The first five targeted browser regressions failed against the original implementation and passed after the fixes. The text-size overlap was found through screenshot review, then covered by an explicit overlap assertion.


## Limits

The original October checks did not establish authenticated flows, native vaults,
physical playback, deployed headers or assistive-technology acceptance. Later
targeted device evidence and current failures are recorded in verification.
