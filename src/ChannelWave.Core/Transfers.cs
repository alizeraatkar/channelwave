using System.Text.Json;

namespace ChannelWave.Core;

public enum TransferPurpose { Offline, Export }
public enum TransferState { Queued, Downloading, Saving, Waiting, ReadyToExport, Exporting, Completed, Failed, Canceled }
public sealed record TransferProgress(long Bytes, long Total, string? Phase = null);
public sealed record TransferJob(Guid Id, Track Track, TransferPurpose Purpose, TransferState State,
    DateTimeOffset CreatedAt, Guid? Batch = null, int Attempts = 0, DateTimeOffset? RetryAt = null,
    string? Error = null, string? Result = null)
{
    public bool Finished => State is TransferState.Completed or TransferState.Canceled;
    public bool NeedsAttention => State == TransferState.Failed;
}
public sealed class TransferWaitException(string message, TimeSpan delay) : IOException(message)
{
    public TimeSpan Delay { get; } = delay;
}
public sealed class TransferRetryException(string message, TimeSpan delay) : IOException(message)
{
    public TimeSpan Delay { get; } = delay;
}
public interface ITransferWorker
{
    Task PrepareAsync(TransferJob job, IProgress<TransferProgress> progress, CancellationToken ct);
    Task<string> ExportAsync(TransferJob job, CancellationToken ct);
    Task CancelAsync(TransferJob job);
}

/// <summary>Durable account-scoped user intent. Its lifetime belongs to the host, not a view.</summary>
public sealed class TransferManager(ILibraryRepository library, ITransferWorker worker, TimeProvider? time = null) : IAsyncDisposable
{
    public const string JournalKey = "transfers:v1";
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim lifecycle = new(1, 1), writes = new(1, 1), workerGate = new(1, 1);
    private readonly object sync = new();
    private TransferJob[] jobs = [];
    private readonly Dictionary<Guid, TransferProgress> progress = [];
    private CancellationTokenSource? lifetime, activeCancellation;
    private Guid? activeId;
    private Task runner = Task.CompletedTask;
    public long? Account { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public IReadOnlyList<TransferJob> Jobs { get { lock (sync) return jobs.ToArray(); } }
    public TransferProgress Progress(Guid id) { lock (sync) return progress.GetValueOrDefault(id) ?? new(0, 0); }
    private sealed record Journal(int Version, TransferJob[] Jobs);

    public async Task BindAsync(long? account, CancellationToken ct = default)
    {
        // An external save may hold lifecycle while awaiting a file/share operation.
        // Interrupt it before taking lifecycle so sign-out/account switching can drain it.
        if (Account != account)
        {
            if (activeId is { } pending) await CancelActiveAsync(pending);
            try { lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        }
        await lifecycle.WaitAsync(ct);
        try
        {
            if (Account == account && lifetime is { IsCancellationRequested: false } && (Error is null || !runner.IsCompleted)) return;
            await StopAsync();
            await writes.WaitAsync(ct);
            try
            {
            Account = null;
            lock (sync) { jobs = []; progress.Clear(); }
            if (account is null) { Changed?.Invoke(); return; }
            var stored = await library.ReadStateAsync(account.Value, JournalKey, ct);
            var journal = stored is null ? null : JsonSerializer.Deserialize<Journal>(stored);
            if (journal is { Version: not 1 }) throw new IOException("This download history requires a newer ChannelWave version.");
            var recovered = (journal?.Jobs ?? []).Where(j => j.Track.Origin.AccountId == account).Select(j => j.State switch
            {
                TransferState.Downloading or TransferState.Saving => j with { State = TransferState.Queued },
                TransferState.Exporting => j with { State = TransferState.ReadyToExport, Error = "The previous save may have reached your device. Check your files before saving again." },
                _ => j
            }).ToArray();
            await library.SaveStateAsync(account.Value, JournalKey, JsonSerializer.Serialize(new Journal(1, recovered)), ct);
            lock (sync) jobs = recovered;
            Account = account;
            lifetime = new(); Error = null;
            runner = RunAsync(account.Value, lifetime.Token);
            Changed?.Invoke();
            }
            finally { writes.Release(); }
        }
        finally { lifecycle.Release(); }
    }

    public async Task<Guid[]> EnqueueAsync(IEnumerable<Track> tracks, TransferPurpose purpose, Guid? batch = null, CancellationToken ct = default)
    {
        var account = Account ?? throw new InvalidOperationException("Connect Telegram before saving music.");
        var added = new List<Guid>();
        var requested = tracks.DistinctBy(t => t.Id).ToArray();
        if (requested.Any(t => t.Origin.AccountId != account)) throw new InvalidOperationException("These songs belong to another account.");
        await ChangeAsync(list =>
        {
            foreach (var track in requested)
            {
                var existing = list.FirstOrDefault(j => j.Track.Id == track.Id && Metadata.MediaKey(j.Track) == Metadata.MediaKey(track)
                    && j.Purpose == purpose && !j.Finished);
                if (existing is not null) { added.Add(existing.Id); continue; }
                // Intent needs identity and labels, not a second copy of embedded artwork/tag payloads.
                var snapshot = track with { Artwork = null, EmbeddedMetadata = null };
                var job = new TransferJob(Guid.NewGuid(), snapshot, purpose,
                    track.Origin.Protected || !track.Available ? TransferState.Failed : TransferState.Queued,
                    clock.GetUtcNow(), batch, Error: track.Origin.Protected ? "This source restricts saving." : !track.Available ? "This Telegram message is unavailable." : null);
                list.Add(job); added.Add(job.Id);
            }
            var old = list.Where(j => j.Finished).OrderByDescending(j => j.CreatedAt).Skip(100).Select(j => j.Id).ToHashSet();
            list.RemoveAll(j => old.Contains(j.Id));
            if (list.Count > 10000) throw new InvalidOperationException("Finish or cancel existing downloads before adding more songs.");
        }, ct, account);
        return added.ToArray();
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        await CancelActiveAsync(id);
        await ChangeAsync(list => Replace(list, id, j => j.Finished ? j : j with { State = TransferState.Canceled, RetryAt = null, Error = null }), ct);
    }
    public async Task CancelAllAsync(CancellationToken ct = default)
    {
        if (activeId is { } id) await CancelActiveAsync(id);
        await ChangeAsync(list => { for (var i = 0; i < list.Count; i++) if (!list[i].Finished) list[i] = list[i] with { State = TransferState.Canceled, RetryAt = null }; }, ct);
    }
    public Task RetryAsync(Guid id, CancellationToken ct = default) => ChangeAsync(list => Replace(list, id, j =>
        j.State is TransferState.Failed or TransferState.Canceled or TransferState.Waiting
            ? j with { State = TransferState.Queued, Attempts = 0, RetryAt = null, Error = null } : j), ct);
    public Task ClearCompletedAsync(CancellationToken ct = default) => ChangeAsync(list => list.RemoveAll(j => j.Finished), ct);
    public async Task RemoveSourceAsync(long chat, CancellationToken ct = default)
    {
        var ids = Jobs.Where(j => j.Track.Origin.ChatId == chat).Select(j => j.Id).ToArray();
        foreach (var id in ids) await CancelAsync(id, ct);
        await ChangeAsync(list => list.RemoveAll(j => j.Track.Origin.ChatId == chat), ct);
    }
    public async Task ExportAsync(Guid id, CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var job = Jobs.FirstOrDefault(j => j.Id == id && j.Purpose == TransferPurpose.Export && j.State == TransferState.ReadyToExport);
            if (job is null) return;
            await workerGate.WaitAsync(ct);
            try
            {
            // Persist BEFORE invoking the external save/share UI. Recovery never repeats the handoff.
            await ChangeAsync(list => Replace(list, id, j => j with { State = TransferState.Exporting, Error = null }), ct);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime!.Token);
            activeId = id; activeCancellation = cancellation;
            try
            {
                var result = await worker.ExportAsync(job, cancellation.Token);
                await FinishAsync(id, TransferState.Completed, result: result);
            }
            catch (OperationCanceledException) { await FinishAsync(id, TransferState.ReadyToExport); }
            catch (Exception e) { await FinishAsync(id, TransferState.ReadyToExport, error: e.Message.Split('\n')[0]); }
            finally { activeId = null; activeCancellation = null; }
            }
            finally { workerGate.Release(); }
        }
        finally { lifecycle.Release(); }
    }

    private async Task RunAsync(long account, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Export handoff and download preparation share a single worker.
                var next = activeId is null ? Jobs.FirstOrDefault(j => j.State == TransferState.Queued
                    || j.State == TransferState.Waiting && j.RetryAt <= clock.GetUtcNow()) : null;
                if (next is null) { await Task.Delay(TimeSpan.FromMilliseconds(300), clock, ct); continue; }
                await workerGate.WaitAsync(ct);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                activeId = next.Id; activeCancellation = cancellation;
                try
                {
                    await ChangeAsync(list => Replace(list, next.Id, j => j.State is TransferState.Queued or TransferState.Waiting
                        ? j with { State = TransferState.Downloading, Error = null, RetryAt = null } : j), ct);
                    if (Jobs.FirstOrDefault(j => j.Id == next.Id)?.State != TransferState.Downloading) continue;
                    await worker.PrepareAsync(next, new InlineProgress(p => Report(next.Id, p)), cancellation.Token);
                    await FinishAsync(next.Id, next.Purpose == TransferPurpose.Offline ? TransferState.Completed : TransferState.ReadyToExport);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    if (!ct.IsCancellationRequested) await FinishAsync(next.Id, TransferState.Canceled);
                }
                catch (TransferWaitException e) { await WaitAsync(next, e.Message, e.Delay, false); }
                catch (TransferRetryException e) { await WaitAsync(next, e.Message, e.Delay, false); }
                catch (IOException e) when (next.Attempts < 3)
                {
                    await WaitAsync(next, e.Message, TimeSpan.FromSeconds(new[] { 2, 10, 30 }[next.Attempts]), true);
                }
                catch (Exception e) { await FinishAsync(next.Id, TransferState.Failed, error: e.Message.Split('\n')[0]); }
                finally { activeId = null; activeCancellation = null; workerGate.Release(); Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Error = "Downloads stopped: " + e.Message.Split('\n')[0]; Changed?.Invoke(); }
    }
    private Task WaitAsync(TransferJob job, string error, TimeSpan delay, bool attempt) => ChangeAsync(list => Replace(list, job.Id,
        j => j.Finished ? j : j with { State = TransferState.Waiting, Error = error, RetryAt = clock.GetUtcNow() + delay, Attempts = j.Attempts + (attempt ? 1 : 0) }));
    private Task FinishAsync(Guid id, TransferState state, string? error = null, string? result = null) => ChangeAsync(list => Replace(list, id,
        j => j.Finished ? j : j with { State = state, Error = error, Result = result, RetryAt = null }));
    private void Report(Guid id, TransferProgress value)
    {
        lock (sync) progress[id] = value;
        Changed?.Invoke();
    }
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress> { public void Report(TransferProgress value) => report(value); }
    private static void Replace(List<TransferJob> list, Guid id, Func<TransferJob, TransferJob> update)
    {
        var i = list.FindIndex(j => j.Id == id); if (i >= 0) list[i] = update(list[i]);
    }
    private async Task ChangeAsync(Action<List<TransferJob>> change, CancellationToken ct = default, long? expectedAccount = null)
    {
        await writes.WaitAsync(ct);
        try
        {
            var account = Account ?? throw new InvalidOperationException("Connect Telegram before managing downloads.");
            if (expectedAccount is { } owner && owner != account) throw new OperationCanceledException("The Telegram account changed. Retry from the current account.", ct);
            List<TransferJob> updated; lock (sync) updated = jobs.ToList();
            change(updated);
            await library.SaveStateAsync(account, JournalKey, JsonSerializer.Serialize(new Journal(1, updated.ToArray())), ct);
            lock (sync) jobs = updated.ToArray();
        }
        finally { writes.Release(); }
        Changed?.Invoke();
    }
    private async Task CancelActiveAsync(Guid id)
    {
        if (activeId != id || activeCancellation is null) return;
        var job = Jobs.FirstOrDefault(j => j.Id == id);
        var cancellation = activeCancellation;
        if (job is not null) await worker.CancelAsync(job);
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task StopAsync()
    {
        if (activeId is { } id) await CancelActiveAsync(id);
        lifetime?.Cancel();
        await runner;
        lifetime?.Dispose(); lifetime = null;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); }
}
