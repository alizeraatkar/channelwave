using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class LocalMetadataTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Local_corrections_survive_restart_concurrent_favorites_and_reindex_then_revert_to_latest_source()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-labels-" + Guid.NewGuid() + ".sqlite");
        try
        {
            var source = new Source(7, 1, "Source"); var track = Fixture.Track(source, 1);
            await using (var db = new SqliteLibrary(path))
            {
                await db.InitializeAsync(Token); await db.SavePageAsync(source, [track], Token);
                await db.SavePageAsync(source with { AccountId = 8 }, [track with { Origin = track.Origin with { AccountId = 8 } }], Token);

                // Act
                await Task.WhenAll(db.SetLocalMetadataAsync(7, track.Id, "فارسی 🎵", "Local artist", Token), db.SetFavoriteAsync(7, track.Id, true, Token));
                var fresh = track with { Id = Guid.NewGuid(), OriginalTitle = "Latest Telegram title", OriginalPerformer = "Latest artist", Title = "Latest Telegram title", Performer = "Latest artist" };
                await db.SavePageAsync(source, [fresh], Token);
            }
            await using var reopened = new SqliteLibrary(path);
            var corrected = Assert.Single(await reopened.SearchAsync(7, "فارسی", ct: Token));

            // Assert
            Assert.Equal(track.Id, corrected.Id); Assert.True(corrected.Favorite);
            Assert.Equal("Local artist", corrected.Performer); Assert.Equal("Latest Telegram title", corrected.OriginalTitle);
            Assert.Empty(await reopened.SearchAsync(8, "فارسی", ct: Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetLocalMetadataAsync(9, track.Id, "Wrong account", null, Token));
            var restored = await reopened.SetLocalMetadataAsync(7, track.Id, null, null, Token);
            Assert.Equal("Latest Telegram title", restored.Title); Assert.Equal("Latest artist", restored.Performer);
            Assert.Null(restored.TitleOverride); Assert.Null(restored.PerformerOverride); Assert.True(restored.Favorite);
            Assert.Empty(await reopened.SearchAsync(7, "فارسی", ct: Token)); Assert.Single(await reopened.SearchAsync(7, "latest", ct: Token));
            await reopened.RemoveSourceAsync(7, 1, Token);
            await reopened.SavePageAsync(source, [track], Token);
            Assert.Null(Assert.Single(await reopened.SearchAsync(7, "", ct: Token)).TitleOverride);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
}
