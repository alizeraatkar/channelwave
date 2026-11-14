namespace ChannelWave.Core;

public enum LibraryOrder { AddedNewest, AddedOldest, Title, TitleDescending, Artist, ArtistDescending, PublishedNewest, PublishedOldest, Shortest, Longest, Album }
public sealed record LibraryGroup(string Key, string Label, string Artist, int Count, Guid ArtworkTrack);
public sealed record RecentTrack(Guid TrackId, long LastActive, ListeningTrack? Reference = null);

public static class LibraryDiscovery
{
    public const string BaselineKey = "library-added-baseline-v1";
    public static LibraryOrder? PlaylistOrder(PlaylistSort sort) => sort switch
    {
        PlaylistSort.AddedNewest => LibraryOrder.AddedNewest, PlaylistSort.AddedOldest => LibraryOrder.AddedOldest,
        PlaylistSort.TitleDescending => LibraryOrder.TitleDescending, PlaylistSort.ArtistDescending => LibraryOrder.ArtistDescending,
        PlaylistSort.Oldest => LibraryOrder.PublishedOldest, PlaylistSort.Shortest => LibraryOrder.Shortest,
        PlaylistSort.Longest => LibraryOrder.Longest, _ => null
    };
    public static string? ArtistKey(Track track) => string.IsNullOrWhiteSpace(track.PerformerOverride)
        && string.IsNullOrWhiteSpace(track.EmbeddedMetadata?.Artist) && string.IsNullOrWhiteSpace(track.OriginalPerformer)
        ? null : Metadata.Normalize(track.Performer) is { Length: > 0 } key ? key : null;
    public static string? AlbumKey(Track track) => ArtistKey(track) is { } artist
        && Metadata.Normalize(track.EmbeddedMetadata?.Album ?? "") is { Length: > 0 } album
        ? $"{artist.Length}:{artist}{album}" : null;
    public static int Number(string? value) => int.TryParse(value?.Split('/')[0], out var number) && number > 0 ? number : 0;
    public static IEnumerable<LibrarySearchRow> Order(IEnumerable<LibrarySearchRow> rows, LibraryOrder order)
    {
        var stable = rows.OrderBy(x => x.Identity, StringComparer.Ordinal);
        return order switch
        {
            LibraryOrder.AddedNewest => stable.OrderByDescending(x => x.AddedAt),
            LibraryOrder.AddedOldest => stable.OrderBy(x => x.AddedAt is null).ThenBy(x => x.AddedAt),
            LibraryOrder.Title => stable.OrderBy(x => x.Title, StringComparer.Ordinal),
            LibraryOrder.TitleDescending => stable.OrderByDescending(x => x.Title, StringComparer.Ordinal),
            LibraryOrder.Artist => stable.OrderBy(x => x.Artist, StringComparer.Ordinal).ThenBy(x => x.Title, StringComparer.Ordinal),
            LibraryOrder.ArtistDescending => stable.OrderByDescending(x => x.Artist, StringComparer.Ordinal).ThenBy(x => x.Title, StringComparer.Ordinal),
            LibraryOrder.PublishedNewest => stable.OrderByDescending(x => x.PublishedAt),
            LibraryOrder.PublishedOldest => stable.OrderBy(x => x.PublishedAt),
            LibraryOrder.Shortest => stable.OrderBy(x => x.Duration <= 0).ThenBy(x => x.Duration),
            LibraryOrder.Longest => stable.OrderBy(x => x.Duration <= 0).ThenByDescending(x => x.Duration),
            LibraryOrder.Album => stable.OrderBy(x => x.Disc > 0 ? x.Disc : x.Number > 0 ? 1 : int.MaxValue)
                .ThenBy(x => x.Number > 0 ? x.Number : int.MaxValue).ThenBy(x => x.Title, StringComparer.Ordinal),
            _ => stable
        };
    }
    public static LibraryGroup[] Groups(IEnumerable<LibrarySearchRow> rows, LibrarySearchRequest request, string kind, CancellationToken ct)
    {
        var snapshot = rows.ToArray();
        var ids = LibrarySearch.Select(snapshot, request with { Offset = 0, Limit = int.MaxValue }, ct).ToHashSet();
        return snapshot.Where(x => ids.Contains(x.Id) && (kind == "albums" ? x.AlbumKey : x.ArtistKey) is not null)
            .GroupBy(x => kind == "albums" ? x.AlbumKey! : x.ArtistKey!, StringComparer.Ordinal)
            .Select(g => { var first = g.OrderBy(x => x.Identity, StringComparer.Ordinal).First(); return new LibraryGroup(g.Key,
                kind == "albums" ? first.AlbumLabel : first.ArtistLabel, kind == "albums" ? first.ArtistLabel : "", g.Count(), first.Id); })
            .Where(x => LibrarySearchMatcher.Tokenize(request.GroupText ?? "").All(term => Metadata.Normalize(x.Label).Contains(term, StringComparison.Ordinal)))
            .OrderBy(x => Metadata.Normalize(x.Label), StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Skip(request.Offset).Take(request.Limit).ToArray();
    }
    public static async Task<LibraryGroup[]> GroupsFallbackAsync(ILibraryRepository library, long account, LibrarySearchRequest request, string kind, CancellationToken ct)
    {
        var tracks = await library.SearchAsync(account, request with { Offset = 0, Limit = int.MaxValue }, ct);
        return Groups(tracks.Select(LibrarySearchRow.FromTrack), request, kind, ct);
    }
    public static async Task<RecentTrack[]> RecentFallbackAsync(IListeningStore store, long account, int limit, CancellationToken ct)
    {
        var found = new Dictionary<Guid, RecentTrack>(); ListeningCursor? cursor = null;
        do
        {
            var page = await store.HistoryAsync(account, new(Before: cursor, Limit: 100), ct);
            foreach (var listen in page.Items)
                if (listen.ActiveMilliseconds > 0) found.TryAdd(listen.Track.Id, new(listen.Track.Id, listen.LastActive, listen.Track));
            cursor = page.Next;
        } while (cursor is not null && found.Count < limit);
        return found.Values.Take(limit).ToArray();
    }
}
