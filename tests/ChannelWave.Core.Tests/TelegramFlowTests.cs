using ChannelWave.Core;
using ChannelWave.Telegram;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class TelegramFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Account_lookup_retries_without_stopping_file_or_closed_updates()
    {
        // Arrange
        var transport = new ScriptedTransport();
        var attempts = 0;
        transport.Override = query =>
        {
            if (query["@type"]!.GetValue<string>() == "getMe" && Interlocked.Increment(ref attempts) == 1)
                throw new TelegramException(500, "Synthetic account lookup failure");
            return null;
        };
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);
        var fileReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Update += update => { if (update.Type() == "updateFile") fileReceived.TrySetResult(); };

        // Act: a pending identity lookup must not hold up other updates.
        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
        transport.Push(Td.Query("updateFile", ("file", ScriptedTransport.File(true))));
        await fileReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await WaitUntil(() => session.AccountId == 7);

        // Assert
        Assert.Equal(2, attempts);
        Assert.Null(session.Error);
        Assert.True(await session.LogOutAsync(Token));
        Assert.Null(session.AccountId);
        Assert.Equal("closed", session.Authorization.State);
    }

    [Fact]
    public async Task Account_lookup_exhaustion_can_be_retried_explicitly()
    {
        // Arrange
        var transport = new ScriptedTransport();
        var unavailable = true;
        var attempts = 0;
        transport.Override = query =>
        {
            if (query["@type"]!.GetValue<string>() == "getMe")
            {
                Interlocked.Increment(ref attempts);
                if (unavailable) throw new TelegramException(500, "Synthetic account lookup failure");
            }
            return null;
        };
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);

        // Act
        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
        await WaitUntil(() => attempts == 3 && !session.IsResolvingAccount);

        // Assert, then retry against a recovered transport.
        Assert.Null(session.AccountId);
        Assert.Contains("Synthetic account lookup failure", session.Error);
        unavailable = false;
        await session.RetryAccountInitializationAsync();
        Assert.Equal(7, session.AccountId);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task Late_account_lookup_cannot_restore_identity_after_closure()
    {
        // Arrange: the transport deliberately ignores cancellation.
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport();
        transport.AsyncOverride = (query, _) =>
        {
            if (query["@type"]!.GetValue<string>() != "getMe") return null;
            requested.TrySetResult();
            return response.Task;
        };
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);
        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var pending = session.RetryAccountInitializationAsync();

        // Act
        await SetState(session, transport, "authorizationStateClosed", "closed");
        response.SetResult(JsonSerializer.SerializeToElement(Td.Query("user", ("id", 7))));
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.Null(session.AccountId);
        Assert.Equal("closed", session.Authorization.State);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Malformed_update_does_not_prevent_later_valid_updates()
    {
        // Arrange
        var transport = new ScriptedTransport();
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Update += update => { if (update.Type() == "updateFile") delivered.TrySetResult(); };

        // Act
        transport.Push(Td.Query("updateNewChat"));
        transport.Push(Td.Query("updateFile", ("file", ScriptedTransport.File(true))));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert: subsequent authorization and closure still work too.
        await Ready(session, transport);
        Assert.True(await session.LogOutAsync(Token));
    }

    [Fact]
    public async Task Superseded_account_lookup_cannot_overwrite_a_new_identity()
    {
        // Arrange
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var transport = new ScriptedTransport();
        transport.AsyncOverride = (query, _) => query["@type"]!.GetValue<string>() == "getMe" && Interlocked.Increment(ref attempts) == 1 ? response.Task : null;
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);
        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
        await WaitUntil(() => attempts == 1);
        var superseded = session.RetryAccountInitializationAsync();

        // Act
        await Ready(session, transport);
        response.SetResult(JsonSerializer.SerializeToElement(Td.Query("user", ("id", 8))));
        await superseded.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.Equal(7, session.AccountId);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task Schema_mismatch_blocks_authentication()
    {
        var transport = new ScriptedTransport { WrongRevision = true }; await using var session = new TelegramSession(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Config(), Token));
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() == "setTdlibParameters");
    }

    [Fact]
    public async Task Login_responds_to_parameters_phone_code_password_and_email_states()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token);
        await SetState(session, transport, "authorizationStateWaitPhoneNumber", "phone");
        await session.SubmitAsync("+10000000000", Token);
        Assert.Contains(transport.Requests, q => q["@type"]!.GetValue<string>() == "setAuthenticationPhoneNumber");
        await SetState(session, transport, "authorizationStateWaitPassword", "password");
        await session.SubmitAsync("synthetic-password", Token);
        Assert.Contains(transport.Requests, q => q["@type"]!.GetValue<string>() == "checkAuthenticationPassword");
        await SetState(session, transport, "authorizationStateWaitEmailAddress", "email");
        await session.SubmitAsync("test@example.invalid", Token);
        Assert.Contains(transport.Requests, q => q["@type"]!.GetValue<string>() == "setAuthenticationEmailAddress");
        await Ready(session, transport); Assert.Equal(7, session.AccountId);
        var parameters = Assert.Single(transport.Requests, q => q["@type"]!.GetValue<string>() == "setTdlibParameters");
        Assert.True(parameters["use_message_database"]!.GetValue<bool>()); Assert.False(parameters["use_secret_chats"]!.GetValue<bool>());
        Assert.False(parameters.ContainsKey("parameters"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_distinguishes_remote_revocation_from_local_destruction(bool offline)
    {
        var transport = new ScriptedTransport { OfflineLogout = offline }; await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);
        Assert.Equal(!offline, await session.LogOutAsync(Token)); Assert.Null(session.AccountId);
        Assert.Equal("closed", session.Authorization.State);
        Assert.Equal(offline, transport.Requests.Any(q => q["@type"]!.GetValue<string>() == "destroy"));
    }

    [Fact]
    public async Task Download_waits_for_completion_update_and_resolves_current_file_id()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);
        var downloads = new TelegramDownloads(session); var progress = new List<long>(); downloads.Progress += (done, _) => progress.Add(done);
        var task = downloads.CompleteAsync(Fixture.Track(new(7, -1001, "Test"), 1), Token);
        await transport.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(task.IsCompleted);
        transport.Push(Td.Query("updateFile", ("file", ScriptedTransport.File(true))));
        var complete = await task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(complete.GetProperty("local").Flag("is_downloading_completed"));
        Assert.Equal(555, Assert.Single(transport.Requests, q => q["@type"]!.GetValue<string>() == "downloadFile")["file_id"]!.GetValue<int>());
        Assert.Contains(100L, progress);
    }

    [Fact]
    public async Task Download_reuses_completed_file_after_rechecking_message_permissions()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);
        transport.Override = query => query["@type"]!.GetValue<string>() == "getMessage"
            ? Td.Query("message", ("can_be_saved", !transport.ProtectedContent), ("content", Td.Query("messageAudio", ("audio", Td.Query("audio", ("audio", ScriptedTransport.File(true))))))) : null;
        var downloads = new TelegramDownloads(session);
        var track = Fixture.Track(new(7, -1001, "Test"), 1);
        Assert.True((await downloads.CompleteAsync(track, Token)).GetProperty("local").Flag("is_downloading_completed"));
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() == "downloadFile");
        transport.ProtectedContent = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloads.CompleteAsync(track, Token));
    }

    [Fact]
    public async Task Download_cancellation_reaches_Telegram_and_protected_content_never_downloads()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);
        var downloads = new TelegramDownloads(session); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var track = Fixture.Track(new(7, -1001, "Test"), 1);
        var pending = downloads.CompleteAsync(track, cancellation.Token);
        await transport.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Contains(transport.Requests, q => q["@type"]!.GetValue<string>() == "cancelDownloadFile");
        transport.ProtectedContent = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloads.CompleteAsync(track, Token));
        Assert.Single(transport.Requests, q => q["@type"]!.GetValue<string>() == "downloadFile");
    }

    [Fact]
    public async Task Stream_resolution_rechecks_account_and_current_message_permissions_before_any_file_request()
    {
        var transport = new ScriptedTransport(); await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);
        var downloads = new TelegramDownloads(session); var track = Fixture.Track(new(7, -1001, "Test"), 1);
        Assert.Equal(555, (await downloads.ResolveAsync(track, Token)).GetProperty("id").GetInt32());
        transport.ProtectedContent = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloads.ResolveAsync(track, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloads.ResolveAsync(track with { Origin = track.Origin with { AccountId = 8 } }, Token));
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() is "downloadFile" or "readFile");
    }

    [Fact]
    public async Task Channel_loading_keeps_cached_results_when_a_page_times_out_and_reaches_archive()
    {
        var transport = new ScriptedTransport();
        transport.Override = query =>
        {
            var method = query["@type"]!.GetValue<string>();
            if (method == "loadChats")
            {
                if (query["chat_list"]!["@type"]!.GetValue<string>() == "chatListMain") throw new OperationCanceledException("Simulated transport deadline");
                throw new TelegramException(404, "No more archive chats");
            }
            if (method == "getChats")
            {
                var isMain = query["chat_list"]!["@type"]!.GetValue<string>() == "chatListMain";
                var ids = isMain ? Enumerable.Range(1000, 75).Select(x => (long)x) : [2000L];
                return Td.Query("chats", ("chat_ids", ids.Take(query["limit"]!.GetValue<int>()).ToArray()));
            }
            if (method == "getChat")
            {
                var id = query["chat_id"]!.GetValue<long>();
                var isChannel = id is 1010 or 1060 or 2000;
                return Td.Query("chat", ("id", id), ("title", $"Channel {id}"),
                    ("type", Td.Query(isChannel ? "chatTypeSupergroup" : "chatTypePrivate", ("is_channel", isChannel))));
            }
            return null;
        };
        await using var session = new TelegramSession(transport);
        await session.ConnectAsync(Config(), Token); await Ready(session, transport);

        var main = await session.LoadChannelsAsync(Token);
        Assert.True(main.TimedOut);
        Assert.Equal("Main", main.ListName);
        Assert.Equal(40, main.Channels.Count);

        var archive = await session.LoadChannelsAsync(Token);
        Assert.False(archive.TimedOut);
        Assert.True(archive.ListExhausted);
        Assert.Equal(41, archive.Channels.Count);

        var moreMain = await session.LoadChannelsAsync(Token);
        Assert.True(moreMain.TimedOut);
        Assert.Equal(76, moreMain.Channels.Count);
        Assert.Equal(76, transport.Requests.Count(q => q["@type"]!.GetValue<string>() == "getChat"));
        Assert.All(transport.Requests.Where(q => q["@type"]!.GetValue<string>() == "loadChats"),
            q => Assert.Equal(40, q["limit"]!.GetValue<int>()));
        Assert.All(transport.Requests.Where(q => q["@type"]!.GetValue<string>() == "getChats"),
            q => Assert.InRange(q["limit"]!.GetValue<int>(), 1, 80));
    }

    private static TelegramConfiguration Config() => new(1, new string('0', 32), "test-only", "test-only", "");
    private static async Task SetState(TelegramSession session, ScriptedTransport transport, string type, string state)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (session.Authorization.State == state) changed.TrySetResult(); }
        session.Changed += OnChanged;
        try { transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query(type)))); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { session.Changed -= OnChanged; }
    }
    private static async Task Ready(TelegramSession session, ScriptedTransport transport)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (session.AccountId == 7) ready.TrySetResult(); }
        session.Changed += Changed;
        try { transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady")))); await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); }
        finally { session.Changed -= Changed; }
    }
}

internal sealed class ScriptedTransport : ITdTransport
{
    private readonly Channel<JsonElement> updates = Channel.CreateUnbounded<JsonElement>();
    public ConcurrentQueue<JsonObject> Requests { get; } = new();
    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool WrongRevision { get; init; }
    public bool OfflineLogout { get; init; }
    public bool ProtectedContent { get; set; }
    public Func<JsonObject, JsonObject?>? Override { get; set; }
    public Func<JsonObject, CancellationToken, Task<JsonElement>?>? AsyncOverride { get; set; }
    public ChannelReader<JsonElement> Updates => updates.Reader;
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Push(JsonObject json) => updates.Writer.TryWrite(JsonSerializer.SerializeToElement(json));
    public Task<JsonElement> SendAsync(JsonObject query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Requests.Enqueue(query);
        if (AsyncOverride?.Invoke(query, ct) is { } pending) return pending;
        if (Override?.Invoke(query) is { } custom) return Task.FromResult(JsonSerializer.SerializeToElement(custom));
        JsonObject result;
        switch (query["@type"]!.GetValue<string>())
        {
            case "getOption": result = Td.Query("optionValueString", ("value", query["name"]!.GetValue<string>() == "version" ? "1.8.68" : WrongRevision ? "wrong" : Td.Revision)); break;
            case "getAuthorizationState": result = Td.Query("authorizationStateWaitTdlibParameters"); Push(Td.Query("updateAuthorizationState", ("authorization_state", result))); break;
            case "getMe": result = Td.Query("user", ("id", 7)); break;
            case "logOut" when OfflineLogout: throw new TelegramException(500, "Synthetic offline failure");
            case "logOut": case "destroy": Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateClosed")))); result = Td.Query("ok"); break;
            case "getMessage": result = Td.Query("message", ("can_be_saved", !ProtectedContent), ("content", Td.Query("messageAudio", ("audio", Td.Query("audio", ("audio", File(false))))))); break;
            case "downloadFile": DownloadStarted.TrySetResult(); result = File(false); break;
            default: result = Td.Query("ok"); break;
        }
        return Task.FromResult(JsonSerializer.SerializeToElement(result));
    }
    public static JsonObject File(bool complete) => Td.Query("file", ("id", 555), ("size", 100), ("expected_size", 100), ("local", Td.Query("localFile", ("is_downloading_completed", complete), ("downloaded_size", complete ? 100 : 0), ("path", "/synthetic-test.mp3"))));
    public ValueTask DisposeAsync() { updates.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
