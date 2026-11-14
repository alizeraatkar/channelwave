# Privacy and session behavior

## Listening data

Listening history, statistics, saved queues and playback preferences stay in the
account-scoped local SQLite database or browser IndexedDB. Collection defaults
to on; retention defaults to Until deleted. Account → Listening & privacy works
for the established local account while disconnected too. Pausing stops both
history and statistics collection without stopping playback or player recovery.
There is no cloud listening sync, external analytics or artist lookup.

Partial and repeated listens retain recorded music/source labels and observed
active intervals. Seeking, buffering, pauses, app mute and zero app volume add no
time. Unknown artist identities are not inferred. Historical artwork falls back
to currently available local artwork or a placeholder.

Individual deletion and clearing remove the corresponding statistical facts as
well. Automatic 90/365-day retention keeps recent listens and limits all-time
statistics to retained records. Removing a source preserves listening records;
use its history filter to find and delete them. Sign out and erase local data
removes this account's listening records, settings and saved player snapshots.
Clearing history alone preserves playlists, favorites, audio and the queue.

Saved player references contain no temporary audio URLs, runtime file handles or
resources. They restore the interface paused; Resume re-resolves media. The
existing native metadata/database and browser-origin storage protections below
also apply to this listening data. Clearing site data can remove browser history
and saved state. See [listening behavior](listening-experience.md) for counting,
retention, recovery and the conditional checkpoint-loss target.

## Audio outputs and casting

When you select a Cast receiver, ChannelWave sends the current song and playback metadata over the local network. On Android and iOS, the receiver fetches the song through a temporary URL scoped to that receiver. ChannelWave closes the URL when the song changes, casting ends or the account signs out. On Linux, libVLC supplies the receiver connection and media transport. The playback route and media URL are not stored with the saved queue.

The temporary mobile Cast URL uses local HTTP: receiver/address and capability
checks restrict requests, but do not encrypt the song against a network observer.
Use casting on a local network you trust. Telegram login/session keys are not
included in that media URL.

## Telegram session and local library

ChannelWave creates a real Telegram user-client session. Its authorization is not technically limited to selected channels or music. Channel discovery automatically catalogs channel IDs and names from Main and Archive, without reading audio messages. Audio history is indexed only for channels explicitly selected in the picker. Discovery and selected audio indexing have separate pause/resume controls and account-scoped local progress. Unfinished audio indexing resumes on reconnect unless paused. Automatic file details also read bounded metadata ranges for indexed tracks; full audio is prepared only after Stream, Play, Save offline or Download is chosen. No account creation, automatic joins, automatic leaves, private-chat indexing, redistribution or AI processing is implemented.

Automatic file details run while the app is open and default to enabled. They read embedded tags and covers from selected tracks using offline copies or bounded TDLib ranges, without a full-file download fallback or a third-party metadata service. Derived labels, scan outcomes and thumbnails stay in the account-scoped local library. Account settings provides pause/resume and retry controls; explicit pause survives reopening. Manual corrections take precedence over embedded tags and Telegram labels.

Unindex channel removes that source's local audio metadata, favorites, history cursor and playlist references, and stops playback of removed tracks. It remains in the discovered channel catalog and can be selected again. Unindexing does not leave the Telegram channel, delete Telegram messages or purge downloaded media from TDLib storage.

The application has no analytics or backend for sessions. Phone numbers, codes and passwords are submitted to TDLib and cleared from the form. Application code does not log them. TDLib native logging is disabled. Security wrapper version 2 suppresses wrapper/generated module logging and keeps secret chats disabled. The web publishing script enforces CSP and denies framing; browser-origin storage is still not a credential vault. See [security audit and remaining platform gates](security-audit.md). Source changes do not update an existing deployment.

Native TDLib databases receive a random encryption key held by MAUI SecureStorage or Linux Secret Service. No plaintext key fallback is used. Native library metadata and downloaded media are **not encrypted by ChannelWave**: they rely on the OS account, app sandbox and disk encryption. Linux requires a current-user-owned, real profile directory with owner-only 0700 permissions and an unlocked Secret Service keyring. Startup refuses a profile it cannot protect. Android app backup is disabled. Apple backup exclusions, Windows profile ownership and packaged sandbox behavior require device validation.

Browser session, metadata, and app-owned offline audio live in the origin's IndexedDB. This is not equivalent to an OS credential vault. Origin scripts, XSS, browser extensions with sufficient privileges and the device owner can access it. Use a dedicated HTTPS origin and no third-party scripts. Saving offline requests persistent storage on a best-effort basis; the browser can deny the request or reclaim data, and clearing site data removes saved audio. There is no guaranteed offline retention. One tab holds a Web Lock for the profile; another tab is refused.

Sign out saves cleanup intent before canceling work and pausing playback, requests `logOut`, waits for TDLib's final closed state, clears account metadata and offline audio, then removes the native key or browser session stores. If remote logout fails, `destroy` is attempted and the UI explicitly tells the user to revoke the session in Telegram Settings → Devices. Independent library/session cleanup failures remain visible with a retry action; reconnecting and offline reads are blocked while cleanup is pending. A small recovery checkpoint retains the affected account ID, requested/closed phase and confirmed remote-logout result until cleanup succeeds. Reopening a pending requested cleanup uses the saved profile only for closure, without restoring identity, discovery or playback. If storage rejects both the checkpoint and erasure, the error instructs you to keep the app open and retry; recovery after a crash cannot then be guaranteed. Browser cache names and the main-thread file-database handle remain tied to the pinned tdweb implementation and must be reviewed on upgrade.

The browser's audio service worker bridges bounded file-range requests to the owning page through MessageChannel. URLs contain temporary random stream tokens; the page resolves those tokens to active TDLib file IDs. Streaming responses use `Cache-Control: no-store`. The worker caches the public application shell for offline startup, excluding developer configuration, Telegram traffic, and audio. App-owned saved audio is stored separately in account-scoped IndexedDB and erased on source removal or sign-out. Stopping or changing playback invalidates stream tokens and cancels outstanding TDLib downloads. TDLib itself may cache received media. PWA installation uses the same origin and storage boundary. Rendering uses Razor text encoding; captions/metadata are not interpreted as HTML. Original-message links are restricted to validated HTTPS t.me destinations. Native bridge/navigation behavior still requires device validation.

Download saves a separate original audio file through the browser download mechanism, Linux Downloads directory, or MAUI system share/save sheet. These exported files are outside the application library. Unindexing and signing out do not erase copies saved on your device. Browser success feedback means the file was handed to the browser, not that the user confirmed a save destination.

Before distribution, review [Telegram API terms](https://core.telegram.org/api/terms), [sponsored-message requirements](https://core.telegram.org/api/sponsored-messages), and [content licensing terms](https://telegram.org/tos/content-licensing). Music-focused client eligibility and applicable sponsored-message handling remain release blockers. Protected messages are rejected by both current streaming and download paths, with fresh account/message checks before resolving any file. A passing build is not store approval or a legal-compliance finding.
