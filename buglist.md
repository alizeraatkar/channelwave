# ChannelWave QA bug list

Test date: **2026-10-16**, Asia/Tehran. Report prepared at approximately 09:31.

**Current result (2026-10-17): all 8 reported defects fixed; 0 remain open.** The original QA pass confirmed 8 defects: 6 P2 and 2 P3. Original observations and evidence below are retained.

## Scope and environment

- Windows host; .NET SDK 10.0.302; Node 24.15.0; Microsoft Edge 155.0.4283.45, headless, isolated temporary profiles.
- Tested the local Blazor WebAssembly app using synthetic Telegram accounts, channel metadata, and original test audio. Actual Razor components, C# services, IndexedDB, service worker, and browser audio were exercised. No personal Telegram account or messages were accessed.
- Repository base: `913c7f8de114e0e17e5fce53d7816c8a03973bf2`, with pre-existing uncommitted changes. Other workspace activity changed files during this session. Results describe the built snapshots exercised, not a guarantee about subsequent edits.
- Shared solution and Windows MAUI compilation were checked. Native application interaction, audio, and physical devices were **not** tested.
- P2 = functional/accessibility problem with a workaround or narrower trigger. P3 = lower-impact usability/display defect. The current disposition is recorded below; reproduction details describe the original defects.

## Verification results

| Check | Result | Evidence |
| --- | --- | --- |
| Shared solution build | Passed; 0 warnings, 0 errors | [Build log](artifacts/qa-build.log) |
| Core/SQLite tests, final built snapshot | **52 passed**, 0 failed/skipped | [Core log](artifacts/qa-core-final.log) |
| Browser app regressions | **68 passed** in the final app-suite run | [Browser log](artifacts/qa-browser-final.log) |
| Real pinned TDLib worker probe | **1 passed** separately with external networking blocked | [Engine log](artifacts/qa-engine.log) |
| Schema verification | 22 method signatures verified against pinned TDLib revision in the final source check | `node scripts/verify-td-schema.mjs` |
| Windows MAUI build | Passed; 0 errors, 2 PRI249 resource-qualifier warnings (`FLUENT-DARK`, `FLUENT-LIGHT`) | [Native build log](artifacts/qa-windows-build.log) |
| Additional responsive checks | 23 viewports × 5 views = **115 view/layout combinations**; no ordinary-size horizontal overflow/control-bound violations detected by that scan | [Exploratory results](artifacts/qa/exploratory.json) |
| Additional behavior/accessibility checks | Confirmed the defects below; inspected generated screenshots | [Edge-case results](artifacts/qa/edge-cases.json), [accessibility results](artifacts/qa/accessibility.json) |

The final alternate-port browser run reported one failure in addition to its 68 passes: the engine test hardcodes an allowlist for port 5188 and therefore blocked the alternate test origin before loading the app. It passed when rerun using its original configuration. Thus **all 69 distinct existing browser checks passed across the final app run and separate engine run**, not in one green 69-test invocation.

An earlier run was interrupted after native compilation refreshed shared assemblies and invalidated development assets. Its buffering/startup failures are excluded from application findings; the web assets were rebuilt and the app suite rerun successfully afterward.

## Fix status - 2026-10-17

| ID | Status | Change |
| --- | --- | --- |
| CW-QA-001 | Fixed | Source removal preserves unrelated exports/offline saves, expires affected tag handles, and prevents late offline saves after remove/re-add. Sign-out still invalidates all media work. |
| CW-QA-002 | Fixed | Measured fixed-control clearance reserves keyboard scrolling space; focused main-content controls are brought above overlays. |
| CW-QA-003 | Fixed in existing layout changes, verified | The mobile brand can shrink and truncate while the account target remains fully visible at 320px/200% text. |
| CW-QA-004 | Fixed | Sidebar height reserves the header/player space; at short heights the entire sidebar scrolls so collection rows remain fully reachable. |
| CW-QA-005 | Fixed | Offline channel options are derived separately from selected library sources, preserving global sidebar shortcuts/counts. |
| CW-QA-006 | Fixed | Screen and collection changes create browser history entries; Back/Forward and reload restore the selected screen. |
| CW-QA-007 | Fixed | A persistent transfer panel shows the song, byte progress and cancellation across views and filters, above the player/navigation with measured keyboard clearance. |
| CW-QA-008 | Fixed | Durations display total elapsed hours, including 25:00:00. |

Regression coverage: `tests/browser/buglist.spec.mjs` and `tests/browser/transfers.spec.mjs`. See `docs/verification.md` for final validation and platform limits.

## Confirmed defects

### CW-QA-001 — Removing an unrelated channel cancels a pending export or offline save

**Priority:** P2. **Area:** transfer lifecycle and source isolation. **Observed at:** 1440×900; applies to browser transfers.

**Reproduction:**

1. Connect the synthetic account and add at least channels A and B to the library.
2. Start **Download** for a song from A. Complete its TDLib download, but delay the final `readFile` response using the test fixture.
3. Open Channels and **Remove from library** for B, leaving A selected.
4. Return to Your Library and release the delayed read.
5. Repeat using **Save offline** instead of Download.

**Actual:** A's export fails with `Couldn't download Café 🎵. Download canceled.` Its offline save fails with `Couldn't save Café 🎵 offline. Saving offline was canceled.` No offline copy is published. The user did not cancel A or remove its source.

**Expected:** Removing B cancels only B's work. A's authorized transfer should complete.

**Cause identified:** `removeSource(account, chat)` calls global `cancelMediaWork()` before source-scoped deletion. This increments cancellation generations shared by every export/offline save. See `src/ChannelWave.Platform.Browser/wwwroot/channelwave.js:286` and `:5`.

**Suggested correction:** Scope media cancellation to the affected account/source/operation; retain global invalidation for sign-out.

**Evidence:** [Download screenshot](artifacts/qa/unrelated-source-Download.png), [offline-save screenshot](artifacts/qa/unrelated-source-Save-offline.png), [observations](artifacts/qa/edge-cases.json). Both variants reproduced in the synchronized follow-up run. Delaying the final read simulates a slow browser/large-file operation rather than changing production behavior.

### CW-QA-002 — Keyboard focus moves behind the fixed player and mobile navigation

**Priority:** P2. **Area:** accessibility and scrolling. **Observed at:** 390×844, 320×568, and 844×390.

**Reproduction:**

1. Add three channels, open Your Library, stream a song, then pause it so the player remains mounted.
2. Focus the main content and use **Tab** through the search, filters, and track actions without manually scrolling.
3. Continue to actions on later rows.

**Actual:** The focused control can be covered by the fixed player or bottom navigation. At 390×844, Favorite, Stream, Save offline, Download, and playlist buttons on a row were focused at `y=734.77`, height 44, behind the player. Hit testing at their centers returned the player. At 320×568, some actions were covered by the bottom navigation as well. At 844×390, search and several row controls were obscured.

**Expected:** Every focused control and its focus indicator remain visible above fixed overlays as the user tabs through the app.

**Cause identified:** Layout clearance adds bottom padding, but keyboard-driven scrolling does not reserve the fixed overlays' occupied area. Relevant files: `src/ChannelWave.UI/wwwroot/music.css`, `channelwave.css`, and `navigation.js`.

**Suggested correction:** Apply appropriate scroll padding/margins or focus-aware scrolling using the measured player/navigation heights; check focus visibility, not only viewport bounds.

**Evidence:** [390px keyboard screenshot](artifacts/qa/keyboard-covered-390.png), [320px keyboard screenshot](artifacts/qa/keyboard-covered-320.png), [measured focused controls](artifacts/qa/accessibility.json).

### CW-QA-003 — At 320px and 200% text, the header clips the account control

**Priority:** P2. **Area:** accessibility and responsive header. **Observed at:** 320×568 with text enlarged to 200%.

**Reproduction:**

1. Open the app at 320×568.
2. Enlarge text to 200%; the reproduction doubles computed font sizes for the same element set used by the existing enlarged-text test.
3. Inspect the top header and try to see the full account/settings control.

**Actual:** The ChannelWave wordmark consumes the header width, pushing the account control beyond the right edge. Document scroll width becomes **341px** in a **320px** viewport. The screenshot shows a cropped account button. The player and mobile navigation still avoid each other, so testing only their overlap misses this failure.

**Expected:** Header controls remain visible and operable without horizontal overflow when text is enlarged.

**Cause identified:** The mobile header retains a single fixed-height flex row and a non-wrapping brand without a sufficient narrow-width text-growth strategy. See `.app-topbar`, `.brand`, and `.account-button` in `src/ChannelWave.UI/wwwroot/channelwave.css`.

**Suggested correction:** Allow the brand/header layout to shrink or wrap while preserving the full account target.

**Evidence:** [200% text screenshot](artifacts/qa/320-text-200.png), [width measurements](artifacts/qa/accessibility.json). This is computed-font-size emulation, not physical-device OS font scaling or browser zoom certification.

### CW-QA-004 — The desktop sidebar extends behind the player on short landscape screens

**Priority:** P2. **Area:** responsive sidebar. **Observed at:** 844×390 with a mounted player.

**Reproduction:**

1. Use an 844×390 viewport, add multiple channels, and stream/pause a song.
2. Inspect the sidebar collections below Your Library.
3. Scroll the sidebar collection list to Your channels.

**Actual:** The sidebar runs from `y=72` to `y=350`, while the player starts at `y=290`: **60px of vertical overlap**. Collection rows and their labels are partly concealed by the player. Scrolling a collection into view does not make the whole row visible. A Playwright click could still hit its exposed portion, so this is a visibility/layout failure rather than a claim that every click is impossible.

**Expected:** The sidebar's usable scrolling area ends above the player, with full collection labels/targets visible.

**Cause identified:** `.app-sidebar` uses `height:calc(100dvh - 112px)` without accounting for its top offset and the player; the desktop layout is selected solely by width. See `src/ChannelWave.UI/wwwroot/channelwave.css` and `.music-player` in `music.css`.

**Suggested correction:** Size the sidebar against the measured header/player space, and consider a short-height layout breakpoint.

**Evidence:** [Landscape sidebar screenshot](artifacts/qa/landscape-sidebar-obscured.png), [landscape player screenshot](artifacts/qa/player-844x390.png), [geometry](artifacts/qa/edge-cases.json).

### CW-QA-005 — Opening Offline hides selected channels from the global sidebar and changes the library count

**Priority:** P2. **Area:** navigation and collection state. **Observed at:** 1440×900.

**Reproduction:**

1. Add three channels to the library.
2. Save a song from only one channel offline.
3. Confirm that the sidebar lists three channels and says **3 in your library**.
4. Open **Offline** using the library filter.

**Actual:** The sidebar now lists only the channel with a saved song and says **1 in your library**. The two unsaved channels disappear from the global source shortcuts. Direct IndexedDB inspection still finds **three** selected sources. Returning to the online library restores the shortcuts; no source data loss was observed.

**Expected:** Offline filters the displayed songs and its channel selector while the global sidebar continues to represent the user's selected library sources.

**Cause identified:** The offline branch of `Diagnostic.Refresh()` replaces the shared `sources` collection with groups derived only from offline tracks. That collection also drives the global sidebar and its count. See `src/ChannelWave.UI/Diagnostic.razor:249`.

**Suggested correction:** Keep the selected library sources and the offline-view source options in separate state.

**Evidence:** [Offline screenshot](artifacts/qa/offline-sidebar.png), [before/after observations](artifacts/qa/exploratory.json), [independent confirmation: 1 visible source / 3 stored sources](artifacts/qa/final-confirm.json). Active Offline filter state was independently checked; this is not a transient screenshot during navigation.

### CW-QA-006 — Browser Back exits the app instead of reversing in-app navigation

**Priority:** P2. **Area:** browser navigation. **Observed at:** 1440×900; shared web behavior.

**Reproduction:**

1. Open ChannelWave, connect, and navigate to Your Library.
2. Navigate to Search.
3. Press the browser's **Back** button.

**Actual:** In-app navigation leaves the URL unchanged and creates no history entries. Back returns to the document before ChannelWave; in the isolated test it returned to `about:blank`. It does not return to Your Library.

**Expected:** Browser Back/Forward should follow the app's screen navigation without leaving the app unexpectedly.

**Cause identified:** `Diagnostic.Navigate()` changes component state only; `navigation.js` scrolls/focuses the main region but does not integrate browser history. See `src/ChannelWave.UI/Diagnostic.razor:151`.

**Suggested correction:** Use routable views or history entries with corresponding Back/Forward handling.

**Evidence:** [Recorded URL/history and Back result](artifacts/qa/exploratory.json). This is a confirmed browser usability gap; the README does not explicitly promise deep links.

### CW-QA-007 — A running download has no visible progress or cancellation control after leaving its row

**Priority:** P3. **Area:** transfer feedback. **Observed at:** 1440×900.

**Reproduction:**

1. Start Download on a library song and keep the synthetic download pending.
2. Confirm that the row shows progress and **Cancel download**.
3. Navigate Home.

**Actual:** The download continues, but the progress and Cancel control are hidden with the library row. Home shows no transfer indicator or cancellation action. The user must return to the original library view to cancel. Filtering the downloading row out creates the same structural problem.

**Expected:** An active transfer should retain an accessible progress/cancel affordance across views, or navigation should clearly indicate where the transfer can be managed.

**Cause identified:** Progress and cancellation are rendered only inside the downloading track's article. See `src/ChannelWave.UI/MusicLibrary.razor:62`.

**Suggested correction:** Add a persistent transfer status/action outside the filtered track list.

**Evidence:** [Home during download](artifacts/qa/hidden-download-home.png), [pending-transfer observation](artifacts/qa/edge-cases.json). Returning to Your Library and canceling succeeded.

### CW-QA-008 — Durations of 24 hours or more wrap their displayed hour count

**Priority:** P3. **Area:** recording duration display. **Observed at:** 1440×900 with synthetic boundary metadata.

**Reproduction:**

1. Set a fixture track's duration to **90,000 seconds** (25 hours), preserving its valid source/identity.
2. Refresh Your Library.
3. Read its duration in the row's file details.

**Actual:** The app displays **1:00:00**, representing only the hour component after the full day.

**Expected:** Display **25:00:00**, or an explicit day/hour representation.

**Cause identified:** `Time()` checks `TotalHours`, then formats using `TimeSpan.ToString(@"h\:mm\:ss")`; `h` excludes whole days. See `src/ChannelWave.UI/MusicLibrary.razor:243`.

**Suggested correction:** Format total elapsed hours, retaining the minutes/seconds components.

**Evidence:** [25-hour metadata screenshot](artifacts/qa/duration-25h.png), [observed output](artifacts/qa/edge-cases.json). This verifies metadata rendering; no 25-hour audio file was played.

## Coverage matrix

| Area | Scenarios exercised | Outcome |
| --- | --- | --- |
| Connection/auth | Synthetic phone/code/password prompts; invalid phone preservation; missing config; mismatched engine; account identity retry; actual worker initialization without account credentials | Existing regressions passed |
| Channel discovery/indexing | Discovery without indexing, deduplication, search/filtering, add/remove/re-add, independent pause/resume, inaccessible sources, late pages, restart persistence | Existing regressions passed; new cross-source transfer defect CW-QA-001 |
| Library/search | Unicode normalization, source/favorite/list filtering beyond the first page, 10,000-record persistence/account isolation, pagination, Enter submission and clearing, empty states | Existing regressions and Enter checks passed |
| Favorites/listening list | Add/remove/order/deduplication, reload persistence, failed write recovery, favorite edits during offline saves | Existing regressions passed |
| Playback | Stream, pause/resume/stop, previous/next, seek/volume, volume across Next, replay after natural end, autoplay denial, slow first chunk, cancellation, transfer errors, stale selections | Existing regressions and extra volume/end checks passed |
| Export/offline | Exact exported bytes, truncated responses, save/remove, offline reload/playback with networking disabled, quota failures, final-read cancellation, source removal and sign-out cleanup | Existing regressions passed; CW-QA-001, 005, 007 found |
| Navigation/accessibility | Main desktop/mobile views, skip/focus behavior, Back, keyboard traversal with hit testing, 200% text emulation, reduced motion, multilingual/long metadata | CW-QA-002, 003, 004, 006 found |
| Long-recording boundary | 25-hour metadata display | CW-QA-008 found |
| Lifecycle/storage | Profile ownership/second tab, storage denial, database upgrade, failed cleanup recovery after reload, service-worker shell update while old tabs remain open | Existing regressions passed |

Responsive exploratory matrix: **280×653, 320×568, 360×640, 375×667, 390×844, 412×915, 540×720, 600×960, 759×800, 760×800, 761×800, 768×1024, 820×1180, 844×390, 896×414, 1024×768, 1280×720, 1440×900, 1920×1080, 2560×1440, 667×375, 568×320, 320×256**. Home, Library, Channels, Account, and Search were checked at each size with a paused player mounted. The 280px width and 256px height were additional stress cases. The existing suite separately covers Offline, favorites/listening list, signed-out views, 200% text at 390px, and desktop/mobile flows.

## Known release risks and untested coverage

These are **not included in the eight newly confirmed defects**:

- `docs/app-audit.md` already records an unresolved pinned TDLib dependency/toolchain security release blocker, including 53 upstream npm audit entries. This QA pass did not independently rerun that audit or demonstrate exploitability. Do not interpret passing behavior tests as resolving it.
- Live Telegram authentication delivery, QR/email variants beyond available fixtures, real-account indexing, real network reconnection/rate limits, remote session revocation, and actual Telegram media compatibility were not end-to-end tested. Synthetic failures are narrower evidence.
- Chrome as a separate installed product, Firefox, Safari/WebKit, physical Android/iPhone/tablets, touch gestures, virtual keyboards, device safe-area changes, screen readers, and actual OS text scaling were not tested.
- Windows native behavior was compilation-only. Linux/Android/iOS/macOS application launch, audio, keyrings, exports, signing, installation, and distribution remain unverified.
- Production HTTPS hosting, response security headers, deployed updates, long-duration soak/resource use, very large real audio, real browser eviction/storage pressure, and external CI were not validated.
- Documented unfinished features such as multiple-playlist CRUD, background/OS playback controls, native offline collections, cache budgets, live message reconciliation, full localization, and PWA installation were not mislabeled as regressions of implemented functionality.

This is a broad functional QA pass over representative sizes and scenarios, **not exhaustive testing of every possible size, device, account state, or input**.

## Reproducing the additional checks

The generated QA scripts, observations, logs, and screenshots remain locally under ignored `artifacts/`; this Markdown report is the requested repository deliverable. Retain the artifacts separately if sharing the report outside this checkout.

Start a consistent build/server before running the extra checks, and avoid concurrent builds that change its shared assemblies:

```powershell
dotnet build ChannelWave.slnx --no-restore -m:1
dotnet run --project src/ChannelWave.Host.Web --no-build --no-launch-profile --urls http://127.0.0.1:1933
```

In a second terminal:

```powershell
node artifacts/qa-edge-cases.mjs
node artifacts/qa-accessibility.mjs
node artifacts/qa-final-confirm.mjs
```

`artifacts/qa-exploratory.mjs` used port 5188 for the initial 115-combination matrix; use that port or adjust its local URL when rerunning it. Existing browser regressions use `npm run test:browser`; set `CHANNELWAVE_VERIFY_TDWEB=1` to include the real-engine probe when the pinned assets are present.
