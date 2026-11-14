using ChannelWave.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

namespace ChannelWave.Storage.Sqlite;

[DbContext(typeof(LibraryDb))]
[Migration("202611060003_MetadataMediaLookup")]
public sealed class MetadataMediaLookup : Migration
{
    internal const string MediaKeySql = """
        CASE WHEN json_valid(Json) THEN
            CASE WHEN json_extract(Json, '$.Media.Size') > 0 THEN
                CASE
                    WHEN json_extract(Json, '$.Media.UniqueId') <> '' THEN 'unique:' || json_extract(Json, '$.Media.UniqueId') || ':' || json_extract(Json, '$.Media.Size')
                    WHEN json_extract(Json, '$.Media.RemoteId') <> '' THEN 'remote:' || json_extract(Json, '$.Media.RemoteId') || ':' || json_extract(Json, '$.Media.Size')
                END
            END
        END
        """;

    protected override void Up(MigrationBuilder migration) =>
        migration.Sql($"CREATE INDEX IX_Tracks_MetadataMedia ON Tracks(Account, ({MediaKeySql}))");

    protected override void Down(MigrationBuilder migration) =>
        migration.Sql("DROP INDEX IX_Tracks_MetadataMedia");
}

public sealed partial class SqliteLibrary
{
    private static IQueryable<TrackRow> MatchingMedia(LibraryDb db, Track track)
    {
        var account = track.Origin.AccountId;
        var key = Metadata.SharedMediaKey(track);
        return key is null
            ? db.Tracks.Where(row => row.Account == account && row.Id == track.Id)
            : db.Tracks.FromSqlRaw("SELECT * FROM Tracks WHERE Account = {0} AND ("
                + MetadataMediaLookup.MediaKeySql + ") = {1}", account, key);
    }

    public async Task<CachedMetadata?> FindEmbeddedMetadataAsync(Track track, CancellationToken ct = default)
    {
        if (track.Origin.Protected) return null;
        await using var db = Open();
        var rows = await MatchingMedia(db, track).AsNoTracking().Select(row => row.Json).ToArrayAsync(ct);
        var matches = rows.Select(json => JsonSerializer.Deserialize<Track>(json)!).ToArray();
        if (!matches.Any(row => row.Id == track.Id && !row.Origin.Protected && Metadata.IsSameMedia(track, row))) return null;
        return matches.Select(CachedMetadata.FromTrack)
            .Where(result => result is not null)
            .OrderBy(result => result!.Scan.Status)
            .ThenByDescending(result => result!.Artwork is not null)
            .FirstOrDefault();
    }

    public async Task<Track[]> SaveEmbeddedMetadataAsync(Track track, CachedMetadata metadata, CancellationToken ct = default)
    {
        if (!metadata.Scan.IsReusable(track)) throw new ArgumentException("Invalid embedded metadata result.");
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var rows = await MatchingMedia(db, track).ToArrayAsync(ct);
            var matches = rows.Select(row => (Row: row, Track: JsonSerializer.Deserialize<Track>(row.Json)!))
                .Where(match => !match.Track.Origin.Protected && Metadata.IsSameMedia(track, match.Track)).ToArray();
            if (!matches.Any(match => match.Track.Id == track.Id)) return [];

            var updated = new List<Track>();
            foreach (var (row, current) in matches)
            {
                if (current.EmbeddedMetadata == metadata.Fields && current.MetadataScan == metadata.Scan) continue;
                var next = Metadata.ApplyOverrides(current with
                {
                    EmbeddedMetadata = metadata.Fields,
                    MetadataScan = metadata.Scan
                }, current.TitleOverride, current.PerformerOverride);
                row.Json = JsonSerializer.Serialize(next);
                row.SearchText = next.SearchText;
                updated.Add(next);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            foreach (var row in updated) CacheTrack(row);
            return [.. updated];
        }
        finally { writes.Release(); }
    }
}
