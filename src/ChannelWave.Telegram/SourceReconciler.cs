using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ChannelWave.Core;

namespace ChannelWave.Telegram;

/// <summary>Foreground-only live updates, reconnect catch-up and incremental history verification.</summary>
public sealed class SourceReconciler(TelegramSession session, ILibraryRepository library, SourceIndexer indexer) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<(long Chat, long Message), JsonElement> pending = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? cancellation;
    private Task runner = Task.CompletedTask;
    private HashSet<long> selected = [];
    private long account;
    public bool IsRunning => !runner.IsCompleted;
    public string Phase { get; private set; } = "watching";
    public string? Error { get; private set; }
    public event Action? Changed;

    public void Start(long owner)
    {
        if (IsRunning) { Refresh(); return; }
        account = owner; cancellation?.Dispose(); cancellation = new();
        session.Update += Receive;
        runner = RunAsync(cancellation.Token);
    }

    public void Refresh() => wake.Writer.TryWrite(true);

    private void Receive(JsonElement update)
    {
        var type = update.Type();
        if (type == "updateMessageSendSucceeded")
        {
            update = JsonSerializer.SerializeToElement(Td.Query("updateNewMessage", ("message", update.GetProperty("message"))));
            type = "updateNewMessage";
        }
        if (type is "updateSupergroup" or "updateBasicGroup") { Refresh(); return; }
        if (type == "updateNewMessage" && update.GetProperty("message").GetProperty("content").Type() != "messageAudio") return;
        if (type == "updateNewMessage" && update.GetProperty("message").TryGetProperty("sending_state", out var sending) && sending.ValueKind == JsonValueKind.Object) return;
        if (type is not ("updateNewMessage" or "updateMessageContent" or "updateMessageEdited" or "updateDeleteMessages" or "updateChatTitle")) return;
        var chat = type == "updateNewMessage" ? update.GetProperty("message").Id("chat_id") : update.Id("chat_id");
        if (!Volatile.Read(ref selected).Contains(chat)) return;
        if (type == "updateDeleteMessages")
        {
            if (update.Flag("from_cache")) return;
            foreach (var id in update.GetProperty("message_ids").EnumerateArray().Select(x => x.GetInt64()))
                pending[(chat, id)] = JsonSerializer.SerializeToElement(Td.Query("updateDeleteMessages", ("chat_id", chat),
                    ("message_ids", new[] { id }), ("is_permanent", update.Flag("is_permanent")), ("from_cache", false)));
        }
        else
        {
            var id = type == "updateChatTitle" ? 0 : type == "updateNewMessage" ? update.GetProperty("message").Id("id") : update.Id("message_id");
            // A confirmed deletion takes precedence over delayed content notifications.
            pending.AddOrUpdate((chat, id), update.Clone(), (_, previous) => previous.Type() == "updateDeleteMessages" && previous.Flag("is_permanent") ? previous : update.Clone());
        }
        Refresh();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var opened = new HashSet<long>();
        var checkedAt = new Dictionary<long, DateTimeOffset>();
        var retry = new Dictionary<long, DateTimeOffset>();
        try
        {
            while (!ct.IsCancellationRequested && session.AccountId == account)
            {
                var force = false;
                while (wake.Reader.TryRead(out _)) force = true;
                var sources = await library.SourcesAsync(account, ct);
                Volatile.Write(ref selected, sources.Select(s => s.ChatId).ToHashSet());
                foreach (var chat in opened.Where(chat => !selected.Contains(chat)).ToArray())
                {
                    await session.Transport.SendAsync(Td.Query("closeChat", ("chat_id", chat)), ct); opened.Remove(chat);
                }
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    if (session.AccountId != account) return;
                    var deadline = retry.GetValueOrDefault(source.ChatId);
                    if (source.Sync?.RetryAt is { } saved && saved > deadline) deadline = saved;
                    if (deadline > DateTimeOffset.UtcNow) continue;
                    try
                    {
                        await session.WaitForRequestsAsync(ct);
                        if (!opened.Contains(source.ChatId))
                        {
                            await session.Transport.SendAsync(Td.Query("openChat", ("chat_id", source.ChatId)), ct);
                            opened.Add(source.ChatId);
                        }
                        foreach (var change in pending.Where(x => x.Key.Chat == source.ChatId).Take(100))
                        {
                            Phase = "updating";
                            await indexer.ApplyUpdateAsync(account, change.Value, ct);
                            ((ICollection<KeyValuePair<(long Chat, long Message), JsonElement>>)pending).Remove(change);
                        }
                        if (!source.HistoryComplete) continue;
                        var current = await library.SourceAsync(account, source.ChatId, ct);
                        if (current is null) continue;
                        if (force || current.Sync?.CatchUpCursor > 0 || !checkedAt.TryGetValue(source.ChatId, out var last) || last < DateTimeOffset.UtcNow.AddSeconds(-60))
                        {
                            Phase = "catchingUp"; Changed?.Invoke();
                            await indexer.ReconcilePageAsync(account, source.ChatId, false, ct);
                            checkedAt[source.ChatId] = DateTimeOffset.UtcNow;
                        }
                        // Audit only after catch-up completes. It never downloads media.
                        current = await library.SourceAsync(account, source.ChatId, ct);
                        if (current is not null && current.Sync?.Inaccessible != true && current.Sync?.CatchUpCursor is not > 0 && (current.Sync?.AuditedAt is null || current.Sync.AuditedAt < DateTimeOffset.UtcNow.AddDays(-1)))
                        {
                            Phase = "auditing"; Changed?.Invoke();
                            await indexer.ReconcilePageAsync(account, source.ChatId, true, ct);
                        }
                        Error = null; retry.Remove(source.ChatId);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested || session.AccountId != account) { return; }
                    catch (Exception e)
                    {
                        TelegramMessageReader.DeferRateLimit(session, e);
                        Error = source.Name + ": " + Td.SafeError(e);
                        retry[source.ChatId] = session.RetryUntil ?? DateTimeOffset.UtcNow.AddSeconds(30);
                        if (session.AccountId == account && !ct.IsCancellationRequested)
                            await indexer.RecordFailureAsync(account, source.ChatId, Error, retry[source.ChatId], e is TelegramException { Code: 403 }, ct);
                        Phase = "waiting";
                    }
                }
                if (Error is null) Phase = "watching";
                Changed?.Invoke();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                try { await wake.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Error = Td.SafeError(e); Phase = "failed"; Changed?.Invoke(); }
        finally
        {
            session.Update -= Receive;
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var chat in opened)
            {
                if (session.AccountId != account) break;
                try { await session.Transport.SendAsync(Td.Query("closeChat", ("chat_id", chat)), closing.Token); }
                catch (Exception) { /* Closing a TDLib subscription is best effort during suspension. */ }
            }
        }
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel(); await runner;
        pending.Clear(); selected = [];
    }

    public async ValueTask DisposeAsync() { await StopAsync(); cancellation?.Dispose(); }
}
