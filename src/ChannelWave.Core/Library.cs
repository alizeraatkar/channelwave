using System.Globalization;
using System.Text;

namespace ChannelWave.Core;

public sealed record TrackOrigin(long AccountId, long ChatId, long MessageId, string SourceName, bool Protected,
    TelegramMessageInfo? Message = null);
public sealed record MediaFile(string RemoteId, string UniqueId, int RuntimeId, string FileName, string MimeType, long Size);
public sealed record Track(Guid Id, TrackOrigin Origin, MediaFile Media, string OriginalTitle, string OriginalPerformer,
    string Title, string Performer, int Duration, DateTimeOffset PublishedAt, bool Available = true, bool Favorite = false,
    string? TitleOverride = null, string? PerformerOverride = null, ArtworkData? Artwork = null,
    EmbeddedMetadata? EmbeddedMetadata = null, MetadataScan? MetadataScan = null, DateTimeOffset? AddedAt = null)
{
    public string SearchText => Metadata.Normalize($"{Title} {Performer} {Origin.SourceName}");
    public string Identity => $"{Origin.AccountId}:{Origin.ChatId}:{Origin.MessageId}";
}
public sealed record Source(long AccountId, long ChatId, string Name, long HistoryCursor = 0,
    long LatestMessageId = 0, bool HistoryComplete = false, DateTimeOffset? RefreshedAt = null, string? Error = null,
    SourceSyncState? Sync = null);
public sealed record SourceSyncState(long CatchUpCursor = 0, long CatchUpBoundary = 0, long CatchUpHead = 0,
    long AuditCursor = 0, DateTimeOffset? AuditedAt = null, long[]? DeletedMessages = null,
    string Status = "pending", bool Inaccessible = false, bool AuditVerifying = false, long VerificationCursor = 0,
    DateTimeOffset? RetryAt = null);
public sealed record Playlist(Guid Id, string Name, Guid[] Tracks);
public enum PlaylistSort { Custom, Title, Artist, Newest, AddedNewest, AddedOldest, TitleDescending, ArtistDescending, Oldest, Shortest, Longest }
public sealed record QueueSnapshot(Guid[] Tracks, int Index = 0, double Position = 0, RepeatMode Repeat = RepeatMode.Off, bool Shuffle = false);
public enum RepeatMode { Off, One, All }
public sealed record PlatformCapabilities(string Host, bool BackgroundAudio, bool SystemMediaControls, bool OfflineFiles, bool Pwa);

public interface ILibraryRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    async Task<LibraryOverview> OverviewAsync(long account, CancellationToken ct = default)
    {
        var tracks = await SearchAsync(account, new LibrarySearchRequest(new(), Limit: int.MaxValue), ct);
        return new(tracks.Count, tracks.Select(LibraryDiscovery.ArtistKey).OfType<string>().Distinct().Count(),
            tracks.Select(LibraryDiscovery.AlbumKey).OfType<string>().Distinct().Count());
    }
    Task SavePageAsync(Source source, IReadOnlyList<Track> tracks, CancellationToken ct = default);
    Task ApplySourceChangesAsync(Source source, IReadOnlyList<Track> tracks, IReadOnlyCollection<long> unavailable,
        bool allUnavailable = false, CancellationToken ct = default);
    Task<Track?> TrackAsync(long account, Guid id, CancellationToken ct = default);
    Task<Guid[]> QueueTrackIdsAsync(long account, LibrarySearchRequest request, CancellationToken ct = default);
    Task<LibraryGroup[]> GroupsAsync(long account, LibrarySearchRequest request, string kind, CancellationToken ct = default)
        => LibraryDiscovery.GroupsFallbackAsync(this, account, request, kind, ct);
    Task<long[]> SourceMessageIdsAsync(long account, long chat, long before = 0, CancellationToken ct = default);
    Task<Track?> SetMessageInfoAsync(long account, Guid id, long chat, long message, TelegramMessageInfo info,
        DateTimeOffset publishedAt, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> SearchAsync(long account, string query, int offset = 0, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> SearchAsync(long account, LibrarySearchRequest request, CancellationToken ct = default)
        => LibrarySearch.SearchFallbackAsync(this, account, request, ct);
    Task<IReadOnlyList<Source>> SourcesAsync(long account, CancellationToken ct = default);
    Task<Source?> SourceAsync(long account, long chat, CancellationToken ct = default);
    Task<Guid[]> RemoveSourceAsync(long account, long chat, CancellationToken ct = default);
    Task SetFavoriteAsync(long account, Guid id, bool favorite, CancellationToken ct = default);
    Task<Track> SetLocalMetadataAsync(long account, Guid id, string? title, string? performer, CancellationToken ct = default);
    Task<Track> SetArtworkAsync(long account, Guid id, ArtworkData artwork, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> PendingMetadataAsync(long account, DateTimeOffset now, int limit = 50, CancellationToken ct = default);
    Task<Track?> SetMetadataScanAsync(long account, Guid id, string mediaKey, EmbeddedMetadata? metadata, MetadataScan scan, CancellationToken ct = default);
    Task<CachedMetadata?> FindEmbeddedMetadataAsync(Track track, CancellationToken ct = default);
    Task<Track[]> SaveEmbeddedMetadataAsync(Track track, CachedMetadata metadata, CancellationToken ct = default);
    Task RetryMetadataAsync(long account, CancellationToken ct = default);
    Task SaveStateAsync(long account, string key, string value, CancellationToken ct = default);
    Task<string?> ReadStateAsync(long account, string key, CancellationToken ct = default);
    Task ClearAccountAsync(long account, CancellationToken ct = default);
}

public static class LibraryBrowsing
{
    // Apply collection filters before pagination, including on stores whose search API
    // only supports text. Fetch bounded batches instead of rendering the whole library.
    public static async Task<IReadOnlyList<Track>> ReadViewAsync(this ILibraryRepository library,
        long account, string query, bool favorites, long? chat, Guid[]? playlist, int limit, CancellationToken ct = default,
        PlaylistSort sort = PlaylistSort.Custom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var order = playlist?.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        if (order?.Count == 0) return [];
        var found = new List<Track>();
        for (var offset = 0; ;)
        {
            ct.ThrowIfCancellationRequested();
            var batchSize = !favorites && chat is null && order is null ? Math.Min(1000, limit - found.Count) : 1000;
            var batch = await library.SearchAsync(account, query, offset, batchSize, ct);
            found.AddRange(batch.Where(t => (!favorites || t.Favorite) && (chat is null || t.Origin.ChatId == chat)
                && (order is null || order.ContainsKey(t.Id))));
            if (batch.Count < batchSize || order is null && found.Count >= limit || order is not null && found.Count == order.Count) break;
            offset += batchSize;
        }
        IEnumerable<Track> sorted = order is null ? found : found.OrderBy(t => order[t.Id]);
        if (order is not null) sorted = sort switch
        {
            PlaylistSort.Title => sorted.OrderBy(t => Metadata.Normalize(t.Title), StringComparer.Ordinal),
            PlaylistSort.Artist => sorted.OrderBy(t => Metadata.Normalize(t.Performer), StringComparer.Ordinal).ThenBy(t => Metadata.Normalize(t.Title), StringComparer.Ordinal),
            PlaylistSort.Newest => sorted.OrderByDescending(t => t.PublishedAt),
            _ => sorted
        };
        if (order is not null && LibraryDiscovery.PlaylistOrder(sort) is { } general)
            return LibrarySearch.Filter(found, new(new(), Playlist: playlist, Order: general, Limit: limit), ct);
        return sorted.Take(limit).ToArray();
    }
}

public static class Metadata
{
    public static string FileUseKey(Track track) => $"{track.Origin.AccountId}:" +
        (string.IsNullOrEmpty(track.Media.UniqueId) && string.IsNullOrEmpty(track.Media.RemoteId) ? track.Identity : MediaKey(track));
    public static string MediaKey(Track track) => (string.IsNullOrEmpty(track.Media.UniqueId) ? track.Media.RemoteId : track.Media.UniqueId) + ":" + track.Media.Size.ToString(CultureInfo.InvariantCulture);
    public static string? SharedMediaKey(Track track) => track.Media.Size <= 0 ? null
        : !string.IsNullOrEmpty(track.Media.UniqueId) ? "unique:" + MediaKey(track)
        : !string.IsNullOrEmpty(track.Media.RemoteId) ? "remote:" + MediaKey(track)
        : null;
    public static bool IsSameMedia(Track expected, Track current) =>
        MediaKey(expected) == MediaKey(current) && SharedMediaKey(expected) == SharedMediaKey(current);
    public static Track ApplyOverrides(Track track, string? title, string? performer) => track with
    {
        TitleOverride = title,
        PerformerOverride = performer,
        Title = title ?? NonEmpty(track.EmbeddedMetadata?.Title) ?? Title(track.OriginalTitle, track.Media.FileName),
        Performer = performer ?? NonEmpty(track.EmbeddedMetadata?.Artist) ?? (string.IsNullOrWhiteSpace(track.OriginalPerformer) ? "Unknown artist" : track.OriginalPerformer)
    };
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? ValidateOverride(string? value)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.Length is < 1 or > 1000 || value.Any(char.IsControl))
            throw new ArgumentException("Enter a label between 1 and 1000 characters without control characters.");
        return value;
    }

    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        // Browser ICU supports canonical decomposition, but not compatibility forms (KC/KD).
        // Use the same form on every host so stored search keys match query normalization.
        foreach (var c in value.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark) continue;
            result.Append(c switch { 'ي' or 'ى' => 'ی', 'ك' => 'ک', '\u200c' or '\u200d' => ' ', '\u0640' => ' ', _ => c });
        }
        return string.Join(' ', result.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Title(string supplied, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied.Trim();
        var name = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? "Unknown title" : name;
    }
}

public static class Playlists
{
    public static Playlist Create(string name) => new(Guid.NewGuid(), ValidateName(name), []);
    public static Playlist Rename(Playlist playlist, string name) => playlist with { Name = ValidateName(name) };
    public static Playlist Add(Playlist playlist, Guid track) => playlist with { Tracks = playlist.Tracks.Append(track).Distinct().ToArray() };
    public static Playlist AddRange(Playlist playlist, IEnumerable<Guid> tracks) => playlist with { Tracks = playlist.Tracks.Concat(tracks).Distinct().ToArray() };
    public static Playlist Remove(Playlist playlist, Guid track) => playlist with { Tracks = playlist.Tracks.Where(id => id != track).Distinct().ToArray() };
    public static Playlist RemoveRange(Playlist playlist, IEnumerable<Guid> tracks)
    {
        var removed = tracks.ToHashSet();
        return playlist with { Tracks = playlist.Tracks.Where(id => !removed.Contains(id)).Distinct().ToArray() };
    }
    public static Playlist Duplicate(Playlist playlist, string name) => new(Guid.NewGuid(), ValidateName(name), playlist.Tracks.Distinct().ToArray());
    public static Playlist Move(Playlist playlist, int from, int to)
    {
        if (from < 0 || to < 0 || from >= playlist.Tracks.Length || to >= playlist.Tracks.Length) throw new ArgumentOutOfRangeException(nameof(from));
        var items = playlist.Tracks.ToList(); var item = items[from]; items.RemoveAt(from); items.Insert(to, item);
        return playlist with { Tracks = items.ToArray() };
    }
    private static string ValidateName(string name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl)
        ? throw new ArgumentException("Enter a playlist name between 1 and 100 characters.") : name.Trim();
}
