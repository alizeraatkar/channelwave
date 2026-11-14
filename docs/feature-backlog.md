# ChannelWave future feature backlog

Implementation review date: 2026-11-06. Priorities remain proposals for product review.

ChannelWave turns audio from selected Telegram channels into a personal music library. The selected F01 → F02 → F03 milestone implements current-source listening, queue editing and automatic playback. F04 paused player recovery and local listening history/statistics now have shared implementations and automated verification; target-specific device acceptance remains separate. Priorities below are proposals for review, not a delivery schedule.

## Current baseline

Already available: Telegram authentication, channel discovery and selective history indexing, advanced local search and channel filters, favorites, multiple named playlists alongside the original listening list, browser streaming, native full-file playback adapters, file export, and docked/full-screen players with seek, volume and previous/next controls. Browser Save offline and offline reload/playback, Telegram message links, local metadata corrections, artwork, MP3 tag editing/export, browser view history and measured large-library rendering are implemented.

Native hosts and playback adapters have different levels of validation. Android background-service and media-session code and browser Media Session integration exist; physical-device background and interruption checks remain pending. Editable queues, continuous selected-source reconciliation and automatic next/shuffle/repeat are now implemented in the shared flow. Listening-session restoration, native offline collections and cache budgets have shared implementations; real-account and Android device acceptance remain separate gates. Implementation status below includes current uncommitted source changes and does not imply release or device validation; see the [verification ledger](verification.md).

## How to review and prioritize

- **P1:** proposed next features for the core listening experience.
- **P2:** follow after the core experience, or promote for the chosen launch audience.
- **P3:** optional expansion ideas to validate with users.
- **Effort:** S = a contained change; M = several connected changes; L = substantial storage, sync or platform work; XL = a new cross-device or infrastructure capability. These are relative estimates, not time commitments.
- **Origin:** Planned = part of the original product baseline; Extension = a proposed expansion. Partial means supporting implementation already exists.
- Checkboxes represent implementation completion: checked items have an implemented product flow and recorded verification within the stated platform scope. Unchecked items are incomplete; partial work and remaining gaps are noted below. Current implementation status is summarized below.

## Release prerequisites

These should be tracked alongside features and resolved for the chosen release targets.

- **G01 — Dependency and deployment readiness.** Rebuild old TDLib/tdweb deployments using the maintained browser recipe, verify the bundle/runtime, and validate production hosting and native bridge restrictions. See [security audit](security-audit.md).
- **G02 — Real account and device verification.** Complete login → choose channel → index → play/export → favorite/playlist → restart/restore, plus interruption and sign-out isolation checks, on each advertised target. See [verification ledger](verification.md).
- **G03 — Existing QA defects: resolved in the working tree.** All eight original defects in [buglist.md](../buglist.md), including transfer isolation, obscured focus/controls, offline sidebar state and browser Back navigation, have recorded synthetic browser regression coverage. See [QA fixes](verification.md) and [persistent transfer controls](verification.md). Native/device and assistive-technology verification remain separate gates.
- **G04 — Distribution readiness.** Complete the recorded Telegram eligibility and sponsored-message review, privacy/license review, target-specific packaging and signing checks before distribution. See the [architecture and release requirements](architecture-decision.md).

## P1 Core listening features

### F01 Keep selected channels up to date

- [x] Implemented in shared logic and both metadata adapters; synthetic browser/core coverage is recorded in the [verification ledger](verification.md). Live Telegram verification remains pending. **Origin:** Planned. **Effort:** L.
- Add new audio automatically while connected, reconcile edited/deleted messages and lost access, and catch up after reconnect. Users should not need to remove and re-add a source to discover newer music.
- **Done when:** reconnect finds missed tracks without duplicates; metadata edits retain favorites; removed or inaccessible entries have a clear state; progress and retry remain resumable.
- **Current status:** selected-source live updates, independent catch-up checkpoints, resumable older-audio discovery/verification, unavailable states, pause/resume and retry are implemented. Only foreground sync is enabled; Android background scheduling remains outside this milestone.

### F02 Editable playback queue

- [x] Implemented in the shared UI/coordinator with account-bound in-memory queues and synthetic browser/core coverage. Android device interaction remains pending. **Origin:** Planned. **Effort:** L.
- Show Up next with Play next, Add to queue, remove, reorder and clear actions. Preserve the sequence when changing views and allow tracks beyond the currently loaded page to be queued.
- **Done when:** button and keyboard alternatives support every edit; removing a source reconciles its queue entries; player and system commands use one shared queue. Establish this foundation before F03 and F04.
- **Current status:** Up next supports Play next, Add to queue, repeated entries, Play now, Move up/down, drag ordering, removal and Clear upcoming. The full matching collection is captured beyond loaded rows, with 40-entry queue pages. App/system actions share one coordinator. Queue restoration after restart remains F04.

### F03 Automatic next track with shuffle and repeat

- [x] Shared product flow and browser/core verification are implemented; Android ARM64 compiles, while device background/interruption acceptance remains pending under F06. **Origin:** Planned. **Effort:** M. **Depends on:** F02.
- Continue through the queue when a song ends, with shuffle, repeat one and repeat all controls.
- **Done when:** natural track completion advances correctly, repeat modes behave consistently, and unavailable tracks cannot cause an endless retry loop.
- **Current status:** natural completion, shuffle without replacement, repeat off/one/all and playback history are wired through the shared coordinator. Browser/Android completion notifications are generation-guarded; other engines have a coordinator-owned polling fallback. Unsupported entries are bounded, connection/permission errors stop traversal, and no UI component is required for advancement.

### F04 Restore the listening session

- [x] Shared implementation and automated Core/SQLite/browser verification are available; physical-device acceptance remains pending. **Origin:** Planned. **Effort:** M. **Depends on:** F02.
- Save the queue, selected track, position and playback settings so returning users can continue where they stopped.
- **Done when:** restart restores a paused session for the correct account; resuming seeks to the saved position; missing tracks recover gracefully without autoplay.
- **Current status:** account-scoped snapshots preserve duplicate entry identity, queue/shuffle/navigation order, position, repeat, output and context. The interface restores paused; Resume re-resolves and seeks media. Active native playback attaches without restarting. Checksummed fallback snapshots, periodic/immediate transactions, corruption recovery and storage-error feedback are implemented. The conditional crash-loss target and real-device limits are documented in [listening behavior](listening-experience.md) and the verification ledger.

### F05 Multiple named playlists

- [x] Implemented in the shared UI and account-scoped SQLite/IndexedDB storage; [playlist verification](verification.md) records browser coverage and native/device limits. **Origin:** Planned. **Effort:** L.
- Create, rename and delete playlists; add songs to a chosen playlist; reorder tracks and remove entries. Migrate the existing listening list without losing its order.
- **Done when:** playlists persist across restart, remain account-scoped, handle unavailable songs, and can be played in their saved order. Connect playback to F02 when available.

### F06 Background playback and system controls

- [ ] Not complete. **Origin:** Planned, partial Android implementation. **Effort:** L per native target.
- Finish and validate playback with the screen locked or the app backgrounded, including notifications, headset commands, interruption handling and OS media controls. Add browser Media Session where supported.
- **Done when:** real devices pass lock-screen, call/audio-focus, headphone removal and stop/quit checks; system actions stay synchronized with app state. Browser support must reflect actual capabilities and does not promise playback after tab closure.
- **Current status:** Android service/session controls and browser Media Session metadata/actions exist; Windows publishes playback metadata. Real-device acceptance remains pending, and Linux MPRIS/Apple system integration is incomplete. See [notification verification](verification.md).

### F07 Native offline collections

- [ ] Not complete. **Origin:** Planned; browser baseline exists. **Effort:** L.
- Bring Save offline, Offline browsing, individual removal and disconnected playback to native hosts using verified app-managed copies.
- **Done when:** saved tracks play after a disconnected restart; corrupt/missing copies show a recoverable state; source removal and sign-out erase the correct account's copies. Coordinate retention with F08.
- **Current status:** Android/Linux verified-copy adapters, native Offline browsing/repair, one-time playlist saves and scoped cleanup are implemented in the working tree. Native filesystem/core tests and ARM64 compilation pass; authenticated disconnected-restart acceptance on physical Android/Linux remains pending.

### F08 Storage usage and cache limits

- [ ] Not complete. **Origin:** Planned, partial eviction rules. **Effort:** L.
- Show storage usage, set a temporary-cache budget, clear temporary files, and distinguish cached files, saved offline copies and exported files.
- **Done when:** eviction preserves playing and explicitly saved tracks; low-space and browser-quota failures explain recovery; accounting includes duplicate TDLib/app copies. Browser eviction limits remain visible.
- **Current status:** Account → Storage, editable budgets, native TDLib eviction and browser tracked-media cleanup are implemented. Playing/offline/export-ready files are protected; browser estimates and residual managed storage are explicit. Device/quota acceptance remains a release gate.

### F09 Persistent transfer manager

- [ ] Not complete. **Origin:** Planned, partial row controls. **Effort:** L.
- Keep download/offline-save progress, cancel and retry available across views. Add bounded scheduling and restart recovery for saved transfer intent, with rate-limit handling.
- **Done when:** users can manage transfers away from their original rows, cancellation affects only the intended work, and interrupted work resumes or requests a clear retry without duplicate exports. Includes CW-QA-001 and CW-QA-007.
- **Current status:** dedicated Downloads, durable account-scoped intent, bounded scheduling, cancellation/retry, persisted retry deadlines, automatic offline-save recovery and explicit export handoff are implemented in the working tree with core/browser regression coverage. Real-account/native acceptance remains pending; see [milestone verification](verification.md).

### F10 Easier Telegram connection and recovery

- [ ] Not complete. **Origin:** Planned, partial protocol support. **Effort:** M.
- Finish QR login, code resend/countdown and delivery-method guidance; give clear recovery actions for expired codes, disconnected sessions and unavailable engines.
- **Done when:** each displayed action follows Telegram's current authorization state, retries preserve relevant input, and users never need developer configuration or credentials in the normal login flow.

## P2 Library and usability improvements

### F11 Sorting and richer local search

- [x] Implemented in shared Core/UI and SQLite/IndexedDB adapters. **Origin:** Planned. **Effort:** M.
- Sort the full matching catalog by added date, title, artist credit, publication date or duration in either direction; combine source, liked, saved-offline and advanced filters before pagination. Playlists retain Custom order and add the remaining sort options.
- Browse paginated Artists and Albums using actual local metadata. Full artist credits are preserved; albums require known album and artist labels. Group details play the full matching collection, including unloaded rows. Album order follows disc/track numbers.
- **Current status:** stable account-scoped browsing preferences, group routes with Back/Forward/reload, album text search, metadata-change invalidation and queue-order parity are implemented. Existing undated tracks use one labeled import baseline. See [behavior](discovery.md) and [verification](verification.md). Authenticated and physical-device acceptance remain separate.

### F12 Listening history and useful Home sections

- [x] Implemented in the shared UI with account-scoped listening storage. **Origin:** Planned. **Effort:** M. **Depends on:** F04 for Continue listening.
- Returning-user Home shows one current/saved listening session, distinct Recently played tracks (including partial listens), and Recently added music. Desktop previews show six rows per recent section; phone previews show three. Empty libraries retain onboarding.
- Recently added means first indexed into the local library. Pre-upgrade tracks share one initial batch labeled Existing library · Imported [date]. Home previews and navigation never autoplay; explicit Resume uses the existing paused restoration and shared queue.
- Dedicated History and Statistics remain available with collection, retention and deletion controls. Home follows retained history and resolves current metadata; unavailable tracks keep honest status. See [behavior](discovery.md) and [verification](verification.md).

### F13 Open the original Telegram message

- [x] Implemented through Telegram's exact-message link methods, with cached attribution and private/unavailable-link guidance; [message-link verification](verification.md) records browser coverage and native-launch limits. **Origin:** Planned. **Effort:** S.
- Offer Open in Telegram from track details using a validated message link; preserve visible channel attribution.
- **Done when:** public/private source links resolve through the appropriate Telegram method and inaccessible links produce a clear explanation.

### F14 Full screen Now Playing

- [x] Implemented in the shared UI with synchronized transport/seek controls, source details and responsive layouts; [player verification](verification.md) records keyboard, enlarged-text, landscape and playback-preservation coverage. Editable queue access remains dependent on F02. **Origin:** Planned. **Effort:** M.
- Expand the mini-player into a mobile-friendly view with larger transport/seek controls, source details and access to the queue.
- **Done when:** opening/closing the view preserves playback and position; touch, keyboard, large text and landscape layouts remain usable. Queue access follows F02.

### F15 Accessible navigation and keyboard shortcuts

- [ ] Not complete. **Origin:** Planned, partial. **Effort:** M.
- Complete screen-reader flows, visible focus, scalable text and keyboard playback/navigation shortcuts with discoverable help.
- **Done when:** focused controls stay above fixed overlays, enlarged text preserves all controls, and shortcuts ignore editable fields. Include CW-QA-002 through CW-QA-004 and assistive-technology testing.
- **Current status:** focus clearance, nested dialog dismissal and enlarged-text regressions are covered. Discoverable playback/navigation shortcuts and assistive-technology acceptance remain pending.

### F17 Browser routes and navigation history

- [x] Implemented for app views and built-in collections through stable `#view=` routes, Back/Forward and reload restoration; [navigation regression coverage](verification.md) includes CW-QA-006. Individual named-playlist deep links are not implemented. **Origin:** Planned. **Effort:** M.
- Give views stable routes and make Back/Forward follow in-app navigation. Add direct links to local collections where they can resolve safely.
- **Done when:** Back returns to the previous view, refreshing a route restores the view, and unavailable account-local destinations recover clearly. Includes CW-QA-006; track links must not expose session data.

### F18 Installable PWA and update controls

- [ ] Not complete. **Origin:** Planned, partial offline shell. **Effort:** M.
- Complete install metadata and supported-browser install guidance; show a safe update prompt and preserve the listening session before applying updates.
- **Done when:** installation and offline startup pass on advertised browsers; updates preserve assets used by existing tabs and restore playback paused. Session restoration uses F04.
- **Current status:** offline shell startup and automatic idle-only, multi-tab-safe updates are implemented and tested. Install metadata and paused listening-session restoration are implemented; physical installation and cross-browser acceptance remain pending. The current update flow defers while media is loaded; see [update verification](verification.md).

### F19 Large library performance

- [x] Implemented and measured in headless Edge with 10,000 synthetic tracks at phone/desktop viewport sizes: bounded rendering, incremental paging and isolated playback polling. [Performance verification](verification.md) records responsiveness results, the scroll p95 tradeoff and remaining native-device limits. **Origin:** Planned. **Effort:** L.
- Improve scrolling, search and filtering for large channel collections using measured rendering and query bottlenecks.
- **Done when:** documented device/browser checks with at least 10,000 tracks meet review-agreed responsiveness targets without breaking selection, focus or pagination.

### F20 Bulk library and offline actions

- [ ] Not complete. **Origin:** Extension. **Effort:** M. **Depends on:** F05/F09 for playlist/transfer actions.
- Select multiple tracks to favorite, add to a playlist, save offline or remove offline copies, with an explicit count and cancelable transfer work.
- **Done when:** the scope is clear across pages, per-track failures are recoverable, and protected-content restrictions apply to every selected item.
- **Current status:** multi-selection with counts and bulk playlist add/remove are implemented. Bulk favorite/offline actions and cancelable batch transfers remain pending.

### F21 Local metadata corrections and artwork

- [x] Implemented for shared browser, Linux and MAUI UI; device verification limits are recorded in [the verification ledger](verification.md). **Origin:** Extension. **Effort:** M.
- Let users correct local title/artist labels without changing Telegram messages. Display permitted embedded/channel artwork with a deterministic fallback.
- **Done when:** reindexing preserves local overrides, users can revert to source metadata, and missing artwork never becomes a fabricated album cover.
- Embedded ID3 covers from cached audio take precedence over Telegram audio thumbnails and labelled channel photos. Missing/unsupported images retain the music icon. Display thumbnails are bounded and account-scoped; indexing does not fetch artwork. [Artwork behavior and limits](artwork.md).

### F22 Duplicate recording groups

- [ ] Not complete. **Origin:** Extension. **Effort:** M.
- Group likely duplicate recordings across channels and let users choose a preferred copy while retaining source attribution.
- **Done when:** matching confidence is visible, similar titles alone do not trigger destructive merging, and favorites/playlist references remain intact.

### F29 Read and edit ID3 tags

- [x] Implemented for browser, Linux and MAUI hosts; platform verification limits remain recorded in [the verification ledger](verification.md). **Origin:** Extension. **Effort:** M. **Related:** F11/F21.
- Read embedded MP3 tags for title, artist, album, year, genre, track/disc numbers and cover artwork. Let users review and edit tags, then export a tagged copy; keep local library corrections distinct from changes written into an audio file.
- **Done when:** supported ID3 versions and editable fields are documented; Unicode tags and artwork round-trip correctly; unrelated tags and audio remain intact; missing or malformed tags have safe fallbacks. Reading tags does not trigger bulk media downloads during channel indexing. Writing changes preserves the original file and Telegram message, respects protected-content restrictions, and reports unsupported formats clearly.
- [Compatibility and user workflow](id3-tags.md). Local corrections and cover display are documented under [F21 artwork](artwork.md).

## P3 Expansion ideas

### F23 Sleep timer

- [ ] Not complete. **Origin:** Extension. **Effort:** S for foreground; native background behavior requires F06.
- Stop playback after a chosen interval or at the end of the current track.
- **Done when:** the remaining timer is visible, can be canceled, and fires reliably in each supported playback mode.

### F24 Playback speed and recording bookmarks

- [ ] Not complete. **Origin:** Extension. **Effort:** M.
- Add speed controls and named position bookmarks for long recordings, mixes and spoken audio.
- **Done when:** supported engines retain speed across track changes, bookmarks survive restart, and unsupported capabilities are clearly handled.

### F25 Smart local playlists

- [ ] Not complete. **Origin:** Extension. **Effort:** M. **Depends on:** F05/F11; history rules also need F12.
- Save rules such as Favorites from selected channels, Recently added or Unplayed tracks, with predictable local results.
- **Done when:** users can inspect/edit rules and understand when membership changes; unavailable metadata does not produce misleading matches.

### F26 Portable library backup and restore

- [ ] Not complete. **Origin:** Extension. **Effort:** L. **Depends on:** F05.
- Export/import playlists, favorites, selected source references and local preferences, with a preview and merge choices.
- **Done when:** backups exclude Telegram credentials and audio by default, imports validate versions and account identity, and unavailable references are reported before applying changes.

### F27 Multiple Telegram account profiles

- [ ] Not complete. **Origin:** Extension; storage already account-scoped. **Effort:** L.
- Add intentional account switching with separate libraries, playlists, sessions, offline files and cleanup controls.
- **Done when:** switching cancels or pauses the right work, account data never leaks across profiles, and removing one profile leaves others intact.

### F28 Optional cross-device library sync

- [ ] Not complete. **Origin:** Extension. **Effort:** XL. **Depends on:** F05/F26.
- Explore opt-in synchronization of favorites, playlists and listening position. Choose the storage/service model and conflict rules before implementation.
- **Done when:** a reviewed design covers privacy, authentication, offline edits, conflicts and deletion; a two-device test demonstrates convergence without transferring Telegram sessions or restricted media.

## Suggested starting order

Resolve remaining G01/G02/G04 prerequisites alongside feature work; G03's original defects are fixed with browser regression coverage. The **F01 → F02 → F03** milestone is implemented in the working tree within the verification scope above. F04 now implements paused restart restoration with its verification limits recorded separately. F05's named-playlist flow is implemented. Put **F06** into the first milestone if mobile background listening is the launch requirement; promote **F07–F09** if offline use is the primary goal. Complete target-specific acceptance checks before treating an item as shipped.

## Implementation status

Implemented: **F01, F02, F03, F04, F05, F11, F12, F13, F14, F17, F19, F21, F29**,
within the platform scopes and verification limits above. Web and Android are the
primary acceptance targets; physical-device acceptance remains pending.

## Repository references

- [Current functionality and limitations](../README.md)
- [Interface design](../DESIGN.md)
- [Architecture and release requirements](architecture-decision.md)
- [Verification evidence and remaining gates](verification.md)
- [Dependency and application audit](app-audit.md)
- [Existing QA defects](../buglist.md)

The current baseline uses the README, verification ledger and working-tree implementation where older planning descriptions differ. The P3 group and entries marked Extension are proposed product ideas.
