# Local labels and cover artwork (F21)

**Edit details → Local library labels** saves title/artist corrections on this device. Reindexing retains the overrides, identity and favorites; **Revert to source** restores the latest indexed Telegram labels and existing filename/unknown-artist fallbacks. Search, saved-offline metadata and current playback/system labels update without restarting audio. These actions never edit Telegram messages or original files. [MP3 tags](id3-tags.md) are a separate export-only draft.

The library and Now Playing display actual available artwork in this order:

1. A verified embedded ID3 JPEG/PNG, preferring the front cover.
2. Telegram's audio album-cover thumbnail.
3. The source channel's small photo, explicitly identified as **Channel photo** in its accessible description and tooltip.
4. The existing music icon.

No album artwork is generated or looked up from song/artist names. Failed, malformed, unsupported or oversized images leave the fallback usable and never stop playback. Protected recordings do not acquire or display artwork. Connected requests re-resolve the message and check current saving permission and account identity; channel photos also check the channel's protected status. Offline copies use cached restrictions, so unseen remote permission changes cannot be detected while disconnected.

Only visible rows (with an 80px prefetch margin) request artwork, with two concurrent jobs. Indexing remains metadata-only. Embedded inspection reads the leading ID3 ranges, bounded to 32 MiB, from already cached TDLib bytes or a saved offline copy. It never downloads audio for covers. Native hosts inspect completed local files; browser streaming can inspect a cached leading tag and retries when playback becomes ready. Telegram thumbnail/channel image transfers are limited to 512 KiB each and a 20-second operation deadline. Closing/disposal, account changes and source removal invalidate pending work; late writes also validate the current stored row and media identity.

Verified images are reduced to at most 320px on their longest side and stored as JPEG thumbnails of at most 512 KiB. Track JSON holds optional artwork data, media identity, kind and cache time; no database schema change is required. Each account retains at most 64 thumbnails and 16 MiB of image bytes, evicting the oldest entries. Browser offline metadata snapshots receive the same cache updates and evictions. Reindexing preserves thumbnails for the same recording and clears them when media identity changes or protection becomes known. Source removal/sign-out deletes their account-scoped rows and cached artwork. This cache can be regenerated and is subject to browser storage eviction.

Opening **Read tags** also makes an original embedded cover available to the library. Replacing/removing a picture in the file editor remains an unsaved export draft and does not change the displayed source cover. Original audio, offline audio and Telegram messages remain intact.

Shared Razor components, JS thumbnail normalization and Core models are used by browser, Linux and MAUI hosts. Native platform verification and browser regression results are recorded in [verification](verification.md).
