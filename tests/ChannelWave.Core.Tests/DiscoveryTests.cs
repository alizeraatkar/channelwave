using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChannelWave.Core.Tests;

[Collection("Library search")]
public sealed class DiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Track Track(int id, string artist = "Artist", string album = "Album", int duration = 180) => new(
        Guid.NewGuid(), new(7, 100, id, "Source", false), new("remote", "unique" + id, 1, "fixture.mp3", "audio/mpeg", 100),
        "Song " + id, artist, "Song " + id, artist, duration, DateTimeOffset.UnixEpoch.AddDays(id),
        Favorite: id % 2 == 0, EmbeddedMetadata: new(Album: album, Track: id.ToString()));

    [Fact]
    public async Task Catalog_order_filters_groups_and_queue_agree_beyond_the_first_page()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-discovery-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var tracks = Enumerable.Range(1, 1205).Select(i => Track(i, i % 3 == 0 ? "هنرمند" : "Artist", i % 4 == 0 ? "Other" : "Album", i == 1205 ? 0 : i)).ToArray();
            foreach (var batch in tracks.Chunk(100)) await db.SavePageAsync(new(7, 100, "Source"), batch, Token);
            var request = new LibrarySearchRequest(new(Text: "album", LikedOnly: true), Order: LibraryOrder.Longest,
                OfflineIds: tracks.Where(t => t.Duration > 1000).Select(t => t.Id).ToArray(), Offset: 2, Limit: 13);

            // Act
            var page = await db.SearchAsync(7, request, Token);
            var queue = await db.QueueTrackIdsAsync(7, request, Token);
            var groups = await db.GroupsAsync(7, request with { Offset = 0, Limit = 100 }, "artists", Token);

            // Assert
            var expected = LibrarySearch.Filter(tracks, request, Token);
            Assert.Equal(expected.Select(t => t.Id), page.Select(t => t.Id));
            Assert.Equal(queue.Skip(2).Take(13), page.Select(t => t.Id));
            Assert.Equal(queue.Length, groups.Sum(g => g.Count));
            Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));
            Assert.Empty(await db.SearchAsync(8, request, Token));
            Assert.Empty(await db.GroupsAsync(8, request, "albums", Token));
            var overview = await db.OverviewAsync(7, Token);
            Assert.Equal(1205, overview.Tracks);
            Assert.Equal(2, overview.Artists);
            Assert.Equal(4, overview.Albums);
            var labels = await db.GroupsAsync(7, new(new(), Limit: 1, GroupText: "artist"), "artists", Token);
            Assert.Equal("Artist", Assert.Single(labels).Label);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Grouping_uses_real_full_credits_and_album_numbering_without_merging_artists()
    {
        // Arrange
        var first = Track(2, "A & B", "Shared") with { EmbeddedMetadata = new(Album: "Shared", Disc: "1/2", Track: "2/10") };
        var second = Track(1, "A & B", "Shared") with { EmbeddedMetadata = new(Album: "Shared", Disc: "1", Track: "1") };
        var other = Track(3, "C", "Shared");
        var unknown = Track(4, "", "Shared") with { Performer = "Unknown artist" };
        var unnumbered = Track(5, "A & B", "Shared") with { EmbeddedMetadata = new(Album: "Shared") };
        Track[] tracks = [first, other, unknown, unnumbered, second];

        // Act
        var groups = LibraryDiscovery.Groups(tracks.Select(LibrarySearchRow.FromTrack), new(new()), "albums", Token);
        var album = LibrarySearch.Filter(tracks, new(new(), AlbumKey: LibraryDiscovery.AlbumKey(first), Order: LibraryOrder.Album), Token);

        // Assert
        Assert.Equal(2, groups.Length);
        Assert.Equal(new[] { second.Id, first.Id, unnumbered.Id }, album.Select(t => t.Id));
        Assert.Null(LibraryDiscovery.ArtistKey(unknown));
        Assert.Equal("a & b", LibraryDiscovery.ArtistKey(first));
        Assert.NotEqual(LibraryDiscovery.AlbumKey(first), LibraryDiscovery.AlbumKey(other));
    }

    [Theory]
    [InlineData(LibraryOrder.Shortest)]
    [InlineData(LibraryOrder.Longest)]
    public void Unknown_duration_is_last_in_both_directions(LibraryOrder order)
    {
        // Arrange
        Track[] tracks = [Track(1, duration: 0), Track(2, duration: 20), Track(3, duration: 100)];
        // Act
        var result = LibrarySearch.Filter(tracks, new(new(), Order: order), Token);
        // Assert
        Assert.Equal(tracks[0].Id, result[^1].Id);
        Assert.Equal(order == LibraryOrder.Shortest ? tracks[1].Id : tracks[2].Id, result[0].Id);
    }

    [Fact]
    public async Task Upgrade_baseline_and_first_indexed_dates_survive_edits_and_reopening()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-discovery-" + Guid.NewGuid() + ".sqlite");
        var legacy = Track(1); var newer = Track(2);
        try
        {
            string? baseline; DateTimeOffset? indexed;
            await using (var db = new SqliteLibrary(path))
            {
                await db.InitializeAsync(Token);
                await using (var connection = new SqliteConnection("Data Source=" + path))
                {
                    await connection.OpenAsync(Token); await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO Tracks VALUES(7,@identity,@id,@search,@json)";
                    command.Parameters.AddWithValue("@identity", legacy.Identity); command.Parameters.AddWithValue("@id", legacy.Id.ToString().ToUpperInvariant());
                    command.Parameters.AddWithValue("@search", legacy.SearchText); command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(legacy));
                    await command.ExecuteNonQueryAsync(Token);
                }
                // Act
                await db.SearchAsync(7, new(new(), Order: LibraryOrder.AddedNewest), Token);
                baseline = await db.ReadStateAsync(7, LibraryDiscovery.BaselineKey, Token);
                await db.SavePageAsync(new(7, 100, "Source"), [newer], Token);
                indexed = (await db.TrackAsync(7, newer.Id, Token))!.AddedAt;
                await db.SetLocalMetadataAsync(7, newer.Id, "Corrected", "Other credit", Token);
                await db.SavePageAsync(new(7, 100, "Source"), [newer with { Title = "Edited post" }, legacy], Token);
                var warmOrder = await db.SearchAsync(7, new(new(), Order: LibraryOrder.AddedOldest), Token);
                Assert.Equal(legacy.Id, warmOrder[0].Id);
            }
            await using var reopened = new SqliteLibrary(path); await reopened.InitializeAsync(Token);
            var ordered = await reopened.SearchAsync(7, new(new(), Order: LibraryOrder.AddedNewest), Token);

            // Assert
            Assert.NotNull(indexed); Assert.NotNull(baseline);
            Assert.Equal(indexed, (await reopened.TrackAsync(7, newer.Id, Token))!.AddedAt);
            Assert.Null((await reopened.TrackAsync(7, legacy.Id, Token))!.AddedAt);
            Assert.Equal(baseline, await reopened.ReadStateAsync(7, LibraryDiscovery.BaselineKey, Token));
            Assert.Equal(newer.Id, ordered[0].Id);
            Assert.Equal("Other credit", (await reopened.GroupsAsync(7, new(new(Title: "corrected")), "artists", Token)).Single().Label);
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path) { SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }

    [Fact]
    public async Task Sqlite_library_reads_leave_the_calling_thread_and_cancel_a_superseded_query()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-search-worker-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path);
            await db.InitializeAsync(Token);
            var track = Track(1, "Fixture artist");
            await db.SavePageAsync(new(7, 100, "Source"), [track], Token);
            using var parent = new Activity("Sqlite search regression").Start();
            using var superseded = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var workers = new ConcurrentQueue<int>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "ChannelWave.Sqlite.Search",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.Parent != parent) return;
                    workers.Enqueue(Environment.CurrentManagedThreadId);
                    if (activity.DisplayName == "Search tracks") superseded.Cancel();
                }
            };
            ActivitySource.AddActivityListener(listener);

            var (caller, search, groups) = await Task.Factory.StartNew(() =>
            {
                var request = new LibrarySearchRequest(new(Artist: "Fixture artist"));
                return (Environment.CurrentManagedThreadId, db.SearchAsync(7, request, superseded.Token),
                    db.GroupsAsync(7, request, "artists", Token));
            }, Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
            Assert.Equal("Fixture artist", Assert.Single(await groups).Label);
            Assert.Equal(2, workers.Count);
            Assert.All(workers, worker => Assert.NotEqual(caller, worker));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Committed_alias_updates_and_removals_keep_warm_search_projections_account_scoped()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-search-cache-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path);
            await db.InitializeAsync(Token);
            var original = Track(1);
            var alias = Track(2) with { Media = original.Media };
            var other = original with { Id = Guid.NewGuid(), Origin = original.Origin with { AccountId = 8 } };
            var source = new Source(7, 100, "Source");
            await db.SavePageAsync(source, [original], Token);
            await db.SavePageAsync(source with { AccountId = 8 }, [other], Token);

            using var parent = new Activity("Sqlite cache regression").Start();
            var builds = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = activitySource => activitySource.Name == "ChannelWave.Sqlite.Search",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.DisplayName == "Build search projections" && activity.Parent?.Parent == parent) builds.Enqueue(activity);
                }
            };
            ActivitySource.AddActivityListener(listener);
            var request = new LibrarySearchRequest(new(Text: "song"));
            await db.SearchAsync(7, request, Token);
            await db.SearchAsync(8, request, Token);

            await db.SavePageAsync(source, [alias], Token);
            var metadata = new CachedMetadata(new(Title: "Updated tags"),
                new(Metadata.MediaKey(original), EmbeddedMetadataReader.Version, MetadataScanStatus.Completed));
            await db.SaveEmbeddedMetadataAsync(original, metadata, Token);
            var updated = await db.SearchAsync(7, new(new(Text: "updated tags")), Token);
            var otherMatches = await db.SearchAsync(8, request, Token);
            await db.RemoveSourceAsync(7, source.ChatId, Token);
            var remaining = await db.SearchAsync(7, new(new(Text: "updated tags")), Token);

            Assert.Equal(new[] { original.Id, alias.Id }, updated.Select(track => track.Id));
            Assert.Equal(other.Id, Assert.Single(otherMatches).Id);
            Assert.Empty(remaining);
            Assert.Equal(2, builds.Count);

            await db.ClearAccountAsync(7, Token);
            await db.SavePageAsync(source, [Track(3)], Token);
            Assert.Single(await db.SearchAsync(7, request, Token));
            Assert.Equal(other.Id, Assert.Single(await db.SearchAsync(8, request, Token)).Id);
            Assert.Equal(3, builds.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Recent_tracks_page_past_one_hundred_and_react_to_history_deletion()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-recents-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var tracks = Enumerable.Range(1, 235).Select(i => Track(i)).ToArray();
            var listens = tracks.Select((track, i) => new ListeningEvent(Guid.NewGuid(), 7, null, ListeningTrack.From(track),
                i * 2000, i * 2000 + 1000, 1000, 30000, null, 0, 1, "changed", [])).ToArray();
            var duplicate = listens[0] with { Id = Guid.NewGuid(), LastActive = 999999 };
            await db.CommitListeningAsync(new(7, 0, 0, new(), null, [.. listens, duplicate]), Token);
            var first = await db.RecentTracksPageAsync(7, 0, 100, Token);
            var second = await db.RecentTracksPageAsync(7, 100, 100, Token);
            var third = await db.RecentTracksPageAsync(7, 200, 100, Token);
            Assert.Equal(tracks[0].Id, first[0].TrackId);
            Assert.Equal(235, first.Concat(second).Concat(third).Select(t => t.TrackId).Distinct().Count());
            Assert.Equal(35, third.Length);
            Assert.Empty(await db.RecentTracksPageAsync(8, 0, 100, Token));
            await db.DeleteListeningAsync(7, duplicate.Id, ct: Token);
            Assert.Equal(tracks[^1].Id, (await db.RecentTracksPageAsync(7, 0, 1, Token))[0].TrackId);
            await db.DeleteListeningAsync(7, null, ct: Token);
            Assert.Empty(await db.RecentTracksPageAsync(7, 0, 100, Token));
        }
        finally { Cleanup(path); }
    }
}
