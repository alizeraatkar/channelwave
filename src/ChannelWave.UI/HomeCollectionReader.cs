using ChannelWave.Core;

namespace ChannelWave.UI;

public sealed class HomeCollectionReader(ILibraryRepository library, IRuntimeHost host, PlaybackMediaResolver media)
{
    public async Task<HomeCollectionSnapshot> ReadAsync(long account, string tab, bool preview, bool online,
        LibrarySearchRequest? request, int initialLimit, bool append, bool checkAvailability, HomeCollectionSnapshot? previous, CancellationToken ct)
    {
        var saved = (await host.OfflineCopiesAsync(account, ct)).Where(c => c.Health == OfflineHealth.Saved)
            .Select(c => c.Track.Id).ToHashSet();
        ct.ThrowIfCancellationRequested();
        if (request is null) return new([], saved, Unavailable: true);
        var query = request with { OfflineIds = request.Criteria.SavedOnly || !online ? saved.ToArray() : null };
        var limit = preview ? 6 : append ? 100 : Math.Max(Math.Max(100, initialLimit), previous?.Items.Count ?? 0);
        IReadOnlyList<Track> found;
        bool more;
        var recentOffset = 0;
        if (tab == "recent" && library is IListeningStore listening)
        {
            var recent = new List<RecentTrack>();
            var start = append ? previous?.RecentOffset ?? 0 : 0;
            while (recent.Count < limit + 1)
            {
                var size = Math.Min(1000, limit + 1 - recent.Count);
                var batch = await listening.RecentTracksPageAsync(account, start + recent.Count, size, ct);
                recent.AddRange(batch);
                if (batch.Length < size) break;
            }
            var tracks = new List<Track>();
            foreach (var entry in recent.Take(limit))
            {
                if (!online && !saved.Contains(entry.TrackId)) continue;
                var track = await media.ResolveAsync(account, entry.TrackId, ct) ?? entry.Reference?.Placeholder();
                if (track is not null) tracks.Add(track);
            }
            found = tracks; more = recent.Count > limit; recentOffset = start + Math.Min(limit, recent.Count);
        }
        else
        {
            found = await library.SearchAsync(account, query with { Offset = append ? previous?.Items.Count ?? 0 : 0, Limit = limit + 1 }, ct);
            more = found.Count > limit;
        }
        ct.ThrowIfCancellationRequested();
        var items = (append ? (previous?.Items ?? []).Concat(found.Take(limit)) : found.Take(limit)).DistinctBy(t => t.Id).ToArray();
        var unavailable = false;
        if (items.Length == 0 && checkAvailability)
            unavailable = online || (await library.SearchAsync(account, query with { OfflineIds = null, Offset = 0, Limit = 1 }, ct)).Count == 0;
        ct.ThrowIfCancellationRequested();
        return new(items, saved, more, unavailable, recentOffset);
    }
}
