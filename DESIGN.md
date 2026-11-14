# Interface design

## Overview

ChannelWave uses a black frame, subtly green charcoal surfaces, off-white content, and vivid green actions to make a personal Telegram music library familiar to Spotify users. Compact headings, square artwork, and source-aware rows keep the music visible while navigation and playback stay predictable.

Desktop and mobile share the same collection vocabulary and player state. Song artwork and titles open an independent full-screen preview; explicit play controls start or change playback. Authentic track artwork is used when available, with the existing artwork fallback and symbolic collection covers when it is not.

The interface uses:
- Green charcoal surfaces, off-white content, and green action or selection state.
- Compact hierarchy with square music artwork and source attribution.
- Four navigation roots with focused detail pages and a persistent player.
- Scrollable Home tabs, personal collections, visible focus, and responsive grids.

The ChannelWave brand mark is a continuous rounded C-and-wave SVG in Listening Green (`#1ed760`). Its uniform stroke and open counter keep the silhouette clear at small sizes. The header image fills a 38px wrapper on desktop and a 32px wrapper on mobile; the browser favicon and public offline shell use the same SVG. The SVG master is the source for raster exports.

The first-use Home record sleeve and label reuse the mark in black. Native launcher icons use the shared SVG foreground over Charcoal Surface, with a 0.65 foreground scale for safe clear space; the splash screen uses the green mark on the same charcoal background. The web touch icon and Linux window icon share the 192px raster export.

The shared stylesheets and Razor components define the interface. Screenshots use synthetic test data; verification is recorded in `docs/verification.md`.

## Colors

Green charcoal neutrals carry the interface; off-white content supplies contrast, while green identifies actions, current roots, Home tabs, display mode, connection, saved state, and playback.

### Primary

- **Listening Green** (`primary`, `primary-hover`): primary actions, current root labels, selected Home tabs and display controls, active track titles, favorites, and progress.
- **Soft Success** (`success-text`): task feedback. Errors use `error-text` over `error-bg` near the failed task.

### Secondary

- **Collection Forest** (`list-cover`, `list-symbol`): library and listening-list artwork.
- **Channel Terracotta** (`channel-cover`, `channel-symbol`): the channel collection cover.
- **Source Slate** (`source-cover`, `source-symbol`): channel initials and source identities.
- Liked Songs uses the implemented violet-to-mint gradient; artwork colors identify a collection, not interactive success.

### Neutral

- **Black Frame** (`canvas`) surrounds the main work area and desktop player.
- **Green Charcoal** (`surface`) carries main content and sidebar. Library tiles, Settings rows, search entry, and display controls use `raised`; fields retain `field`. The floating Home strip uses `tab-strip`, and current desktop navigation uses `nav-active`.
- **Control Charcoal** (`control`, `control-hover`) separates resting and hovered buttons.
- **Soft Ivory** (`text`) is the content foreground; inherited controls retain **Control White** (`control-text`). **Sage Gray** (`muted`) supplies secondary copy. `line` separates rows and sections; `field-border` makes input and outline-button boundaries visible.

Use green for primary actions and meaningful selected, saved, connected, or playback state. Current roots and Home tabs use green; ordinary filter pills retain their white selected treatment. Expose the same state semantically and in text or icon form.

## Typography

**Body and Control Font:** Segoe UI, with system-ui and sans-serif fallbacks. The current heading family is inherited from the existing application; this record does not establish it as a separate display identity.

### Hierarchy

- **Headline:** the compact page heading token becomes 28px at 760px and below. Home's wordmark heading is 25px on desktop and 23px on phones.
- **Section:** collection sections use the section token, reducing to 19px on phones. Specific legacy headings may retain a narrower component rule.
- **Title:** Home list titles are 15px; grid titles are 14px. Library category titles are 18px on desktop and 17px on phones. Existing song rows remain 15px desktop and 14px phone; artist text is 13px and 12px respectively.
- **Body:** explanatory copy uses the body line height; introductions range from 13–16px. Channel prose is capped at 60–75ch where defined.
- **Label:** compact metadata and source attribution generally use 12px; desktop root labels are 15px and Home tabs are 13px. Phone search and form fields retain 16px text where already defined.
- **Full-screen song identity:** titles scale from 28–48px on larger screens and use 29px on phones. Long song titles and artist names wrap.

Track names and source names wrap or truncate according to their available column; Home artwork-card titles wrap. User-provided titles and names retain automatic text direction. Numeric durations and file metadata use tabular numerals.

Build operational hierarchy through size, weight, spacing, and muted color within the existing sans stack.

## Layout

The desktop shell uses a 264px sidebar, an 8px gutter, a 72px top row, and 8px outer horizontal padding. The sidebar fills the available shell height. Main content uses 28px vertical and 32px horizontal padding; Home is capped at 1200px, Library at 900px, and Settings/Profile at 720px. The desktop top bar contains the brand and Downloads shortcut. The main content panel is the scrolling region; measured player, navigation, and transfer heights reserve its lower clearance.

Responsive layout:

| Width | Built behavior |
| --- | --- |
| At most 1150px | Sidebar becomes 224px. Redesign content retains 32px horizontal padding. |
| At least 1100px | Existing song rows align identity, source, duration, and Favorite/Play/More; desktop player volume remains available. |
| 761–1099px | Existing rows use compact source/duration placement and Play/More; Favorite moves into More. Player volume hides. |
| Above 760px | Root navigation stays in the sidebar on both root and detail pages; Home artwork grids auto-fill columns from a 160px minimum. |
| At most 760px | Header and sidebar hide; content uses 20px top, 16px horizontal, and 24px bottom padding. Home artwork and playlist grids use two columns. Four-item bottom navigation appears only on root pages. |

Home's floating tab strip stays at the top of the scrolling content, moves horizontally when its tabs overflow, and retains its selected tab. All shows bounded previews of each other tab: at most two items on phones and six above the mobile breakpoint, followed by an explicit See all action. Dedicated tab collections load beyond the preview limit. List and grid preferences are retained per collection.

Library presents eight equal category tiles in two columns at every size, followed by saved-filter tiles. Desktop tile gaps are 12px and phone gaps are 10px; tiles have a 132px desktop minimum height and 110px phone minimum height, with saved-filter tiles at least 100px. A search icon in the Library heading opens the dedicated Search page.

Mobile root navigation has a 68px minimum height and safe-area bottom padding. The persistent player sits 8px from each horizontal edge immediately above the measured root navigation; focused detail pages omit bottom navigation and place the player above the bottom safe area. Detail pages retain a visible Back control. Active phone docks keep separate play/pause and expand controls, seekbar, and textual status; compact timestamps, previous/next/stop, and volume stay hidden. The existing idle state remains available before playback starts.

Square artwork remains square in grid and list presentations. Home list rows use 64px artwork on desktop and 48px on phones; the phone action group moves below identity. Grid artwork fills its square container and titles wrap below it. Existing song rows retain their 56px phone artwork, source/duration metadata, and standalone Play/More controls; their desktop and compact minimum heights remain 80px and 96px respectively. Phone channel management keeps compact source rows and a discovery action beside its heading.

The full-screen player occupies the viewport with safe-area padding. Desktop content is capped at 1200px and places artwork and controls in two columns; square artwork is capped at 520px and adapts to viewport height. Phones stack artwork, identity, seekbar, transport, and source details, with artwork capped at 360px and volume hidden. Portrait tablets from 761–900px also stack the player within a 520px content width, with artwork capped at 380px.

Downloads and Storage reuse the shell with a content width capped at 1100px. Their headings follow the compact redesign scale, reducing at the mobile breakpoint. Downloads aligns a 48px music fallback, flexible identity, status and actions in four desktop columns. At 1100px and below, status and actions stack beneath identity in the second column; text wraps rather than clipping errors or source names. Filter pills, action groups and cache-limit choices wrap. At 760px and below, download rows reduce vertical padding and actions remain at least 44px high. Storage usage pairs flexible labels with tabular values; its explanatory copy is capped at 72ch. Enlarged text retains the main panel's scrolling and persistent player clearance.

Base spacing uses 4, 8, 12, 16, 24, 32 and 48px. Components may use intermediate spacing where needed.

## Elevation & Depth

Tonal separation and restrained dividers define operational surfaces. Main panels and rows have no ambient card shadows. The sticky Home tabs use a restrained floating shadow (`0 6px 18px #0004`), and the saved-filter action menu uses `0 10px 28px #0008` to separate an overlay from its tile. Collection headers fade from a collection tint into the main surface. The illustrative record and large library artwork carry physical shadows: record disc `0 12px 24px #0005`, sleeve `0 16px 32px #0005`, and library cover `0 8px 24px #0004`. Full-screen artwork adds `0 20px 48px #0005` against an artwork-colored radial background; the full-screen surface itself has no shadow.

The full-screen player samples its displayed artwork locally at 32×32 pixels, groups similar colors and darkens the dominant usable color to a relative luminance of at most 0.06. That color fades into charcoal toward the lower/right edges; absent, unreadable, near-black or all-white artwork uses the charcoal fallback. Source photos follow the same rule. Background color transitions take 350ms and are disabled for reduced motion. White primary text, neutral light secondary text and green playback controls retain their roles across cover colors. Disabled seek controls leave timestamps fully readable. Palette values are scoped to each player and are neither persisted nor applied to library rows.

The mini player uses the current song's sampled color mixed at 45% with charcoal (`#121212`), creating a subtle solid tint on both desktop and phones. It retains its existing text and playback-control colors, with a high-contrast seek track. Missing artwork uses neutral charcoal rather than the previous fixed phone green. Previewing another song changes only the full-screen preview; the dock continues to follow the playing song. Both players share image lifecycle cleanup, fallback behavior and reduced-motion treatment.

Use charcoal layers and dividers for ordinary rows and containers. Physical artwork shadows, the floating Home strip, and temporary action menus are deliberate depth roles.

## Shapes

Pill controls use the pill radius; circles identify avatars, brand marks, and transport affordances. Desktop structural surfaces use the surface radius, reducing to square edges for the main mobile work area. Home artwork uses square proportions with the panel radius; existing small covers retain their artwork radius. Library tiles use the surface radius. Fields retain the row radius; song cards and full-screen artwork use gently rounded 8px corners. The first-use hero retains the panel radius. Display-mode controls and the phone dock use the dock radius. The full-screen dialog has square viewport edges; ordinary phone action dialogs retain rounded top corners as bottom sheets.

Icons are inline SVG with a 24px viewBox and rounded strokes; visible sizes vary from 14–64px by role. Favorites and play symbols intentionally use filled paths. Channel initials are content identities, not substitute UI glyph icons.

## Components

### Buttons

Green primary, white light, neutral filled, transparent outline, and muted text variants share the pill vocabulary. Base buttons have a 44px minimum height, 10px 20px padding, and 650 weight. Primary buttons use 14px vertical and 26px horizontal padding. Dense desktop chips retain 36px sizing, while song-row action targets are 44px across sizes. Mobile filters, source links, account, search submission, and dock transport targets are at least 44px. The desktop dock's central toggle is 40px; the full-screen primary toggle is 72px, reducing to 68px on phones.

Hover changes fill or text color; pressed enabled buttons scale to .98. Disabled controls use .45 opacity and a not-allowed cursor. Keyboard focus is a white 2px outline with a 4px offset. Button color transitions last .16s with ease. Reduced-motion preference disables transitions, animation, smooth scrolling, and pressed transforms.

### Chips

Ordinary filters use neutral pills with white text; selected chips invert to white with black text and expose `aria-pressed`. Desktop padding is 8px 14px; mobile filter chips use 8px 12px and a 44px minimum height. Home collection tabs are a distinct green selected treatment within a floating charcoal pill strip: 40px minimum height, 8px 16px padding, and 12px horizontal padding on phones. They use tablist/tab/tabpanel semantics, roving focus, and Arrow/Home/End navigation. Display-mode buttons expose `aria-pressed` and combine a green icon with the selection surface.

### Cards / Containers

Home collection cards keep a square artwork target above wrapping identity, artist/source metadata, and explicit actions. Artwork/title previews a song; play controls start listening. The list presentation uses the same content with compact square art beside it. Authentic artwork is preferred; the existing music-symbol fallback remains visible when no image is available.

Library category tiles are raised charcoal buttons with an inline SVG, strong label, and available count. They retain two columns on phones and desktop. Saved-filter tiles use the same surface with a separate More disclosure; Save a filter uses a dashed border and green plus. Playlist summaries pair a small symbolic cover with name/count and a separate pin action. These are content identities, not fabricated album artwork.

### Inputs / Fields

Fields use charcoal fill, a 1px visible gray border, 12px padding, and a 44px minimum height. Search compounds use an inline SVG icon and a focus-within white 2px outline; music search is pill-shaped and channel search has gently rounded corners. Search labels remain available to assistive technology when visually hidden on mobile. Authentication fields keep a visible task label.

### Navigation

The four roots are Home, Library, Settings, and Profile. Desktop navigation uses left-aligned SVG/label rows with muted resting text and green active text on a subtly green charcoal fill. It stays visible on detail routes. Mobile uses four equal icon-over-label columns only at those roots; the active label and icon are green and expose `aria-current`.

Focused collections, Search, Home customization, Sources, Downloads, Storage, and other details show Back in the content header. Mobile omits root navigation there, while the player remains mounted. Browser/Android Back first dismisses the top dialog, then restores the route and its recorded scroll/focus context; direct-entry details fall back to their parent root. A skip link exposes main content. Fresh navigation focuses main content, and Search focuses its query field.

The frame fills the dynamic viewport. Header, sidebar, root bottom navigation and player stay stationary while the main panel scrolls; Home tabs stick within that panel. Measured playback, navigation and transfer heights reserve the lower area, including enlarged error messages and safe areas. Dialogs lock the panel without resetting its position. Accessibility labels remain contained by the panel.

**The Focused Detail Rule.** Keep the four roots stable; use Back and the persistent player on focused mobile details without repeating bottom navigation.

### Settings and Profile

Settings collects Offline downloads, storage management, automatic music-detail loading, listening/privacy preferences, and the current Dark appearance. Profile owns Telegram identity, connection/authentication, sign-out cleanup, Listening & privacy, About, and a Connection details disclosure. Shared raised rows use an icon, label, and trailing value or arrow; Profile initials use a circular avatar. These pages preserve existing account boundaries and confirmation/recovery behavior.

### Track rows and player

Song identity and transport are separate targets. Artwork/title opens the full-screen player without autoplay; the row's circular Play/Pause control starts or toggles listening. Desktop rows expose Favorite, Play/Pause, and More; compact tablet and phone rows expose Play/Pause and More, with Favorite in the menu. More carries edit details, save/remove offline copy, export download, and listening-list membership at every size. Source attribution is a separate filter button. The current title is green and includes textual playback status; previewing another song does not select its row as the playing song.

Protected-source restrictions remain next to the affected track, and active transfer progress/cancellation stays reachable above the dock even when the row is filtered out. Failure adds an inline retry action. Empty, filtered-empty, disconnected, and loading states use distinct explanatory copy and relevant next actions.

The player stays mounted while content views change. Its identity and expand controls always open the actual current song. Desktop transport includes previous, play/pause, next, and Stop above the shared seekbar; source attribution and volume remain visible. The phone dock prioritizes identity, play/pause, expand, the seekbar, and textual status. Opening or minimizing a full-screen preview preserves playback.

Full-screen preview is independent of playback: opening song B while A plays shows B as ready to play and identifies A as currently playing. A continues until B's explicit Play control is used. Preview previous/next browses the captured visible sequence when the shown song is not current; when showing the current song, previous/next controls change playback. The full-screen composition pairs prominent square artwork with title/artist, Favorite, status, seekbar, and previous/play/next transport, followed by source and file details. Desktop adds volume. More remains available in the header, including Stop for the current song. Minimize returns to the library or underlying view.

Listening-list membership is represented by Add/Remove text in More and changes only after storage succeeds; the list retains insertion order. Collection filtering precedes pagination. Playback follows the collection sequence captured when listening starts, independently of later searches. Fixed-player offsets and content clearance follow measured navigation/player heights so enlarged text and errors do not overlap the controls.

### Downloads, offline collections and storage

A desktop top-bar download icon with an unfinished count opens Downloads; Settings provides Offline downloads at every size. The current transfer summary links to this view and keeps Cancel reachable while browsing. Offline remains a separate collection with the existing song rows and player; its toolbar links back to Downloads.

Downloads uses In progress, Completed and Needs attention filter pills with counts and `aria-pressed`. Named row actions accompany textual queued, waiting, saving, ready-to-export, completed, canceled and failed states. Known byte totals use labeled native determinate progress; unknown totals use indeterminate progress. Errors and retry times stay beside the affected song. Failed jobs expose Retry and Manage storage; export-ready jobs require Save file. A confirmation dialog precedes Cancel all and explains that already saved songs remain. Each filter has its own empty state; longer histories use Show more downloads. Interrupted export handoffs retain an explicit warning before another save.

Offline distinguishes Save offline from Download a separate file in More. Missing or damaged copies show textual status, Save again and a reconnect explanation when needed. Playlist Save current songs offline is a one-time snapshot, with feedback explaining that later edits do not download automatically. Deleting the playlist retains saved songs. Source removal and sign-out remove their offline copies; external exported files stay under device-file management.

Storage is reached through Settings and identifies the current account and device. A decorative segmented usage bar uses existing green and two grays; a definition list independently names and quantifies Saved offline, Temporary cache and Database, artwork & staging. Browser estimates and reclaimability are explicit copy, and external exports are described separately. Cache-limit pills expose selection semantically and keep readable white/black selected treatment while busy; unselected busy controls retain muted text on charcoal. Measuring and updating use labeled loading indicators and `aria-busy`; errors offer Retry measuring storage, and completed changes use status feedback. Clear temporary cache opens the existing accessible confirmation dialog and explains the preserved offline songs and playback. Reclaimable-size copy also accounts for files in use, pending exports and untracked Telegram files. This protects listening and avoids implying that the selected temporary budget caps all device storage.

During playlist selection, collection playback and snapshot actions give way to Done selecting and bulk actions so checkboxes remain visible above the phone player dock. Browser verification and physical Android/Linux acceptance are recorded separately in `docs/verification.md`.

### Seekbar

The dock and full-screen player share one accessible native range input with a decorative SVG track. Only the played segment becomes a smooth, evenly repeated wave while playing; the remaining track and buffered portion stay straight. Paused playback returns the played segment to a straight line. Reduced motion keeps the playing wave visible but static. The wave moves through a 24px repeat over 1.2 seconds without changing the range value, and the hidden dock animation pauses while the full-screen player is open.

The played segment and remaining line meet beneath the thumb on one centerline, with no baseline under the wave. Buffering is drawn only beyond the played position. A shared 14px thumb dimension sets the decorative track's endpoint insets so the visual boundary follows the native range at every progress value.

The range retains a 44px input target, a visible thumb and keyboard focus, and elapsed/total time in `aria-valuetext`. Dragging previews position before committing a seek; cancel or blur clears that proposal. Seeking is disabled during preparation or when media/duration is unavailable. Dock timestamps hide on phones while the native accessible value remains.

### Dialogs

Song actions and the full-screen player share the native modal dialog lifecycle. Opening contains focus and locks background scrolling; Escape, the close/minimize control, or Android Back dismisses the topmost dialog. Ordinary action sheets also dismiss on a completed backdrop click. Nested action dialogs close before their parent, and focus returns to the connected visible opener, falling back to the dock or main content when necessary. View changes, source removal, sign-out cleanup, and component disposal dismiss affected previews and sheets.

### Offline entry and session restoration

Offline listening uses the existing collection layout and persistent player. In More, “Save offline” is distinct from “Download a separate file.” A saved song's menu action uses a green check and “Remove offline copy”; there is no dedicated saved icon in the song row. Library exposes Offline through its category tile; saved music remains available without connecting Telegram. The player says “Playing offline.”

Returning profiles automatically reopen their saved Telegram session on startup. The sidebar and connection button show “Reconnecting…” during startup work; first-time profiles retain the explicit Connect action. Restoring a session does not block offline listening, change an explicitly selected Offline view, or bypass pending sign-out cleanup. Expired sessions expose the normal authentication form, and startup failures retain manual Connect as recovery. Session detection reads the pinned browser session database's existence or the native protected database-key entry; it stores no additional credentials.


### Loading and progress

Startup uses the existing green ChannelWave mark above a 28px wordmark on the charcoal surface, followed by a restrained five-bar wave and “Opening your music…” in muted 14px text. Web, Linux and MAUI boot shells share `loading.css` with the Razor startup component. The startup screen ends after local initialization; reconnection remains inline so saved music is reachable.

Track and source skeletons follow the real responsive row geometry, with six track rows, three source rows, or three appended pagination rows. A 150ms reveal delay avoids flicker without delaying results. Existing rows remain available during refreshes; a reserved status slot prevents the update indicator from shifting the list. Compact spinners accompany operation-specific text; actual transfer byte counts retain determinate progress, while unknown totals use native indeterminate progress. Loading, empty and failed reads are distinct, with a local retry action for failed library reads. Screen-reader status and busy semantics accompany loading regions, and reduced motion disables loading animation. Artwork loading ends at image load/error or the normal music-icon fallback, retaining cached images during refreshes.

### Playlist management

A labeled chooser pairs playlist names and song counts with New playlist. The Playlist actions disclosure reveals Rename, Duplicate, Clear tracks, and Delete playlist; the original listening list cannot be deleted. Create, rename, and copy-name forms appear inline on a raised charcoal surface. Clear and delete use inline confirmation with Cancel and explain that songs remain in the library. Named playlists appear in the dedicated Playlists collection and can be pinned to Home tabs.

Scoped search and Custom order, Title, Artist, or Newest first sorting sit above the existing song rows. Play playlist and Shuffle play are explicit actions. Organize tracks reveals labeled row checkboxes, selection count, Select shown, Clear, Add to playlist, and Remove selected. Named up/down buttons provide keyboard-accessible reordering in Custom order with search and source filters cleared; explanatory text identifies that requirement. The destination chooser uses the existing accessible dialog and identifies songs already added. General library selection uses an accessible checkbox-shaped SVG button beside the source filter, retaining the original compact toolbar density.

At 760px and below, the playlist chooser stays beside a labeled-for-accessibility 44px plus button; its visible New playlist text hides. Playlist actions and the compact sort selector share the next row, fields use 16px text, and selection bulk actions wrap to a full-width row. Existing phone song geometry, visible focus, source attribution, detail Back navigation, safe areas, and the persistent player remain in use. Desktop retains the text-bearing creation button and aligned song columns. No new shipping raster or design tokens are introduced.

Playlist screenshots in `artifacts/playlists/` use synthetic data. See `docs/verification.md` for platform coverage.

### Instant and advanced search

Search extends the existing library without replacing its navigation, song rows or player. The query field updates after a 250ms typing pause, with Search and Enter retained for immediate submission. Its clear control preserves advanced filters. Short helper text explains live results and quoted phrases.

The dedicated Search page exposes result kinds for Tracks, Artists, Albums, and Playlists, and collection tools retain a Source pill and a Filters pill showing the number of applied advanced filter groups. Filters open a centered dialog on desktop and a full-width bottom sheet on phones. The sheet uses the existing charcoal palette, rounded upper corners, a working drag handle, a close button, independently scrolling fields and a fixed Reset / Show results footer. Publication dates and duration appear as grouped ranges, followed by Artist, Title and two duration presets. Dates stack below 360px, and phone input text is 16px.

Filter edits are staged until Show results. Reset clears the draft; Close, Escape, backdrop, swipe down and Android Back discard it and return focus to the Filters trigger. The query remains instant. Applied chips remain outside the sheet for quick removal; source selection remains a separate scope control. Invalid draft ranges show nearby errors and disable application while the previous results remain intact. The sheet uses native dialog focus containment and the shared dialog stack; editing filters does not interrupt playback.

Phone playlists keep the query helper visually hidden and associated with the field for screen readers, preserving space for management, selection and the first track above the player dock. Library and Search screens retain the visible helper.

Result feedback describes matches shown rather than a complete count. Existing rows remain visible while refreshing, newer queries own loading/error state, and the persistent player keeps its current sequence. Search captures in `artifacts/search/` use synthetic tracks and the existing music-icon fallback. The generated previews are planning references; no generated raster or catalog ships with this feature.

### Large-library performance

The existing track design now uses a measured viewport window with one screen of overscan above and below. Artwork, protected-source notices, selection controls and text scaling keep their natural heights. Account/track identity anchors browsing across updates, and focused rows remain mounted for keyboard and dialog focus restoration. The main panel remains the only library scroll container.

Another 100 tracks load automatically near the end of the loaded collection. The existing Load more tracks action remains as a keyboard and manual fallback; failed reads retain the collection and expose Retry. Counts and Select shown refer to the loaded collection, including rows outside the viewport. Player position updates render the player independently, preserving browsing and selection. No design tokens, artwork assets or shipping catalog were added.

### Source updates, Up next and continuous playback

Sources and queue controls use the shared black/charcoal/green interface. Sources show automatic-update status before discovery once a library exists. Refresh now, Pause sync, Resume sync, per-source Retry and unavailable labels keep recovery next to the affected work. Discovery still selects no sources automatically. A completed history scan continues checking while active; background suspension pauses reconciliation and returning catches up.

Up next opens as an accessible right-side dialog on desktop and a full-height sheet on phones. The current song is distinct from upcoming entries and keeps a direct pause/resume control. Current and upcoming songs show wrapping Telegram source attribution alongside title and artist. Compact rows expose Play now and a native More disclosure containing Move up, Move down and Remove; desktop drag ordering is an additional path. Each repeated song has its own queue identity. Clear upcoming retains current playback. Fixed footer controls keep shuffle/repeat and feedback reachable; 40-entry pages bound rendering and metadata hydration. Existing dialog focus/Back handling is reused, including focus recovery after moving or removing rows.

The desktop dock, full-screen player and queue footer share playback modes. The phone dock opens Up next; shuffle and repeat remain in the queue footer and full-screen player. Natural completion is handled outside Razor components; repeat off stops at the end, repeat one still permits manual Next, repeat all restarts the queue and shuffle visits each remaining entry once. Play captures the full matching collection; Play next inserts immediately, Add to queue appends and a new explicit collection Play replaces the queue. Source updates refresh metadata/availability without silently adding new queue entries. Restart restores the queue paused until explicit Resume.

Synthetic captures live under `artifacts/features/`: queue, player and sources at phone/desktop sizes. Physical-device playback and assistive-technology acceptance are tracked in `docs/verification.md`.

## Accessibility and content

### Keep

- use the same collection symbols, action colors, square artwork, and control shapes across desktop and mobile.
- retain the four navigation roots, focused-detail Back control, and persistent playback.
- keep source attribution, playback state, and recovery actions connected to the affected music or channel.
- preserve visible focus, semantic state, automatic text direction, safe-area offsets, and reduced-motion behavior.
- use symbolic covers when authentic music artwork is unavailable.

### Avoid

- Invent artists, albums, or catalog content to fill a screen.
- Hide necessary playback or row actions behind hover on mobile.
- Apply artwork shadows to every operational row or container.
- Communicate selected, saved, loading, or failed states through color alone.

Small legacy metadata text is not a body-text standard. Use readable type and retain visible controls at enlarged text sizes. See `docs/verification.md` for browser and device coverage.
