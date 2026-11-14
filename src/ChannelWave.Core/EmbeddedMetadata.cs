namespace ChannelWave.Core;

public interface IReadOnlyMediaFile : IAsyncDisposable
{
    long AccountId { get; }
    long Length { get; }
    Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct);
}

public sealed record EmbeddedMetadata(string Title = "", string Artist = "", string Album = "", string Year = "",
    string Genre = "", string Track = "", string Disc = "");
public enum MetadataScanStatus { Completed, NoTags, Unsupported, Failed }
public sealed record MetadataScan(string MediaKey, int ReaderVersion, MetadataScanStatus Status,
    int Attempts = 1, DateTimeOffset? RetryAt = null, string? Reason = null)
{
    public bool Matches(Track track) => MediaKey == Metadata.MediaKey(track) && ReaderVersion == EmbeddedMetadataReader.Version;
    public bool IsReusable(Track track) => !track.Origin.Protected && Matches(track) && !WasRestricted
        && Status is MetadataScanStatus.Completed or MetadataScanStatus.NoTags or MetadataScanStatus.Unsupported;
    private bool WasRestricted => Status == MetadataScanStatus.Unsupported && Reason == "This source restricts saving.";
    public static bool IsPending(Track track, DateTimeOffset now) => !track.Origin.Protected &&
        (track.MetadataScan is not { } scan || !scan.Matches(track) || scan.WasRestricted
        || scan.Status == MetadataScanStatus.Failed && scan.Attempts < 3 && scan.RetryAt is { } retry && retry <= now);
}

public sealed record CachedMetadata(EmbeddedMetadata? Fields, MetadataScan Scan, ArtworkData? Artwork = null)
{
    public static CachedMetadata? FromTrack(Track track) => track.MetadataScan is { } scan && scan.IsReusable(track)
        ? new(track.EmbeddedMetadata, scan, track.Artwork is { Kind: ArtworkKind.Embedded } art && art.Matches(track) ? art : null)
        : null;
}

public sealed record MetadataReadResult(EmbeddedMetadata Metadata, byte[]? Artwork, MetadataScanStatus Status, string? Reason = null);

public interface IMetadataSource
{
    Task<IReadOnlyMediaFile> OpenAsync(Track track, CancellationToken ct);
    Task ObserveAsync(Track track, byte[]? artwork, CancellationToken ct);
}
