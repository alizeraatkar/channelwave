using ChannelWave.Core;
using ChannelWave.Telegram;

namespace ChannelWave.UI;

public sealed class MetadataMediaSource(TelegramSession session, IRuntimeHost host, ArtworkCoordinator artwork,
    PlaybackCoordinator playback) : IMetadataSource
{
    public async Task<IReadOnlyMediaFile> OpenAsync(Track track, CancellationToken ct)
    {
        IReadOnlyMediaFile? offline = null;
        try { offline = await host.OpenOfflineMetadataAsync(track, ct); }
        catch (Exception e) when (track.Available && e is IOException or InvalidDataException)
        {
            // A stale or damaged saved copy need not block inspection of the current source.
        }
        return offline ?? await TelegramMetadataFile.OpenAsync(session, track,
            (file, offset, count, token) => host.ReadMetadataRangeAsync(track, file, offset, count, token), ct);
    }
    public async Task ObserveAsync(Track track, byte[]? image, CancellationToken ct)
    {
        if (image is not null) await artwork.ObserveEmbeddedBytesAsync(track, image, ct);
        else await playback.UpdateTrackMetadataAsync(track with { Artwork = artwork.Cached(track) }, ct);
    }
}
