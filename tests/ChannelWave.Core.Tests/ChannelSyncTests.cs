using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using ChannelWave.Telegram;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class ChannelSyncTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_includes_regular_chats_without_selecting_or_indexing_them(bool cached)
    {
        // Arrange: both lists contain the same chats, as well as unsupported chat types.
        await using var fixture = await SyncFixture.CreateAsync(9, selectSources: false);
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "getChat"
            ? MixedChat(q["chat_id"]!.GetValue<long>()) : null);
        if (cached)
        {
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Session.Update += update => { if (update.Type() == "updateChatTitle" && update.Id("chat_id") == 999) delivered.TrySetResult(); };
            for (var id = 1; id <= 9; id++) fixture.Transport.Push(Td.Query("updateNewChat", ("chat", MixedChat(id))));
            fixture.Transport.Push(Td.Query("updateChatTitle", ("chat_id", 999L), ("title", "Sentinel")));
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        await fixture.Library.SaveStateAsync(7, "channel-catalog-v1", JsonSerializer.Serialize(new
        {
            Paused = false,
            Channels = new Source[] { new(7, 1, "Public channel"), new(8, 99, "Other account") }
        }), Token);
        await using var catalog = new ChannelCatalog(fixture.Session, fixture.Library);

        // Act: restoring an existing catalog discovers the newly supported chats.
        await catalog.RestoreAsync(Token);
        await catalog.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.Equal("completed", catalog.Progress.Phase);
        Assert.Equal(7, catalog.Progress.Found);
        Assert.Equal(Enumerable.Range(1, 7).Select(x => (long)x), catalog.Channels.Select(x => x.ChatId).Order());
        Assert.All(catalog.Channels, source => Assert.Equal(7, source.AccountId));
        Assert.Empty(await fixture.Library.SourcesAsync(7, Token));
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() is "searchChatMessages" or "downloadFile");
        if (cached) Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "getChat");
    }

    [Theory]
    [InlineData(3L)]
    [InlineData(4L)]
    [InlineData(5L)]
    [InlineData(6L)]
    [InlineData(7L)]
    public async Task Selected_regular_chat_resumes_its_cursor_and_keeps_accounts_isolated(long chatId)
    {
        // Arrange
        await using var fixture = await SyncFixture.CreateAsync(9, selectSources: false);
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "getChat"
            ? MixedChat(q["chat_id"]!.GetValue<long>()) : null);
        await using var catalog = new ChannelCatalog(fixture.Session, fixture.Library);
        await catalog.RestoreAsync(Token);
        await catalog.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var selected = Assert.Single(catalog.Channels, x => x.ChatId == chatId);
        await fixture.Library.SavePageAsync(new(8, chatId, "Other account", HistoryCursor: 77), [], Token);
        var nextPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            if (q["from_message_id"]!.GetValue<long>() == 0) return SyncTransport.Page(1, 10);
            nextPage.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null;
        };

        // Act: pause after one committed page, then restore the job.
        await using (var sync = fixture.NewSync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SelectAsync(selected with { AccountId = 8 }, Token));
            await sync.SelectAsync(selected, Token);
            await nextPage.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await sync.PauseAsync(preserveResume: true);
        }
        Assert.Equal(10, (await fixture.Library.SourceAsync(7, chatId, Token))!.HistoryCursor);
        var before = fixture.Transport.Requests.Count;
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "searchChatMessages" ? SyncTransport.Page(2, 0) : null);
        await using var restored = fixture.NewSync();
        await restored.RestoreAsync(Token);
        await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        var resumed = Assert.Single(fixture.Transport.Requests.Skip(before), q => q["@type"]!.GetValue<string>() == "searchChatMessages");
        Assert.Equal(10, resumed["from_message_id"]!.GetValue<long>());
        Assert.All(fixture.Transport.Requests.Where(q => q["@type"]!.GetValue<string>() == "searchChatMessages"),
            q => Assert.Equal(chatId, q["chat_id"]!.GetValue<long>()));
        Assert.True(Assert.Single(await fixture.Library.SourcesAsync(7, Token)).HistoryComplete);
        Assert.Equal(2, (await fixture.Library.SearchAsync(7, "", ct: Token)).Count);
        Assert.Equal(77, (await fixture.Library.SourceAsync(8, chatId, Token))!.HistoryCursor);
        Assert.Empty(await fixture.Library.SearchAsync(8, "", ct: Token));
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() == "downloadFile");
    }

    private static JsonObject MixedChat(long id)
    {
        var (name, type) = id switch
        {
            1 => ("Public channel", Td.Query("chatTypeSupergroup", ("supergroup_id", id), ("is_channel", true))),
            2 => ("Private channel", Td.Query("chatTypeSupergroup", ("supergroup_id", id), ("is_channel", true))),
            3 => ("Basic group", Td.Query("chatTypeBasicGroup", ("basic_group_id", id))),
            4 => ("Supergroup", Td.Query("chatTypeSupergroup", ("supergroup_id", id), ("is_channel", false))),
            5 => ("Private conversation", Td.Query("chatTypePrivate", ("user_id", id))),
            6 => ("Music bot", Td.Query("chatTypePrivate", ("user_id", id))),
            7 => ("Saved Messages", Td.Query("chatTypePrivate", ("user_id", id))),
            8 => ("Secret chat", Td.Query("chatTypeSecret", ("secret_chat_id", 1), ("user_id", id))),
            _ => ("Unknown chat", Td.Query("chatTypeFuture"))
        };
        return Td.Query("chat", ("id", id), ("title", name), ("type", type));
    }

    [Fact]
    public async Task Discovery_drains_large_loaded_lists_and_deduplicates_main_archive_and_updates()
    {
        await using var fixture = await SyncFixture.CreateAsync(170, selectSources: false);
        // TDLib already supplies full chat details through updates. No redundant getChat requests are needed.
        for (var id = 1; id <= 170; id++) fixture.Transport.Push(Td.Query("updateNewChat", ("chat", SyncTransport.Chat(id))));
        fixture.Transport.Push(Td.Query("updateChatTitle", ("chat_id", 999L), ("title", "Sentinel")));
        await WaitForAsync(() => fixture.Transport.ProcessedSentinel);
        await using var catalog = new ChannelCatalog(fixture.Session, fixture.Library);
        await catalog.RestoreAsync(Token); await catalog.Completion.WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal("completed", catalog.Progress.Phase); Assert.Equal(170, catalog.Progress.Found);
        var sources = await fixture.Library.SourcesAsync(7, Token);
        Assert.Empty(sources);
        Assert.Equal(170, catalog.Channels.Select(x => x.ChatId).Distinct().Count());
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() is "downloadFile" or "getChat" or "searchChatMessages");
    }

    [Fact]
    public async Task Interrupted_sync_resumes_committed_cursors_and_preserves_track_identity_and_favorites()
    {
        await using var fixture = await SyncFixture.CreateAsync(1);
        var nextPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            if (q["from_message_id"]!.GetValue<long>() == 0) return SyncTransport.Page(1, 10);
            nextPage.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null;
        };
        await using (var first = fixture.NewSync())
        {
            await first.StartAsync(Token); await nextPage.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await first.PauseAsync(preserveResume: true);
        }
        var original = Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token));
        await fixture.Library.SetFavoriteAsync(7, original.Id, true, Token);
        var before = fixture.Transport.Requests.Count;
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "searchChatMessages" ? SyncTransport.Page(1, 0) : null);
        await using var restored = fixture.NewSync();
        await restored.RestoreAsync(Token); await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("completed", restored.Progress.Phase);
        var request = Assert.Single(fixture.Transport.Requests.Skip(before), q => q["@type"]!.GetValue<string>() == "searchChatMessages");
        Assert.Equal(10, request["from_message_id"]!.GetValue<long>());
        var track = Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token));
        Assert.Equal(original.Id, track.Id); Assert.True(track.Favorite);
        await restored.StartAsync(Token); await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Single(fixture.Transport.Requests.Skip(before), q => q["@type"]!.GetValue<string>() == "searchChatMessages");
        Assert.Equal(1, restored.Progress.Completed);
    }

    [Fact]
    public async Task Explicit_pause_does_not_autoresume_on_reconnect()
    {
        await using var fixture = await SyncFixture.CreateAsync(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null;
        };
        await using (var first = fixture.NewSync())
        {
            await first.StartAsync(Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); await first.PauseAsync();
        }
        var count = fixture.Transport.Requests.Count;
        await using var restored = fixture.NewSync(); await restored.RestoreAsync(Token);
        Assert.False(restored.IsRunning); Assert.Equal(count, fixture.Transport.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_deadline_survives_pause_resume_and_stops_new_requests()
    {
        await using var fixture = await SyncFixture.CreateAsync(1);
        var limited = false;
        fixture.Transport.Handler = (q, _) =>
        {
            if (q["@type"]!.GetValue<string>() == "searchChatMessages" && !limited)
            { limited = true; throw new TelegramException(429, "Too Many Requests: retry after 1"); }
            return Task.FromResult<JsonObject?>(null);
        };
        await using var sync = fixture.NewSync(); await sync.StartAsync(Token);
        await WaitForAsync(() => sync.Progress.Phase == "waiting" && sync.Progress.RetryAt is not null);
        var deadline = sync.Progress.RetryAt!.Value;
        await sync.PauseAsync();
        var count = fixture.Transport.Requests.Count;
        await sync.StartAsync(Token);
        await Task.Delay(100, Token);
        Assert.Equal(count, fixture.Transport.Requests.Count);
        await sync.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(DateTimeOffset.UtcNow >= deadline); Assert.Equal("completed", sync.Progress.Phase);
    }

    [Fact]
    public async Task Inaccessible_channel_does_not_block_other_channels_and_parallelism_is_bounded()
    {
        await using var fixture = await SyncFixture.CreateAsync(5);
        var active = 0; var peak = 0;
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapStarted = 0;
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            if (q["chat_id"]!.GetValue<long>() == 1) throw new TelegramException(403, "Synthetic access denied");
            var concurrent = Interlocked.Increment(ref active); lock (fixture) peak = Math.Max(peak, concurrent);
            try
            {
                if (q["chat_id"]!.GetValue<long>() is 3 or 4)
                {
                    if (Interlocked.Increment(ref overlapStarted) == 2) overlap.TrySetResult();
                    await overlap.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                }
                return SyncTransport.Page(1, 0);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        await using var sync = fixture.NewSync();
        await Task.WhenAll(sync.StartAsync(Token), sync.StartAsync(Token));
        await sync.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("completed", sync.Progress.Phase); Assert.Equal(4, sync.Progress.Completed); Assert.Equal(1, sync.Progress.Failed);
        Assert.InRange(peak, 2, 2);
        Assert.Equal(5, fixture.Transport.Requests.Count(q => q["@type"]!.GetValue<string>() == "searchChatMessages"));
    }

    [Fact]
    public async Task Concurrent_manual_requests_use_the_committed_cursor_instead_of_repeating_a_page()
    {
        await using var fixture = await SyncFixture.CreateAsync(1);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            await Task.Delay(20, ct);
            return SyncTransport.Page(1, q["from_message_id"]!.GetValue<long>() == 0 ? 10 : 0);
        };
        var indexer = new SourceIndexer(fixture.Transport, fixture.Library);
        var source = new Source(7, 1, "One");
        await Task.WhenAll(indexer.IndexPageAsync(source, Token), indexer.IndexPageAsync(source, Token));
        Assert.Equal(new long[] { 0, 10 }, fixture.Transport.Requests.Where(q => q["@type"]!.GetValue<string>() == "searchChatMessages").Select(q => q["from_message_id"]!.GetValue<long>()));
        Assert.True((await fixture.Library.SourceAsync(7, 1, Token))!.HistoryComplete);
        Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token));
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task Selecting_channels_during_an_active_job_indexes_only_those_sources()
    {
        await using var fixture = await SyncFixture.CreateAsync(3, selectSources: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            if (q["chat_id"]!.GetValue<long>() == 1) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            return SyncTransport.Page(1, 0);
        };
        await using var sync = fixture.NewSync();
        await sync.SelectAsync(new(7, 1, "One"), Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await sync.SelectAsync(new(7, 2, "Two"), Token);
        await sync.SelectAsync(new(7, 2, "Two"), Token);
        release.TrySetResult(); await sync.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, sync.Progress.Completed);
        Assert.Equal(2, (await fixture.Library.SourcesAsync(7, Token)).Count);
        Assert.Equal(new long[] { 1, 2 }, fixture.Transport.Requests.Where(q => q["@type"]!.GetValue<string>() == "searchChatMessages").Select(q => q["chat_id"]!.GetValue<long>()));
        Assert.DoesNotContain(fixture.Transport.Requests, q => q["@type"]!.GetValue<string>() is "getChats" or "loadChats" or "downloadFile");
        Assert.Null(await fixture.Library.SourceAsync(7, 3, Token));
    }

    [Fact]
    public async Task Unindex_cancels_inflight_pages_resumes_other_sources_and_does_not_restore_removed_selection()
    {
        await using var fixture = await SyncFixture.CreateAsync(2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.Handler = async (q, ct) =>
        {
            if (q["@type"]!.GetValue<string>() != "searchChatMessages") return null;
            if (q["chat_id"]!.GetValue<long>() != 1) return SyncTransport.Page(1, 0);
            if (q["from_message_id"]!.GetValue<long>() == 0) return SyncTransport.Page(1, 10);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return SyncTransport.Page(2, 0); // A late transport reply must not be committed.
        };
        await using var sync = fixture.NewSync(); await sync.StartAsync(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Single(await sync.UnindexAsync(new(7, 1, "Channel 1"), Token));
        await sync.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Null(await fixture.Library.SourceAsync(7, 1, Token));
        Assert.Equal(2, Assert.Single(await fixture.Library.SearchAsync(7, "", ct: Token)).Origin.ChatId);
        var requests = fixture.Transport.Requests.Count;
        await using var restored = fixture.NewSync(); await restored.RestoreAsync(Token);
        await restored.StartAsync(Token); await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(requests, fixture.Transport.Requests.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.UnindexAsync(new(8, 2, "Wrong account"), Token));
        fixture.Transport.Handler = (_, _) => Task.FromResult<JsonObject?>(null);
        await restored.SelectAsync(new(7, 1, "Channel 1"), Token); await restored.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, (await fixture.Library.SearchAsync(7, "", ct: Token)).Count);
    }

    [Fact]
    public async Task Catch_up_commits_its_boundary_only_after_all_new_pages_and_preserves_history_and_local_edits()
    {
        // Arrange
        await using var fixture = await SyncFixture.CreateAsync(1, selectSources: false);
        var source = new Source(7, 1, "Source", HistoryCursor: 999, LatestMessageId: 10, HistoryComplete: true);
        var old = SourceIndexer.ParseTrack(source, JsonSerializer.SerializeToElement(SyncTransport.Page(10, 0)).GetProperty("messages")[0]);
        await fixture.Library.SavePageAsync(source, [old], Token);
        await fixture.Library.SetFavoriteAsync(7, old.Id, true, Token);
        await fixture.Library.SetLocalMetadataAsync(7, old.Id, "My title", null, Token);
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "searchChatMessages"
            ? SyncTransport.Page(q["from_message_id"]!.GetValue<long>() == 0 ? 20 : 10, q["from_message_id"]!.GetValue<long>() == 0 ? 19 : 0) : null);
        var indexer = new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session);

        // Act
        await indexer.ReconcilePageAsync(7, 1, false, Token);
        var partial = (await fixture.Library.SourceAsync(7, 1, Token))!;
        await indexer.ReconcilePageAsync(7, 1, false, Token);
        var complete = (await fixture.Library.SourceAsync(7, 1, Token))!;

        // Assert
        Assert.Equal(10, partial.LatestMessageId); Assert.Equal(19, partial.Sync!.CatchUpCursor);
        Assert.Equal(20, complete.LatestMessageId); Assert.Equal(0, complete.Sync!.CatchUpCursor); Assert.Equal(999, complete.HistoryCursor);
        var retained = (await fixture.Library.TrackAsync(7, old.Id, Token))!;
        Assert.True(retained.Favorite); Assert.Equal("My title", retained.Title);
        Assert.Equal(2, (await fixture.Library.SearchAsync(7, "", ct: Token)).Count);
    }

    [Fact]
    public async Task Cache_eviction_is_ignored_and_permanent_deletion_cannot_be_resurrected_by_stale_history()
    {
        // Arrange
        await using var fixture = await SyncFixture.CreateAsync(1, selectSources: false);
        var source = new Source(7, 1, "Source");
        var old = SourceIndexer.ParseTrack(source, JsonSerializer.SerializeToElement(SyncTransport.Page(1, 0)).GetProperty("messages")[0]);
        await fixture.Library.SavePageAsync(source, [old], Token);
        await fixture.Library.SetFavoriteAsync(7, old.Id, true, Token);
        var indexer = new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session);
        JsonElement Delete(bool cache) => JsonSerializer.SerializeToElement(Td.Query("updateDeleteMessages", ("chat_id", 1L), ("message_ids", new[] { 1L }), ("is_permanent", true), ("from_cache", cache)));

        // Act
        await indexer.ApplyUpdateAsync(7, Delete(true), Token);
        var cached = await fixture.Library.TrackAsync(7, old.Id, Token);
        await indexer.ApplyUpdateAsync(7, Delete(false), Token);
        await indexer.IndexPageAsync(source, Token);

        // Assert
        Assert.True(cached!.Available);
        var removed = (await fixture.Library.TrackAsync(7, old.Id, Token))!;
        Assert.False(removed.Available); Assert.True(removed.Favorite);
        Assert.Equal(new[] { 1L }, (await fixture.Library.SourceAsync(7, 1, Token))!.Sync!.DeletedMessages);
    }

    [Fact]
    public async Task Audit_verifies_missing_messages_and_does_not_infer_deletion_from_search_omission()
    {
        // Arrange
        await using var fixture = await SyncFixture.CreateAsync(1, selectSources: false);
        var source = new Source(7, 1, "Source", HistoryComplete: true);
        var first = SourceIndexer.ParseTrack(source, JsonSerializer.SerializeToElement(SyncTransport.Page(1, 0)).GetProperty("messages")[0]);
        var second = first with { Id = Guid.NewGuid(), Origin = first.Origin with { MessageId = 2 } };
        await fixture.Library.SavePageAsync(source, [first, second], Token);
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() == "getMessages"
            ? Td.Query("messages", ("messages", new object?[] { null, SyncTransport.Page(1, 0)["messages"]![0]!.DeepClone() })) : null);
        var indexer = new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session);

        // Act
        await indexer.ReconcilePageAsync(7, 1, true, Token);
        var beforeVerify = await fixture.Library.TrackAsync(7, second.Id, Token);
        await indexer.ReconcilePageAsync(7, 1, true, Token);

        // Assert
        Assert.True(beforeVerify!.Available); Assert.False((await fixture.Library.TrackAsync(7, second.Id, Token))!.Available);
        Assert.True((await fixture.Library.TrackAsync(7, first.Id, Token))!.Available);
        Assert.NotNull((await fixture.Library.SourceAsync(7, 1, Token))!.Sync!.AuditedAt);
    }

    [Fact]
    public async Task Live_reconciliation_handles_source_renames_and_waits_for_outgoing_message_confirmation()
    {
        // Arrange
        await using var fixture = await SyncFixture.CreateAsync(1, selectSources: false);
        var source = new Source(7, 1, "Old source", LatestMessageId: 1, HistoryComplete: true, Sync: new(AuditedAt: DateTimeOffset.UtcNow));
        await fixture.Library.SavePageAsync(source, [], Token);
        await fixture.Library.SavePageAsync(new(8, 1, "Other account"), [], Token);
        fixture.Transport.Handler = (q, _) => Task.FromResult<JsonObject?>(q["@type"]!.GetValue<string>() switch
        {
            "openChat" or "closeChat" => Td.Query("ok"),
            "getMessage" => SyncTransport.Page(q["message_id"]!.GetValue<long>(), 0)["messages"]![0]!.DeepClone().AsObject(),
            _ => null
        });
        var indexer = new SourceIndexer(fixture.Transport, fixture.Library, fixture.Session);
        await using var live = new SourceReconciler(fixture.Session, fixture.Library, indexer);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Changed += () => { if (live.Phase == "watching") ready.TrySetResult(); };
        live.Start(7); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        indexer.Changed += (_, _) => changed.TrySetResult();

        // Act
        fixture.Transport.Push(Td.Query("updateChatTitle", ("chat_id", 1L), ("title", "New source")));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var pending = SyncTransport.Page(99, 0)["messages"]![0]!.DeepClone().AsObject();
        pending["chat_id"] = 1; pending["sending_state"] = Td.Query("messageSendingStatePending");
        fixture.Transport.Push(Td.Query("updateNewMessage", ("message", pending)));
        var confirmed = SyncTransport.Page(2, 0)["messages"]![0]!.DeepClone().AsObject(); confirmed["chat_id"] = 1;
        fixture.Transport.Push(Td.Query("updateMessageSendSucceeded", ("old_message_id", 99L), ("message", confirmed)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!(await fixture.Library.SearchAsync(7, "", ct: deadline.Token)).Any(t => t.Origin.MessageId == 2))
            await Task.Delay(20, deadline.Token);

        // Assert
        Assert.Equal("New source", (await fixture.Library.SourceAsync(7, 1, Token))!.Name);
        Assert.Equal("Other account", (await fixture.Library.SourceAsync(8, 1, Token))!.Name);
        var tracks = await fixture.Library.SearchAsync(7, "", ct: Token);
        Assert.Equal(new long[] { 1, 2 }, tracks.Select(t => t.Origin.MessageId).Order());
        Assert.All(tracks, track => Assert.Equal("New source", track.Origin.SourceName));
        Assert.DoesNotContain(fixture.Transport.Requests, query => query["@type"]!.GetValue<string>() == "getMessage" && query["message_id"]!.GetValue<long>() == 99);
    }

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "channelwave-sync-" + Guid.NewGuid() + ".sqlite");
        public SqliteLibrary Library { get; }
        public SyncTransport Transport { get; }
        public TelegramSession Session { get; }
        private SyncFixture(int chats) { Library = new(path); Transport = new(chats); Session = new(Transport); }
        public ChannelSync NewSync() => new(Session, Library, new(Transport, Library, Session), new(YieldMilliseconds: 1, ContinuousUpdates: false));
        public static async Task<SyncFixture> CreateAsync(int chats, bool selectSources = true)
        {
            var fixture = new SyncFixture(chats); await fixture.Library.InitializeAsync(Token);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Session.Changed += () => { if (fixture.Session.AccountId == 7) ready.TrySetResult(); };
            await fixture.Session.ConnectAsync(new(1, new('0', 32), "fixture", "fixture", ""), Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (selectSources)
                for (var id = 1; id <= chats; id++) await fixture.Library.SavePageAsync(new(7, id, "Channel " + id), [], Token);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync(); await Library.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private sealed class SyncTransport(int chats) : ITdTransport
    {
        private readonly Channel<JsonElement> updates = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<JsonObject> Requests { get; } = new();
        public Func<JsonObject, CancellationToken, Task<JsonObject?>>? Handler { get; set; }
        public bool ProcessedSentinel => updates.Reader.Count == 0;
        public ChannelReader<JsonElement> Updates => updates.Reader;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Push(JsonObject json) => updates.Writer.TryWrite(JsonSerializer.SerializeToElement(json));
        public async Task<JsonElement> SendAsync(JsonObject query, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Enqueue(query);
            var custom = Handler is null ? null : await Handler(query, ct);
            if (custom is not null) return JsonSerializer.SerializeToElement(custom);
            JsonObject result;
            switch (query["@type"]!.GetValue<string>())
            {
                case "getOption": result = Td.Query("optionValueString", ("value", query["name"]!.GetValue<string>() == "version" ? "1.8.68" : Td.Revision)); break;
                case "getAuthorizationState": result = Td.Query("authorizationStateReady"); Push(Td.Query("updateAuthorizationState", ("authorization_state", result))); break;
                case "getMe": result = Td.Query("user", ("id", 7)); break;
                case "getChats": result = Td.Query("chats", ("chat_ids", Enumerable.Range(1, chats).Select(i => (long)i).Take(query["limit"]!.GetValue<int>()).ToArray())); break;
                case "loadChats": throw new TelegramException(404, "End of fixture list");
                case "getChat": result = Chat(query["chat_id"]!.GetValue<long>()); break;
                case "searchChatMessages": result = Page(1, 0); break;
                default: throw new InvalidOperationException("Unexpected fixture request");
            }
            return JsonSerializer.SerializeToElement(result);
        }
        public static JsonObject Chat(long id) => Td.Query("chat", ("id", id), ("title", "Channel " + id), ("type", Td.Query("chatTypeSupergroup", ("is_channel", true))));
        public static JsonObject Page(long message, long next) { var page = JsonNode.Parse("""{"@type":"foundChatMessages","next_from_message_id":0,"messages":[{"@type":"message","id":1,"date":1700000000,"can_be_saved":true,"content":{"@type":"messageAudio","audio":{"title":"Fixture","performer":"Artist","duration":1,"file_name":"fixture.wav","mime_type":"audio/wav","audio":{"id":1,"size":10,"remote":{"id":"fixture","unique_id":"fixture"}}}}}]}""")!.AsObject(); page["next_from_message_id"] = next; page["messages"]![0]!["id"] = message; return page; }
        public ValueTask DisposeAsync() { updates.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
