# Home and Library interface

## Structure and behavior

Home, Library, Settings and Profile are the four roots. Phones show bottom navigation only at roots; desktop retains its sidebar. Focused categories, collections, search and customization have Back. Playback stays mounted across navigation, above navigation at phone roots and at the bottom on focused pages.

Home starts with All, Recently Played, New, Playlists and Liked. All follows the configured order and includes custom sections. Its previews contain at most two items on phones and six on desktop. See all selects the corresponding tab. A custom tab refers to an artist key, album key, playlist ID, track ID or saved-filter ID. Opening music never implies playing it; explicit play builds the full collection queue.

The Library grid always uses two columns and orders Tracks, Artists, Albums, Playlists, Offline, History, Statistics, Sources before saved-filter tiles. Search is a header icon opening a focused page. Search collection modes filter labels before paging. Saved filters retain supported criteria and collection constraints, resolve current account data on each visit, and can be pinned to Home. Hiding a Library tile preserves the definition and Home pins.

Settings owns downloads, storage, automatic metadata, listening and privacy. Appearance describes the current dark theme. Profile owns actual Telegram identity, connection/session details, About, and sign-out. Telegram transport status and source indexing status are separate.

## Implementation

`LibraryPresentationStore` serializes a versioned `library-presentation-v1` document through the existing repository state API. Account-generation checks reject old queued writes. Detaching the account drains active writes before sign-out erases local data; profile identity caching uses the same writer. Unsupported future preference versions are preserved. No SQLite schema or IndexedDB object-store change is needed.

Shared collection components use row-aware virtualization, including responsive card columns and keyboard-focused items. Home sections begin reading when near the viewport and cancel superseded reads. Grid/List choices are local to an account, device, and individual tab or category. Existing serialized browse links remain supported, including the former `library` browse route.

## Validation evidence

The synthetic browser acceptance tests are in `tests/browser/redesign.spec.mjs` and `tests/browser/redesign-edge-cases.spec.mjs`; captures are written to `artifacts/redesign/`. They cover 320, 390, 820 and 1440 pixel layouts, every pin type, saved-filter restoration, recent-listen pagination, live queries, keyboard navigation, focus/scroll restoration, and the dialog-first Android Back handler. At the narrowest phone widths, artwork and the song title open the full player while the dock retains playback, queue and output controls. Repository checks and native verification limits are recorded in `docs/verification.md`.
