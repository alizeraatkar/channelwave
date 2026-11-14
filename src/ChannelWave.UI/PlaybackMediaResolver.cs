using ChannelWave.Core;
using ChannelWave.Telegram;

namespace ChannelWave.UI;

/// <summary>Retained by playback; contains services only, never a Razor component or its cancellation token.</summary>
public sealed class PlaybackMediaResolver(ILibraryRepository library, IRuntimeHost host, TelegramSession session, MediaStorageService storage)
{
    public async Task<Track?> ResolveAsync(long account, Guid id, CancellationToken ct)
    {
        if (session.AccountId is { } connected && connected != account) throw new OperationCanceledException("The Telegram account changed.", ct);
        var track = await library.TrackAsync(account, id, ct);
        if (track is not null) return track;
        return (await host.OfflineTracksAsync(account, ct)).FirstOrDefault(t => t.Id == id && t.Origin.AccountId == account);
    }

    public async Task<PreparedMedia> PrepareAsync(Track requested, bool offlineOnly, CancellationToken ct)
    {
        var track = await ResolveAsync(requested.Origin.AccountId, requested.Id, ct)
            ?? throw new UnplayableTrackException("This song is no longer in your library.");
        if (track.Origin.Protected) throw new UnplayableTrackException("This source restricts playback and saving in ChannelWave.");
        try
        {
            var saved = await host.OpenOfflineAsync(track, ct);
            if (saved is not null) return saved;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.JSInterop.JSException)
        {
            if (offlineOnly) throw new UnplayableTrackException("This offline copy is incomplete. Reconnect and save it again.");
        }
        if (offlineOnly) throw new UnplayableTrackException("This song is no longer saved on this device.");
        if (!track.Available) throw new UnplayableTrackException("This Telegram message is unavailable. An existing saved copy can still play.");
        if (session.AccountId != track.Origin.AccountId) throw new IOException("Reconnect Telegram, then retry playback.");
        var transfer = new TelegramDownloads(session);
        using var lease = await session.MediaWork.FileAsync(Metadata.FileUseKey(track), ct);
        try
        {
            if (host.CanStream)
            {
                var file = await transfer.ResolveAsync(track, ct);
                await storage.RegisterAsync(track, track.Media.Size, ct);
                return await host.StreamAsync(file, track.Media.MimeType, ct);
            }
            var completed = await transfer.CompleteAsync(track, ct);
            await storage.RegisterAsync(track, completed.Id("size"), ct);
            return await host.PrepareAsync(completed, ct);
        }
        catch (TelegramException e) when (e.Code is 403 or 404) { throw new UnplayableTrackException("This Telegram recording is unavailable."); }
    }
}
