# Find and resume music

F11 and F12 extend the shared local library and Home. They use selected Telegram
sources, locally available metadata, retained listening history and the existing
playback coordinator. Browsing never starts playback or downloads tags.

## Library

Tracks, Artists and Albums share text, source, liked, saved-offline, duration and
publication filters. Filters apply before pagination. Text also searches known
album titles. Sort tracks by library-add date, title, full artist credit, Telegram
publication date or duration in either direction. Unknown duration follows known
duration. Playlists retain their existing Custom order and playlist sorting.
Explicit collection Play captures every matching track ID in the chosen order,
including unloaded pages. Later browsing does not change an active queue.

Artist groups use normalized full effective credits, including local corrections;
collaborations are not split. Missing credits do not produce an invented artist.
Albums require known embedded album and artist metadata and group by album title
plus full artist credit. Same-name albums with different credits remain separate.
Compilation credits are not inferred. Album order uses disc/track numbers, then
title, with unnumbered tracks following numbered tracks. Group artwork uses an
existing track artwork reference and the normal fallback.

Browsing preferences are account scoped. Nondefault browsing destinations record
their filters, sorting and group in the local hash route for Back/Forward and
reload. These routes contain labels and filter text, but never Telegram sessions,
runtime file IDs or media paths. A group link resolves only against the current
account's local library. Removing all members leaves a recoverable empty result.

## Added dates and existing libraries

New tracks receive `AddedAt` on their first successful database write. Reindexing,
message updates, local corrections and media replacements preserve it. Older
tracks without this field share one durable account-scoped import baseline,
stored as `library-added-baseline-v1`. Home labels them **Existing library ·
Imported [date]**; the baseline is not a claim about their original index date.
The upgrade does not rewrite every track or require a database schema change.

## Home

Returning users see one Continue listening panel when a current or saved session
exists. It mirrors the shared player and queue. Resume is explicit; page loading
and previewing a track do not load/play audio. The existing paused session
restoration, output settings and privacy controls remain authoritative.

Recently played shows distinct local track IDs by their latest positive observed
listening activity, including partial listens. Current metadata is resolved for
display. Older repeats remain in History. Clearing/deleting history and retention
remove corresponding recent activity; pausing collection adds no new history.
Recently added follows library-add dates and opens the full sorted library.
Desktop previews show six entries per section; phone previews show three.

Home rows reuse track preview and action dialogs, with explicit playback,
favorites, playlist, queue, export and offline actions. Disconnected playback uses
verified saved copies. Missing or damaged files keep existing repair guidance.
Users without indexed music retain the onboarding screen. All data is local and
account-scoped, and sign-out removes the account's new state with its library.

See [verification](verification.md) for automated evidence and physical-device
limits. Synthetic content and artwork in captures are test fixtures only.

## Clickable credits

Known artist and album names open their exact local collection from Home, song
rows, the dock and full-screen player. Artist credits in queue/history and album
credits in read-only file details also navigate. Album cards keep their album
opener separate from the artist link. Title/artwork previews and credit links
are sibling controls; navigation never starts or restarts playback. Missing
usable credits remain plain text. Following a link closes its player/queue/details
dialog, resets unrelated filters and supports browser Back/Forward. Disconnected
links filter the current account's saved tracks in Offline, preserving offline-only
playback. Returning from an album to All tracks restores a valid general sort.
