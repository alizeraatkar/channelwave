# Listening history, statistics and player recovery

The shared listening experience extends ChannelWave's local-first music library.
History and Statistics live under Your Library on desktop and mobile. Action
buttons use the existing SVG icon language with accessible names and tooltips.
Period, source and other value choices use labeled native fields.
[Activity screenshots](design/listening-experience/README.md) use synthetic data.

## Local listening records

Collection defaults to enabled and retention to Until deleted. Account →
Listening & privacy controls collection, retention, remembered player state and
clearing. Pausing collection pauses both history and statistics. The player
continues to work. Resuming collection starts a fresh recorded attempt; private
intervals are never recovered afterward.

A listen begins only after observed audio progress. Selection, preparing media,
autoplay rejection and opening a player create no listen. Pauses and interrupted
resumes retain the same listen; another queue entry, natural repeat or explicit
replay creates a separate attempt. Queue navigation history remains independent.

History preserves partial and repeated listens and recorded title/artist/source
labels. It supports text, date, source, qualification and track filtering, with
50-event keyset pages and day-continuation rows. Each row offers playback and
details; details contain queue/source/message actions, recorded intervals and
deletion. Missing current library rows keep their historical labels. An available
verified offline copy can still play under the existing media restrictions.
Historical artwork uses current locally available artwork or the shared fallback;
history does not retain an independent permanent copy of album imagery.

Deleting one listen removes all its continued-day rows and statistical
contributions. Clearing removes this account's listening events and aggregate
facts, preserving music, favorites, playlists and the queue. Deleting an active
listen suppresses the remaining attempt, including after restart. Account erasure
also removes listening data and saved player state. Source removal preserves
listening facts, which can be filtered and deleted independently.

90-day and one-year retention remove whole listens last active before the
90/365-day UTC cutoff. A listen crossing that cutoff remains whole. Retention
runs at binding/settings changes and daily while the coordinator is active.

## Counting

Time is the observed monotonic elapsed time bounded by non-seek media progress.
Preparing, buffering, seeking, pauses, errors, app mute and zero app volume add
no listening time. Backward seeks can lead to real replay time; forward seeks
never supply time. Device output volume and human attention cannot be measured.
Unexplained observation gaps over two seconds are excluded, not extrapolated.

An attempt qualifies once after 30 seconds or half its duration, whichever is
shorter. Unknown duration uses 30 seconds. The threshold and rule version are
captured at recording start. Completion alone does not qualify a recording.
For example, hearing 20 seconds, seeking forward two minutes and hearing another
eight seconds contributes 28 seconds and no play for a three-minute recording.

Unique tracks use account/chat/message recording identity. Cross-posts are
separate recordings. Artist counts use complete normalized known credits from
manual overrides, embedded metadata or Telegram, never inferred people or
channel names. Unknown fallback credits are excluded; collaborations remain one
supplied credit. Rankings order qualified plays, listening time and stable key.
Partial time contributes to totals and to the matching ranked entity's time.

UTC timestamps and millisecond durations are stored. The current device timezone
determines local day groups. Listening time is split at day boundaries; qualified
plays belong to when the threshold was reached. Custom dates include both dates
through the next local midnight; built-in periods end at now. Last 7/30 days
include today and the preceding 6/29 dates. Daylight saving and timezone changes
regroup instants without changing all-time totals.

Statistics provide time, qualified plays, distinct recordings/credits, track/
artist/source rankings, and activity. Long periods use week/month/year chart buckets;
the accompanying semantic table retains daily values. No external analytics or
music metadata service is introduced.

## Ownership and storage

Core owns the recorder, snapshot validation, counters, civil-time range rules
and query contracts. One coordinator-owned observation loop supplies the
recorder and UI presenter. Razor component lifetimes and state reads do not own
recording. Adapter notifications and the polling fallback share selection and
completion generation guards.

SQLite adds Listens and ListenBuckets through an additive EF migration.
IndexedDB upgrades version 2 to version 3 with corresponding stores and timeline,
source, account and listen indexes. Records and hourly per-listen aggregate facts
are committed with player/settings state in one transaction. Aggregation reads
bounded 500-bucket batches; history returns at most 50 events per UI page.
Distinct counts are recomputed from qualified identities, never summed per day.

Stable listen IDs, cumulative updates, account revisions and deletion generations
prevent retries or stale queued saves from adding time or resurrecting deleted
events. Writes are serialized. Retry buffers hold at most 128 pending attempts;
storage failures stay visible and excluded intervals are not invented afterward.
SQLite initializes WAL/FULL durability; browser writes request strict IndexedDB
durability where supported.

## Recovery

Snapshots retain exact queue-entry IDs and duplicates, current/order/original
shuffle order, navigation trail and cursor, position, repeat/shuffle, output
volume/mute, collection context, offline-only mode and an interrupted listen ID.
Volume/mute preferences also survive an empty queue. Snapshot envelopes carry
versions and SHA-256 checksums; the previous snapshot provides corruption
fallback. Unknown future versions are preserved and block writes.

The current implementation commits a complete queue snapshot with each changed
checkpoint. This favors a single verifiable transaction and simple fallback;
factoring immutable large queues into separate revision documents is a future
write-amplification optimization, not a prerequisite for queue identity recovery.

Startup completes pending sign-out checks, establishes an authenticated or
previously established local account, then binds playback. A live native
singleton wins over saved state on activity/WebView recreation. Otherwise the
interface restores paused. Restoring does not prepare, load, seek or play audio.
The remembered song and seek position appear normally, without restoration
status text or a play-button tooltip.
Resume re-resolves the recording, prefers verified offline media, applies output
settings, seeks after loading, then requests playback. Temporary URLs, handles,
runtime file IDs and prepared media are never stored in playback snapshots.

The coordinator schedules five-second progress checkpoints and immediate saves
for important commands. The expected crash-loss target is at most six seconds
under healthy scheduling/storage with writes completing within one second;
browser suspension, failed writes, site eviction and physical power loss cannot
carry that guarantee. Shutdown hooks are supplementary. Explicit Stop clears
saved queues; UI detachment and native interruption preserve recovery state.

The existing exclusive browser profile Web Lock remains authoritative. Another
tab cannot take over or write listening data. Linux retains its profile file
lock. Native activity recreation attaches to existing playback; Android process
death never makes its non-sticky service autoplay old music.

See `docs/verification.md` for the exact automated and real-device evidence.
