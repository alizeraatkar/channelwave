using ChannelWave.Core;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class EditableQueueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Duplicate_entries_are_independent_and_clearing_upcoming_preserves_current_audio()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3); await Start(playback, tracks);

        // Act
        playback.Enqueue(tracks[1], Resolve(tracks), Prepare, next: true);
        var first = playback.Upcoming[0]; var duplicate = playback.Upcoming[1];
        playback.RemoveEntry(first.Id);
        var remaining = playback.Upcoming.ToArray();
        playback.ClearUpcoming();

        // Assert
        Assert.Equal(first.TrackId, duplicate.TrackId); Assert.NotEqual(first.Id, duplicate.Id);
        Assert.Contains(duplicate, remaining); Assert.DoesNotContain(first, remaining);
        Assert.Empty(playback.Upcoming); Assert.Equal(tracks[0].Id, playback.SelectedTrack);
        Assert.True(playback.IsPlaying); Assert.Equal(1, engine.Loads);
    }

    [Fact]
    public async Task Natural_completion_advances_once_and_old_or_paused_signals_do_not_advance()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3); await Start(playback, tracks); var firstEpoch = engine.Epoch;

        // Act
        await engine.Signal(firstEpoch, "ended"); await engine.Signal(firstEpoch, "ended");
        var second = playback.SelectedTrack;
        await playback.PauseAsync(); await engine.Signal(engine.Epoch, "ended");

        // Assert
        Assert.Equal(tracks[1].Id, second); Assert.Equal(second, playback.SelectedTrack);
        Assert.Equal(2, engine.Loads); Assert.False(playback.IsPlaying);
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.One)]
    [InlineData(RepeatMode.All)]
    public async Task Natural_completion_obeys_repeat_and_manual_next_escapes_repeat_one(RepeatMode repeat)
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(2); await Start(playback, tracks);
        for (var i = 0; i < (int)repeat; i++) playback.CycleRepeat();

        // Act
        await engine.Signal(engine.Epoch, "ended"); var afterFirst = playback.SelectedTrack;
        if (repeat == RepeatMode.One) await playback.SkipAsync(1, Token);
        await engine.Signal(engine.Epoch, "ended");

        // Assert
        Assert.Equal(repeat == RepeatMode.One ? tracks[0].Id : tracks[1].Id, afterFirst);
        Assert.Equal(repeat == RepeatMode.All ? tracks[0].Id : tracks[1].Id, playback.SelectedTrack);
        Assert.Equal(repeat != RepeatMode.Off, playback.IsPlaying);
    }

    [Fact]
    public async Task Shuffle_visits_each_entry_once_and_previous_follows_actual_history()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(12); await Start(playback, tracks); playback.SetShuffle(true);
        var expected = playback.Entries.Select(e => e.TrackId).ToArray(); var heard = new List<Guid> { playback.SelectedTrack!.Value };

        // Act
        for (var i = 1; i < tracks.Length; i++) { await engine.Signal(engine.Epoch, "ended"); heard.Add(playback.SelectedTrack!.Value); }
        await playback.SkipAsync(-1, Token); var previous = playback.SelectedTrack;
        await playback.SkipAsync(1, Token);

        // Assert
        Assert.Equal(expected, heard); Assert.Equal(tracks.Length, heard.Distinct().Count());
        Assert.Equal(heard[^2], previous); Assert.Equal(heard[^1], playback.SelectedTrack);
    }

    [Fact]
    public async Task Play_next_has_priority_while_shuffled_and_manual_move_disables_shuffle()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(5); await Start(playback, tracks); playback.SetShuffle(true);

        // Act
        playback.Enqueue(tracks[4], Resolve(tracks), Prepare, next: true);
        var immediate = playback.Upcoming[0];
        await playback.SkipAsync(1, Token);
        var upcoming = playback.Upcoming[1]; playback.MoveEntry(upcoming.Id, -1);

        // Assert
        Assert.Equal(immediate.Id, playback.CurrentEntryId); Assert.Equal(tracks[4].Id, playback.SelectedTrack);
        Assert.Equal(upcoming.Id, playback.Upcoming[0].Id); Assert.False(playback.Queue.Shuffle);
    }

    [Fact]
    public async Task Protected_entries_do_not_select_load_or_prepare_during_large_queue_traversal()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(150).Select((track, index) => index is > 0 and < 149
            ? track with { Origin = track.Origin with { Protected = true } } : track).ToArray();
        var prepared = new List<Guid>(); var selected = new List<Guid>();
        playback.Changed += () => { if (playback.SelectedTrack is { } id) selected.Add(id); };
        Task<PreparedMedia> PrepareAllowed(Track track, CancellationToken ct) { prepared.Add(track.Id); return Prepare(track, ct); }
        await playback.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[0].Id, Resolve(tracks), PrepareAllowed, ct: Token);

        // Act
        await playback.SkipAsync(1, Token);

        // Assert
        Assert.Equal(tracks[^1].Id, playback.SelectedTrack);
        Assert.Equal(new[] { tracks[0].Id, tracks[^1].Id }, prepared);
        Assert.All(selected, id => Assert.True(id == tracks[0].Id || id == tracks[^1].Id));
        Assert.Equal(2, engine.Loads);
    }

    [Fact]
    public async Task Unavailable_entries_are_skipped_once_but_network_failure_stops_traversal()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(4); var attempted = new List<Guid>();
        Task<PreparedMedia> PrepareSome(Track track, CancellationToken ct)
        {
            attempted.Add(track.Id);
            if (track.Id == tracks[1].Id) throw new UnplayableTrackException("Removed from Telegram");
            if (track.Id == tracks[2].Id) throw new IOException("Connection lost. Retry playback.");
            return Prepare(track, ct);
        }
        await playback.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[0].Id, Resolve(tracks), PrepareSome, ct: Token);

        // Act
        await engine.Signal(engine.Epoch, "ended");

        // Assert
        Assert.Equal(tracks.Take(3).Select(t => t.Id), attempted);
        Assert.Contains("Connection lost", playback.Error); Assert.False(playback.IsPlaying);
        Assert.DoesNotContain(tracks[3].Id, attempted);
    }

    [Fact]
    public async Task Exhausted_unavailable_queue_pauses_and_decode_errors_cannot_loop_with_repeat_all()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(2); await Start(playback, tracks);
        playback.CycleRepeat(); playback.CycleRepeat();

        // Act
        await engine.Signal(engine.Epoch, "decode-error");
        await engine.Signal(engine.Epoch, "decode-error");

        // Assert
        Assert.False(playback.IsPlaying); Assert.Equal(2, engine.Loads);
        Assert.Contains("No playable songs", playback.Error);
    }

    [Fact]
    public async Task Queue_uses_full_id_snapshot_and_hydrates_only_requested_tracks()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(10001); var reads = 0;
        Task<Track?> Read(Guid id, CancellationToken ct) { reads++; return Resolve(tracks)(id, ct); }

        // Act
        await playback.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[9999].Id, Read, Prepare, ct: Token);
        await playback.SkipAsync(1, Token);

        // Assert
        Assert.Equal(10001, playback.Entries.Count); Assert.Equal(tracks[10000].Id, playback.SelectedTrack);
        Assert.Equal(2, reads); Assert.Equal(2, engine.Loads);
    }

    [Fact]
    public async Task Switching_account_and_late_queue_resolution_cannot_replace_new_selection()
    {
        // Arrange
        var engine = new QueueEngine(); await using var playback = new PlaybackCoordinator(engine);
        var old = Tracks(1); var current = Tracks(1); var late = new TaskCompletionSource<Track?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = playback.StartQueueAsync(7, [old[0].Id], old[0].Id, (_, _) => late.Task, Prepare, ct: Token);

        // Act
        await Start(playback, current); late.SetResult(old[0]); await pending;

        // Assert
        Assert.Equal(current[0].Id, playback.SelectedTrack); Assert.Equal(1, engine.Loads);
        var wrong = current[0] with { Origin = current[0].Origin with { AccountId = 8 } };
        Assert.Throws<InvalidOperationException>(() => playback.Enqueue(wrong, Resolve(current), Prepare));
    }

    private static Track[] Tracks(int count) => Enumerable.Range(1, count).Select(i => Fixture.Track(new(7, 1, "Synthetic source"), i)).ToArray();
    private static Func<Guid, CancellationToken, Task<Track?>> Resolve(Track[] tracks) => (id, _) => Task.FromResult(tracks.FirstOrDefault(t => t.Id == id));
    private static Task<PreparedMedia> Prepare(Track track, CancellationToken ct) => Task.FromResult(new PreparedMedia(track.Title, false, 1));
    private static Task Start(PlaybackCoordinator playback, Track[] tracks) => playback.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[0].Id, Resolve(tracks), Prepare, ct: Token);

    private sealed class QueueEngine : IAudioEngine, IAudioNotifications
    {
        public long Epoch { get; private set; }
        public int Loads { get; private set; }
        private bool playing;
        private Func<long, string, double, Task>? notify;
        public Task ObserveAsync(long id, Func<long, string, double, Task> callback, CancellationToken ct) { Epoch = id; notify = callback; return Task.CompletedTask; }
        public Task Signal(long epoch, string action) => notify!(epoch, action, 0);
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Loads++; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { playing = true; return Task.CompletedTask; }
        public Task PauseAsync() { playing = false; return Task.CompletedTask; }
        public Task UnloadAsync() { playing = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public Task<AudioState?> StateAsync(CancellationToken ct = default) => Task.FromResult<AudioState?>(new(1, 10, 10, !playing, false, 1));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
