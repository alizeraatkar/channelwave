namespace ChannelWave.Core;

public sealed record QueueEntry(Guid Id, Guid TrackId);
public sealed record QueueRow(QueueEntry Entry, Track? Track, string? Notice = null);

public sealed partial class PlaybackCoordinator
{
    private readonly List<QueueEntry> entries = [];
    private readonly Dictionary<Guid, Track> knownTracks = [];
    private readonly Dictionary<Guid, int> originalOrder = [];
    private readonly List<Guid> history = [];
    private readonly Dictionary<Guid, string> skipped = [];
    private Func<Guid, CancellationToken, Task<Track?>>? resolveTrack;
    private int currentIndex = -1, historyIndex = -1;
    private long queueRequest, loadedGeneration;
    private bool completionArmed;
    private readonly CancellationTokenSource playbackLifetime = new();
    private Task monitoring = Task.CompletedTask;
    public long? QueueAccount { get; private set; }
    public bool OfflineOnly { get; private set; }
    public long QueueVersion { get; private set; }
    public Guid? CurrentEntryId { get { lock (selectionGate) return entries.ElementAtOrDefault(currentIndex)?.Id; } }
    public IReadOnlyList<QueueEntry> Entries { get { lock (selectionGate) return entries.ToArray(); } }
    public IReadOnlyList<QueueEntry> Upcoming { get { lock (selectionGate) return entries.Skip(currentIndex + 1).ToArray(); } }
    public int UpcomingCount { get { lock (selectionGate) return Math.Max(0, entries.Count - currentIndex - 1); } }

    private void ResetQueue()
    {
        completionArmed = false;
        entries.Clear(); knownTracks.Clear(); originalOrder.Clear(); history.Clear(); skipped.Clear();
        resolveTrack = null; currentIndex = historyIndex = -1; QueueAccount = null; OfflineOnly = false;
        Queue = new([]); QueueVersion++;
    }

    private void SetLegacyQueue(Track track, IReadOnlyList<Track> sequence, Func<Track, CancellationToken, Task<PreparedMedia>> prepare)
    {
        lock (selectionGate)
        {
            ResetQueue(); QueueAccount = track.Origin.AccountId;
            foreach (var item in sequence.Where(t => t.Origin.AccountId == QueueAccount))
            {
                knownTracks[item.Id] = item; entries.Add(new(Guid.NewGuid(), item.Id));
            }
            if (!entries.Any(e => e.TrackId == track.Id)) entries.Add(new(Guid.NewGuid(), track.Id));
            knownTracks[track.Id] = track;
            currentIndex = entries.FindIndex(e => e.TrackId == track.Id);
            resolveTrack = (id, _) => Task.FromResult(knownTracks.GetValueOrDefault(id)); prepareTrack = prepare;
            history.Add(entries[currentIndex].Id); historyIndex = 0;
            RememberOrder(); UpdateQueue();
        }
    }

    public async Task StartQueueAsync(long account, IReadOnlyList<Guid> tracks, Guid selected,
        Func<Guid, CancellationToken, Task<Track?>> resolve, Func<Track, CancellationToken, Task<PreparedMedia>> prepare,
        bool offlineOnly = false, bool shuffle = false, CancellationToken ct = default)
    {
        await BindListeningAsync(account, resolve, prepare, ct);
        FinishListen("replaced");
        await StopCoreAsync(false);
        Guid entry;
        lock (selectionGate)
        {
            ++queueRequest; selection?.Cancel(); completionArmed = false;
            ResetQueue(); QueueAccount = account; OfflineOnly = offlineOnly; resolveTrack = resolve; prepareTrack = prepare;
            foreach (var id in tracks) entries.Add(new(Guid.NewGuid(), id));
            currentIndex = entries.FindIndex(e => e.TrackId == selected);
            if (currentIndex < 0) throw new UnplayableTrackException("This song is no longer in the selected collection. Refresh your library.");
            RememberOrder();
            if (shuffle)
            {
                var first = entries[currentIndex]; entries.RemoveAt(currentIndex); Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries));
                entries.Insert(0, first); currentIndex = 0;
            }
            Queue = Queue with { Shuffle = shuffle }; entry = entries[currentIndex].Id; currentIndex = -1; UpdateQueue();
        }
        await SelectEntryAsync(entry, ct);
    }

    public void Enqueue(Track track, Func<Guid, CancellationToken, Task<Track?>> resolve,
        Func<Track, CancellationToken, Task<PreparedMedia>> prepare, bool next = false, bool offlineOnly = false)
    {
        lock (selectionGate)
        {
            if (QueueAccount is { } owner && owner != track.Origin.AccountId) throw new InvalidOperationException("The queue belongs to another account.");
            QueueAccount = track.Origin.AccountId;
            if (resolveTrack is null) { resolveTrack = resolve; prepareTrack = prepare; OfflineOnly = offlineOnly; }
            var entry = new QueueEntry(Guid.NewGuid(), track.Id);
            knownTracks[track.Id] = track;
            entries.Insert(next ? currentIndex + 1 : entries.Count, entry);
            var canonical = entries.Where(e => e.Id != entry.Id).OrderBy(e => originalOrder.GetValueOrDefault(e.Id, int.MaxValue)).ToList();
            var insertion = next && CurrentEntryId is { } current ? canonical.FindIndex(e => e.Id == current) + 1 : canonical.Count;
            canonical.Insert(insertion, entry);
            originalOrder.Clear(); for (var i = 0; i < canonical.Count; i++) originalOrder[canonical[i].Id] = i;
            UpdateQueue();
        }
        NotifyChanged();
    }

    public bool HasAdjacent(int delta) { lock (selectionGate) return AdjacentEntry(delta) is not null; }
    private QueueEntry? AdjacentEntry(int delta)
    {
        if (delta < 0 && historyIndex > 0)
            return entries.FirstOrDefault(e => e.Id == history[historyIndex - 1]);
        if (delta > 0 && historyIndex >= 0 && historyIndex + 1 < history.Count)
            return entries.FirstOrDefault(e => e.Id == history[historyIndex + 1]);
        var next = currentIndex + Math.Sign(delta);
        if (next >= 0 && next < entries.Count) return entries[next];
        return delta > 0 && Queue.Repeat == RepeatMode.All ? entries.FirstOrDefault() : null;
    }

    public Task SelectEntryAsync(Guid id, CancellationToken ct = default) => SelectEntryCoreAsync(id, true, ct);

    private async Task SelectEntryCoreAsync(Guid id, bool recordHistory, CancellationToken ct, bool automatic = false,
        (long Generation, long Request)? expected = null)
    {
        long request;
        Func<Guid, CancellationToken, Task<Track?>> resolve;
        Func<Track, CancellationToken, Task<PreparedMedia>> prepare;
        long owner;
        lock (selectionGate)
        {
            if (expected is { } pending && (pending.Generation != generation || pending.Request != queueRequest)) return;
            if (resolveTrack is null || prepareTrack is null || QueueAccount is not { } account) return;
            request = ++queueRequest; selection?.Cancel(); completionArmed = false;
            resolve = resolveTrack; prepare = prepareTrack; owner = account;
        }
        var visited = new HashSet<Guid>();
        while (true)
        {
            QueueEntry entry;
            lock (selectionGate)
            {
                if (request != queueRequest) return;
                entry = entries.FirstOrDefault(e => e.Id == id) ?? throw new UnplayableTrackException("This queue entry was removed.");
                if (!visited.Add(entry.Id)) throw new UnplayableTrackException("No playable songs remain. Reconnect or choose another collection.");
            }
            try
            {
                lock (selectionGate)
                    if (automatic && skipped.TryGetValue(entry.Id, out var notice)) throw new UnplayableTrackException(notice);
                var track = await resolve(entry.TrackId, ct) ?? throw new UnplayableTrackException("This song is no longer in your library.");
                // Refused entries must not become selected or trigger player/checkpoint
                // updates while traversing a large collection.
                if (track.Origin.Protected) throw new UnplayableTrackException("This source restricts playback and saving in ChannelWave.");
                lock (selectionGate)
                {
                    if (request != queueRequest) return;
                    if (track.Origin.AccountId != owner) throw new InvalidOperationException("Cross-account playback rejected.");
                    currentIndex = entries.FindIndex(e => e.Id == entry.Id);
                    if (currentIndex < 0) return;
                    knownTracks[track.Id] = track; skipped.Remove(entry.Id);
                    PlayingSequence = entries.Where(e => knownTracks.ContainsKey(e.TrackId)).Select(e => knownTracks[e.TrackId]).ToArray();
                    UpdateQueue();
                }
                await SelectCoreAsync(track.Id, track, PlayingSequence, prepare, token => prepare(track, token), ct);
                lock (selectionGate)
                {
                    if (request == queueRequest && recordHistory && CanResume)
                    {
                        if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                        if (history.LastOrDefault() != entry.Id) history.Add(entry.Id);
                        if (history.Count > 20000) history.RemoveRange(0, history.Count - 20000);
                        historyIndex = history.Count - 1;
                    }
                }
                return;
            }
            catch (UnplayableTrackException error)
            {
                bool exhausted;
                lock (selectionGate)
                {
                    if (request != queueRequest) return;
                    skipped[entry.Id] = error.Message;
                    var index = entries.FindIndex(e => e.Id == entry.Id);
                    exhausted = index < 0 || index + 1 >= entries.Count;
                    if (exhausted)
                    {
                        Error = "No playable songs remain. Reconnect or choose another collection."; completionArmed = false;
                    }
                    else id = entries[index + 1].Id;
                }
                if (exhausted) { await PauseAsync(); return; }
            }
        }
    }

    private Task NavigateAsync(int delta, bool natural, CancellationToken ct, (long Generation, long Request)? expected = null)
    {
        Guid? target;
        var record = true;
        lock (selectionGate)
        {
            if (expected is { } pending && (pending.Generation != generation || pending.Request != queueRequest)) return Task.CompletedTask;
            if (natural && Queue.Repeat == RepeatMode.One) target = CurrentEntryId;
            else if (delta > 0 && currentIndex == entries.Count - 1 && Queue.Repeat == RepeatMode.All)
            {
                if (Queue.Shuffle && entries.Count > 1)
                {
                    var last = CurrentEntryId;
                    Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries));
                    if (entries[0].Id == last) (entries[0], entries[1]) = (entries[1], entries[0]);
                }
                target = entries.FirstOrDefault()?.Id;
            }
            else
            {
                target = AdjacentEntry(delta)?.Id;
                if (target is not null && delta < 0 && historyIndex > 0) { historyIndex--; record = false; }
                else if (target is not null && delta > 0 && historyIndex + 1 < history.Count) { historyIndex++; record = false; }
            }
        }
        return target is { } id ? SelectEntryCoreAsync(id, record, ct, automatic: true, expected: expected) : Task.CompletedTask;
    }

    public void RemoveEntry(Guid id)
    {
        lock (selectionGate)
        {
            if (id == CurrentEntryId) return;
            var current = CurrentEntryId;
            entries.RemoveAll(e => e.Id == id); PruneHistory();
            currentIndex = current is null ? -1 : entries.FindIndex(e => e.Id == current);
            UpdateQueue();
        }
        NotifyChanged();
    }

    public void MoveEntry(Guid id, int delta)
    {
        lock (selectionGate)
        {
            var index = entries.FindIndex(e => e.Id == id); var target = index + delta;
            if (index <= currentIndex || target <= currentIndex || index < 0 || target >= entries.Count) return;
            var entry = entries[index]; entries.RemoveAt(index); entries.Insert(target, entry);
            Queue = Queue with { Shuffle = false }; RememberOrder(); UpdateQueue();
        }
        NotifyChanged();
    }

    public void ClearUpcoming()
    {
        lock (selectionGate) { entries.RemoveRange(currentIndex + 1, UpcomingCount); PruneHistory(); UpdateQueue(); }
        NotifyChanged();
    }

    public void SetShuffle(bool enabled)
    {
        lock (selectionGate)
        {
            if (Queue.Shuffle == enabled) return;
            var remaining = entries.Skip(currentIndex + 1).ToArray();
            if (enabled) Random.Shared.Shuffle(remaining);
            else remaining = remaining.OrderBy(e => originalOrder.GetValueOrDefault(e.Id, int.MaxValue)).ToArray();
            entries.RemoveRange(currentIndex + 1, remaining.Length); entries.AddRange(remaining);
            Queue = Queue with { Shuffle = enabled }; UpdateQueue();
        }
        NotifyChanged();
    }

    public void CycleRepeat()
    {
        lock (selectionGate) { Queue = Queue with { Repeat = (RepeatMode)(((int)Queue.Repeat + 1) % 3) }; UpdateQueue(); }
        NotifyChanged();
    }

    private void RememberOrder() { originalOrder.Clear(); for (var i = 0; i < entries.Count; i++) originalOrder[entries[i].Id] = i; }
    private void UpdateQueue() { Queue = Queue with { Tracks = entries.Select(e => e.TrackId).ToArray(), Index = Math.Max(0, currentIndex) }; QueueVersion++; }
    private void PruneQueue(HashSet<Guid> removed)
    {
        if (entries.Count == 0) return;
        var current = CurrentEntryId;
        entries.RemoveAll(e => removed.Contains(e.TrackId));
        currentIndex = current is null ? -1 : entries.FindIndex(e => e.Id == current);
        PruneHistory();
        foreach (var id in removed) knownTracks.Remove(id);
        UpdateQueue();
    }

    private void PruneHistory()
    {
        var retained = entries.Select(e => e.Id).ToHashSet();
        var before = history.Take(historyIndex + 1).Count(retained.Contains);
        history.RemoveAll(id => !retained.Contains(id)); historyIndex = before - 1;
    }

    public async Task<IReadOnlyList<QueueRow>> QueuePageAsync(int offset, int limit, CancellationToken ct = default)
    {
        QueueEntry[] page; Func<Guid, CancellationToken, Task<Track?>>? resolve;
        lock (selectionGate) { page = entries.Skip(currentIndex + 1 + offset).Take(limit).ToArray(); resolve = resolveTrack; }
        var rows = new List<QueueRow>();
        foreach (var entry in page)
        {
            var track = resolve is null ? null : await resolve(entry.TrackId, ct);
            lock (selectionGate)
            {
                if (track is not null && track.Origin.AccountId == QueueAccount) knownTracks[track.Id] = track;
                rows.Add(new(entry, track, skipped.GetValueOrDefault(entry.Id)));
            }
        }
        return rows;
    }

    public async Task ReconcileSourceAsync(long account, long chat, CancellationToken ct = default)
    {
        Guid[] ids; Func<Guid, CancellationToken, Task<Track?>>? resolve;
        lock (selectionGate)
        {
            if (QueueAccount != account) return;
            ids = knownTracks.Values.Where(t => t.Origin.ChatId == chat).Select(t => t.Id).ToArray(); resolve = resolveTrack;
        }
        if (resolve is null) return;
        foreach (var id in ids)
        {
            var track = await resolve(id, ct);
            if (track is null) continue;
            if (track.Origin.AccountId != account) continue;
            if (track.Origin.Protected && CurrentTrack?.Id == id) { await RemoveTracksAsync([id]); continue; }
            await UpdateTrackMetadataAsync(track, ct);
            lock (selectionGate)
            {
                if (QueueAccount == account)
                {
                    knownTracks[id] = track;
                    if (track.Available && !track.Origin.Protected)
                        foreach (var entry in entries.Where(e => e.TrackId == id)) skipped.Remove(entry.Id);
                }
            }
        }
        lock (selectionGate) QueueVersion++;
        NotifyChanged();
    }

    private void EnsureMonitoring()
    {
        if (monitoring.IsCompleted) monitoring = MonitorAsync();
    }
    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(playbackLifetime.Token))
            {
                if ((clock ?? TimeProvider.System).GetUtcNow() - lastCheckpoint >= TimeSpan.FromSeconds(5)) await CheckpointAsync();
                await ApplyRetentionAsync();
                var epoch = loadedGeneration;
                if (IsPreparing || !CanResume) continue;
                try
                {
                    var state = await StateAsync(playbackLifetime.Token);
                    if (epoch != loadedGeneration || IsPreparing) continue;
                    RecordObservation(state);
                    if (state?.Ended == true && completionArmed) await AudioSignalAsync(epoch, "ended", 0);
                    else if (state?.Error is not null) { await PauseAsync(); Error = state.Error; NotifyChanged(); }
                }
                catch (OperationCanceledException) when (playbackLifetime.IsCancellationRequested) { break; }
                catch (Exception e) { Error = e.Message.Split('\n')[0]; NotifyChanged(); }
            }
        }
        catch (OperationCanceledException) when (playbackLifetime.IsCancellationRequested) { }
    }

    public async Task AudioSignalAsync(long epoch, string action, double value = 0)
    {
        if (epoch != loadedGeneration || playbackLifetime.IsCancellationRequested) return;
        try
        {
            switch (action)
            {
                case "ended":
                    (long Generation, long Request) completion;
                    await commands.WaitAsync(playbackLifetime.Token);
                    try { if (epoch == loadedGeneration && completionArmed && !IsPreparing) await ObserveEngineAsync(); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // A final position sample is optional. Losing it must not discard
                        // an authoritative completion notification and disarm the queue.
                        recorder.Break();
                    }
                    finally { commands.Release(); }
                    lock (selectionGate)
                    {
                        if (!completionArmed || IsPreparing || epoch != loadedGeneration) return;
                        completionArmed = false; IsPlaying = false;
                        completion = (generation, queueRequest);
                        FinishListen("completed");
                    }
                    await NavigateAsync(1, true, playbackLifetime.Token, completion); NotifyChanged(); break;
                case "seeking": recorder.Break(); break;
                case "output-changed":
                    if (engine is not IAudioRouting route) return;
                    await commands.WaitAsync(playbackLifetime.Token);
                    try
                    {
                        if (epoch == loadedGeneration && CanResume && !IsPreparing)
                            SetActiveOutput(await route.GetActiveOutputAsync(playbackLifetime.Token));
                    }
                    finally { commands.Release(); }
                    break;
                case "nexttrack": await SkipAsync(1, playbackLifetime.Token); break;
                case "previoustrack": await SkipAsync(-1, playbackLifetime.Token); break;
                case "play": await ResumeAsync(playbackLifetime.Token); break;
                case "pause": await PauseAsync(); break;
                case "output-lost":
                    await commands.WaitAsync(playbackLifetime.Token);
                    try
                    {
                        if (epoch != loadedGeneration || !CanResume || IsPreparing) return;
                        completionArmed = false;
                        var position = Queue.Position;
                        await engine.PauseAsync();
                        recorder.Break();
                        IsPlaying = false;
                        if (engine is IAudioRouting routing)
                        {
                            if (ActiveOutput.Remote)
                                await routing.SelectOutputAsync(AudioOutput.Default.Id, position, false, playbackLifetime.Token);
                            SetActiveOutput(await routing.GetActiveOutputAsync(playbackLifetime.Token));
                        }
                        await ObserveEngineAsync();
                        Error = "Audio output disconnected. Choose where to resume playback.";
                    }
                    finally { commands.Release(); }
                    NotifyChanged();
                    break;
                case "stop": await StopAsync(); break;
                case "seekto": await SeekAsync(value); break;
                case "decode-error":
                    lock (selectionGate)
                    {
                        if (!completionArmed || IsPreparing || epoch != loadedGeneration) return;
                        completionArmed = false; IsPlaying = false;
                        Error = "This device cannot decode this recording. Choose another song.";
                        if (CurrentEntryId is { } entry) skipped[entry] = Error;
                    }
                    await NavigateAsync(1, false, playbackLifetime.Token); NotifyChanged(); break;
                case "error":
                    if (!completionArmed || IsPreparing) return;
                    await PauseAsync(); Error = "Playback stopped. Check your connection, then retry."; NotifyChanged(); break;
            }
        }
        catch (OperationCanceledException) when (playbackLifetime.IsCancellationRequested) { }
        catch (Exception e) { completionArmed = false; Error = e.Message.Split('\n')[0]; NotifyChanged(); }
    }
}
