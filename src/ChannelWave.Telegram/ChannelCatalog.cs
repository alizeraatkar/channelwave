using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed record ChannelCatalogProgress(string Phase = "idle", int Found = 0, DateTimeOffset? RetryAt = null, string? Error = null);

/// <summary>Discovers source metadata only. Never selects sources or requests audio messages.</summary>
public sealed class ChannelCatalog(TelegramSession session, ILibraryRepository library) : IAsyncDisposable
{
    private const string Key = "channel-catalog-v1";
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task runner = Task.CompletedTask;
    private long? account;
    private CatalogState state = new(false, []);
    public IReadOnlyList<Source> Channels { get; private set; } = [];
    public ChannelCatalogProgress Progress { get; private set; } = new();
    public bool IsRunning => !runner.IsCompleted || Progress.Phase == "starting";
    public Task Completion => runner;
    public event Action? Changed;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var current = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (account == current) return;
            await library.InitializeAsync(ct);
            var saved = await library.ReadStateAsync(current, Key, ct);
            state = saved is null ? new(false, []) : JsonSerializer.Deserialize<CatalogState>(saved) ?? new(false, []);
            account = current;
            Channels = state.Channels.Where(x => x.AccountId == current).DistinctBy(x => x.ChatId).ToArray();
            state = state with { Channels = Channels.ToArray() };
            Publish(new(state.Paused ? "paused" : "idle", Channels.Count));
            if (!state.Paused) await StartCoreAsync(current, ct);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (IsRunning) return;
            var current = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            if (account != current) throw new InvalidOperationException("Restore the source catalog first.");
            await StartCoreAsync(current, ct);
        }
        finally { lifecycle.Release(); }
    }

    private async Task StartCoreAsync(long current, CancellationToken ct)
    {
        state = state with { Paused = false };
        await SaveAsync(current, ct);
        await session.RestartDiscoveryAsync(Channels, ct);
        cancellation?.Dispose(); cancellation = new();
        Progress = new("starting", Channels.Count);
        runner = RunAsync(current, cancellation.Token);
        Changed?.Invoke();
    }

    public async Task PauseAsync(bool preserveResume = false)
    {
        await lifecycle.WaitAsync();
        try
        {
            cancellation?.Cancel(); await runner;
            if (!preserveResume && account is { } current)
            {
                state = state with { Paused = true };
                await SaveAsync(current, CancellationToken.None);
            }
            if (Progress.Phase is not "completed" and not "idle" and not "failed") Publish(Progress with { Phase = "paused" });
        }
        finally { lifecycle.Release(); }
    }

    private async Task RunAsync(long current, CancellationToken ct)
    {
        var known = Channels.ToDictionary(x => x.ChatId);
        var completedLists = new HashSet<string>();
        var retries = 0;
        try
        {
            while (completedLists.Count < 2)
            {
                ct.ThrowIfCancellationRequested();
                if (session.AccountId != current) throw new OperationCanceledException(ct);
                if (state.RetryAt is { } savedDeadline) session.DeferRequestsUntil(savedDeadline);
                if (session.RetryUntil is { } retry)
                {
                    var wait = retry - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) { Publish(new("waiting", known.Count, retry, Progress.Error)); await Task.Delay(wait, ct); }
                    state = state with { RetryAt = null }; await SaveAsync(current, ct);
                }
                Publish(new("discovering", known.Count));
                try
                {
                    var page = await session.LoadChannelsAsync(ct);
                    var advanced = page.ListExhausted && completedLists.Add(page.ListName);
                    foreach (var channel in page.Channels)
                    {
                        if (!known.TryGetValue(channel.ChatId, out var old) || old.Name != channel.Name) advanced = true;
                        known[channel.ChatId] = new(current, channel.ChatId, channel.Name);
                    }
                    if (advanced)
                    {
                        Channels = known.Values.OrderBy(x => x.Name).ToArray();
                        state = state with { Channels = Channels.ToArray() };
                        await SaveAsync(current, ct);
                        retries = 0;
                    }
                    Publish(new("discovering", known.Count));
                    if (page.TimedOut) throw new TimeoutException("Source discovery timed out. Sources already found are saved.");
                }
                catch (Exception e) when (!ct.IsCancellationRequested && (e is TimeoutException or IOException or OperationCanceledException || e is TelegramException { Code: 429 or >= 500 }))
                {
                    ++retries;
                    var seconds = e is TelegramException { Code: 429 } rate ? Math.Max(1, rate.RetryAfter ?? 30) : Math.Min(30, 1 << Math.Min(retries - 1, 5));
                    state = state with { RetryAt = DateTimeOffset.UtcNow.AddSeconds(seconds) };
                    session.DeferRequestsUntil(state.RetryAt.Value);
                    await SaveAsync(current, ct); Publish(new("waiting", known.Count, state.RetryAt, Td.SafeError(e)));
                    if (retries > 4) throw;
                }
                if (completedLists.Count < 2) await Task.Delay(100, ct);
            }
            Publish(new("completed", known.Count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || session.AccountId != current) { Publish(new("paused", known.Count)); }
        catch (Exception e) { Publish(new("failed", known.Count, Error: Td.SafeError(e))); }
    }

    private Task SaveAsync(long current, CancellationToken ct) => library.SaveStateAsync(current, Key, JsonSerializer.Serialize(state), ct);
    private void Publish(ChannelCatalogProgress value) { Progress = value; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() { await PauseAsync(preserveResume: true); cancellation?.Dispose(); }
    private sealed record CatalogState(bool Paused, Source[] Channels, DateTimeOffset? RetryAt = null);
}
