using ChannelWave.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChannelWave.Telegram;

public sealed class TelegramSession(ITdTransport transport) : IAsyncDisposable
{
    private static readonly string[] ChatLists = ["chatListMain", "chatListArchive"];
    private readonly Dictionary<long, Source> knownChannels = new();
    private readonly Dictionary<long, JsonElement> chatDetails = new();
    private readonly object chatsLock = new();
    private readonly SemaphoreSlim discovery = new(1, 1);
    private readonly object retryLock = new();
    private DateTimeOffset retryUntil;
    public DateTimeOffset? RetryUntil { get { lock (retryLock) return retryUntil > DateTimeOffset.UtcNow ? retryUntil : null; } }
    public void DeferRequestsUntil(DateTimeOffset until) { lock (retryLock) if (until > retryUntil) retryUntil = until; }
    public async Task WaitForRequestsAsync(CancellationToken ct)
    {
        while (RetryUntil is { } until)
        {
            var wait = until - DateTimeOffset.UtcNow;
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), ct);
        }
    }
    private readonly HashSet<long> inspectedChats = [];
    private readonly int[] chatSnapshotLimits = [0, 0];
    private readonly bool[] chatListsExhausted = [false, false];
    private readonly bool[] snapshotsDrained = [false, false];
    private int nextChatList;
    private readonly CancellationTokenSource lifetime = new();
    private Task? updateLoop;
    private readonly object accountLock = new();
    private CancellationTokenSource? accountLookup;
    private Task accountInitialization = Task.CompletedTask;
    public bool IsResolvingAccount { get { lock (accountLock) return accountLookup is not null; } }
    private TelegramConfiguration? configuration;
    private readonly object parametersLock = new();
    private Task? parameterInitialization;
    private bool parametersApplied;
    private bool cleanupOnly;
    private TaskCompletionSource? cleanupReady;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AuthorizationPrompt Authorization { get; private set; } = new("idle", "Connect Telegram");
    public long? AccountId { get; private set; }
    public TelegramIdentity? Identity { get; private set; }
    public string ConnectionState { get; private set; } = "unknown";
    public string? ConnectionLabel => ConnectionState switch
    {
        "connectionStateWaitingForNetwork" => "Waiting for network…",
        "connectionStateConnectingToProxy" => "Connecting to proxy…",
        "connectionStateConnecting" => "Connecting…",
        "connectionStateUpdating" => "Updating…",
        _ => null
    };
    public string? Error { get; private set; }
    public string? Version { get; private set; }
    public event Action? Changed;
    public event Action<JsonElement>? Update;
    public ITdTransport Transport => transport;
    public MediaWorkCoordinator MediaWork { get; } = new();

    public async Task<string> ProbeAsync(CancellationToken ct = default)
    {
        await transport.StartAsync(ct);
        var version = await transport.SendAsync(Td.Query("getOption", ("name", "version")), ct);
        var commit = await transport.SendAsync(Td.Query("getOption", ("name", "commit_hash")), ct);
        Version = version.Text("value");
        if (!string.Equals(commit.Text("value"), Td.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException($"TDLib source mismatch. Build the pinned revision {Td.Revision}; authentication is disabled for this binary.");
        Changed?.Invoke();
        return $"TDLib {Version}; pinned commit verified";
    }

    public async Task ConnectAsync(TelegramConfiguration config, CancellationToken ct = default)
    {
        if (cleanupOnly) throw new InvalidOperationException("Finish sign-out cleanup and reopen ChannelWave before connecting.");
        config.Validate();
        await ProbeAsync(ct);
        configuration = config;
        updateLoop ??= ProcessUpdatesAsync();
        // TDLib emits the initial authorization state when the client is created.
        await transport.SendAsync(Td.Query("getAuthorizationState"), ct);
    }

    /// <summary>Opens a saved TDLib profile only to finish an interrupted sign-out.</summary>
    public async Task<bool> RecoverLogOutAsync(TelegramConfiguration config, CancellationToken ct = default)
    {
        lock (accountLock)
        {
            cleanupOnly = true;
            accountLookup?.Cancel();
            AccountId = null; Identity = null;
        }
        if (closed.Task.IsCompleted) return false;
        config.Validate();
        await ProbeAsync(ct);
        configuration = config;
        cleanupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updateLoop ??= ProcessUpdatesAsync();
        using var initialization = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initialization.CancelAfter(TimeSpan.FromSeconds(45));
        var state = await transport.SendAsync(Td.Query("getAuthorizationState"), initialization.Token);
        // A retry may reuse an already initialized client, which need not emit a new update.
        if (state.Type() == "authorizationStateWaitTdlibParameters") await SetParametersAsync(config).WaitAsync(initialization.Token);
        else cleanupReady.TrySetResult();
        await cleanupReady.Task.WaitAsync(initialization.Token);
        if (closed.Task.IsCompleted) return false;
        if (Authorization.State is "closing" or "loggingOut" || state.Type() is "authorizationStateClosing" or "authorizationStateLoggingOut")
        {
            await closed.Task.WaitAsync(initialization.Token);
            return false;
        }
        return await LogOutAsync(ct);
    }

    private Task SetParametersAsync(TelegramConfiguration config)
    {
        lock (parametersLock)
        {
            if (parameterInitialization is null || parameterInitialization.IsFaulted || parameterInitialization.IsCanceled)
                parameterInitialization = InitializeParametersAsync(config);
            return parameterInitialization;
        }
    }

    private async Task InitializeParametersAsync(TelegramConfiguration c)
    {
        if (!parametersApplied)
        {
            await transport.SendAsync(Td.Query("setTdlibParameters", ("use_test_dc", false),
                ("database_directory", c.DatabaseDirectory), ("files_directory", c.FilesDirectory),
                ("database_encryption_key", c.DatabaseEncryptionKey), ("use_file_database", true),
                ("use_chat_info_database", true), ("use_message_database", true), ("use_secret_chats", false),
                ("api_id", c.ApiId), ("api_hash", c.ApiHash), ("system_language_code", "en"),
                ("device_model", "ChannelWave"), ("system_version", "1"), ("application_version", "0.1.0")), lifetime.Token);
            parametersApplied = true;
        }
        if (c.HttpProxy is { } proxy)
        {
            // addProxy is available after TDLib parameters, before account authorization.
            await transport.SendAsync(Td.Query("addProxy",
                ("proxy", Td.Query("proxy", ("server", proxy.Server), ("port", proxy.Port),
                    ("type", Td.Query("proxyTypeHttp", ("username", ""), ("password", ""), ("http_only", false))))),
                ("enable", true), ("comment", "ChannelWave configured HTTP proxy")), lifetime.Token);
        }
    }

    private async Task ProcessUpdatesAsync()
    {
        try
        {
            await foreach (var update in transport.Updates.ReadAllAsync(lifetime.Token))
            {
                try
                {
                    if (cleanupOnly && update.Type() != "updateAuthorizationState") continue;
                    if (update.Type() == "updateConnectionState")
                    {
                        ConnectionState = update.GetProperty("state").Type();
                        Changed?.Invoke();
                    }
                    if (update.Type() == "updateUser" && AccountId is { } identityAccount
                        && update.GetProperty("user").Id("id") == identityAccount)
                    {
                        Identity = TelegramIdentity.From(update.GetProperty("user"));
                        Changed?.Invoke();
                    }
                    if (update.Type() == "updateNewChat")
                    {
                        var chat = update.GetProperty("chat");
                        lock (chatsLock) chatDetails[chat.Id("id")] = chat.Clone();
                    }
                    if (update.Type() == "updateChatTitle")
                    {
                        var id = update.Id("chat_id");
                        lock (chatsLock)
                        {
                            chatDetails.Remove(id);
                            if (knownChannels.TryGetValue(id, out var channel)) knownChannels[id] = channel with { Name = update.Text("title") };
                        }
                    }
                    if (update.Type() == "updateAuthorizationState")
                    {
                        var state = update.GetProperty("authorization_state");
                        lock (accountLock)
                        {
                            accountLookup?.Cancel();
                            Authorization = AuthorizationPrompt.From(state); Error = null;
                            if (Authorization.State != "ready") { AccountId = null; Identity = null; }
                        }
                        // Publish ready only after identity resolution owns its pending state.
                        // Otherwise the UI can expose Retry while the automatic lookup is still starting.
                        if (Authorization.State == "ready" && !cleanupOnly) _ = RetryAccountInitializationAsync();
                        Changed?.Invoke();
                        if (Authorization.State == "parameters" && configuration is { } c)
                        {
                            try
                            {
                                await SetParametersAsync(c);
                            }
                            catch (Exception e) { Error = Td.SafeError(e); cleanupReady?.TrySetException(e); }
                        }
                        if (Authorization.State != "parameters") cleanupReady?.TrySetResult();
                        if (Authorization.State == "closed") closed.TrySetResult();
                        Changed?.Invoke();
                    }
                    if (!cleanupOnly) Update?.Invoke(update);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception e) { Error = Td.SafeError(e); Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { Error = Td.SafeError(e); Changed?.Invoke(); }
    }

    public Task RetryAccountInitializationAsync()
    {
        lock (accountLock)
        {
            if (cleanupOnly || Authorization.State != "ready" || AccountId is not null) return Task.CompletedTask;
            if (accountLookup is { IsCancellationRequested: false }) return accountInitialization;
            var lookup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            accountLookup = lookup;
            accountInitialization = ResolveAccountAsync(lookup);
            return accountInitialization;
        }
    }

    private async Task ResolveAccountAsync(CancellationTokenSource lookup)
    {
        var ct = lookup.Token;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await WaitForRequestsAsync(ct);
                    var me = await transport.SendAsync(Td.Query("getMe"), ct);
                    var account = me.Id("id");
                    if (account <= 0) throw new InvalidDataException("Telegram returned an invalid account identity.");
                    lock (accountLock)
                    {
                        if (ct.IsCancellationRequested || !ReferenceEquals(accountLookup, lookup) || Authorization.State != "ready") return;
                        AccountId = account; Identity = TelegramIdentity.From(me); Error = null;
                    }
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    lock (accountLock)
                    {
                        if (ct.IsCancellationRequested || !ReferenceEquals(accountLookup, lookup)) return;
                        Error = Td.SafeError(e);
                    }
                    if (e is TelegramException { Code: 429 } rate)
                        DeferRequestsUntil(DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, rate.RetryAfter ?? 30)));
                    if (attempt == 2 || e is not (IOException or TimeoutException or OperationCanceledException or TelegramException { Code: 429 or >= 500 })) return;
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            lock (accountLock)
            {
                if (ReferenceEquals(accountLookup, lookup)) accountLookup = null;
                lookup.Dispose();
            }
            Changed?.Invoke();
        }
    }

    public async Task SubmitAsync(string value, CancellationToken ct = default)
    {
        JsonObject query = Authorization.State switch
        {
            "phone" => Td.Query("setAuthenticationPhoneNumber", ("phone_number", value), ("settings", null)),
            "code" => Td.Query("checkAuthenticationCode", ("code", value)),
            "password" => Td.Query("checkAuthenticationPassword", ("password", value)),
            "email" => Td.Query("setAuthenticationEmailAddress", ("email_address", value)),
            "emailCode" => Td.Query("checkAuthenticationEmailCode", ("code", Td.Query("emailAddressAuthenticationCode", ("code", value)))),
            _ => throw new InvalidOperationException("Telegram is not requesting input in this state.")
        };
        await transport.SendAsync(query, ct);
    }
    public Task<JsonElement> ResendAsync(CancellationToken ct = default) => transport.SendAsync(Td.Query("resendAuthenticationCode", ("reason", Td.Query("resendCodeReasonUserRequest"))), ct);
    public Task<JsonElement> RequestQrAsync(CancellationToken ct = default) => transport.SendAsync(Td.Query("requestQrCodeAuthentication", ("other_user_ids", Array.Empty<long>())), ct);

    /// <returns>True only when Telegram confirmed remote logout and final closure.</returns>
    public async Task<bool> LogOutAsync(CancellationToken ct = default)
    {
        lock (accountLock) accountLookup?.Cancel();
        var remoteConfirmed = false;
        using var remoteTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        remoteTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await transport.SendAsync(Td.Query("logOut"), remoteTimeout.Token);
            await closed.Task.WaitAsync(remoteTimeout.Token);
            remoteConfirmed = true;
        }
        catch (Exception e) when (e is TelegramException or OperationCanceledException or IOException)
        {
            // destroy deletes the local session without claiming remote revocation.
            using var localTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (!closed.Task.IsCompleted) await transport.SendAsync(Td.Query("destroy"), localTimeout.Token);
            await closed.Task.WaitAsync(localTimeout.Token);
        }
        AccountId = null; Identity = null; ConnectionState = "unknown"; configuration = null;
        lock (chatsLock) { knownChannels.Clear(); chatDetails.Clear(); inspectedChats.Clear(); }
        Array.Clear(chatSnapshotLimits); Array.Clear(chatListsExhausted); Array.Clear(snapshotsDrained); nextChatList = 0;
        return remoteConfirmed;
    }

    public async Task<ChannelLoadResult> LoadChannelsAsync(CancellationToken ct = default)
    {
        await WaitForRequestsAsync(ct);
        await discovery.WaitAsync(ct);
        try
        {
            var account = AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            var listIndex = nextChatList;
            if (chatListsExhausted[listIndex] && snapshotsDrained[listIndex]
                && !(chatListsExhausted[1 - listIndex] && snapshotsDrained[1 - listIndex])) listIndex = 1 - listIndex;
            nextChatList = 1 - listIndex;
            var listName = ChatLists[listIndex];
            if (chatListsExhausted[listIndex] && snapshotsDrained[listIndex])
            {
                lock (chatsLock) return new(knownChannels.Values.OrderBy(x => x.Name).ToArray(),
                    listIndex == 0 ? "Main" : "Archive", 0, true, false);
            }
            var list = Td.Query(listName);
            var scanned = await ScanLoadedChatsAsync(account, list, listIndex, ct);
            var timedOut = false;
            if (!chatListsExhausted[listIndex])
            {
                await WaitForRequestsAsync(ct);
                try { await transport.SendAsync(Td.Query("loadChats", ("chat_list", list), ("limit", 40)), ct); }
                catch (TelegramException e) when (e.Code == 404) { chatListsExhausted[listIndex] = true; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
                if (!timedOut)
                {
                    scanned += await ScanLoadedChatsAsync(account, list, listIndex, ct);
                }
            }
            lock (chatsLock)
                return new(knownChannels.Values.OrderBy(x => x.Name).ToArray(),
                    listIndex == 0 ? "Main" : "Archive", scanned, chatListsExhausted[listIndex] && snapshotsDrained[listIndex], timedOut);
        }
        finally { discovery.Release(); }
    }

    public async Task RestartDiscoveryAsync(IEnumerable<Source> saved, CancellationToken ct = default)
    {
        await discovery.WaitAsync(ct);
        try
        {
            Array.Clear(chatSnapshotLimits); Array.Clear(chatListsExhausted); Array.Clear(snapshotsDrained); nextChatList = 0;
            lock (chatsLock)
                foreach (var source in saved.Where(x => x.AccountId == AccountId))
                {
                    knownChannels.TryAdd(source.ChatId, source);
                    inspectedChats.Add(source.ChatId);
                }
        }
        finally { discovery.Release(); }
    }

    private async Task<int> ScanLoadedChatsAsync(long account, JsonObject list, int listIndex, CancellationToken ct)
    {
        // Once loading ends, drain the remaining snapshot geometrically instead of stopping at the first 404.
        chatSnapshotLimits[listIndex] = (int)Math.Min(int.MaxValue, chatListsExhausted[listIndex]
            ? Math.Max(40L, (long)chatSnapshotLimits[listIndex] * 2) : (long)chatSnapshotLimits[listIndex] + 40);
        JsonElement chats;
        await WaitForRequestsAsync(ct);
        try { chats = await transport.SendAsync(Td.Query("getChats", ("chat_list", list), ("limit", chatSnapshotLimits[listIndex])), ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Reading Telegram's loaded chats timed out. Check the connection and retry.");
        }
        var scanned = 0;
        snapshotsDrained[listIndex] = chats.GetProperty("chat_ids").GetArrayLength() < chatSnapshotLimits[listIndex];
        foreach (var id in chats.GetProperty("chat_ids").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var chatId = id.GetInt64();
            lock (chatsLock) if (inspectedChats.Contains(chatId)) continue;
            JsonElement chat;
            try
            {
                bool cached;
                lock (chatsLock) cached = chatDetails.TryGetValue(chatId, out chat);
                if (!cached) { await WaitForRequestsAsync(ct); chat = await transport.SendAsync(Td.Query("getChat", ("chat_id", chatId)), ct); }
            }
            catch (TelegramException e) when (e.Code == 404) { lock (chatsLock) inspectedChats.Add(chatId); continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Reading a loaded Telegram chat timed out. Check the connection and retry.");
            }
            lock (chatsLock) inspectedChats.Add(chatId);
            scanned++;
            var type = chat.GetProperty("type");
            if (type.Type() is "chatTypePrivate" or "chatTypeBasicGroup" or "chatTypeSupergroup")
                lock (chatsLock) knownChannels[chatId] = new(account, chatId, chat.Text("title"));
        }
        return scanned;
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (updateLoop is not null) await updateLoop;
        await accountInitialization;
        await transport.DisposeAsync(); lifetime.Dispose();
    }
}

public sealed record ChannelLoadResult(IReadOnlyList<Source> Channels, string ListName, int ChatsScanned, bool ListExhausted, bool TimedOut);
