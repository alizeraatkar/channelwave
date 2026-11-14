using ChannelWave.Core;
using ChannelWave.Telegram;

namespace ChannelWave.UI;

public sealed class ListeningPlaybackActions(PlaybackCoordinator playback, PlaybackMediaResolver media, TelegramSession session)
{
    public async Task PlayAsync(ListeningTrack reference, CancellationToken ct = default)
    {
        var track = await ResolveAsync(reference, ct);
        var offline = session.AccountId != reference.Account;
        await playback.StartQueueAsync(reference.Account, [track.Id], track.Id,
            (id, token) => media.ResolveAsync(reference.Account, id, token),
            (item, token) => media.PrepareAsync(item, offline, token), offline, ct: ct);
        playback.SetContext(new("history", "Listening history"));
    }
    public async Task EnqueueAsync(ListeningTrack reference, bool next, CancellationToken ct = default)
    {
        var track = await ResolveAsync(reference, ct);
        var offline = playback.QueueAccount is null ? session.AccountId != reference.Account : playback.OfflineOnly;
        playback.Enqueue(track, (id, token) => media.ResolveAsync(reference.Account, id, token),
            (item, token) => media.PrepareAsync(item, offline, token), next, offline);
    }
    private async Task<Track> ResolveAsync(ListeningTrack reference, CancellationToken ct)
    {
        if (playback.ListeningAccount != reference.Account) throw new InvalidOperationException("This history belongs to another account.");
        return await media.ResolveAsync(reference.Account, reference.Id, ct)
            ?? throw new UnplayableTrackException("This recording is no longer available. An existing saved copy may need repair.");
    }
}
