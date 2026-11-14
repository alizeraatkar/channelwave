using System.Text.Json;

namespace ChannelWave.Core;

public sealed partial class PlaybackCoordinator
{
    private readonly ListeningRecorder recorder = new(clock ?? TimeProvider.System);
    private readonly SemaphoreSlim persistenceGate = new(1, 1), bindingGate = new(1, 1);
    private readonly Dictionary<Guid, ListeningEvent> pendingListens = [];
    private ListeningState listeningState = new();
    private long dirtyRevision, savedRevision, observationSequence;
    private DateTimeOffset lastCheckpoint;
    private DateTimeOffset lastRetention;
    private bool binding, persistenceReadOnly;
    private ListeningTrack? restoredReference;
    public long? ListeningAccount { get; private set; }
    public ListeningPreferences ListeningPreferences { get; private set; } = new();
    public double OutputVolume { get; private set; } = 1;
    public bool Muted { get; private set; }
    public bool RestoredPaused { get; private set; }
    public string? PersistenceError { get; private set; }
    public string? RecoveryNotice { get; private set; }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public PlaybackContext Context { get; private set; } = new();
    public event Action<AudioState?>? Observed;
    public event Action? PersistenceChanged;
    public void SetContext(PlaybackContext context) { Context = context; NotifyChanged(); }

    private void NotifyChanged()
    {
        Changed?.Invoke();
        if (binding || ListeningAccount is null || playbackLifetime.IsCancellationRequested) return;
        Interlocked.Increment(ref dirtyRevision);
        _ = CheckpointAsync();
    }

    public async Task BindListeningAsync(long? account, Func<Guid, CancellationToken, Task<Track?>>? resolve = null,
        Func<Track, CancellationToken, Task<PreparedMedia>>? prepare = null, CancellationToken ct = default)
    {
        if (listeningStore is null) return;
        await bindingGate.WaitAsync(ct);
        var changedAccount = false;
        try
        {
            if (ListeningAccount == account) return;
            changedAccount = true;
            if (ListeningAccount is not null) { await PauseAsync(); await CheckpointAsync(); }
            binding = true;
            await persistenceGate.WaitAsync(ct);
            try
            {
                if (ListeningAccount is not null) await StopCoreAsync(false);
                lock (selectionGate) { pendingListens.Clear(); recorder.Finish("account-changed"); }
                ListeningAccount = account; listeningState = new(); ListeningPreferences = new();
                persistenceReadOnly = false; PersistenceError = RecoveryNotice = null;
                if (account is not { } owner) return;
                persistenceReadOnly = true;
                listeningState = await listeningStore.ReadListeningStateAsync(owner, ct);
                ListeningPreferences = listeningState.Preferences ?? new();
                OutputVolume = double.IsFinite(ListeningPreferences.Volume) ? Math.Clamp(ListeningPreferences.Volume, 0, 1) : 1;
                Muted = ListeningPreferences.Muted;
                SavedPlayerSnapshot? snapshot = null;
                foreach (var envelope in new[] { listeningState.Player, listeningState.PreviousPlayer })
                {
                    if (envelope is null) continue;
                    try { snapshot = envelope.Read(owner); }
                    catch (JsonException) { }
                    if (snapshot is not null) break;
                    RecoveryNotice = "The latest saved player was damaged. Recovered the previous valid state if available.";
                }
                if (snapshot is not null && ListeningPreferences.RememberPlayer && resolve is not null && prepare is not null)
                {
                    var current = snapshot.Current is { } reference ? await resolve(reference.Id, ct) : null;
                    lock (selectionGate)
                    {
                        ResetQueue(); QueueAccount = owner; OfflineOnly = snapshot.OfflineOnly;
                        entries.AddRange(snapshot.Entries); currentIndex = entries.FindIndex(e => e.Id == snapshot.CurrentEntry);
                        for (var i = 0; i < snapshot.OriginalOrder.Length; i++) originalOrder[snapshot.OriginalOrder[i]] = i;
                        history.AddRange(snapshot.Navigation); historyIndex = snapshot.NavigationIndex;
                        resolveTrack = resolve; prepareTrack = prepare; restoredReference = snapshot.Current;
                        CurrentTrack = current ?? snapshot.Current?.Placeholder(); SelectedTrack = CurrentTrack?.Id;
                        if (CurrentTrack is { } track) knownTracks[track.Id] = track;
                        Queue = new(entries.Select(e => e.TrackId).ToArray(), Math.Max(0, currentIndex), snapshot.Position, snapshot.Repeat, snapshot.Shuffle);
                        OutputVolume = snapshot.Volume; Muted = snapshot.Muted; Context = snapshot.Context;
                        RestoredPaused = CurrentTrack is not null; IsPlaying = false; completionArmed = false; UpdateQueue();
                    }
                    if (snapshot.ActiveListen is { } listen && ListeningPreferences.Collect && listeningState.SuppressedListen != listen)
                        recorder.Restore(await listeningStore.ListenAsync(owner, listen, ct));
                    recorder.Suppressed = snapshot.Suppressed || snapshot.ActiveListen is { } active && listeningState.SuppressedListen == active;
                }
                persistenceReadOnly = false; savedRevision = dirtyRevision; EnsureMonitoring();
            }
            finally { persistenceGate.Release(); }
            if (account is { } selected && ListeningPreferences.RetentionDays > 0)
                await DeleteListeningAsync(null, (clock ?? TimeProvider.System).GetUtcNow().AddDays(-ListeningPreferences.RetentionDays).ToUnixTimeMilliseconds(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ListeningPreferences = ListeningPreferences with { Collect = false };
            PersistenceError = e.Message.Split('\n')[0];
        }
        finally
        {
            binding = false; bindingGate.Release();
            if (changedAccount) { Changed?.Invoke(); PersistenceChanged?.Invoke(); }
        }
    }

    private SavedPlayerSnapshot CapturePlayer(long account)
    {
        var retained = entries.Select(e => e.Id).ToHashSet();
        return new(1, account, entries.ToArray(), CurrentEntryId,
            entries.OrderBy(e => originalOrder.GetValueOrDefault(e.Id, int.MaxValue)).Select(e => e.Id).ToArray(),
            history.Where(retained.Contains).ToArray(), historyIndex, Math.Max(0, Queue.Position), Queue.Repeat,
            Queue.Shuffle, OutputVolume, Muted, OfflineOnly, Context,
            CurrentTrack is null ? null : RestoredPaused && restoredReference is not null ? restoredReference : ListeningTrack.From(CurrentTrack),
            recorder.Current?.Id, recorder.Suppressed);
    }

    public async Task CheckpointAsync()
    {
        if (listeningStore is null || ListeningAccount is null || persistenceReadOnly) return;
        await persistenceGate.WaitAsync();
        try
        {
            if (ListeningAccount is not { } account || persistenceReadOnly) return;
            ListeningEvent[] events; PlaybackEnvelope? snapshot; long revision;
            lock (selectionGate)
            {
                revision = dirtyRevision;
                if (revision == savedRevision && PersistenceError is null) return;
                events = pendingListens.Values.ToArray();
                snapshot = ListeningPreferences.RememberPlayer && entries.Count > 0 ? PlaybackEnvelope.Create(CapturePlayer(account)) : null;
            }
            var preferences = ListeningPreferences;
            var saved = await listeningStore.CommitListeningAsync(new(account, listeningState.Revision, listeningState.Generation,
                preferences, snapshot, events, !preferences.RememberPlayer || entries.Count == 0));
            listeningState = listeningState with { Revision = saved, Preferences = preferences, PreviousPlayer = listeningState.Player, Player = snapshot };
            lock (selectionGate)
                foreach (var item in events) if (pendingListens.TryGetValue(item.Id, out var current) && ReferenceEquals(current, item)) pendingListens.Remove(item.Id);
            savedRevision = revision; lastCheckpoint = (clock ?? TimeProvider.System).GetUtcNow(); LastSavedAt = lastCheckpoint;
            var recovered = PersistenceError is not null; PersistenceError = null;
            if (recovered || events.Length > 0) PersistenceChanged?.Invoke();
        }
        catch (Exception e)
        {
            var message = "Recent listening and player progress could not be saved. " + e.Message.Split('\n')[0];
            if (PersistenceError != message) { PersistenceError = message; PersistenceChanged?.Invoke(); }
        }
        finally { persistenceGate.Release(); }
    }

    public async Task RetryPersistenceAsync()
    {
        if (listeningStore is null || ListeningAccount is not { } account) return;
        if (persistenceReadOnly)
        {
            try
            {
                var state = await listeningStore.ReadListeningStateAsync(account);
                if (state.Player is { Version: > 1 } || state.PreviousPlayer is { Version: > 1 })
                    throw new NotSupportedException("Update ChannelWave to read this saved player.");
                listeningState = state; ListeningPreferences = state.Preferences ?? new();
                persistenceReadOnly = false;
            }
            catch (Exception e) { PersistenceError = e.Message; PersistenceChanged?.Invoke(); return; }
        }
        Interlocked.Increment(ref dirtyRevision); await CheckpointAsync();
    }

    private async Task ApplyRetentionAsync()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (ListeningPreferences.RetentionDays == 0 || now - lastRetention < TimeSpan.FromDays(1)) return;
        lastRetention = now;
        try { await DeleteListeningAsync(null, now.AddDays(-ListeningPreferences.RetentionDays).ToUnixTimeMilliseconds()); }
        catch (Exception e) { PersistenceError = "Old listening records could not be removed. " + e.Message; PersistenceChanged?.Invoke(); }
    }

    private void FinishListen(string outcome)
    {
        lock (selectionGate)
        {
            if (recorder.Finish(outcome) is { } listen) pendingListens[listen.Id] = listen;
            Interlocked.Increment(ref dirtyRevision);
        }
    }
    private void RecordObservation(AudioState? state)
    {
        if (state is null) return;
        lock (selectionGate)
        {
            state = state with { Muted = Muted || state.Muted };
            Queue = Queue with { Position = double.IsFinite(state.Position) ? Math.Max(0, state.Position) : Queue.Position };
            if (CurrentTrack is { } track && ListeningAccount == track.Origin.AccountId && pendingListens.Count < 128)
            {
                if (recorder.Observe(++observationSequence, track, CurrentEntryId, state, ListeningPreferences.Collect, OfflineOnly) is { } listen)
                    pendingListens[listen.Id] = listen;
            }
            else recorder.Break();
            Interlocked.Increment(ref dirtyRevision);
        }
        Observed?.Invoke(state);
    }
    private async Task ObserveEngineAsync()
    {
        if (CanResume) RecordObservation(await engine.StateAsync(playbackLifetime.Token));
    }
    public async Task SetListeningPreferencesAsync(ListeningPreferences preferences, CancellationToken ct = default)
    {
        if (preferences.RetentionDays is not (0 or 90 or 365)) throw new ArgumentException("Choose until deleted, 90 days or one year.");
        if (ListeningPreferences.Collect != preferences.Collect) FinishListen("collection-paused");
        ListeningPreferences = preferences;
        NotifyChanged(); await CheckpointAsync();
        if (preferences.RetentionDays > 0) await DeleteListeningAsync(null,
            (clock ?? TimeProvider.System).GetUtcNow().AddDays(-preferences.RetentionDays).ToUnixTimeMilliseconds(), ct);
    }
    public async Task DeleteListeningAsync(Guid? id, long? olderThan = null, CancellationToken ct = default)
    {
        if (listeningStore is null || ListeningAccount is not { } account) return;
        await persistenceGate.WaitAsync(ct);
        try
        {
            var state = await listeningStore.DeleteListeningAsync(account, id, olderThan, ct);
            lock (selectionGate)
            {
                listeningState = state;
                foreach (var listen in pendingListens.Values.ToArray())
                    if ((id is null || listen.Id == id) && (olderThan is null || listen.LastActive < olderThan)) pendingListens.Remove(listen.Id);
                if (olderThan is null && (id is null || recorder.Current?.Id == id))
                { recorder.Finish("deleted"); recorder.Suppressed = true; }
                else if (olderThan is { } cutoff && recorder.Current is { } expired && expired.LastActive < cutoff)
                    recorder.Finish("retention-expired");
                Interlocked.Increment(ref dirtyRevision);
            }
        }
        finally { persistenceGate.Release(); }
        await CheckpointAsync(); PersistenceChanged?.Invoke(); Changed?.Invoke();
    }
    public async Task SetMutedAsync(bool muted)
    {
        await commands.WaitAsync();
        try { await ObserveEngineAsync(); recorder.Break(); Muted = muted; ListeningPreferences = ListeningPreferences with { Muted = muted }; await engine.SetVolumeAsync(muted ? 0 : OutputVolume); }
        finally { commands.Release(); }
        NotifyChanged();
    }
    private async Task ResumeRestoredAsync(CancellationToken ct)
    {
        var id = SelectedTrack ?? throw new UnplayableTrackException("Choose another song from your library.");
        var resolver = resolveTrack ?? throw new UnplayableTrackException("Reconnect and retry playback.");
        var prepare = prepareTrack ?? throw new UnplayableTrackException("Reconnect and retry playback.");
        var track = await resolver(id, ct) ?? throw new UnplayableTrackException("This song is no longer in your library. Choose the next song.");
        var position = Queue.Position;
        if (restoredReference is { } reference && reference.MediaKey != Metadata.MediaKey(track))
        { position = 0; FinishListen("media-changed"); RecoveryNotice = "The recording changed. Playback will start from the beginning."; }
        await SelectCoreAsync(track.Id, track, [], prepare, token => prepare(track, token), ct, position, true);
    }
    public Task RetryAsync(CancellationToken ct = default) => ResumeRestoredAsync(ct);
}
