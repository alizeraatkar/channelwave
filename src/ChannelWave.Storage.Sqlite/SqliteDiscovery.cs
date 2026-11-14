using ChannelWave.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChannelWave.Storage.Sqlite;

public sealed partial class SqliteLibrary
{
    public Task<LibraryOverview> OverviewAsync(long account, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var activity = searchActivity.StartActivity("Read library overview");
            var rows = await ReadSearchRowsAsync(account, ct);
            ct.ThrowIfCancellationRequested();
            return new LibraryOverview(rows.Length, rows.Select(r => r.ArtistKey).OfType<string>().Distinct().Count(),
                rows.Select(r => r.AlbumKey).OfType<string>().Distinct().Count());
        }, ct);
    public async Task<RecentTrack[]> RecentTracksPageAsync(long account, int offset, int limit, CancellationToken ct = default)
        => await ReadRecentAsync(account, offset, limit, ct);
    public async Task<RecentTrack[]> RecentTracksAsync(long account, int limit = 6, CancellationToken ct = default)
        => await ReadRecentAsync(account, 0, Math.Clamp(limit, 1, 100), ct);

    private async Task<RecentTrack[]> ReadRecentAsync(long account, int offset, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await using var db = Open();
        // Group compact indexed columns; no listening-event JSON or media hydration.
        var rows = await db.Set<ListenRow>().FromSqlInterpolated($"SELECT * FROM Listens WHERE Account = {account} AND json_extract(Json, '$.activeMilliseconds') > 0").AsNoTracking()
            .GroupBy(x => x.Track).Select(g => new { Track = g.Key, LastActive = g.Max(x => x.LastActive) })
            .OrderByDescending(x => x.LastActive).ThenBy(x => x.Track).Skip(offset).Take(Math.Clamp(limit, 1, 1000)).ToArrayAsync(ct);
        var recent = new List<RecentTrack>();
        foreach (var row in rows)
        {
            var json = await db.Set<ListenRow>().AsNoTracking().Where(x => x.Account == account && x.Track == row.Track && x.LastActive == row.LastActive)
                .OrderByDescending(x => x.Id).Select(x => x.Json).FirstAsync(ct);
            recent.Add(new(Guid.Parse(row.Track), row.LastActive, JsonSerializer.Deserialize<ListeningEvent>(json, ListeningJson)?.Track));
        }
        return recent.ToArray();
    }
}
