namespace ChannelWave.Core;

public sealed record ListeningRank(string Key, string Label, ListeningTrack Track, int Plays, double Milliseconds);
public sealed record ListeningDay(DateOnly Date, double Milliseconds, int Plays);
public sealed record ListeningStatistics(double Milliseconds, int Plays, int Tracks, int Artists,
    ListeningRank[] TopTracks, ListeningRank[] TopArtists, ListeningRank[] TopSources, ListeningDay[] Days)
{
    public IReadOnlyDictionary<long, string> Sources { get; init; } = new Dictionary<long, string>();
}

public static class ListeningPeriods
{
    public static (long From, long To) Range(string period, DateTimeOffset now, TimeZoneInfo zone,
        DateOnly? start = null, DateOnly? end = null)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = period switch { "today" => today, "30" => today.AddDays(-29), "year" => new(today.Year, 1, 1),
            "all" => new DateOnly(1970, 1, 1), "custom" => start ?? today, _ => today.AddDays(-6) };
        var last = period == "custom" ? (end ?? today).AddDays(1) : today.AddDays(1);
        if (first >= last) throw new ArgumentException("The end date must be on or after the start date.");
        return (Boundary(first, zone), Math.Min(now.ToUnixTimeMilliseconds(), Boundary(last, zone)));
    }
    public static long Boundary(DateOnly day, TimeZoneInfo zone)
    {
        var date = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(date)) date = date.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date, zone)).ToUnixTimeMilliseconds();
    }
    public static DateOnly Day(long utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utc), zone).DateTime);
}

public static class ListeningAggregation
{
    public static double Overlap(ListeningSpan span, long from, long to) => span.End <= span.Start ? 0
        : Math.Max(0, Math.Min(to, span.End) - Math.Max(from, span.Start)) / (double)(span.End - span.Start) * span.ActiveMilliseconds;

    public static Task<ListeningStatistics> StatisticsAsync(this IListeningStore store, long account,
        long from, long to, TimeZoneInfo zone, CancellationToken ct = default)
        => store.StatisticsAsync(account, from, to, zone, null, ct);

    public static async Task<ListeningStatistics> StatisticsAsync(this IListeningStore store, long account,
        long from, long to, TimeZoneInfo zone, long? source, CancellationToken ct = default)
    {
        var tracks = new Dictionary<string, ListeningRank>(StringComparer.Ordinal);
        var artists = new Dictionary<string, ListeningRank>(StringComparer.Ordinal);
        var sources = new Dictionary<string, ListeningRank>(StringComparer.Ordinal);
        var days = new SortedDictionary<DateOnly, ListeningDay>();
        var sourceOptions = new Dictionary<long, string>();
        double total = 0; var plays = 0; string? after = null;
        void Add(Dictionary<string, ListeningRank> ranks, string key, string label, ListeningBucket bucket, int count, double time)
        {
            var previous = ranks.GetValueOrDefault(key) ?? new(key, label, bucket.Track, 0, 0);
            ranks[key] = previous with { Plays = previous.Plays + count, Milliseconds = previous.Milliseconds + time };
        }
        do
        {
            var batch = await store.BucketsAsync(account, from / 3_600_000 * 3_600_000, to, after, ct);
            foreach (var bucket in batch)
            {
                ct.ThrowIfCancellationRequested();
                var time = bucket.Spans.Sum(s => Overlap(s, from, to));
                var qualified = bucket.QualifiedAt is { } q && q >= from && q < to ? 1 : 0;
                if (time <= 0 && qualified == 0) continue;
                sourceOptions[bucket.Track.Chat] = bucket.Track.Source;
                if (source is { } selectedSource && bucket.Track.Chat != selectedSource) continue;
                total += time; plays += qualified;
                Add(tracks, bucket.Track.Identity, bucket.Track.Title, bucket, qualified, time);
                if (!string.IsNullOrWhiteSpace(bucket.Track.ArtistKey)) Add(artists, bucket.Track.ArtistKey, bucket.Track.Artist, bucket, qualified, time);
                Add(sources, bucket.Track.Chat.ToString(System.Globalization.CultureInfo.InvariantCulture), bucket.Track.Source, bucket, qualified, time);
                foreach (var span in bucket.Spans)
                {
                    var cursor = Math.Max(from, span.Start); var end = Math.Min(to, span.End);
                    while (cursor < end)
                    {
                        var day = ListeningPeriods.Day(cursor, zone); var next = Math.Min(end, ListeningPeriods.Boundary(day.AddDays(1), zone));
                        if (next <= cursor) break;
                        var previous = days.GetValueOrDefault(day) ?? new(day, 0, 0);
                        days[day] = previous with { Milliseconds = previous.Milliseconds + Overlap(span, cursor, next) };
                        cursor = next;
                    }
                }
                if (qualified > 0)
                {
                    var day = ListeningPeriods.Day(bucket.QualifiedAt!.Value, zone);
                    var previous = days.GetValueOrDefault(day) ?? new(day, 0, 0);
                    days[day] = previous with { Plays = previous.Plays + 1 };
                }
            }
            after = batch.Length == 500 ? batch[^1].Id : null;
            await Task.Yield();
        } while (after is not null);
        ListeningRank[] Rank(Dictionary<string, ListeningRank> values) => values.Values.Where(x => x.Plays > 0)
            .OrderByDescending(x => x.Plays).ThenByDescending(x => x.Milliseconds).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        return new(total, plays, tracks.Count(x => x.Value.Plays > 0), artists.Count(x => x.Value.Plays > 0),
            Rank(tracks), Rank(artists), Rank(sources), [.. days.Values]) { Sources = sourceOptions };
    }
}
