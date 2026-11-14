using ChannelWave.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

namespace ChannelWave.Storage.Sqlite;

public sealed class ListenRow
{
    public long Account { get; set; }
    public string Id { get; set; } = "";
    public long Started { get; set; }
    public long LastActive { get; set; }
    public string Track { get; set; } = "";
    public long Source { get; set; }
    public bool Qualified { get; set; }
    public string Search { get; set; } = "";
    public string Json { get; set; } = "";
}
public sealed class ListenBucketRow
{
    public long Account { get; set; }
    public string Id { get; set; } = "";
    public string Listen { get; set; } = "";
    public long Hour { get; set; }
    public string Json { get; set; } = "";
}
internal static class ListeningSchema
{
    internal static void Configure(ModelBuilder model)
    {
        foreach (var name in new[] { "Id", "Track", "Search", "Json" }) model.Entity<ListenRow>().Property<string>(name).IsRequired().HasColumnType("TEXT");
        foreach (var name in new[] { "Account", "Started", "LastActive", "Source" }) model.Entity<ListenRow>().Property<long>(name).HasColumnType("INTEGER");
        model.Entity<ListenRow>().Property<bool>("Qualified").HasColumnType("INTEGER");
        foreach (var name in new[] { "Id", "Listen", "Json" }) model.Entity<ListenBucketRow>().Property<string>(name).IsRequired().HasColumnType("TEXT");
        foreach (var name in new[] { "Account", "Hour" }) model.Entity<ListenBucketRow>().Property<long>(name).HasColumnType("INTEGER");
        model.Entity<ListenRow>().ToTable("Listens").HasKey(x => new { x.Account, x.Id });
        model.Entity<ListenRow>().HasIndex(x => new { x.Account, x.LastActive, x.Id });
        model.Entity<ListenRow>().HasIndex(x => new { x.Account, x.Track, x.LastActive });
        model.Entity<ListenRow>().HasIndex(x => new { x.Account, x.Source, x.LastActive });
        model.Entity<ListenBucketRow>().ToTable("ListenBuckets").HasKey(x => new { x.Account, x.Id });
        model.Entity<ListenBucketRow>().HasIndex(x => new { x.Account, x.Listen });
        model.Entity<ListenBucketRow>().HasIndex(x => new { x.Account, x.Hour });
    }
}
[DbContext(typeof(LibraryDb))]
[Migration("202611060001_ListeningExperience")]
public sealed class ListeningExperienceMigration : Migration
{
    protected override void Up(MigrationBuilder migration) => migration.Sql("""
        CREATE TABLE Listens (Account INTEGER NOT NULL, Id TEXT NOT NULL, Started INTEGER NOT NULL,
            LastActive INTEGER NOT NULL, Track TEXT NOT NULL, Source INTEGER NOT NULL, Qualified INTEGER NOT NULL,
            Search TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Account,Id));
        CREATE INDEX IX_Listens_Account_LastActive_Id ON Listens(Account,LastActive,Id);
        CREATE INDEX IX_Listens_Account_Track_LastActive ON Listens(Account,Track,LastActive);
        CREATE INDEX IX_Listens_Account_Source_LastActive ON Listens(Account,Source,LastActive);
        CREATE TABLE ListenBuckets (Account INTEGER NOT NULL, Id TEXT NOT NULL, Listen TEXT NOT NULL,
            Hour INTEGER NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Account,Id));
        CREATE INDEX IX_ListenBuckets_Account_Listen ON ListenBuckets(Account,Listen);
        CREATE INDEX IX_ListenBuckets_Account_Hour ON ListenBuckets(Account,Hour);
        """);
    protected override void Down(MigrationBuilder migration) { migration.DropTable("ListenBuckets"); migration.DropTable("Listens"); }
}

public sealed partial class SqliteLibrary
{
    private const string ListeningKey = "state:listening-v1";
    private static readonly JsonSerializerOptions ListeningJson = new(JsonSerializerDefaults.Web);
    private static async Task<ListeningState> ReadListeningStateAsync(LibraryDb db, long account, CancellationToken ct)
    {
        var row = await db.Documents.FindAsync([account, ListeningKey], ct);
        return row is null ? new() : JsonSerializer.Deserialize<ListeningState>(row.Json, ListeningJson)
            ?? throw new InvalidDataException("Listening settings could not be read. Recording is paused until storage recovers.");
    }
    public async Task<ListeningState> ReadListeningStateAsync(long account, CancellationToken ct = default)
    {
        await using var db = Open(); return await ReadListeningStateAsync(db, account, ct);
    }
    public async Task<long> CommitListeningAsync(ListeningCommit commit, CancellationToken ct = default)
    {
        if (commit.Account <= 0 || commit.Events.Any(e => e.Account != commit.Account || e.Track.Account != commit.Account))
            throw new InvalidOperationException("Cross-account listening write rejected.");
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var state = await ReadListeningStateAsync(db, commit.Account, ct);
            if (state.Revision != commit.ExpectedRevision || state.Generation != commit.Generation)
                throw new InvalidOperationException("Listening storage changed. Reload before writing again.");
            foreach (var listen in commit.Events)
            {
                var id = listen.Id.ToString("N");
                var row = await db.Set<ListenRow>().FindAsync([commit.Account, id], ct);
                if (row is null) { row = new() { Account = commit.Account, Id = id }; db.Add(row); }
                row.Started = listen.Started; row.LastActive = listen.LastActive; row.Track = listen.Track.Id.ToString("N");
                row.Source = listen.Track.Chat; row.Qualified = listen.QualifiedAt is not null;
                row.Search = listen.Track.SearchText; row.Json = JsonSerializer.Serialize(listen, ListeningJson);
                await db.Set<ListenBucketRow>().Where(x => x.Account == commit.Account && x.Listen == id).ExecuteDeleteAsync(ct);
                foreach (var bucket in ListeningBucket.From(listen)) db.Add(new ListenBucketRow { Account = commit.Account,
                    Id = bucket.Id, Listen = id, Hour = bucket.Hour, Json = JsonSerializer.Serialize(bucket, ListeningJson) });
            }
            var next = state with { Revision = state.Revision + 1, Preferences = commit.Preferences,
                Player = commit.ForgetPlayer ? null : commit.Player,
                PreviousPlayer = commit.ForgetPlayer ? null : state.Player };
            await PutDocument(db, commit.Account, ListeningKey, JsonSerializer.Serialize(next, ListeningJson), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return next.Revision;
        }
        finally { writes.Release(); }
    }
    public async Task<ListeningEvent?> ListenAsync(long account, Guid id, CancellationToken ct = default)
    {
        await using var db = Open(); var row = await db.Set<ListenRow>().FindAsync([account, id.ToString("N")], ct);
        return row is null ? null : JsonSerializer.Deserialize<ListeningEvent>(row.Json, ListeningJson);
    }
    public async Task<ListeningPage> HistoryAsync(long account, ListeningQuery query, CancellationToken ct = default)
    {
        await using var db = Open(); var rows = db.Set<ListenRow>().AsNoTracking().Where(x => x.Account == account && x.LastActive >= query.From && x.Started < query.To);
        if (query.Source is { } source) rows = rows.Where(x => x.Source == source);
        if (query.Track is { } track) { var key = track.ToString("N"); rows = rows.Where(x => x.Track == key); }
        if (query.Qualified is { } qualified) rows = rows.Where(x => x.Qualified == qualified);
        foreach (var token in query.Terms) rows = rows.Where(x => x.Search.Contains(token));
        if (query.Before is { } before) rows = rows.Where(x => x.LastActive < before.Time || x.LastActive == before.Time && x.Id.CompareTo(before.Id) < 0);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var result = await rows.OrderByDescending(x => x.LastActive).ThenByDescending(x => x.Id).Take(limit + 1).ToArrayAsync(ct);
        var page = result.Take(limit).ToArray(); var last = page.LastOrDefault();
        return new(page.Select(x => JsonSerializer.Deserialize<ListeningEvent>(x.Json, ListeningJson)!).ToArray(),
            result.Length > limit && last is not null ? new(last.LastActive, last.Id) : null);
    }
    public async Task<ListeningBucket[]> BucketsAsync(long account, long from, long to, string? after, CancellationToken ct = default)
    {
        await using var db = Open(); var rows = db.Set<ListenBucketRow>().AsNoTracking().Where(x => x.Account == account && x.Hour >= from && x.Hour < to);
        if (after is not null) rows = rows.Where(x => x.Id.CompareTo(after) > 0);
        return (await rows.OrderBy(x => x.Id).Take(500).Select(x => x.Json).ToArrayAsync(ct))
            .Select(x => JsonSerializer.Deserialize<ListeningBucket>(x, ListeningJson)!).ToArray();
    }
    public async Task<ListeningState> DeleteListeningAsync(long account, Guid? id, long? olderThan = null, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = Open(); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = db.Set<ListenRow>().Where(x => x.Account == account);
            if (id is { } listen) { var key = listen.ToString("N"); rows = rows.Where(x => x.Id == key); }
            if (olderThan is { } cutoff) rows = rows.Where(x => x.LastActive < cutoff);
            var keys = rows.Select(x => x.Id);
            await db.Set<ListenBucketRow>().Where(x => x.Account == account && keys.Contains(x.Listen)).ExecuteDeleteAsync(ct);
            await rows.ExecuteDeleteAsync(ct);
            var state = await ReadListeningStateAsync(db, account, ct);
            PlaybackEnvelope? Suppress(PlaybackEnvelope? envelope)
            {
                if (envelope is null) return null;
                try
                {
                    var snapshot = envelope.Read(account);
                    return snapshot is not null && olderThan is null && (id is null || snapshot.ActiveListen == id)
                        ? PlaybackEnvelope.Create(snapshot with { ActiveListen = null, Suppressed = true }) : envelope;
                }
                catch (JsonException) { return null; }
                catch (NotSupportedException) { return envelope; }
            }
            state = state with { Revision = state.Revision + 1, Generation = state.Generation + 1,
                Player = Suppress(state.Player), PreviousPlayer = Suppress(state.PreviousPlayer) };
            await PutDocument(db, account, ListeningKey, JsonSerializer.Serialize(state, ListeningJson), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return state;
        }
        finally { writes.Release(); }
    }
}
