using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using Xunit;

namespace ChannelWave.Core.Tests;

[CollectionDefinition("Library search", DisableParallelization = true)]
public sealed class LibrarySearchCollection;

[Collection("Library search")]
public sealed class LibrarySearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public sealed record Sample(int Number, string Title, string Artist, string Source, int Duration, DateTimeOffset PublishedAt, bool Favorite = false);
    public sealed record SearchCase(string Name, LibrarySearchCriteria Criteria, int[] Expected);

    private static Track MakeTrack(Sample sample, long account = 7) => new(
        Guid.NewGuid(), new(account, 100, sample.Number, sample.Source, false), new("remote", "unique", 1, "fixture.mp3", "audio/mpeg", 100),
        sample.Title, sample.Artist, sample.Title, sample.Artist, sample.Duration, sample.PublishedAt, Favorite: sample.Favorite);

    [Fact]
    public async Task Source_filter_applies_before_pagination_queue_and_offline_selection()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-source-search-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path);
            await db.InitializeAsync(Token);
            var source = new Source(7, -100, "Night Radio");
            var tracks = Enumerable.Range(1, 125).Select(i => Fixture.Track(source, i) with { Title = "Same title" }).ToArray();
            var other = new Source(7, -200, "Other source");
            var otherAccount = source with { AccountId = 8 };
            await db.SavePageAsync(source, tracks, Token);
            await db.SavePageAsync(other, [Fixture.Track(other, 1) with { Title = "Same title" }], Token);
            await db.SavePageAsync(otherAccount, [Fixture.Track(otherAccount, 1) with { Title = "Same title" }], Token);
            var query = new LibrarySearchRequest(new(), Chat: source.ChatId, Order: LibraryOrder.AddedNewest, Offset: 100, Limit: 100);
            var page = await db.SearchAsync(7, query, Token);
            Assert.Equal(25, page.Count);
            Assert.All(page, track => Assert.Equal((7L, -100L), (track.Origin.AccountId, track.Origin.ChatId)));
            var queue = await db.QueueTrackIdsAsync(7, query, Token);
            Assert.Equal(125, queue.Length);
            Assert.Equal(tracks.Select(track => track.Id).Order(), queue.Order());
            var saved = new[] { tracks[0].Id, tracks[^1].Id };
            var offline = await db.SearchAsync(7, query with { Offset = 0, OfflineIds = saved }, Token);
            Assert.Equal(saved.Order(), offline.Select(track => track.Id).Order());
            Assert.Equal(saved.Order(), (await db.QueueTrackIdsAsync(7, query with { OfflineIds = saved }, Token)).Order());
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Shared_matching_cases_agree_for_core_and_Sqlite()
    {
        // Arrange
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "search-cases.json"), Token));
        var samples = document.RootElement.GetProperty("tracks").Deserialize<Sample[]>(JsonOptions)!;
        var cases = document.RootElement.GetProperty("cases").Deserialize<SearchCase[]>(JsonOptions)!;
        var tracks = samples.Select(x => MakeTrack(x)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "channelwave-search-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            await db.SavePageAsync(new(7, 100, "Fixture source"), tracks, Token);
            await db.SavePageAsync(new(8, 100, "Other account"), [MakeTrack(samples[0], 8)], Token);

            foreach (var test in cases)
            {
                // Act
                var request = new LibrarySearchRequest(test.Criteria);
                var core = LibrarySearch.Filter(tracks, request, Token);
                var stored = await db.SearchAsync(7, request, Token);
                var queue = await db.QueueTrackIdsAsync(7, request, Token);

                // Assert
                Assert.Equal(test.Expected, core.Select(x => (int)x.Origin.MessageId));
                Assert.Equal(test.Expected, stored.Select(x => (int)x.Origin.MessageId));
                Assert.Equal(core.Select(t => t.Id), queue);
            }
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Cached_search_updates_after_writes_and_filters_before_pagination()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-search-edits-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var source = new Source(7, 100, "Fixture source");
            var tracks = Enumerable.Range(1, 1100).Select(i => Fixture.Track(source, i) with { Title = "Night " + i, Performer = "Artist" }).ToArray();
            foreach (var batch in tracks.Chunk(100)) await db.SavePageAsync(source, batch, Token);
            await db.SearchAsync(7, new LibrarySearchRequest(new(Text: "night")), Token); // warm projections

            // Act
            await db.SetFavoriteAsync(7, tracks[^1].Id, true, Token);
            await db.SetLocalMetadataAsync(7, tracks[^1].Id, "Café sunrise", "Local artist", Token);
            var favorites = await db.SearchAsync(7, new LibrarySearchRequest(new(Text: "sunrise cafe"), Favorites: true, Limit: 1), Token);
            var playlist = new[] { tracks[^1].Id, tracks[1].Id, tracks[0].Id };
            var custom = await db.SearchAsync(7, new LibrarySearchRequest(new(), Playlist: playlist, Limit: 2), Token);
            var titled = await db.SearchAsync(7, new LibrarySearchRequest(new(), Playlist: playlist, Sort: PlaylistSort.Title, Offset: 1, Limit: 1), Token);
            await db.SavePageAsync(source, [tracks[0] with { Title = "Newly indexed label" }], Token);
            var edited = await db.SearchAsync(7, new LibrarySearchRequest(new(Title: "indexed")), Token);
            var fullQueue = await db.QueueTrackIdsAsync(7, new LibrarySearchRequest(new(), Offset: 100, Limit: 1), Token);
            var filteredQueue = await db.QueueTrackIdsAsync(7, new LibrarySearchRequest(new(Text: "sunrise cafe"), Favorites: true), Token);
            await db.RemoveSourceAsync(7, 100, Token);
            var removed = await db.SearchAsync(7, new LibrarySearchRequest(new()), Token);

            // Assert
            Assert.Equal(tracks[^1].Id, Assert.Single(favorites).Id);
            Assert.Equal(playlist.Take(2), custom.Select(x => x.Id));
            Assert.Equal(tracks[0].Id, Assert.Single(titled).Id);
            Assert.Equal(tracks[0].Id, Assert.Single(edited).Id);
            Assert.Equal(1100, fullQueue.Length); Assert.Equal(tracks[^1].Id, Assert.Single(filteredQueue));
            Assert.Empty(removed);
            Assert.Empty(await db.SearchAsync(8, new LibrarySearchRequest(new()), Token));
            await db.ClearAccountAsync(7, Token);
            Assert.Empty(await db.SearchAsync(7, new LibrarySearchRequest(new()), Token));
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Unfiltered_pages_use_account_order_and_do_not_project_unrequested_records()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-paging-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var source = new Source(7, 100, "Fixture source");
            var tracks = Enumerable.Range(1, 205).Select(i => Fixture.Track(source, i)).ToArray();
            await db.SavePageAsync(source, tracks, Token);
            await db.SavePageAsync(new(8, 100, "Other account"), [Fixture.Track(new(8, 100, "Other account"), 1)], Token);
            // A malformed record outside these pages proves that opening them
            // neither extracts JSON fields nor hydrates the rest of the account.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO Tracks(Account,Identity,Id,SearchText,Json) VALUES (7,'zz-unrequested',@id,'','invalid-json')";
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                await command.ExecuteNonQueryAsync(Token);
            }

            // Act
            var first = await db.SearchAsync(7, new LibrarySearchRequest(new(), Limit: 101), Token);
            var second = await db.SearchAsync(7, new LibrarySearchRequest(new(), Offset: 100, Limit: 101), Token);
            var end = await db.SearchAsync(7, new LibrarySearchRequest(new(), Offset: 200, Limit: 5), Token);
            var foreign = await db.SearchAsync(8, new LibrarySearchRequest(new()), Token);

            // Assert
            var expected = tracks.OrderBy(t => t.Identity, StringComparer.Ordinal).Select(t => t.Id).ToArray();
            Assert.Equal(expected.Take(101), first.Select(t => t.Id));
            Assert.Equal(expected.Skip(100).Take(101), second.Select(t => t.Id));
            Assert.Equal(expected.Skip(200), end.Select(t => t.Id));
            Assert.Equal(8, Assert.Single(foreign).Origin.AccountId);
        }
        finally { DeleteDatabase(path); }
    }

    [Fact]
    public async Task Fifty_thousand_track_search_is_bounded_and_reports_cold_and_warm_timings()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-search-scale-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var source = new Source(7, 100, "Large synthetic source");
            for (var offset = 0; offset < 50000; offset += 500)
            {
                var batch = Enumerable.Range(offset, 500).Select(i => Fixture.Track(source, i) with { Title = "Synthetic " + i, Performer = "Scale artist" }).ToArray();
                await db.SavePageAsync(source, batch, Token);
            }
            var clock = Stopwatch.StartNew();

            // Act
            var cold = await db.SearchAsync(7, new LibrarySearchRequest(new(Text: "synthetic artist"), Limit: 101), Token);
            var coldTime = clock.Elapsed; clock.Restart();
            var warm = await db.SearchAsync(7, new LibrarySearchRequest(new(Text: "49999 synthetic")), Token);
            var warmTime = clock.Elapsed;

            // Assert
            Assert.Equal(101, cold.Count);
            Assert.Equal("Synthetic 49999", Assert.Single(warm).Title);
            TestContext.Current.TestOutputHelper!.WriteLine($"50,000 tracks: cold {coldTime.TotalMilliseconds:F0} ms; warm {warmTime.TotalMilliseconds:F0} ms.");
        }
        finally { DeleteDatabase(path); }
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    [InlineData(300, 100)]
    public void Invalid_duration_ranges_are_rejected(int? min, int? max)
    {
        var criteria = new LibrarySearchCriteria(MinDuration: min, MaxDuration: max);
        Assert.Throws<ArgumentException>(criteria.Validate);
    }

    [Fact]
    public void Invalid_publication_range_is_rejected()
    {
        var criteria = new LibrarySearchCriteria(PublishedFrom: DateTimeOffset.Parse("2026-10-28T00:00:00Z", CultureInfo.InvariantCulture),
            PublishedBefore: DateTimeOffset.Parse("2026-10-27T00:00:00Z", CultureInfo.InvariantCulture));
        Assert.Throws<ArgumentException>(criteria.Validate);
    }

    private static void DeleteDatabase(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
