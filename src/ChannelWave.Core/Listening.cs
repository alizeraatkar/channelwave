using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChannelWave.Core;

public sealed record ListeningPreferences(bool Collect = true, int RetentionDays = 0, bool RememberPlayer = true,
    double Volume = 1, bool Muted = false);
public sealed record PlaybackContext(string Kind = "library", string Label = "Your library", Guid? CollectionId = null);
// Durable references deliberately contain no runtime file IDs, URLs, paths or artwork bytes.
public sealed record ListeningTrack(Guid Id, long Account, long Chat, long Message, string MediaKey,
    string Title, string Artist, string? ArtistKey, string Source, int Duration)
{
    public string Identity => $"{Account}:{Chat}:{Message}";
    public string SearchText => Metadata.Normalize($"{Title} {Artist} {Source}");
    public static ListeningTrack From(Track track) => new(track.Id, track.Origin.AccountId, track.Origin.ChatId,
        track.Origin.MessageId, Metadata.MediaKey(track), track.Title, track.Performer,
        track.PerformerOverride is not null || !string.IsNullOrWhiteSpace(track.EmbeddedMetadata?.Artist)
            || !string.IsNullOrWhiteSpace(track.OriginalPerformer) ? Metadata.Normalize(track.Performer) : null,
        track.Origin.SourceName, track.Duration);
    public Track Placeholder() => new(Id, new(Account, Chat, Message, Source, false),
        new("", "", 0, "", "", 0), Title, Artist, Title, Artist, Duration, DateTimeOffset.UnixEpoch, Available: false);
}
public sealed record ListeningSpan(long Start, long End, double ActiveMilliseconds);
public sealed record ListeningEvent(Guid Id, long Account, Guid? EntryId, ListeningTrack Track,
    long Started, long LastActive, double ActiveMilliseconds, double ThresholdMilliseconds,
    long? QualifiedAt, double StartPosition, double EndPosition, string Outcome, ListeningSpan[] Spans,
    bool Offline = false, bool Resumed = false, bool HasGaps = false, int RuleVersion = 1);
public sealed record SavedPlayerSnapshot(int Version, long Account, QueueEntry[] Entries, Guid? CurrentEntry,
    Guid[] OriginalOrder, Guid[] Navigation, int NavigationIndex, double Position, RepeatMode Repeat,
    bool Shuffle, double Volume, bool Muted, bool OfflineOnly, PlaybackContext Context,
    ListeningTrack? Current, Guid? ActiveListen, bool Suppressed = false);
public sealed record PlaybackEnvelope(int Version, string Json, string Checksum)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static PlaybackEnvelope Create(SavedPlayerSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, Options);
        return new(1, json, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
    public SavedPlayerSnapshot? Read(long account)
    {
        if (Version > 1) throw new NotSupportedException("This saved player belongs to a newer version of ChannelWave.");
        if (Version != 1 || string.IsNullOrEmpty(Json) || string.IsNullOrEmpty(Checksum)
            || !string.Equals(Checksum, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json))), StringComparison.Ordinal)) return null;
        var state = JsonSerializer.Deserialize<SavedPlayerSnapshot>(Json, Options);
        if (state is null || state.Version != 1 || state.Account != account || state.Entries is null
            || state.OriginalOrder is null || state.Navigation is null || state.Context is null
            || !double.IsFinite(state.Position) || state.Position < 0 || !double.IsFinite(state.Volume)
            || state.Volume is < 0 or > 1 || !Enum.IsDefined(state.Repeat)
            || state.Entries.Any(e => e.Id == Guid.Empty || e.TrackId == Guid.Empty)
            || state.Entries.Select(e => e.Id).Distinct().Count() != state.Entries.Length
            || state.Current is { } track && track.Account != account) return null;
        var ids = state.Entries.Select(e => e.Id).ToHashSet();
        if (state.CurrentEntry is { } current && !ids.Contains(current)
            || state.OriginalOrder.Length != ids.Count || state.OriginalOrder.Distinct().Count() != ids.Count
            || state.OriginalOrder.Any(id => !ids.Contains(id)) || state.Navigation.Any(id => !ids.Contains(id))
            || state.NavigationIndex < -1 || state.NavigationIndex >= state.Navigation.Length
            || state.Current is { } selected && state.CurrentEntry is { } entry && state.Entries.First(e => e.Id == entry).TrackId != selected.Id) return null;
        return state;
    }
}
public sealed record ListeningState(long Revision = 0, long Generation = 0, ListeningPreferences? Preferences = null,
    PlaybackEnvelope? Player = null, PlaybackEnvelope? PreviousPlayer = null, Guid? SuppressedListen = null);
public sealed record ListeningCommit(long Account, long ExpectedRevision, long Generation,
    ListeningPreferences Preferences, PlaybackEnvelope? Player, ListeningEvent[] Events, bool ForgetPlayer = false);
public sealed record ListeningCursor(long Time, string Id);
public sealed record ListeningQuery(long From = 0, long To = long.MaxValue, string Search = "", long? Source = null,
    Guid? Track = null, bool? Qualified = null, ListeningCursor? Before = null, int Limit = 50)
{
    public string[] Terms => LibrarySearchMatcher.Tokenize(Search);
}
public sealed record ListeningPage(ListeningEvent[] Items, ListeningCursor? Next);
public sealed record ListeningBucket(string Id, long Account, Guid ListenId, long Hour, ListeningTrack Track,
    long? QualifiedAt, ListeningSpan[] Spans)
{
    public static ListeningBucket[] From(ListeningEvent listen)
    {
        var hours = new Dictionary<long, List<ListeningSpan>>();
        foreach (var span in listen.Spans)
        {
            for (var start = span.Start; start < span.End;)
            {
                var hour = start / 3_600_000 * 3_600_000;
                var end = Math.Min(span.End, hour + 3_600_000);
                if (!hours.TryGetValue(hour, out var values)) hours[hour] = values = [];
                values.Add(new(start, end, span.ActiveMilliseconds * (end - start) / (span.End - span.Start)));
                start = end;
            }
        }
        return hours.Select(pair => new ListeningBucket($"{pair.Key:D16}:{listen.Id:N}", listen.Account, listen.Id,
            pair.Key, listen.Track, listen.QualifiedAt is { } q && q / 3_600_000 * 3_600_000 == pair.Key ? q : null, [.. pair.Value])).ToArray();
    }
}
public interface IListeningStore
{
    async Task<RecentTrack[]> RecentTracksPageAsync(long account, int offset, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var items = await LibraryDiscovery.RecentFallbackAsync(this, account, offset + Math.Clamp(limit, 1, 1000), ct);
        return items.Skip(offset).ToArray();
    }
    Task<RecentTrack[]> RecentTracksAsync(long account, int limit = 6, CancellationToken ct = default)
        => LibraryDiscovery.RecentFallbackAsync(this, account, Math.Clamp(limit, 1, 100), ct);
    Task<ListeningState> ReadListeningStateAsync(long account, CancellationToken ct = default);
    Task<long> CommitListeningAsync(ListeningCommit commit, CancellationToken ct = default);
    Task<ListeningEvent?> ListenAsync(long account, Guid id, CancellationToken ct = default);
    Task<ListeningPage> HistoryAsync(long account, ListeningQuery query, CancellationToken ct = default);
    Task<ListeningBucket[]> BucketsAsync(long account, long from, long to, string? after, CancellationToken ct = default);
    Task<ListeningState> DeleteListeningAsync(long account, Guid? id, long? olderThan = null, CancellationToken ct = default);
}

/// <summary>Consumes one ordered engine stream. UI reads never call this recorder.</summary>
public sealed class ListeningRecorder(TimeProvider clock)
{
    private AudioState? previous;
    private long timestamp, utc, sequence = -1;
    private bool discontinuity = true;
    public ListeningEvent? Current { get; private set; }
    public bool Suppressed { get; set; }
    public void Restore(ListeningEvent? listen) { Current = listen is null ? null : listen with { Resumed = true }; Break(); }
    public void Break() { previous = null; discontinuity = true; }
    public ListeningEvent? Finish(string outcome)
    {
        var finished = Current is null ? null : Current with { Outcome = outcome };
        Current = null; Suppressed = false; Break(); return finished;
    }
    public ListeningEvent? Observe(long sample, Track track, Guid? entry, AudioState state, bool collect, bool offline)
    {
        if (sample <= sequence) return null;
        sequence = sample;
        var now = clock.GetTimestamp(); var wall = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var elapsed = previous is null ? 0 : clock.GetElapsedTime(timestamp, now).TotalMilliseconds;
        var advance = previous is null ? 0 : (state.Position - previous.Position) * 1000 / Math.Max(0.01, state.Rate);
        var valid = previous is not null && !previous.Paused && !previous.Ended && previous.Error is null
            && !previous.Buffering && !previous.Seeking && !previous.Muted && previous.Volume > 0
            && !state.Seeking && !state.Buffering && !state.Muted && state.Volume > 0
            && state.Error is null && advance > 0 && elapsed is > 0 and <= 2000
            && advance <= elapsed + 250 && Math.Abs(wall - utc - elapsed) <= 1000;
        if (Current is not null && elapsed > 2000) Current = Current with { HasGaps = true };
        ListeningEvent? changed = null;
        if (valid && collect && !Suppressed)
        {
            var active = Math.Min(elapsed, advance);
            if (Current is null)
            {
                var duration = state.Duration > 0 && double.IsFinite(state.Duration) ? state.Duration : track.Duration;
                Current = new(Guid.NewGuid(), track.Origin.AccountId, entry, ListeningTrack.From(track), utc, wall,
                    0, duration > 0 ? Math.Min(30_000, duration * 500) : 30_000, null,
                    previous!.Position, state.Position, "active", [], offline);
            }
            var spans = Current.Spans.ToList();
            var last = spans.LastOrDefault();
            if (!discontinuity && last is not null && last.End == utc && wall / 60_000 == last.Start / 60_000)
                spans[^1] = last with { End = wall, ActiveMilliseconds = last.ActiveMilliseconds + active };
            else spans.Add(new(utc, wall, active));
            var total = Current.ActiveMilliseconds + active;
            var qualified = Current.QualifiedAt;
            if (qualified is null && total >= Current.ThresholdMilliseconds)
                qualified = utc + (long)Math.Clamp((Current.ThresholdMilliseconds - Current.ActiveMilliseconds) / active * (wall - utc), 0, wall - utc);
            Current = Current with { LastActive = wall, ActiveMilliseconds = total, EndPosition = state.Position,
                QualifiedAt = qualified, Spans = [.. spans], Outcome = "active" };
            changed = Current; discontinuity = false;
        }
        else discontinuity = true;
        previous = state; timestamp = now; utc = wall;
        return changed;
    }
}
