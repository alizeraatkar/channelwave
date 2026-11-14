using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed record MetadataProgress(string Phase = "idle", int Completed = 0, int Failed = 0, string? Current = null, string? Error = null);
public sealed record MetadataScannerOptions(int BatchSize = 25, int YieldMilliseconds = 100, int PollMilliseconds = 5000);

/// <summary>One resumable worker over every selected track, independent of rendered library pages.</summary>
public sealed class MetadataScanner(TelegramSession session, ILibraryRepository library, IMetadataSource source, MetadataScannerOptions? options = null) : IAsyncDisposable
{
    private readonly MetadataScannerOptions settings = options ?? new();
    private const string StateKey = "automatic-metadata-v1";
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim reads = new(1, 1);
    private readonly object readSync = new();
    private CancellationTokenSource? activeRead;
    private int inspections;
    private CancellationTokenSource? cancellation;
    private Task runner = Task.CompletedTask;
    private long? account;
    private bool subscribed, disposed;
    public bool Enabled { get; private set; } = true;
    public bool CanLoadAutomatically(long active) => account == active && Enabled && !runner.IsCompleted;
    public MetadataProgress Progress { get; private set; } = new();
    public event Action? Changed;
    public event Action<Track>? TrackChanged;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!subscribed) { session.Changed += SessionChanged; subscribed = true; }
            var active = session.AccountId;
            if (active is null) { await StopAsync(); account = null; return; }
            if (active == account && !runner.IsCompleted) return;
            await StopAsync();
            if (active != account)
            {
                account = active;
                var saved = await library.ReadStateAsync(active.Value, StateKey, ct);
                Enabled = saved is null || JsonSerializer.Deserialize<bool>(saved);
                var retry = await library.ReadStateAsync(active.Value, StateKey + "-retry-at", ct);
                if (retry is not null) session.DeferRequestsUntil(JsonSerializer.Deserialize<DateTimeOffset>(retry));
                Progress = new(Enabled ? "idle" : "paused");
            }
            if (Enabled)
            {
                cancellation = new();
                runner = RunAsync(active.Value, cancellation.Token);
            }
        }
        finally { lifecycle.Release(); }
    }
    private void SessionChanged() { if (session.AccountId != account) cancellation?.Cancel(); }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var active = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            await StopAsync(); account = active; Enabled = enabled;
            await library.SaveStateAsync(active, StateKey, JsonSerializer.Serialize(enabled), ct);
            Publish(Progress with { Phase = enabled ? "idle" : "paused", Current = null, Error = null });
            if (enabled) { cancellation = new(); runner = RunAsync(active, cancellation.Token); }
        }
        finally { lifecycle.Release(); }
    }
    public async Task RetryFailedAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var active = session.AccountId ?? throw new InvalidOperationException("Connect Telegram first.");
            await StopAsync(); account = active;
            await library.RetryMetadataAsync(active, ct);
            Enabled = true; await library.SaveStateAsync(active, StateKey, "true", ct);
            Publish(Progress with { Phase = "idle", Current = null, Error = null });
            cancellation = new(); runner = RunAsync(active, cancellation.Token);
        }
        finally { lifecycle.Release(); }
    }
    public async Task SuspendAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopAsync(); }
        finally { lifecycle.Release(); }
    }
    private async Task StopAsync()
    {
        cancellation?.Cancel(); await runner;
        cancellation?.Dispose(); cancellation = null;
    }
    private void CheckAccount(long active, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.AccountId != active) throw new OperationCanceledException(ct);
    }

    public async Task<Track?> EnsureAsync(Track requested, CancellationToken ct = default)
    {
        await reads.WaitAsync(ct);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            lock (readSync)
            {
                if (inspections > 0) return null;
                activeRead = read;
            }

            var active = requested.Origin.AccountId;
            CheckAccount(active, read.Token);
            var current = await library.TrackAsync(active, requested.Id, read.Token);
            if (current is null || current.Origin.Protected || !Metadata.IsSameMedia(requested, current)) return null;
            if (CachedMetadata.FromTrack(current) is not null) return current;
            var cached = await library.FindEmbeddedMetadataAsync(current, read.Token);
            if (cached is not null) return await CommitAsync(current, cached, read.Token);
            if (!MetadataScan.IsPending(current, DateTimeOffset.UtcNow)) return current;

            var attempt = current.MetadataScan?.Matches(current) == true ? current.MetadataScan.Attempts + 1 : 1;
            MetadataReadResult result;
            try
            {
                await session.WaitForRequestsAsync(read.Token);
                await using var file = await source.OpenAsync(current, read.Token);
                if (file.AccountId != active) throw new InvalidOperationException("Cross-account metadata access rejected.");
                result = await EmbeddedMetadataReader.ReadAsync(file, read.Token);
            }
            catch (Exception e) when (!read.IsCancellationRequested && session.AccountId == active)
            {
                await RecordFailureAsync(current, attempt, e, read.Token);
                return null;
            }

            CheckAccount(active, read.Token);
            return await CommitAsync(current, ToCached(current, result, attempt), read.Token);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested && !ct.IsCancellationRequested
            && session.AccountId == requested.Origin.AccountId)
        {
            // Explicit inspection took ownership. The next pass rechecks storage.
            return null;
        }
        finally
        {
            lock (readSync) activeRead = null;
            reads.Release();
        }
    }

    public async Task<Track?> InspectAsync(Track track, Func<CancellationToken, Task<Id3Document>> inspect, CancellationToken ct)
    {
        var active = session.AccountId;
        lock (readSync)
        {
            inspections++;
            activeRead?.Cancel();
        }
        try
        {
            await reads.WaitAsync(ct);
            try
            {
                var document = await inspect(ct);
                ct.ThrowIfCancellationRequested();
                if (session.AccountId != active || active is not null && active != track.Origin.AccountId)
                    throw new OperationCanceledException(ct);
                var result = EmbeddedMetadataReader.FromId3(document);
                // Export validation can fail even if the bounded reader can extract tags.
                return result.Status == MetadataScanStatus.Unsupported ? null : await CommitAsync(track, ToCached(track, result, 1), ct);
            }
            finally { reads.Release(); }
        }
        finally { lock (readSync) inspections--; }
    }

    private static CachedMetadata ToCached(Track track, MetadataReadResult result, int attempt)
    {
        var key = Metadata.MediaKey(track);
        return new(result.Status == MetadataScanStatus.Completed ? result.Metadata : null,
            new(key, EmbeddedMetadataReader.Version, result.Status, attempt, Reason: result.Reason),
            result.Artwork is { } bytes ? new(key, ArtworkKind.Embedded, bytes) : null);
    }

    private async Task<Track?> CommitAsync(Track track, CachedMetadata cached, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var updated = await library.SaveEmbeddedMetadataAsync(track, cached, ct);
        foreach (var row in updated)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (cached.Artwork is { } artwork && row.Artwork?.Kind != ArtworkKind.Embedded)
                    await source.ObserveAsync(row, artwork.Data, ct);
                await source.ObserveAsync(row, null, ct);
            }
            catch (Exception) { /* A committed scan remains valid. */ }
            if (ct.IsCancellationRequested) break;
            try { TrackChanged?.Invoke(row); }
            catch (Exception) { /* A subscriber must not invalidate saved tags. */ }
        }
        if (updated.Length > 0) Publish(Progress with { Completed = Progress.Completed + updated.Length });
        return await library.TrackAsync(track.Origin.AccountId, track.Id, ct);
    }

    private async Task RecordFailureAsync(Track track, int attempt, Exception error, CancellationToken ct)
    {
        var active = track.Origin.AccountId;
        CheckAccount(active, ct);
        var transient = error is IOException or TimeoutException or OperationCanceledException || error is TelegramException { Code: 429 or >= 500 };
        var retryAt = transient && attempt < 3 ? DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, attempt)) : (DateTimeOffset?)null;
        if (error is TelegramException { Code: 429 } rate)
        {
            retryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, rate.RetryAfter ?? 30));
            session.DeferRequestsUntil(retryAt.Value);
            await library.SaveStateAsync(active, StateKey + "-retry-at", JsonSerializer.Serialize(retryAt.Value), ct);
        }
        var key = Metadata.MediaKey(track);
        await library.SetMetadataScanAsync(active, track.Id, key, track.EmbeddedMetadata,
            new(key, EmbeddedMetadataReader.Version, MetadataScanStatus.Failed, attempt, retryAt, Td.SafeError(error)), ct);
        Publish(Progress with { Failed = Progress.Failed + 1, Error = Td.SafeError(error) });
    }

    private async Task RunAsync(long active, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                CheckAccount(active, ct);
                var pending = await library.PendingMetadataAsync(active, DateTimeOffset.UtcNow, Math.Clamp(settings.BatchSize, 1, 1000), ct);
                if (pending.Count == 0)
                {
                    if (Progress.Phase != "waiting") Publish(Progress with { Phase = "waiting", Current = null });
                    await Task.Delay(Math.Max(1, settings.PollMilliseconds), ct); continue;
                }
                foreach (var track in pending)
                {
                    CheckAccount(active, ct);
                    Publish(Progress with { Phase = "reading", Current = track.Title, Error = null });
                    await EnsureAsync(track, ct);
                    await Task.Delay(Math.Max(0, settings.YieldMilliseconds), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || session.AccountId != active) { }
        catch (Exception e) { Publish(Progress with { Phase = "failed", Error = Td.SafeError(e), Current = null }); }
    }
    private void Publish(MetadataProgress progress)
    {
        Progress = progress;
        try { Changed?.Invoke(); }
        catch (Exception) { /* Progress reporting does not control the worker. */ }
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; session.Changed -= SessionChanged; await SuspendAsync();
    }
}
