using ChannelWave.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed record ChannelSyncProgress(string Phase = "idle", int Found = 0, int Completed = 0,
    int Pages = 0, int Failed = 0, string? Current = null, DateTimeOffset? RetryAt = null, string? Error = null);
public sealed record ChannelSyncOptions(int ParallelChannels = 2, int MaxRetries = 4, int YieldMilliseconds = 150, bool ContinuousUpdates = true);

/// <summary>Resumable audio history indexing for explicitly selected sources only.</summary>
public sealed class ChannelSync(TelegramSession session, ILibraryRepository library, SourceIndexer indexer,
    ChannelSyncOptions? options = null) : IAsyncDisposable
{
    private const string StateKey = "selected-audio-sync-v1";
    private readonly ChannelSyncOptions settings = options ?? new();
    private readonly SourceReconciler live = new(session, library, indexer);
    private bool observing;
    private string? historyError;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object jobLock = new();
    private readonly ConcurrentQueue<Source> additions = new();
    private bool accepting;
    private CancellationTokenSource? cancellation;
    private Task runner = Task.CompletedTask;
    private long? loadedAccount;
    private Checkpoint checkpoint = new(false);
    public ChannelSyncProgress Progress { get; private set; } = new();
    public bool IsRunning => !runner.IsCompleted || Progress.Phase == "starting" || live.IsRunning;
    public bool IsWatching => live.IsRunning;
    public event Action? Changed;
    public event Action<long, long>? SourceReconciled;
    public Task Completion => runner;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var account = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (loadedAccount == account)
            {
                if (checkpoint.Enabled && !IsRunning) await StartCoreAsync(account, ct);
                return;
            }
            await live.StopAsync();
            await LoadAsync(account, ct);
            if (checkpoint.Enabled) await StartCoreAsync(account, ct);
        }
        finally { lifecycle.Release(); }
    }

    public async Task SelectAsync(Source source, CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var account = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (source.AccountId != account) throw new InvalidOperationException("Cross-account source selection rejected.");
            if (loadedAccount != account) await LoadAsync(account, ct);
            var selected = await indexer.SelectAsync(source, ct);
            lock (jobLock) additions.Enqueue(selected);
            bool active;
            lock (jobLock) active = accepting;
            if (!active) { await runner; await StartCoreAsync(account, ct); }
            live.Refresh();
        }
        finally { lifecycle.Release(); }
    }

    public async Task<Guid[]> UnindexAsync(Source source, CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var account = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (source.AccountId != account) throw new InvalidOperationException("Cross-account source removal rejected.");
            if (loadedAccount != account) await LoadAsync(account, ct);
            var resume = IsRunning;
            await live.StopAsync();
            cancellation?.Cancel(); await runner;
            // Every selected source is already persisted. Rebuild the queue after removal,
            // so neither an old page nor a queued addition can resurrect this channel.
            additions.Clear();
            checkpoint = checkpoint with { Enabled = false }; await SaveAsync(account, CancellationToken.None);
            var removed = await indexer.UnindexAsync(account, source.ChatId, ct);
            var remaining = await library.SourcesAsync(account, CancellationToken.None);
            var pending = remaining.Any(x => !x.HistoryComplete);
            Publish(new(pending ? "paused" : remaining.Count == 0 ? "idle" : "completed", remaining.Count, remaining.Count(x => x.HistoryComplete)));
            if (resume && (pending || settings.ContinuousUpdates && remaining.Count > 0)) await StartCoreAsync(account, CancellationToken.None);
            return removed;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (IsRunning)
            {
                if (!runner.IsCompleted || session.AccountId is not { } owner || !(await library.SourcesAsync(owner, ct)).Any(s => !s.HistoryComplete))
                { live.Refresh(); return; }
            }
            var account = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (loadedAccount != account) await LoadAsync(account, ct);
            await StartCoreAsync(account, ct);
        }
        finally { lifecycle.Release(); }
    }

    private async Task LoadAsync(long account, CancellationToken ct)
    {
        await library.InitializeAsync(ct);
        var stored = await library.ReadStateAsync(account, StateKey, ct);
        checkpoint = stored is null ? new(false) : JsonSerializer.Deserialize<Checkpoint>(stored) ?? new(false);
        loadedAccount = account;
        var selected = await library.SourcesAsync(account, ct);
        Publish(new(selected.Any(x => !x.HistoryComplete) ? "paused" : "idle", selected.Count, selected.Count(x => x.HistoryComplete)));
    }

    private async Task StartCoreAsync(long account, CancellationToken ct)
    {
        historyError = null;
        if (!observing)
        {
            observing = true;
            indexer.Changed += SourceChanged;
            live.Changed += LiveChanged;
        }
        checkpoint = checkpoint with { Enabled = true }; await SaveAsync(account, ct);
        cancellation?.Dispose(); cancellation = new();
        lock (jobLock) accepting = true;
        Progress = new("starting"); runner = RunAsync(account, cancellation.Token); Changed?.Invoke();
        if (settings.ContinuousUpdates) live.Start(account);
    }

    private void SourceChanged(long account, long chat)
    {
        if (account == loadedAccount) { Progress = Progress with { Pages = Progress.Pages + 1 }; SourceReconciled?.Invoke(account, chat); Changed?.Invoke(); }
    }
    private void LiveChanged()
    {
        if (runner.IsCompleted) Publish(Progress with { Phase = Progress.Phase == "failed" && historyError is not null ? "failed" : live.Phase,
            Error = live.Error ?? historyError, RetryAt = session.RetryUntil });
    }

    public async Task PauseAsync(bool preserveResume = false)
    {
        await lifecycle.WaitAsync();
        try
        {
            cancellation?.Cancel(); await runner;
            await live.StopAsync();
            if (!preserveResume && loadedAccount is { } account)
            {
                checkpoint = checkpoint with { Enabled = false }; await SaveAsync(account, CancellationToken.None);
            }
            if (settings.ContinuousUpdates || Progress.Phase is not "idle" and not "completed" and not "failed") Publish(Progress with { Phase = "paused", Current = null });
        }
        finally { lifecycle.Release(); }
    }

    private async Task RunAsync(long account, CancellationToken ct)
    {
        var known = new Dictionary<long, Source>();
        var queue = new Queue<long>(); var queued = new HashSet<long>(); var complete = new HashSet<long>();
        var failed = new Dictionary<long, string>(); var retries = new Dictionary<long, int>();
        var retryTimes = new Dictionary<long, DateTimeOffset>();
        var pages = 0; string? lastError = null;
        try
        {
            foreach (var selected in await library.SourcesAsync(account, ct)) Add(selected);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (session.AccountId != account) throw new OperationCanceledException(ct);
                while (additions.TryDequeue(out var selected)) Add(selected);
                if (queue.Count == 0)
                {
                    lock (jobLock)
                    {
                        if (!additions.IsEmpty) continue;
                        accepting = false;
                    }
                    break;
                }
                if (checkpoint.RetryAt is { } savedDeadline) session.DeferRequestsUntil(savedDeadline);
                if (session.RetryUntil is { } until)
                {
                    var wait = until - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) { Report("waiting", retry: until); await Task.Delay(wait, ct); }
                    checkpoint = checkpoint with { RetryAt = null }; await SaveAsync(account, ct);
                }
                var batch = new List<Source>(); var count = queue.Count;
                for (var i = 0; i < count && batch.Count < Math.Clamp(settings.ParallelChannels, 1, 2); i++)
                {
                    var chat = queue.Dequeue(); queued.Remove(chat);
                    if (retryTimes.TryGetValue(chat, out var ready) && ready > DateTimeOffset.UtcNow) { Enqueue(chat); continue; }
                    batch.Add(known[chat]);
                }
                if (batch.Count > 0)
                {
                    Report("indexing", string.Join(" · ", batch.Select(x => x.Name)));
                    var results = await Task.WhenAll(batch.Select(async source =>
                    {
                        try { return new PageResult(source, await indexer.IndexPageAsync(source, ct), null); }
                        catch (Exception e) when (!ct.IsCancellationRequested) { return new PageResult(source, null, e); }
                    }));
                    foreach (var result in results)
                    {
                        var chat = result.Input.ChatId;
                        if (result.Source is { } updated)
                        {
                            known[chat] = updated; pages++; retries.Remove(chat); retryTimes.Remove(chat);
                            if (updated.HistoryComplete) complete.Add(chat); else Enqueue(chat);
                        }
                        else if (result.Error is { } e)
                        {
                            lastError = result.Input.Name + ": " + Td.SafeError(e);
                            if (e is TelegramException { Code: 403 }) await indexer.RecordFailureAsync(account, chat, lastError, null, true, ct);
                            var attempt = retries.GetValueOrDefault(chat) + 1; retries[chat] = attempt;
                            if (e is TelegramException { Code: 429 } rate)
                            {
                                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, rate.RetryAfter ?? 30));
                                session.DeferRequestsUntil(deadline);
                                if (checkpoint.RetryAt is null || checkpoint.RetryAt < deadline)
                                { checkpoint = checkpoint with { RetryAt = deadline }; await SaveAsync(account, ct); }
                            }
                            if (IsRetryable(e) && attempt <= settings.MaxRetries)
                            {
                                Enqueue(chat);
                                if (e is not TelegramException { Code: 429 }) retryTimes[chat] = DateTimeOffset.UtcNow + Backoff(attempt);
                            }
                            else failed[chat] = lastError;
                        }
                    }
                    Report("indexing");
                }
                var delay = TimeSpan.FromMilliseconds(Math.Max(1, settings.YieldMilliseconds));
                if (batch.Count == 0 && retryTimes.Count > 0)
                {
                    var earliest = retryTimes.Where(x => queued.Contains(x.Key)).Select(x => x.Value).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
                    delay = earliest - DateTimeOffset.UtcNow;
                    if (delay < TimeSpan.FromMilliseconds(1)) delay = TimeSpan.FromMilliseconds(1);
                    Report("waiting", retry: earliest);
                }
                if (queue.Count > 0) await Task.Delay(delay, ct);
            }
            checkpoint = checkpoint with { Enabled = settings.ContinuousUpdates }; await SaveAsync(account, ct);
            historyError = lastError = failed.Count > 0 ? string.Join(" · ", failed.Values.Take(3)) : null;
            // Catch-up can start before the history task ends, while LiveChanged is suppressed.
            Report(live.IsRunning ? live.Phase : "completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || session.AccountId != account) { Report("paused"); }
        catch (Exception e) { historyError = lastError = Td.SafeError(e); Report("failed"); }
        finally { lock (jobLock) accepting = false; }

        void Enqueue(long chat) { if (queued.Add(chat)) queue.Enqueue(chat); }
        void Add(Source source)
        {
            if (source.AccountId != account) return;
            if (known.ContainsKey(source.ChatId)) return;
            known[source.ChatId] = source;
            if (source.HistoryComplete) complete.Add(source.ChatId); else Enqueue(source.ChatId);
        }
        void Report(string phase, string? current = null, DateTimeOffset? retry = null) => Publish(new(phase, known.Count, complete.Count, pages, failed.Count, current, retry, lastError));
    }

    private static bool IsRetryable(Exception e) => e is TimeoutException or IOException or OperationCanceledException || e is TelegramException { Code: 429 or >= 500 };
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));
    private Task SaveAsync(long account, CancellationToken ct) => library.SaveStateAsync(account, StateKey, JsonSerializer.Serialize(checkpoint), ct);
    private void Publish(ChannelSyncProgress value) { Progress = value; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() { await PauseAsync(preserveResume: true); indexer.Changed -= SourceChanged; live.Changed -= LiveChanged; await live.DisposeAsync(); cancellation?.Dispose(); }
    private sealed record Checkpoint(bool Enabled, DateTimeOffset? RetryAt = null);
    private sealed record PageResult(Source Input, Source? Source, Exception? Error);
}
