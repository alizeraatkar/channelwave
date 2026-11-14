using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using ChannelWave.Telegram;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class PersistenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Ten_thousand_tracks_survive_restart_with_favorites_playlists_cursors_and_account_isolation()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-test-" + Guid.NewGuid() + ".sqlite");
        try
        {
            var source = new Source(7, -1001234567890, "Synthetic source", 9007199254740000);
            var tracks = Enumerable.Range(1, 10000).Select(i => Fixture.Track(source, i)).ToArray();
            await using (var db = new SqliteLibrary(path))
            {
                await db.InitializeAsync(Token);
                foreach (var batch in tracks.Chunk(100)) await db.SavePageAsync(source, batch, Token);
                await db.SetFavoriteAsync(7, tracks[42].Id, true, Token);
                await db.SaveStateAsync(7, "playlist", JsonSerializer.Serialize(Playlists.Add(Playlists.Create("Saved"), tracks[42].Id)), Token);
                await db.SaveStateAsync(0, "pending-signout-v1", """{"Account":7,"RemoteLogout":true}""", Token);
                await db.SavePageAsync(source, [tracks[42] with { Id = Guid.NewGuid(), Title = "Edited title" }], Token);
                await db.SavePageAsync(new Source(8, source.ChatId, "Other"), [Fixture.Track(source with { AccountId = 8 }, 42)], Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SavePageAsync(source, [Fixture.Track(source with { AccountId = 8 }, 999)], Token));
            }
            await using var reopened = new SqliteLibrary(path); await reopened.InitializeAsync(Token);
            var all = new List<Track>(); for (var offset = 0; offset < 11000; offset += 1000) all.AddRange(await reopened.SearchAsync(7, "", offset, 1000, Token));
            Assert.Equal(10000, all.Count); Assert.Equal(10000, all.Select(t => t.Identity).Distinct().Count());
            var edited = Assert.Single(await reopened.SearchAsync(7, "Edited title", ct: Token)); Assert.True(edited.Favorite); Assert.Equal(tracks[42].Id, edited.Id);
            Assert.Single(await reopened.SearchAsync(8, "", ct: Token)); Assert.Empty(await reopened.SearchAsync(9, "", ct: Token));
            Assert.Equal(source.HistoryCursor, Assert.Single(await reopened.SourcesAsync(7, Token)).HistoryCursor);
            Assert.Contains(tracks[42].Id.ToString(), (await reopened.ReadStateAsync(7, "playlist", Token))!);
            // Collection filtering must examine the complete catalog before taking a page.
            await reopened.SetFavoriteAsync(7, tracks[9998].Id, true, Token);
            var favorites = await reopened.ReadViewAsync(7, "", true, null, null, 101, Token);
            Assert.Equal(2, favorites.Count); Assert.Contains(favorites, t => t.Id == tracks[9998].Id);
            var list = await reopened.ReadViewAsync(7, "", false, source.ChatId,
                [tracks[9998].Id, tracks[42].Id, tracks[9998].Id], 101, Token);
            Assert.Equal(new[] { tracks[9998].Id, tracks[42].Id }, list.Select(t => t.Id));
            Assert.Empty(await reopened.ReadViewAsync(7, "", false, 99, null, 101, Token));
            Assert.Empty(await reopened.ReadViewAsync(8, "", false, null, [tracks[42].Id], 101, Token));
            await reopened.ClearAccountAsync(7, Token); Assert.Empty(await reopened.SearchAsync(7, "", ct: Token)); Assert.Empty(await reopened.SourcesAsync(7, Token)); Assert.Null(await reopened.ReadStateAsync(7, "playlist", Token));
            Assert.Single(await reopened.SearchAsync(8, "", ct: Token));
            // Recovery metadata survives account erasure/restart until both cleanup stages finish.
            Assert.Equal("""{"Account":7,"RemoteLogout":true}""", await reopened.ReadStateAsync(0, "pending-signout-v1", Token));
            await reopened.SaveStateAsync(0, "pending-signout-v1", "null", Token);
            Assert.Equal("null", await reopened.ReadStateAsync(0, "pending-signout-v1", Token));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Unindex_removes_only_the_selected_channel_and_prunes_its_playlist_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-unindex-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            Source source = new(7, 1, "Remove"), neighbor = new(7, 10, "Keep"), other = new(8, 1, "Other account");
            var removed = Fixture.Track(source, 1); var kept = Fixture.Track(neighbor, 1);
            await db.SavePageAsync(source, [removed with { Favorite = true }], Token);
            await db.SavePageAsync(neighbor, [kept], Token);
            await db.SavePageAsync(other, [Fixture.Track(other, 1)], Token);
            var playlist = new Playlist(Guid.NewGuid(), "Listening list", [removed.Id, kept.Id, removed.Id]);
            await db.SaveStateAsync(7, "playlist", JsonSerializer.Serialize(playlist), Token);
            var named = Playlists.Duplicate(playlist, "Named collection");
            await db.SaveStateAsync(7, "playlists", JsonSerializer.Serialize(new[] { named, Playlists.Create("Empty") }), Token);
            await db.SaveStateAsync(8, "playlists", JsonSerializer.Serialize(new[] { named }), Token);
            await db.SaveStateAsync(7, "channel-catalog-v1", "catalog stays", Token);
            Assert.Equal(new[] { removed.Id }, await db.RemoveSourceAsync(7, 1, Token));
            Assert.Empty(await db.RemoveSourceAsync(7, 1, Token));
            await using var reopened = new SqliteLibrary(path);
            Assert.Null(await reopened.SourceAsync(7, 1, Token));
            Assert.Equal(kept.Id, Assert.Single(await reopened.SearchAsync(7, "", ct: Token)).Id);
            Assert.Single(await reopened.SearchAsync(8, "", ct: Token));
            Assert.Equal(new[] { kept.Id }, JsonSerializer.Deserialize<Playlist>((await reopened.ReadStateAsync(7, "playlist", Token))!)!.Tracks);
            var namedLists = JsonSerializer.Deserialize<Playlist[]>((await reopened.ReadStateAsync(7, "playlists", Token))!)!;
            Assert.Equal(new[] { kept.Id }, namedLists[0].Tracks);
            Assert.Empty(namedLists[1].Tracks);
            Assert.Contains(removed.Id, JsonSerializer.Deserialize<Playlist[]>((await reopened.ReadStateAsync(8, "playlists", Token))!)![0].Tracks);
            Assert.Equal("catalog stays", await reopened.ReadStateAsync(7, "channel-catalog-v1", Token));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Pagination_uses_server_cursor_even_for_short_pages_and_never_downloads_media()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-test-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            await using var transport = new FakeTransport(q => Fixture.Json(q["from_message_id"]!.GetValue<long>() == 0
                ? """{"@type":"foundChatMessages","next_from_message_id":123,"messages":[]}"""
                : """{"@type":"foundChatMessages","next_from_message_id":0,"messages":[]}"""));
            var indexer = new SourceIndexer(transport, db);
            var first = await indexer.IndexPageAsync(new(1, -1001, "Source"), Token); Assert.False(first.HistoryComplete); Assert.Equal(123, first.HistoryCursor);
            var last = await indexer.IndexPageAsync(first, Token); Assert.True(last.HistoryComplete);
            await indexer.IndexPageAsync(last, Token); Assert.Equal(2, transport.Requests.Count);
            Assert.All(transport.Requests, q => Assert.Equal("searchChatMessages", q["@type"]!.GetValue<string>()));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
}

internal static class Fixture
{
    public static Track Track(Source source, int message) => new(Guid.NewGuid(), new(source.AccountId, source.ChatId, message, source.Name, false), new("remote", "unique-" + message, message, "file.mp3", "audio/mpeg", 100), "Original " + message, "Artist", "Title " + message, "Artist", 90, DateTimeOffset.UnixEpoch);
    public static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
}
internal sealed class FakeTransport(Func<JsonObject, JsonElement> respond) : ITdTransport
{
    public Channel<JsonElement> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
    public List<JsonObject> Requests { get; } = [];
    public ChannelReader<JsonElement> Updates => Channel.Reader;
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<JsonElement> SendAsync(JsonObject request, CancellationToken ct = default) { Requests.Add(request); return Task.FromResult(respond(request)); }
    public ValueTask DisposeAsync() { Channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
