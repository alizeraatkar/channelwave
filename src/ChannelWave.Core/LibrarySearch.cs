using System.Text;

namespace ChannelWave.Core;

public sealed record LibrarySearchCriteria(string Text = "", string Title = "", string Artist = "",
    int? MinDuration = null, int? MaxDuration = null, DateTimeOffset? PublishedFrom = null,
    DateTimeOffset? PublishedBefore = null, bool LikedOnly = false, bool SavedOnly = false)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(Text) || HasFilters;
    public bool HasFilters => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist)
        || MinDuration is not null || MaxDuration is not null || PublishedFrom is not null || PublishedBefore is not null || LikedOnly || SavedOnly;

    public void Validate()
    {
        if (MinDuration < 0 || MaxDuration < 0 || MinDuration > MaxDuration)
            throw new ArgumentException("Enter nonnegative durations with the minimum no greater than the maximum.");
        if (PublishedFrom >= PublishedBefore)
            throw new ArgumentException("The publication start date must not follow the end date.");
    }
}

public sealed record LibrarySearchRequest(LibrarySearchCriteria Criteria, bool Favorites = false, long? Chat = null,
    Guid[]? Playlist = null, PlaylistSort Sort = PlaylistSort.Custom, int Offset = 0, int Limit = 100,
    LibraryOrder? Order = null, string? ArtistKey = null, string? AlbumKey = null, Guid[]? OfflineIds = null, string? GroupText = null, Guid? PlaylistId = null)
{
    public void Validate()
    {
        Criteria.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
    }
}

// Search projections deliberately exclude media, artwork and session data.
public sealed record LibrarySearchRow(Guid Id, string Identity, long Chat, string Title, string Artist,
    string Source, int Duration, DateTimeOffset PublishedAt, bool Favorite,
    DateTimeOffset? AddedAt = null, string Album = "", string? ArtistKey = null, string? AlbumKey = null,
    string ArtistLabel = "", string AlbumLabel = "", int Disc = 0, int Number = 0)
{
    public static LibrarySearchRow FromTrack(Track track) => new(track.Id, track.Identity, track.Origin.ChatId,
        Metadata.Normalize(track.Title), Metadata.Normalize(track.Performer), Metadata.Normalize(track.Origin.SourceName),
        track.Duration, track.PublishedAt, track.Favorite, track.AddedAt,
        Metadata.Normalize(track.EmbeddedMetadata?.Album ?? ""), LibraryDiscovery.ArtistKey(track), LibraryDiscovery.AlbumKey(track),
        track.Performer, track.EmbeddedMetadata?.Album?.Trim() ?? "",
        LibraryDiscovery.Number(track.EmbeddedMetadata?.Disc), LibraryDiscovery.Number(track.EmbeddedMetadata?.Track));
}

public sealed class LibrarySearchMatcher
{
    public string[] Terms { get; }
    public string[] TitleTerms { get; }
    public string[] ArtistTerms { get; }
    private readonly LibrarySearchCriteria criteria;

    public LibrarySearchMatcher(LibrarySearchCriteria criteria)
    {
        criteria.Validate();
        this.criteria = criteria;
        Terms = Tokenize(criteria.Text);
        TitleTerms = Tokenize(criteria.Title);
        ArtistTerms = Tokenize(criteria.Artist);
    }

    public bool Matches(LibrarySearchRow row) => Terms.All(term => row.Title.Contains(term, StringComparison.Ordinal)
            || row.Artist.Contains(term, StringComparison.Ordinal) || row.Source.Contains(term, StringComparison.Ordinal)
            || row.Album.Contains(term, StringComparison.Ordinal))
        && TitleTerms.All(term => row.Title.Contains(term, StringComparison.Ordinal))
        && ArtistTerms.All(term => row.Artist.Contains(term, StringComparison.Ordinal))
        && (criteria.MinDuration is not { } min || row.Duration >= min)
        && (criteria.MaxDuration is not { } max || row.Duration <= max)
        && (criteria.PublishedFrom is not { } from || row.PublishedAt >= from)
        && (criteria.PublishedBefore is not { } before || row.PublishedAt < before);

    public static string[] Tokenize(string text)
    {
        var terms = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        void Flush()
        {
            var term = Metadata.Normalize(word.ToString());
            if (term.Length > 0) terms.Add(term);
            word.Clear();
        }
        foreach (var character in text)
        {
            if (character == '"') { Flush(); quoted = !quoted; }
            else if (!quoted && char.IsWhiteSpace(character)) Flush();
            else word.Append(character);
        }
        Flush();
        return terms.ToArray();
    }
}

public static class LibrarySearch
{
    public static Guid[] Select(IEnumerable<LibrarySearchRow> rows, LibrarySearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var matcher = new LibrarySearchMatcher(request.Criteria);
        var order = request.Playlist?.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var matches = new List<LibrarySearchRow>();
        var offline = request.OfflineIds?.ToHashSet() ?? (request.Criteria.SavedOnly ? [] : null);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if ((!(request.Favorites || request.Criteria.LikedOnly) || row.Favorite) && (request.Chat is null || row.Chat == request.Chat)
                && (order is null || order.ContainsKey(row.Id)) && (offline is null || offline.Contains(row.Id))
                && (request.ArtistKey is null || row.ArtistKey == request.ArtistKey)
                && (request.AlbumKey is null || row.AlbumKey == request.AlbumKey) && matcher.Matches(row)) matches.Add(row);
        }
        IEnumerable<LibrarySearchRow> sorted = matches.OrderBy(x => x.Identity, StringComparer.Ordinal);
        if (order is not null)
        {
            sorted = sorted.OrderBy(x => order[x.Id]);
            sorted = request.Sort switch
            {
                PlaylistSort.Title => sorted.OrderBy(x => x.Title, StringComparer.Ordinal),
                PlaylistSort.Artist => sorted.OrderBy(x => x.Artist, StringComparer.Ordinal).ThenBy(x => x.Title, StringComparer.Ordinal),
                PlaylistSort.Newest => sorted.OrderByDescending(x => x.PublishedAt),
                _ => sorted
            };
        }
        if ((request.Order ?? (order is null ? null : LibraryDiscovery.PlaylistOrder(request.Sort))) is { } general)
            sorted = LibraryDiscovery.Order(sorted, general);
        return sorted.Skip(request.Offset).Take(request.Limit).Select(x => x.Id).ToArray();
    }

    public static IReadOnlyList<Track> Filter(IReadOnlyList<Track> tracks, LibrarySearchRequest request, CancellationToken ct = default)
    {
        var byId = tracks.DistinctBy(x => x.Id).ToDictionary(x => x.Id);
        return Select(byId.Values.Select(LibrarySearchRow.FromTrack), request, ct).Select(id => byId[id]).ToArray();
    }

    // Compatibility for small/custom repository implementations. Production adapters
    // override this path to cache projections and hydrate only the selected page.
    public static async Task<IReadOnlyList<Track>> SearchFallbackAsync(ILibraryRepository library, long account,
        LibrarySearchRequest request, CancellationToken ct)
    {
        request.Validate();
        var tracks = new List<Track>();
        for (var offset = 0; ; offset += 1000)
        {
            var batch = await library.SearchAsync(account, "", offset, 1000, ct);
            tracks.AddRange(batch);
            if (batch.Count < 1000) break;
        }
        return Filter(tracks, request, ct);
    }
}
