namespace ChannelWave.Core;

public sealed record PreparedMedia(string Location, bool IsBrowserUrl, long Bytes, bool UsesNetworkRanges = false);
public sealed record AudioState(double Position, double Duration, double Buffered, bool Paused, bool Ended, double Volume, string? Error = null,
    bool Buffering = false, bool Seeking = false, bool Muted = false, double Rate = 1);
public interface IAudioNotifications
{
    Task ObserveAsync(long playbackId, Func<long, string, double, Task> notify, CancellationToken ct);
}
public sealed class UnplayableTrackException(string message) : IOException(message);
public interface IAudioEngine : IAsyncDisposable
{
    Task LoadAsync(PreparedMedia media, CancellationToken ct);
    Task PlayAsync(CancellationToken ct);
    Task PauseAsync();
    Task SeekAsync(double seconds);
    Task<AudioState?> StateAsync(CancellationToken ct = default) => Task.FromResult<AudioState?>(null);
    Task SetVolumeAsync(double volume) => Task.CompletedTask;
    Task UnloadAsync() => PauseAsync();
    Task ReleaseUnusedAsync(PreparedMedia media) => Task.CompletedTask;
    Task SetTrackAsync(Track track) => Task.CompletedTask;
    Task BeginSelectionAsync(Track track, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Owned by the native service/browser profile, never by a Razor component.</summary>
public sealed partial class PlaybackCoordinator(IAudioEngine engine, MediaWorkCoordinator? mediaWork = null,
    IListeningStore? listeningStore = null, TimeProvider? clock = null) : IAsyncDisposable
{
    private IDisposable? foregroundLease;
    private bool networkRanges;
    private CancellationTokenSource? selection;
    private readonly object selectionGate = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private long generation;
    private Guid? loadedTrack;
    private Func<Track, CancellationToken, Task<PreparedMedia>>? prepareTrack;
    public Guid? SelectedTrack { get; private set; }
    public Track? CurrentTrack { get; private set; }
    public IReadOnlyList<Track> PlayingSequence { get; private set; } = [];
    public bool IsPreparing { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public bool IsPlaying { get; private set; }
    public bool CanResume => loadedTrack is not null && loadedTrack == SelectedTrack;
    public QueueSnapshot Queue { get; private set; } = new([]);

    public void Restore(QueueSnapshot state)
    {
        Queue = state with { Index = Math.Clamp(state.Index, 0, Math.Max(0, state.Tracks.Length - 1)), Position = Math.Max(0, state.Position) };
        IsPlaying = false;
    }

    public Task SelectAsync(Guid track, Func<CancellationToken, Task<PreparedMedia>> prepare, CancellationToken ct = default)
        => SelectCoreAsync(track, null, [], null, prepare, ct);

    public Task SelectAsync(Track track, IReadOnlyList<Track> sequence, Func<Track, CancellationToken, Task<PreparedMedia>> prepare, CancellationToken ct = default)
    {
        SetLegacyQueue(track, sequence, prepare);
        return SelectCoreAsync(track.Id, track, sequence, prepare, token => prepare(track, token), ct);
    }

    public Track? Adjacent(int delta)
    {
        lock (selectionGate)
        {
            if (entries.Count > 0) return AdjacentEntry(delta) is { } entry ? knownTracks.GetValueOrDefault(entry.TrackId) : null;
            var index = PlayingSequence.ToList().FindIndex(t => t.Id == SelectedTrack);
            return index >= 0 && index + delta >= 0 && index + delta < PlayingSequence.Count ? PlayingSequence[index + delta] : null;
        }
    }

    public Task SkipAsync(int delta, CancellationToken ct = default)
    {
        if (entries.Count > 0) return NavigateAsync(delta, false, ct);
        lock (selectionGate)
        {
            if (Adjacent(delta) is not { } next || prepareTrack is not { } prepare) return Task.CompletedTask;
            return SelectAsync(next, PlayingSequence, prepare, ct);
        }
    }

    private async Task SelectCoreAsync(Guid track, Track? details, IReadOnlyList<Track> sequence,
        Func<Track, CancellationToken, Task<PreparedMedia>>? resolver, Func<CancellationToken, Task<PreparedMedia>> prepare, CancellationToken ct, double initialPosition = 0, bool resumeAttempt = false)
    {
        if (!resumeAttempt) FinishListen("changed");
        var current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long mine;
        lock (selectionGate)
        {
            mine = ++generation;
            selection?.Cancel();
            routeSelection?.Cancel();
            selection = current;
            SelectedTrack = track;
            CurrentTrack = details;
            PlayingSequence = sequence.ToArray();
            prepareTrack = resolver;
            IsPreparing = true; Error = null; RestoredPaused = false;
            if (details is not null) Queue = Queue with { Position = initialPosition };
            completionArmed = false;
        }
        NotifyChanged();
        try
        {
            await commands.WaitAsync(current.Token);
            try
            {
                if (mine != Volatile.Read(ref generation)) return;
                var lease = mediaWork is null ? null : await mediaWork.ForegroundAsync(current.Token);
                foregroundLease?.Dispose(); foregroundLease = lease;
                await engine.UnloadAsync(); IsPlaying = false; loadedTrack = null;
                if (details is not null) await engine.BeginSelectionAsync(details, current.Token);
            }
            finally { commands.Release(); }
            var media = await prepare(current.Token);
            var handedToEngine = false;
            try
            {
                await commands.WaitAsync(current.Token);
                try
                {
                    if (mine != Volatile.Read(ref generation)) return;
                    current.Token.ThrowIfCancellationRequested();
                    await engine.LoadAsync(media, current.Token);
                    handedToEngine = true;
                    networkRanges = media.UsesNetworkRanges;
                    if (!networkRanges) { foregroundLease?.Dispose(); foregroundLease = null; }
                    if (mine != Volatile.Read(ref generation)) return;
                    loadedTrack = track;
                    loadedGeneration = mine;
                    if (engine is IAudioNotifications notifications) await notifications.ObserveAsync(mine, AudioSignalAsync, current.Token);
                    Track? latest;
                    lock (selectionGate) latest = CurrentTrack;
                    if (latest is not null) await engine.SetTrackAsync(latest);
                    await engine.SetVolumeAsync(Muted ? 0 : OutputVolume);
                    if (initialPosition > 0)
                    {
                        var ready = await engine.StateAsync(current.Token);
                        await engine.SeekAsync(ready?.Duration > 0 ? Math.Min(initialPosition, Math.Max(0, ready.Duration - 0.05)) : initialPosition);
                    }
                    await engine.PlayAsync(current.Token);
                    recorder.Break(); await ObserveEngineAsync();
                    if (mine == Volatile.Read(ref generation)) { IsPlaying = true; completionArmed = true; EnsureMonitoring(); }
                }
                finally { commands.Release(); }
            }
            finally { if (!handedToEngine) await engine.ReleaseUnusedAsync(media); }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (mine == Volatile.Read(ref generation)) Error = e.Message.Split('\n')[0];
            throw;
        }
        finally
        {
            await commands.WaitAsync();
            try { if (mine == Volatile.Read(ref generation) && !CanResume) { foregroundLease?.Dispose(); foregroundLease = null; } }
            finally { commands.Release(); }
            lock (selectionGate)
            {
                if (ReferenceEquals(selection, current)) { selection = null; IsPreparing = false; }
                current.Dispose();
            }
            NotifyChanged();
        }
    }

    public async Task RemoveTracksAsync(IReadOnlyCollection<Guid> tracks)
    {
        var removed = tracks.ToHashSet();
        lock (selectionGate)
        {
            if (SelectedTrack is { } selected && removed.Contains(selected))
            {
                ++generation; selection?.Cancel(); routeSelection?.Cancel(); SelectedTrack = null; CurrentTrack = null; IsPreparing = false; Error = null;
            }
            PlayingSequence = PlayingSequence.Where(t => !removed.Contains(t.Id)).ToArray();
            PruneQueue(removed);
            var current = Queue.Tracks.ElementAtOrDefault(Queue.Index);
            var retained = Queue.Tracks.Where(x => !removed.Contains(x)).ToArray();
            var index = Array.IndexOf(retained, current);
            Queue = Queue with { Tracks = retained, Index = index >= 0 ? index : Math.Min(Queue.Index, Math.Max(0, retained.Length - 1)), Position = index >= 0 ? Queue.Position : 0 };
        }
        await commands.WaitAsync();
        try
        {
            if (loadedTrack is { } loaded && removed.Contains(loaded))
            {
                await engine.UnloadAsync();
                loadedTrack = null; IsPlaying = false;
                foregroundLease?.Dispose(); foregroundLease = null;
            }
            if (SelectedTrack is null)
            {
                if (ActiveOutput.Remote) await ResetOutputAsync();
                foregroundLease?.Dispose(); foregroundLease = null;
            }
        }
        finally { commands.Release(); }
        NotifyChanged();
    }

    public async Task PauseAsync()
    {
        lock (selectionGate) { ++generation; ++queueRequest; selection?.Cancel(); routeSelection?.Cancel(); IsPreparing = false; completionArmed = false; }
        await commands.WaitAsync();
        try { await ObserveEngineAsync(); await engine.PauseAsync(); recorder.Break(); IsPlaying = false; if (!CanResume) { foregroundLease?.Dispose(); foregroundLease = null; } }
        finally { commands.Release(); }
        NotifyChanged();
    }
    public async Task StopAsync() { FinishListen("stopped"); await StopCoreAsync(true); await CheckpointAsync(); }
    private async Task StopCoreAsync(bool notify)
    {
        lock (selectionGate) { ++generation; ++queueRequest; selection?.Cancel(); routeSelection?.Cancel(); SelectedTrack = null; CurrentTrack = null; PlayingSequence = []; prepareTrack = null; IsPreparing = false; RestoredPaused = false; restoredReference = null; Error = null; completionArmed = false; ResetQueue(); }
        await commands.WaitAsync();
        try
        {
            await engine.UnloadAsync();
            if (!notify || ActiveOutput.Remote) await ResetOutputAsync();
        }
        finally
        {
            loadedTrack = null;
            IsPlaying = false;
            foregroundLease?.Dispose();
            foregroundLease = null;
            commands.Release();
        }
        if (notify) NotifyChanged();
    }
    public Task ResumeAsync(CancellationToken ct = default) => ResumeCoreAsync(ct);
    public async Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        await commands.WaitAsync(ct);
        try
        {
            if (!CanResume) return RestoredPaused ? new(Queue.Position, CurrentTrack?.Duration ?? 0, 0, true, false, OutputVolume, Muted: Muted) : null;
            var state = await engine.StateAsync(ct);
            if (state is not null)
            {
                IsPlaying = !state.Paused && !state.Ended && state.Error is null;
                if (state.Error is not null) Error = state.Error;
                if (state.Ended || state.Error is not null) { foregroundLease?.Dispose(); foregroundLease = null; }
            }
            return state;
        }
        finally { commands.Release(); }
    }
    public async Task SeekAsync(double seconds)
    {
        await commands.WaitAsync();
        try
        {
            if (CanResume && double.IsFinite(seconds))
            {
                if (networkRanges && foregroundLease is null && mediaWork is not null) foregroundLease = await mediaWork.ForegroundAsync(CancellationToken.None);
                await ObserveEngineAsync(); recorder.Break();
                await engine.SeekAsync(Math.Max(0, seconds));
                Queue = Queue with { Position = Math.Max(0, seconds) };
                await ObserveEngineAsync();
            }
        }
        finally { commands.Release(); }
        NotifyChanged();
    }
    public async Task SetVolumeAsync(double volume)
    {
        if (!double.IsFinite(volume)) return;
        await commands.WaitAsync();
        try { await ObserveEngineAsync(); recorder.Break(); OutputVolume = Math.Clamp(volume, 0, 1); ListeningPreferences = ListeningPreferences with { Volume = OutputVolume }; await engine.SetVolumeAsync(Muted ? 0 : OutputVolume); }
        finally { commands.Release(); }
        NotifyChanged();
    }
    public async Task UpdateTrackMetadataAsync(Track track, CancellationToken ct = default)
    {
        await commands.WaitAsync(ct);
        try
        {
            bool current;
            lock (selectionGate)
            {
                bool Matches(Track? candidate) => candidate?.Id == track.Id && candidate.Origin.AccountId == track.Origin.AccountId
                    && Metadata.MediaKey(candidate) == Metadata.MediaKey(track);
                PlayingSequence = PlayingSequence.Select(t => Matches(t) ? track : t).ToArray();
                if (QueueAccount == track.Origin.AccountId && knownTracks.ContainsKey(track.Id)) knownTracks[track.Id] = track;
                current = Matches(CurrentTrack);
                if (current) CurrentTrack = track;
            }
            if (current) await engine.SetTrackAsync(track);
        }
        finally { commands.Release(); }
        NotifyChanged();
    }
    private async Task ResumeCoreAsync(CancellationToken ct)
    {
        if (RestoredPaused) { await ResumeRestoredAsync(ct); return; }
        await commands.WaitAsync(ct);
        try
        {
            if (loadedTrack is null || loadedTrack != SelectedTrack) throw new InvalidOperationException("Select and download a track before resuming playback.");
            if (networkRanges && foregroundLease is null && mediaWork is not null) foregroundLease = await mediaWork.ForegroundAsync(ct);
            loadedGeneration = Interlocked.Increment(ref generation);
            if (engine is IAudioNotifications notifications) await notifications.ObserveAsync(loadedGeneration, AudioSignalAsync, ct);
            await engine.PlayAsync(ct); recorder.Break(); await ObserveEngineAsync(); IsPlaying = true; completionArmed = true; Error = null; EnsureMonitoring();
        }
        finally { commands.Release(); }
        NotifyChanged();
    }
    public async ValueTask DisposeAsync()
    {
        await PauseAsync(); await CheckpointAsync(); playbackLifetime.Cancel(); await monitoring;
        binding = true; await StopCoreAsync(false); await engine.DisposeAsync();
        playbackLifetime.Dispose(); commands.Dispose(); persistenceGate.Dispose(); bindingGate.Dispose();
    }
}

public static class QueueRules
{
    public static int? Next(QueueSnapshot queue, bool ended, Random? random = null)
    {
        if (queue.Tracks.Length == 0) return null;
        if (ended && queue.Repeat == RepeatMode.One) return queue.Index;
        if (queue.Shuffle && queue.Tracks.Length > 1)
        {
            var next = (random ?? Random.Shared).Next(queue.Tracks.Length - 1);
            return next >= queue.Index ? next + 1 : next;
        }
        return queue.Index + 1 < queue.Tracks.Length ? queue.Index + 1 : queue.Repeat == RepeatMode.All ? 0 : null;
    }
}

public sealed record CacheEntry(string Key, long Bytes, DateTimeOffset LastUsed, bool Pinned, bool Playing);
public static class CachePolicy
{
    public static IReadOnlyList<CacheEntry> Evictions(IEnumerable<CacheEntry> entries, long budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        var all = entries.ToList(); var total = all.Where(x => !x.Pinned).Sum(x => x.Bytes); var result = new List<CacheEntry>();
        foreach (var entry in all.Where(x => !x.Pinned && !x.Playing).OrderBy(x => x.LastUsed))
        {
            if (total <= budget) break;
            result.Add(entry); total -= entry.Bytes;
        }
        return result;
    }
}
