using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using ChannelWave.Telegram;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class ArtworkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");
    private static ArtworkData Image(Track track, ArtworkKind kind = ArtworkKind.Embedded) => new(Metadata.MediaKey(track), kind, Png);

    [Fact]
    public async Task Artwork_survives_restart_and_reindex_without_losing_concurrent_labels_or_favorites()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-art-" + Guid.NewGuid() + ".sqlite");
        try
        {
            var source = new Source(7, 1, "Source"); var track = Fixture.Track(source, 1);
            await using (var db = new SqliteLibrary(path))
            {
                await db.InitializeAsync(Token); await db.SavePageAsync(source, [track], Token);
                await Task.WhenAll(db.SetArtworkAsync(7, track.Id, Image(track), Token), db.SetFavoriteAsync(7, track.Id, true, Token), db.SetLocalMetadataAsync(7, track.Id, "Local", null, Token));
                await db.SavePageAsync(source, [track with { Id = Guid.NewGuid(), OriginalTitle = "New source" }], Token);
            }
            await using var reopened = new SqliteLibrary(path);
            var row = Assert.Single(await reopened.SearchAsync(7, "", ct: Token));
            Assert.Equal(track.Id, row.Id); Assert.True(row.Favorite); Assert.Equal("Local", row.Title); Assert.Equal(Png, row.Artwork!.Data);
            Assert.Equal(ArtworkKind.Embedded, (await reopened.SetArtworkAsync(7, track.Id, Image(track, ArtworkKind.ChannelPhoto), Token)).Artwork!.Kind);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetArtworkAsync(8, track.Id, Image(track), Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetArtworkAsync(7, track.Id, Image(track) with { MediaKey = "wrong" }, Token));
            await reopened.SavePageAsync(source, [track with { Media = track.Media with { UniqueId = "replacement" } }], Token);
            Assert.Null(Assert.Single(await reopened.SearchAsync(7, "", ct: Token)).Artwork);
            await reopened.SetArtworkAsync(7, track.Id, Image(track with { Media = track.Media with { UniqueId = "replacement" } }), Token);
            await reopened.SavePageAsync(source, [track with { Origin = track.Origin with { Protected = true } }], Token);
            Assert.Null(Assert.Single(await reopened.SearchAsync(7, "", ct: Token)).Artwork);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetArtworkAsync(7, track.Id, Image(track), Token));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Artwork_cache_is_bounded_and_erased_with_its_source_or_account()
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-art-bound-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var source = new Source(7, 1, "Source"); var tracks = Enumerable.Range(1, 65).Select(i => Fixture.Track(source, i)).ToArray();
            await db.SavePageAsync(source, tracks, Token);
            foreach (var track in tracks) await db.SetArtworkAsync(7, track.Id, Image(track), Token);
            Assert.Equal(64, (await db.SearchAsync(7, "", ct: Token)).Count(x => x.Artwork is not null));
            Assert.NotNull((await db.SearchAsync(7, "", ct: Token)).Single(x => x.Id == tracks[^1].Id).Artwork);
            var other = Fixture.Track(source with { AccountId = 8 }, 1);
            await db.SavePageAsync(source with { AccountId = 8 }, [other], Token); await db.SetArtworkAsync(8, other.Id, Image(other), Token);
            await db.RemoveSourceAsync(7, 1, Token); Assert.Empty(await db.SearchAsync(7, "", ct: Token));
            Assert.NotNull(Assert.Single(await db.SearchAsync(8, "", ct: Token)).Artwork);
            await db.ClearAccountAsync(8, Token); Assert.Empty(await db.SearchAsync(8, "", ct: Token));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Telegram_artwork_rechecks_identity_permissions_and_only_downloads_bounded_images()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(new(1, new string('0', 32), "test-only", "test-only", ""), Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => { if (session.AccountId == 7) ready.TrySetResult(); };
        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var protectedContent = false; var unique = "unique-1";
        var image = Td.Query("file", ("id", 2), ("size", Png.Length), ("local", Td.Query("localFile", ("is_downloading_completed", false))));
        transport.Override = query => query["@type"]!.GetValue<string>() switch
        {
            "getMessage" => Td.Query("message", ("can_be_saved", !protectedContent), ("content", Td.Query("messageAudio", ("audio", Td.Query("audio",
                ("audio", Td.Query("file", ("id", 1), ("remote", Td.Query("remoteFile", ("unique_id", unique))))), ("album_cover_thumbnail", Td.Query("thumbnail", ("file", image)))))))),
            "getChat" => Td.Query("chat", ("has_protected_content", protectedContent), ("photo", Td.Query("chatPhotoInfo", ("small", image)))),
            "downloadFile" => Td.Query("file", ("id", 2), ("size", Png.Length), ("local", Td.Query("localFile", ("is_downloading_completed", true)))),
            _ => null
        };
        var track = Fixture.Track(new(7, 1, "Source"), 1); var service = new TelegramArtwork(session);
        var message = await service.ResolveAsync(track, Token); Assert.NotNull(message); Assert.NotNull(message.Thumbnail);
        await service.CompleteImageAsync(7, message.Thumbnail.Value, Token);
        Assert.All(transport.Requests.Where(x => x["@type"]!.GetValue<string>() == "downloadFile"), query => Assert.Equal(2, query["file_id"]!.GetValue<int>()));
        var tooBig = image.DeepClone().AsObject(); tooBig["size"] = ArtworkData.MaxBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CompleteImageAsync(7, JsonSerializer.SerializeToElement(tooBig), Token));
        unique = "changed"; Assert.Null(await service.ResolveAsync(track, Token));
        unique = "unique-1"; protectedContent = true; Assert.Null(await service.ResolveAsync(track, Token)); Assert.Null(await service.ChannelPhotoAsync(track, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(track with { Origin = track.Origin with { AccountId = 8 } }, Token));
    }
}
