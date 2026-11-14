namespace ChannelWave.Core;

/// <summary>Foreground media owns the transfer cache until its lease is released. Background
/// cancellation is drained before ownership passes, so it cannot cancel a foreground download.</summary>
public sealed class MediaWorkCoordinator
{
    private readonly SemaphoreSlim background = new(1, 1);
    private readonly object sync = new();
    private int foreground;
    private CancellationTokenSource? active;
    private readonly Dictionary<string, SemaphoreSlim> files = [];
    private readonly SemaphoreSlim cacheMutation = new(1, 1);
    public IDisposable? TryCacheMutation()
    {
        if (!cacheMutation.Wait(0)) return null;
        lock (sync)
        {
            if (files.Values.Any(gate => gate.CurrentCount == 0)) { cacheMutation.Release(); return null; }
            return new Lease(() => cacheMutation.Release());
        }
    }
    public IDisposable? TryFile(string key)
    {
        if (!cacheMutation.Wait(0)) return null;
        try
        {
            lock (sync)
            {
                if (!files.TryGetValue(key, out var gate)) files[key] = gate = new(1, 1);
                return gate.Wait(0) ? new Lease(() => gate.Release()) : null;
            }
        }
        finally { cacheMutation.Release(); }
    }
    public async Task<IDisposable> FileAsync(string key, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (sync)
        {
            if (!files.TryGetValue(key, out gate!)) files[key] = gate = new(1, 1);
        }
        await cacheMutation.WaitAsync(ct);
        try { await gate.WaitAsync(ct); return new Lease(() => gate.Release()); }
        finally { cacheMutation.Release(); }
    }

    public async Task<IDisposable> ForegroundAsync(CancellationToken ct)
    {
        lock (sync) { foreground++; active?.Cancel(); }
        try { await background.WaitAsync(ct); background.Release(); }
        catch { lock (sync) foreground--; throw; }
        return new Lease(() => { lock (sync) foreground--; });
    }

    public async Task<T> BackgroundAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await background.WaitAsync(ct);
            CancellationTokenSource? token = null;
            lock (sync) { if (foreground == 0) active = token = CancellationTokenSource.CreateLinkedTokenSource(ct); }
            if (token is null) { background.Release(); await Task.Delay(100, ct); continue; }
            try { return await operation(token.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && token.IsCancellationRequested) { }
            finally { lock (sync) { active = null; token.Dispose(); } background.Release(); }
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
