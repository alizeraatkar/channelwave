using ChannelWave.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;
using System.Globalization;

namespace ChannelWave.Storage.Sqlite;

public sealed class TrackRow
{
    public long Account { get; set; }
    public string Identity { get; set; } = "";
    public Guid Id { get; set; }
    public string SearchText { get; set; } = "";
    public string Json { get; set; } = "";
}
public sealed class DocumentRow
{
    public long Account { get; set; }
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
}
public sealed class LibraryDb(DbContextOptions<LibraryDb> options) : DbContext(options)
{
    public DbSet<TrackRow> Tracks => Set<TrackRow>();
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TrackRow>().HasKey(x => new { x.Account, x.Identity });
        model.Entity<TrackRow>().HasIndex(x => new { x.Account, x.Id }).IsUnique();
        model.Entity<DocumentRow>().HasKey(x => new { x.Account, x.Key });
        ListeningSchema.Configure(model);
    }
}

[DbContext(typeof(LibraryDb))]
[Migration("202610090001_InitialLibrary")]
public sealed class InitialLibrary : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        CREATE TABLE Tracks (Account INTEGER NOT NULL, Identity TEXT NOT NULL, Id TEXT NOT NULL, SearchText TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY (Account, Identity));
        CREATE UNIQUE INDEX IX_Tracks_Account_Id ON Tracks(Account, Id);
        CREATE TABLE Documents (Account INTEGER NOT NULL, Key TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Account, Key));
        """);
    protected override void Down(MigrationBuilder migration) { migration.DropTable("Tracks"); migration.DropTable("Documents"); }
    protected override void BuildTargetModel(ModelBuilder model)
    {
        model.Entity<TrackRow>().HasKey(x => new { x.Account, x.Identity });
        model.Entity<TrackRow>().HasIndex(x => new { x.Account, x.Id }).IsUnique();
        model.Entity<DocumentRow>().HasKey(x => new { x.Account, x.Key });
    }
}

public sealed partial class SqliteLibrary(string path) : ILibraryRepository, IListeningStore
{
    private static readonly System.Diagnostics.ActivitySource searchActivity = new("ChannelWave.Sqlite.Search");
    private sealed record SearchCache(DateTimeOffset AddedBaseline, Dictionary<Guid, LibrarySearchRow> Rows);
    private readonly Dictionary<long, SearchCache> searchRows = [];

    private void CacheTrack(Track track)
    {
        if (!searchRows.TryGetValue(track.Origin.AccountId, out var cache)) return;
        var projection = LibrarySearchRow.FromTrack(track);
        cache.Rows[track.Id] = projection.AddedAt is null ? projection with { AddedAt = cache.AddedBaseline } : projection;
    }

    private readonly SemaphoreSlim writes = new(1, 1);
    private LibraryDb Open() => new(new DbContextOptionsBuilder<LibraryDb>().UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options);
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var db = Open();
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", ct);
        await db.Database.MigrateAsync(ct);
    }
    public Task SavePageAsync(Source source, IReadOnlyList<Track> tracks, CancellationToken ct = default)
        => SaveSourceChangesAsync(source, tracks, [], false, false, ct);

    public Task ApplySourceChangesAsync(Source source, IReadOnlyList<Track> tracks, IReadOnlyCollection<long> unavailable,
        bool allUnavailable = false, CancellationToken ct = default)
        => SaveSourceChangesAsync(source, tracks, unavailable, allUnavailable, true, ct);

    private async Task SaveSourceChangesAsync(Source source, IReadOnlyList<Track> tracks, IReadOnlyCollection<long> unavailable,
        bool allUnavailable, bool reconcile, CancellationToken ct)
    {
        if (tracks.Any(x => x.Origin.AccountId != source.AccountId || x.Origin.ChatId != source.ChatId)) throw new InvalidOperationException("Cross-account/source write rejected.");
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var sourceRow = reconcile ? await db.Documents.FindAsync([source.AccountId, "source:" + source.ChatId], ct) : null;
            if (reconcile && sourceRow is null) return;
            var renamed = sourceRow is not null && JsonSerializer.Deserialize<Source>(sourceRow.Json)?.Name != source.Name;
            if (allUnavailable || unavailable.Count > 0 || renamed)
            {
                var candidates = await db.Tracks.Where(x => x.Account == source.AccountId).ToArrayAsync(ct);
                foreach (var candidate in candidates)
                {
                    var saved = JsonSerializer.Deserialize<Track>(candidate.Json)!;
                    if (saved.Origin.ChatId != source.ChatId) continue;
                    var updated = saved with { Available = saved.Available && !allUnavailable && !unavailable.Contains(saved.Origin.MessageId),
                        Origin = saved.Origin with { SourceName = source.Name } };
                    candidate.Json = JsonSerializer.Serialize(updated); candidate.SearchText = updated.SearchText;
                }
            }
            await EnsureAddedBaselineAsync(db, source.AccountId, ct);
            var indexedAt = DateTimeOffset.UtcNow;
            var keys = tracks.Select(x => x.Identity).ToArray();
            var existing = await db.Tracks.Where(x => x.Account == source.AccountId && keys.Contains(x.Identity)).ToDictionaryAsync(x => x.Identity, ct);
            foreach (var track in tracks.DistinctBy(x => x.Identity))
            {
                if (existing.TryGetValue(track.Identity, out var row))
                {
                    var previous = JsonSerializer.Deserialize<Track>(row.Json)!;
                    var sameMedia = Metadata.IsSameMedia(previous, track) && !track.Origin.Protected;
                    var merged = track with { Id = previous.Id, Favorite = previous.Favorite, AddedAt = previous.AddedAt,
                        Available = reconcile ? track.Available : previous.Available && track.Available,
                        Origin = track.Origin with { Message = TelegramMessageInfo.Merge(previous.Origin.Message, track.Origin.Message) },
                        PublishedAt = track.PublishedAt > DateTimeOffset.UnixEpoch ? track.PublishedAt : previous.PublishedAt,
                        TitleOverride = previous.TitleOverride, PerformerOverride = previous.PerformerOverride,
                        EmbeddedMetadata = sameMedia ? previous.EmbeddedMetadata : null,
                        MetadataScan = Metadata.IsSameMedia(previous, track) && previous.Origin.Protected == track.Origin.Protected ? previous.MetadataScan : null,
                        Title = previous.TitleOverride ?? (sameMedia && !string.IsNullOrWhiteSpace(previous.EmbeddedMetadata?.Title) ? previous.EmbeddedMetadata.Title : track.Title),
                        Performer = previous.PerformerOverride ?? (sameMedia && !string.IsNullOrWhiteSpace(previous.EmbeddedMetadata?.Artist) ? previous.EmbeddedMetadata.Artist : track.Performer),
                        Artwork = previous.Artwork?.Matches(track) == true ? previous.Artwork : null };
                    row.Json = JsonSerializer.Serialize(merged); row.SearchText = merged.SearchText;
                }
                else db.Tracks.Add(new() { Account = source.AccountId, Identity = track.Identity, Id = track.Id, SearchText = track.SearchText, Json = JsonSerializer.Serialize(track with { AddedAt = indexedAt }) });
            }
            await PutDocument(db, source.AccountId, "source:" + source.ChatId, JsonSerializer.Serialize(source), ct);
            var changed = searchRows.ContainsKey(source.AccountId)
                ? db.ChangeTracker.Entries<TrackRow>().Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                    .Select(entry => entry.Entity).ToArray()
                : [];
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            foreach (var row in changed) CacheTrack(JsonSerializer.Deserialize<Track>(row.Json)!);
        }
        finally { writes.Release(); }
    }

    public Task<Track?> TrackAsync(long account, Guid id, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            await using var db = Open();
            var json = await db.Tracks.AsNoTracking().Where(x => x.Account == account && x.Id == id).Select(x => x.Json).SingleOrDefaultAsync(ct);
            return json is null ? null : JsonSerializer.Deserialize<Track>(json);
        }, ct);

    public async Task<long[]> SourceMessageIdsAsync(long account, long chat, long before = 0, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT json_extract(Json, '$.Origin.MessageId') FROM Tracks WHERE Account = @account
            AND json_extract(Json, '$.Origin.ChatId') = @chat
            AND (@before = 0 OR json_extract(Json, '$.Origin.MessageId') < @before)
            ORDER BY json_extract(Json, '$.Origin.MessageId') DESC LIMIT 100
            """;
        foreach (var (name, value) in new[] { ("@account", account), ("@chat", chat), ("@before", before) })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
        }
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    public async Task<Guid[]> QueueTrackIdsAsync(long account, LibrarySearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        return LibrarySearch.Select(await ReadSearchRowsAsync(account, ct), request with { Offset = 0, Limit = int.MaxValue }, ct);
    }
    public async Task<IReadOnlyList<Track>> SearchAsync(long account, string query, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await using var db = Open(); var normalized = Metadata.Normalize(query);
        var rows = await db.Tracks.AsNoTracking().Where(x => x.Account == account && x.SearchText.Contains(normalized))
            .OrderBy(x => x.Identity).Skip(offset).Take(Math.Clamp(limit, 1, 1000)).Select(x => x.Json).ToListAsync(ct);
        return rows.Select(x => JsonSerializer.Deserialize<Track>(x)!).ToArray();
    }
    public async Task<Track?> SetMessageInfoAsync(long account, Guid id, long chat, long message, TelegramMessageInfo info,
        DateTimeOffset publishedAt, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open();
            var row = await db.Tracks.SingleOrDefaultAsync(x => x.Account == account && x.Id == id, ct);
            if (row is null) return null;
            var current = JsonSerializer.Deserialize<Track>(row.Json)!;
            if (current.Origin.ChatId != chat || current.Origin.MessageId != message) return null;
            var updated = current with
            {
                Origin = current.Origin with { Message = TelegramMessageInfo.Merge(current.Origin.Message, info) },
                PublishedAt = publishedAt > DateTimeOffset.UnixEpoch ? publishedAt : current.PublishedAt
            };
            row.Json = JsonSerializer.Serialize(updated);
            await db.SaveChangesAsync(ct); CacheTrack(updated); return updated;
        }
        finally { writes.Release(); }
    }
    // Microsoft.Data.Sqlite executes its async I/O synchronously. Keep the entire
    // query, including projection and sorting, off the native UI dispatcher.
    public Task<IReadOnlyList<Track>> SearchAsync(long account, LibrarySearchRequest request, CancellationToken ct = default)
        => Task.Run(() => SearchCoreAsync(account, request, ct), ct);

    private async Task<IReadOnlyList<Track>> SearchCoreAsync(long account, LibrarySearchRequest request, CancellationToken ct)
    {
        using var activity = searchActivity.StartActivity("Search tracks");
        request.Validate();
        if (!request.Criteria.IsActive && !request.Favorites && request.Chat is null && request.Playlist is null
            && request.Order is null && request.ArtistKey is null && request.AlbumKey is null && request.OfflineIds is null)
        {
            // The primary key already orders an account's library. Opening its
            // first page must not normalize or deserialize the whole collection.
            await using var db = Open();
            var page = await db.Tracks.AsNoTracking().Where(x => x.Account == account)
                .OrderBy(x => x.Identity).Skip(request.Offset).Take(request.Limit).Select(x => x.Json).ToArrayAsync(ct);
            var tracks = page.Select(json => JsonSerializer.Deserialize<Track>(json)!).ToArray();
            ct.ThrowIfCancellationRequested();
            return tracks;
        }
        var ids = LibrarySearch.Select(await ReadSearchRowsAsync(account, ct), request, ct);
        ct.ThrowIfCancellationRequested();
        var found = new Dictionary<Guid, Track>();
        await using var hydration = Open();
        foreach (var batch in ids.Chunk(500))
        {
            var json = await hydration.Tracks.AsNoTracking().Where(x => x.Account == account && batch.Contains(x.Id)).Select(x => x.Json).ToArrayAsync(ct);
            foreach (var value in json) { var track = JsonSerializer.Deserialize<Track>(value)!; found[track.Id] = track; }
        }
        ct.ThrowIfCancellationRequested();
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToArray();
    }

    private async Task<LibrarySearchRow[]> ReadSearchRowsAsync(long account, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            if (!searchRows.TryGetValue(account, out var cache))
            {
                using var activity = searchActivity.StartActivity("Build search projections");
                var rows = new Dictionary<Guid, LibrarySearchRow>();
                await using var db = Open();
                var baseline = await EnsureAddedBaselineAsync(db, account, ct);
                await db.SaveChangesAsync(ct);
                await db.Database.OpenConnectionAsync(ct);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                // Read only compact labels and filter values, never thumbnails/media.
                command.CommandText = """
                    SELECT Id, Identity, json_extract(Json, '$.Origin.ChatId'),
                        json_extract(Json, '$.Title'), json_extract(Json, '$.Performer'),
                        json_extract(Json, '$.Origin.SourceName'), json_extract(Json, '$.Duration'),
                        json_extract(Json, '$.PublishedAt'), json_extract(Json, '$.Favorite'),
                        json_extract(Json, '$.AddedAt'), json_extract(Json, '$.OriginalPerformer'),
                        json_extract(Json, '$.PerformerOverride'), json_extract(Json, '$.EmbeddedMetadata')
                    FROM Tracks WHERE Account = @account
                    """;
                var parameter = command.CreateParameter(); parameter.ParameterName = "@account"; parameter.Value = account;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var projected = new Track(Guid.Parse(reader.GetString(0)), new(account, reader.GetInt64(2), 0, reader.GetString(5), false),
                        new("", "", 0, "", "", 0), reader.GetString(3), reader.IsDBNull(10) ? "" : reader.GetString(10),
                        reader.GetString(3), reader.GetString(4), reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                        Favorite: reader.GetBoolean(8), PerformerOverride: reader.IsDBNull(11) ? null : reader.GetString(11),
                        EmbeddedMetadata: reader.IsDBNull(12) ? null : JsonSerializer.Deserialize<EmbeddedMetadata>(reader.GetString(12)),
                        AddedAt: reader.IsDBNull(9) ? baseline : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));
                    var row = LibrarySearchRow.FromTrack(projected) with { Identity = reader.GetString(1) };

                    rows[row.Id] = row;
                    if (rows.Count % 1024 == 0) await Task.Yield();
                }
                searchRows[account] = cache = new(baseline, rows);
            }
            return cache.Rows.Values.ToArray();
        }
        finally { writes.Release(); }
    }
    public async Task<IReadOnlyList<Source>> SourcesAsync(long account, CancellationToken ct = default)
    {
        await using var db = Open(); var rows = await db.Documents.AsNoTracking().Where(x => x.Account == account && x.Key.StartsWith("source:")).Select(x => x.Json).ToListAsync(ct);
        return rows.Select(x => JsonSerializer.Deserialize<Source>(x)!).ToArray();
    }
    public async Task<Source?> SourceAsync(long account, long chat, CancellationToken ct = default)
    {
        await using var db = Open(); var row = await db.Documents.FindAsync([account, "source:" + chat], ct);
        return row is null ? null : JsonSerializer.Deserialize<Source>(row.Json);
    }
    public async Task<Guid[]> RemoveSourceAsync(long account, long chat, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var prefix = $"{account}:{chat}:";
            var rows = db.Tracks.Where(x => x.Account == account && x.Identity.StartsWith(prefix));
            var removed = await rows.Select(x => x.Id).ToArrayAsync(ct);
            await rows.ExecuteDeleteAsync(ct);
            await db.Documents.Where(x => x.Account == account && x.Key == "source:" + chat).ExecuteDeleteAsync(ct);
            var document = await db.Documents.FindAsync([account, "state:playlist"], ct);
            if (document is not null)
            {
                var playlist = JsonSerializer.Deserialize<Playlist>(document.Json)!;
                var ids = removed.ToHashSet();
                document.Json = JsonSerializer.Serialize(playlist with { Tracks = playlist.Tracks.Where(x => !ids.Contains(x)).ToArray() });
            }
            var collections = await db.Documents.FindAsync([account, "state:playlists"], ct);
            if (collections is not null)
            {
                var ids = removed.ToHashSet();
                var playlists = JsonSerializer.Deserialize<Playlist[]>(collections.Json)!;
                collections.Json = JsonSerializer.Serialize(playlists.Select(p => Playlists.RemoveRange(p, ids)));
            }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            if (searchRows.TryGetValue(account, out var cached)) foreach (var id in removed) cached.Rows.Remove(id);
            return removed;
        }
        finally { writes.Release(); }
    }
    public async Task SetFavoriteAsync(long account, Guid id, bool favorite, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); var row = await db.Tracks.SingleAsync(x => x.Account == account && x.Id == id, ct);
            var updated = JsonSerializer.Deserialize<Track>(row.Json)! with { Favorite = favorite };
            row.Json = JsonSerializer.Serialize(updated); await db.SaveChangesAsync(ct); CacheTrack(updated);
        }
        finally { writes.Release(); }
    }
    public async Task<Track> SetLocalMetadataAsync(long account, Guid id, string? title, string? performer, CancellationToken ct = default)
    {
        title = Metadata.ValidateOverride(title); performer = Metadata.ValidateOverride(performer);
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open();
            var row = await db.Tracks.SingleAsync(x => x.Account == account && x.Id == id, ct);
            var updated = Metadata.ApplyOverrides(JsonSerializer.Deserialize<Track>(row.Json)!, title, performer);
            row.Json = JsonSerializer.Serialize(updated); row.SearchText = updated.SearchText;
            await db.SaveChangesAsync(ct); CacheTrack(updated); return updated;
        }
        finally { writes.Release(); }
    }
    public async Task<Track> SetArtworkAsync(long account, Guid id, ArtworkData artwork, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Tracks.SingleAsync(x => x.Account == account && x.Id == id, ct);
            var track = JsonSerializer.Deserialize<Track>(row.Json)!; artwork.Validate(track);
            if (track.Artwork?.Matches(track) == true && track.Artwork.Kind < artwork.Kind) return track;
            var updated = track with { Artwork = artwork with { CachedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } };
            row.Json = JsonSerializer.Serialize(updated); await db.SaveChangesAsync(ct);
            var rows = await db.Tracks.Where(x => x.Account == account && x.Json.Contains("\"Artwork\":{")).ToListAsync(ct);
            var images = rows.Select(x => (Row: x, Track: JsonSerializer.Deserialize<Track>(x.Json)!)).ToArray();
            long bytes = images.Sum(x => (long)(x.Track.Artwork?.Data.Length ?? 0)); var count = images.Length;
            foreach (var old in images.Where(x => x.Row.Id != id).OrderBy(x => x.Track.Artwork!.CachedAt))
            {
                if (count <= ArtworkData.MaxCachedImages && bytes <= ArtworkData.MaxCacheBytes) break;
                bytes -= old.Track.Artwork!.Data.Length; count--; old.Row.Json = JsonSerializer.Serialize(old.Track with { Artwork = null });
            }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return updated;
        }
        finally { writes.Release(); }
    }
    public async Task<IReadOnlyList<Track>> PendingMetadataAsync(long account, DateTimeOffset now, int limit = 50, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var pending = new List<Track>();
        for (var offset = 0; pending.Count < Math.Min(limit, 1000); offset += 1000)
        {
            var batch = await SearchAsync(account, "", offset, 1000, ct);
            pending.AddRange(batch.Where(t => MetadataScan.IsPending(t, now)).Take(Math.Min(limit, 1000) - pending.Count));
            if (batch.Count < 1000) break;
        }
        return pending;
    }
    public async Task<Track?> SetMetadataScanAsync(long account, Guid id, string mediaKey, EmbeddedMetadata? metadata, MetadataScan scan, CancellationToken ct = default)
    {
        if (scan.MediaKey != mediaKey || scan.ReaderVersion != EmbeddedMetadataReader.Version || !Enum.IsDefined(scan.Status)) throw new ArgumentException("Invalid metadata scan.");
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open();
            var row = await db.Tracks.SingleOrDefaultAsync(x => x.Account == account && x.Id == id, ct);
            if (row is null) return null;
            var current = JsonSerializer.Deserialize<Track>(row.Json)!;
            if (Metadata.MediaKey(current) != mediaKey || current.Origin.Protected && metadata is not null
                || scan.Status == MetadataScanStatus.Failed && CachedMetadata.FromTrack(current) is not null) return null;
            var updated = Metadata.ApplyOverrides(current with { EmbeddedMetadata = metadata, MetadataScan = scan }, current.TitleOverride, current.PerformerOverride);
            row.Json = JsonSerializer.Serialize(updated); row.SearchText = updated.SearchText;
            await db.SaveChangesAsync(ct); CacheTrack(updated); return updated;
        }
        finally { writes.Release(); }
    }
    public async Task RetryMetadataAsync(long account, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open();
            var rows = await db.Tracks.Where(x => x.Account == account).ToListAsync(ct);
            foreach (var row in rows)
            {
                var track = JsonSerializer.Deserialize<Track>(row.Json)!;
                if (track.MetadataScan?.Status == MetadataScanStatus.Failed) row.Json = JsonSerializer.Serialize(track with { MetadataScan = null });
            }
            await db.SaveChangesAsync(ct);
        }
        finally { writes.Release(); }
    }
    public async Task SaveStateAsync(long account, string key, string value, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await PutDocument(db, account, "state:" + key, value, ct); await db.SaveChangesAsync(ct);
        }
        finally { writes.Release(); }
    }
    public async Task<string?> ReadStateAsync(long account, string key, CancellationToken ct = default)
    {
        await using var db = Open(); return (await db.Documents.FindAsync([account, "state:" + key], ct))?.Json;
    }
    public async Task ClearAccountAsync(long account, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Set<ListenBucketRow>().Where(x => x.Account == account).ExecuteDeleteAsync(ct);
            await db.Set<ListenRow>().Where(x => x.Account == account).ExecuteDeleteAsync(ct);
            await db.Tracks.Where(x => x.Account == account).ExecuteDeleteAsync(ct);
            await db.Documents.Where(x => x.Account == account).ExecuteDeleteAsync(ct);
            // Delete the shared resume pointer in the same transaction. Erasure must
            // not depend on a later write succeeding when storage is exhausted.
            var accountValue = account.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await db.Documents.Where(x => x.Account == 0 && x.Key == "state:last-listening-account-v1" && x.Json == accountValue).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            searchRows.Remove(account);
        }
        finally { writes.Release(); }
    }
    public Task<LibraryGroup[]> GroupsAsync(long account, LibrarySearchRequest request, string kind, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var activity = searchActivity.StartActivity("Search groups");
            var groups = LibraryDiscovery.Groups(await ReadSearchRowsAsync(account, ct), request, kind, ct);
            ct.ThrowIfCancellationRequested();
            return groups;
        }, ct);

    private static async Task<DateTimeOffset> EnsureAddedBaselineAsync(LibraryDb db, long account, CancellationToken ct)
    {
        var key = "state:" + LibraryDiscovery.BaselineKey;
        var saved = await db.Documents.FindAsync([account, key], ct);
        if (saved is not null) return JsonSerializer.Deserialize<DateTimeOffset>(saved.Json);
        var now = DateTimeOffset.UtcNow;
        await PutDocument(db, account, key, JsonSerializer.Serialize(now), ct);
        return now;
    }
    private static async Task PutDocument(LibraryDb db, long account, string key, string json, CancellationToken ct)
    {
        var row = await db.Documents.FindAsync([account, key], ct);
        if (row is null) db.Documents.Add(new() { Account = account, Key = key, Json = json }); else row.Json = json;
    }
    public ValueTask DisposeAsync() { searchRows.Clear(); return ValueTask.CompletedTask; }
}
