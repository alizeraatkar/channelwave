using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using ChannelWave.Telegram;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class TelegramMessageDetailsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const long MessageId = 4503599626321920;
    private static readonly Source Source = new(7, -1001234567890, "Music group");

    private static JsonObject Message(bool channel = false, string? signature = null, long sender = 42, long id = MessageId)
        => Td.Query("message", ("id", id), ("chat_id", Source.ChatId), ("date", 1700000000), ("edit_date", 1800000000),
            ("is_channel_post", channel), ("author_signature", signature ?? ""), ("can_be_saved", true),
            ("sender_id", sender > 0 ? Td.Query("messageSenderUser", ("user_id", sender)) : Td.Query("messageSenderChat", ("chat_id", sender))),
            ("content", Td.Query("messageAudio", ("audio", Td.Query("audio", ("title", "Track"), ("performer", "Artist"),
                ("duration", 90), ("file_name", "track.mp3"), ("mime_type", "audio/mpeg"),
                ("audio", Td.Query("file", ("id", 3), ("size", 100), ("remote", Td.Query("remoteFile", ("id", "remote"), ("unique_id", "unique"))))))))));

    [Fact]
    public async Task Indexing_preserves_group_sender_and_posting_date_without_using_forwarded_origin()
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var message = Message();
        message["forward_info"] = Td.Query("messageForwardInfo", ("date", 1600000000),
            ("origin", Td.Query("messageOriginUser", ("sender_user_id", 999))));
        fixture.Transport.Override = query => query["@type"]!.GetValue<string>() switch
        {
            "searchChatMessages" => Td.Query("foundChatMessages", ("next_from_message_id", 0), ("messages", new[] { message, Message(id: MessageId + 1) })),
            "getUser" => Td.Query("user", ("first_name", "سارا"), ("last_name", "Ahmadi")),
            _ => null
        };

        // Act
        await new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session).IndexPageAsync(Source, Token);
        var tracks = await fixture.Library.SearchAsync(7, "", ct: Token);

        // Assert
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, track =>
        {
            Assert.Equal("سارا Ahmadi", track.Origin.Message!.Attribution);
            Assert.Equal(42, track.Origin.Message.SenderId);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), track.PublishedAt);
        });
        Assert.Single(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "getUser");
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "downloadFile");
    }

    [Fact]
    public async Task Channel_signatures_are_per_message_and_unsigned_posts_do_not_infer_an_admin()
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        fixture.Transport.Override = query => query["@type"]!.GetValue<string>() == "searchChatMessages"
            ? Td.Query("foundChatMessages", ("next_from_message_id", 0), ("messages", new[]
            {
                Message(true, "Admin A", Source.ChatId), Message(true, "Admin B", Source.ChatId, MessageId + 1),
                Message(true, sender: Source.ChatId, id: MessageId + 2)
            })) : null;

        // Act
        await new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session).IndexPageAsync(Source, Token);
        var tracks = (await fixture.Library.SearchAsync(7, "", ct: Token)).OrderBy(t => t.Origin.MessageId).ToArray();

        // Assert
        Assert.Equal(new[] { "Admin A", "Admin B", "Not provided by Telegram" }, tracks.Select(t => t.Origin.Message!.Attribution));
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() is "getUser" or "getSupergroupMembers");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Details_enrich_legacy_tracks_and_preserve_exact_public_or_private_message_links(bool isPublic)
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var track = await fixture.SaveLegacyAsync();
        var url = isPublic ? "https://t.me/test_music/4294967295?single" : "https://t.me/c/1234567890/4294967295?single";
        fixture.Transport.Override = query => query["@type"]!.GetValue<string>() switch
        {
            "getMessage" => Message(),
            "getUser" => Td.Query("user", ("first_name", "Sara"), ("last_name", "Ahmadi")),
            "getMessageProperties" => Td.Query("messageProperties", ("can_get_link", true)),
            "getMessageLink" => Td.Query("messageLink", ("link", url), ("is_public", isPublic)),
            _ => null
        };

        // Act
        var result = await new TelegramMessageDetails(fixture.Session, fixture.Library).RefreshAsync(track, Token);
        await using var reopened = new SqliteLibrary(fixture.Path);
        var saved = Assert.Single(await reopened.SearchAsync(7, "", ct: Token));

        // Assert
        Assert.NotNull(result.Track);
        Assert.Equal("Sara Ahmadi", saved.Origin.Message!.Attribution);
        Assert.Equal(new TelegramMessageLink(url, isPublic), saved.Origin.Message.Link);
        Assert.True(saved.Favorite); Assert.Equal("My title", saved.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), saved.PublishedAt);
        var request = Assert.Single(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "getMessageLink");
        Assert.Equal(MessageId, request["message_id"]!.GetValue<long>());
        Assert.Equal(Source.ChatId, request["chat_id"]!.GetValue<long>());
        Assert.False(request["for_album"]!.GetValue<bool>()); Assert.False(request["in_message_thread"]!.GetValue<bool>());
        Assert.Equal(0, request["media_timestamp"]!.GetValue<int>());
        Assert.Equal(0, request["checklist_task_id"]!.GetValue<int>()); Assert.Equal("", request["poll_option_id"]!.GetValue<string>());
        Assert.Equal(Source, await reopened.SourceAsync(7, Source.ChatId, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_sender_and_dates_index_successfully_and_lookup_failures_do_not_drop_tracks(bool deadline)
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var missing = Message(); missing.Remove("date"); missing.Remove("sender_id"); missing.Remove("is_channel_post"); missing.Remove("author_signature");
        Exception failure = deadline ? new OperationCanceledException("Synthetic transport deadline") : new TelegramException(500, "Synthetic lookup failure");
        fixture.Transport.Override = query => query["@type"]!.GetValue<string>() switch
        {
            "searchChatMessages" => Td.Query("foundChatMessages", ("next_from_message_id", 0), ("messages", new[] { missing, Message(id: MessageId + 1) })),
            "getUser" => throw failure,
            _ => null
        };

        // Act
        await new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session).IndexPageAsync(Source, Token);
        var tracks = (await fixture.Library.SearchAsync(7, "", ct: Token)).OrderBy(t => t.Origin.MessageId).ToArray();

        // Assert
        Assert.Equal(2, tracks.Length);
        Assert.Equal(DateTimeOffset.UnixEpoch, tracks[0].PublishedAt);
        Assert.All(tracks, t => Assert.Equal("Unknown sender", t.Origin.Message!.Attribution));
        Assert.Equal(42, tracks[1].Origin.Message!.SenderId);
        Assert.False(tracks[1].Origin.Message!.SenderResolved);
    }

    [Fact]
    public async Task Reindex_and_targeted_updates_merge_metadata_without_recreating_removed_tracks()
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var legacy = await fixture.SaveLegacyAsync();
        var date = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var info = new TelegramMessageInfo(true, "chat", Source.ChatId, "Music group", "Admin A", true,
            new("https://t.me/c/1234567890/12", false), true);

        // Act
        await fixture.Library.SetMessageInfoAsync(7, legacy.Id, Source.ChatId, MessageId, info, date, Token);
        await fixture.Library.SavePageAsync(Source, [legacy with { Id = Guid.NewGuid() }], Token);
        var preserved = Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token));
        var unsigned = await fixture.Library.SetMessageInfoAsync(7, legacy.Id, Source.ChatId, MessageId,
            new(true, "chat", Source.ChatId), DateTimeOffset.UnixEpoch, Token);
        var crossAccount = await fixture.Library.SetMessageInfoAsync(8, legacy.Id, Source.ChatId, MessageId, info, date, Token);
        var wrongMessage = await fixture.Library.SetMessageInfoAsync(7, legacy.Id, Source.ChatId, 99, info, date, Token);
        await fixture.Library.RemoveSourceAsync(7, Source.ChatId, Token);
        var removed = await fixture.Library.SetMessageInfoAsync(7, legacy.Id, Source.ChatId, MessageId, info, date, Token);

        // Assert
        Assert.Equal(info, preserved.Origin.Message); Assert.Equal(date, preserved.PublishedAt);
        Assert.Equal(legacy.Id, preserved.Id); Assert.True(preserved.Favorite); Assert.Equal("My title", preserved.Title);
        Assert.Equal("Not provided by Telegram", unsigned!.Origin.Message!.Attribution);
        Assert.Equal(info.Link, unsigned.Origin.Message.Link); Assert.Equal(date, unsigned.PublishedAt);
        Assert.Null(crossAccount); Assert.Null(wrongMessage); Assert.Null(removed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(403)]
    [InlineData(503)]
    [InlineData(-1)]
    public async Task Link_unavailability_or_failure_preserves_attribution_and_handles_cached_links(int code)
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var track = await fixture.SaveLegacyAsync();
        var cached = new TelegramMessageLink("https://t.me/c/1234567890/12", false);
        track = (await fixture.Library.SetMessageInfoAsync(7, track.Id, Source.ChatId, MessageId,
            new(Link: cached, LinkResolved: true), track.PublishedAt, Token))!;
        fixture.Transport.Override = query => query["@type"]!.GetValue<string>() switch
        {
            "getMessage" => Message(true, "Signed", Source.ChatId),
            "getMessageProperties" => code == -1 ? throw new OperationCanceledException("Synthetic transport deadline")
                : code == 0 ? Td.Query("messageProperties", ("can_get_link", false)) : throw new TelegramException(code, "Synthetic link failure"),
            _ => null
        };

        // Act
        var result = await new TelegramMessageDetails(fixture.Session, fixture.Library).RefreshAsync(track, Token);

        // Assert
        Assert.Equal("Signed", result.Track!.Origin.Message!.Attribution);
        Assert.Equal(code is 503 or -1 ? cached : null, result.Track.Origin.Message.Link);
        Assert.Equal(code == 403, result.LinkDenied);
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "getMessageLink");
    }

    [Fact]
    public async Task Account_change_during_a_late_response_cannot_save_metadata()
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var track = await fixture.SaveLegacyAsync();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.AsyncOverride = (q, _) => q["@type"]!.GetValue<string>() == "getMessage" ? response.Task : null;
        var pending = new TelegramMessageDetails(fixture.Session, fixture.Library).RefreshAsync(track, Token);

        // Act
        await fixture.Session.LogOutAsync(Token);
        response.SetResult(JsonSerializer.SerializeToElement(Message()));

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token)).Origin.Message);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Unavailable_messages_clear_cached_links_but_keep_available_attribution_and_dates(int code)
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var track = await fixture.SaveLegacyAsync();
        var date = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var info = new TelegramMessageInfo(true, "chat", Source.ChatId, "Music group", "Admin A", true,
            new("https://t.me/c/1234567890/12", false), true);
        track = (await fixture.Library.SetMessageInfoAsync(7, track.Id, Source.ChatId, MessageId, info, date, Token))!;
        fixture.Transport.Override = q => q["@type"]!.GetValue<string>() == "getMessage" ? throw new TelegramException(code, "Synthetic inaccessible message") : null;

        // Act
        var result = await new TelegramMessageDetails(fixture.Session, fixture.Library).RefreshAsync(track, Token);
        var saved = Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token));

        // Assert
        Assert.True(result.LinkDenied);
        Assert.Null(saved.Origin.Message!.Link);
        Assert.Equal("Admin A", saved.Origin.Message.Attribution);
        Assert.Equal(date, saved.PublishedAt);
    }

    [Fact]
    public void Anonymous_groups_and_exposed_channel_authors_use_only_provided_identities()
    {
        // Arrange
        var anonymous = new TelegramMessageInfo(false, "chat", Source.ChatId, "Music group", "Curator", true);
        var exposed = new TelegramMessageInfo(true, "user", 42, "Sara", SenderResolved: true);

        // Act / Assert
        Assert.Equal("Music group · Curator", anonymous.Attribution);
        Assert.Equal("Sara", exposed.Attribution);
        Assert.Equal("Not provided by Telegram", (exposed with { SenderType = "chat" }).Attribution);
        Assert.False(new TelegramMessageLink("javascript:alert(1)", true).IsValid);
        Assert.False(new TelegramMessageLink("https://t.me.evil.example/track", true).IsValid);
    }

    [Fact]
    public void Sender_replacement_or_missing_authoritative_identity_does_not_reuse_an_old_author()
    {
        // Arrange
        var previous = new TelegramMessageInfo(true, "user", 42, "Previous admin", SenderResolved: true);

        // Act
        var changed = TelegramMessageInfo.Merge(previous, new(true, "user", 43));
        var absent = TelegramMessageInfo.Merge(previous, new(true));
        var retry = TelegramMessageInfo.Merge(previous, new(true, "user", 42));
        var linkOnly = TelegramMessageInfo.Merge(previous, new(LinkResolved: true));

        // Assert
        Assert.Equal("Not provided by Telegram", changed!.Attribution);
        Assert.Equal("Not provided by Telegram", absent!.Attribution);
        Assert.Null(absent.SenderId);
        Assert.Equal("Previous admin", retry!.Attribution);
        Assert.Equal("Previous admin", linkOnly!.Attribution);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("999999999999999999")]
    [InlineData("\"not a date\"")]
    public void Unavailable_or_invalid_dates_and_partial_sender_records_use_unknown_fallbacks(string date)
    {
        // Arrange
        var message = Fixture.Json("{\"date\":" + date + ",\"sender_id\":{\"@type\":\"messageSenderUser\"}}");

        // Act
        var info = TelegramMessageReader.Read(message);
        var posted = TelegramMessageReader.PostingDate(message);

        // Assert
        Assert.Null(info.SenderId);
        Assert.Equal("Unknown sender", info.Attribution);
        Assert.Equal(DateTimeOffset.UnixEpoch, posted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_or_removed_details_cannot_be_saved_after_a_late_response(bool remove)
    {
        // Arrange
        await using var fixture = await DetailsFixture.CreateAsync();
        var track = await fixture.SaveLegacyAsync();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.AsyncOverride = (q, _) => q["@type"]!.GetValue<string>() == "getMessage" ? response.Task : null;
        fixture.Transport.Override = q => q["@type"]!.GetValue<string>() == "getMessageProperties"
            ? Td.Query("messageProperties", ("can_get_link", false)) : null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = new TelegramMessageDetails(fixture.Session, fixture.Library).RefreshAsync(track, cancellation.Token);

        // Act
        if (remove) await fixture.Library.RemoveSourceAsync(7, Source.ChatId, Token);
        else cancellation.Cancel();
        response.SetResult(JsonSerializer.SerializeToElement(Message(true, sender: Source.ChatId)));

        // Assert
        if (remove)
        {
            Assert.Null((await pending).Track);
            Assert.Empty(await fixture.Library.SearchAsync(7, "", ct: Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token)).Origin.Message);
        }
    }

    private sealed class DetailsFixture(string path, ScriptedTransport transport, TelegramSession session, SqliteLibrary library) : IAsyncDisposable
    {
        public string Path => path;
        public ScriptedTransport Transport => transport;
        public TelegramSession Session => session;
        public SqliteLibrary Library => library;
        public static async Task<DetailsFixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "channelwave-message-" + Guid.NewGuid() + ".sqlite");
            var transport = new ScriptedTransport(); var session = new TelegramSession(transport); var library = new SqliteLibrary(path);
            await library.InitializeAsync(Token);
            await session.ConnectAsync(new(1, new string('0', 32), "test-only", "test-only", ""), Token);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (session.AccountId == 7) ready.TrySetResult(); }
            session.Changed += Changed;
            transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            finally { session.Changed -= Changed; }
            return new(path, transport, session, library);
        }
        public async Task<Track> SaveLegacyAsync()
        {
            var track = Fixture.Track(Source, 1);
            track = track with { Origin = track.Origin with { MessageId = MessageId } };
            await library.SavePageAsync(Source, [track], Token);
            await library.SetFavoriteAsync(7, track.Id, true, Token);
            return await library.SetLocalMetadataAsync(7, track.Id, "My title", null, Token);
        }
        public async ValueTask DisposeAsync()
        {
            await session.DisposeAsync(); await library.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
