using ChannelWave.Core;
using ChannelWave.Host.Maui;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class PlaybackCompletionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Poll_recovers_EOF_when_the_native_completion_event_is_missing()
    {
        // Arrange
        var engine = new CompletionEngine { Position = 10 };
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3);
        await Start(playback, tracks);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Changed += () => { if (playback.SelectedTrack == tracks[1].Id && playback.IsPlaying) advanced.TrySetResult(); };

        // Act
        engine.LoseCompletion();
        await advanced.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.Equal(tracks[1].Id, playback.SelectedTrack);
        Assert.Equal(2, engine.Loads);
    }

    [Theory]
    [InlineData(true, false, 10, 10, true)]
    [InlineData(true, false, 9.98, 10, true)]
    [InlineData(false, false, 10, 10, false)]
    [InlineData(true, true, 10, 10, false)]
    [InlineData(true, false, 5, 10, false)]
    [InlineData(true, false, 0, 0, false)]
    [InlineData(true, false, double.NaN, 10, false)]
    [InlineData(true, false, 10, double.PositiveInfinity, false)]
    public void EOF_monitoring_requires_observed_playback_and_valid_terminal_timestamps(
        bool observedPlaying, bool paused, double position, double duration, bool ended)
    {
        // Arrange
        var completion = new AndroidPlaybackCompletion();
        var token = completion.Start();
        if (observedPlaying) completion.ObserveState(true, 0, duration);
        if (paused) completion.Invalidate();

        // Act
        completion.ObserveState(false, position, duration);

        // Assert
        Assert.Equal(ended, completion.Ended);
        if (ended) Assert.False(completion.TryComplete(token));
    }

    [Fact]
    public async Task Completion_still_advances_when_the_final_position_read_fails()
    {
        // Arrange
        var engine = new CompletionEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3);
        await Start(playback, tracks);
        engine.FailNextState = true;

        // Act
        await engine.CompleteAsync(notify: true);

        // Assert
        Assert.Equal(tracks[1].Id, playback.SelectedTrack);
        Assert.True(playback.IsPlaying);
        Assert.Null(playback.Error);
    }

    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(0, 0, false)]
    [InlineData(9, 10, true)]
    [InlineData(0, 0, true)]
    public async Task Native_completion_advances_without_requiring_position_to_match_duration(double position, double duration, bool poll)
    {
        // Arrange
        var engine = new CompletionEngine { Position = position, Duration = duration };
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3);
        await Start(playback, tracks);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Changed += () => { if (playback.SelectedTrack == tracks[1].Id && playback.IsPlaying) advanced.TrySetResult(); };

        // Act
        await engine.CompleteAsync(notify: !poll);
        await advanced.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert
        Assert.Equal(tracks[1].Id, playback.SelectedTrack);
        Assert.Equal(2, engine.Loads);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task Poll_and_direct_notifications_for_the_same_completion_advance_once()
    {
        // Arrange
        var engine = new CompletionEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3);
        await Start(playback, tracks);
        var epoch = engine.Epoch;
        Task duplicate = Task.CompletedTask;
        var sent = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Observed += state =>
        {
            if (state?.Ended != true || Interlocked.Exchange(ref sent, 1) != 0) return;
            duplicate = engine.SignalAsync(epoch);
            observed.TrySetResult();
        };

        // Act
        await engine.CompleteAsync(notify: false);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await duplicate;
        await engine.SignalAsync(epoch);

        // Assert
        Assert.Equal(tracks[1].Id, playback.SelectedTrack);
        Assert.Equal(2, engine.Loads);
    }

    [Theory]
    [InlineData(RepeatMode.Off, false)]
    [InlineData(RepeatMode.All, true)]
    [InlineData(RepeatMode.One, true)]
    public async Task Single_song_completion_obeys_repeat(RepeatMode repeat, bool playing)
    {
        // Arrange
        var engine = new CompletionEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(1);
        await Start(playback, tracks);
        for (var i = 0; i < (int)repeat; i++) playback.CycleRepeat();

        // Act
        await engine.CompleteAsync(notify: true);

        // Assert
        Assert.Equal(tracks[0].Id, playback.SelectedTrack);
        Assert.Equal(playing, playback.IsPlaying);
        Assert.Equal(playing ? 2 : 1, engine.Loads);
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("stop", false)]
    [InlineData("select", false)]
    [InlineData("pause", true)]
    [InlineData("stop", true)]
    [InlineData("select", true)]
    public async Task User_command_cancels_completion_waiting_for_the_next_track(string action, bool preparing)
    {
        // Arrange
        var engine = new CompletionEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var tracks = Tracks(3);
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Track?> Resolve(Guid id, CancellationToken ct)
        {
            if (!preparing && id == tracks[1].Id) { resolving.TrySetResult(); await release.Task.WaitAsync(Token); }
            return tracks.FirstOrDefault(t => t.Id == id);
        }
        async Task<PreparedMedia> PrepareNext(Track track, CancellationToken ct)
        {
            if (preparing && track.Id == tracks[1].Id) { resolving.TrySetResult(); await release.Task.WaitAsync(ct); }
            return await Prepare(track, ct);
        }
        await playback.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[0].Id, Resolve, PrepareNext, ct: Token);
        var epoch = engine.Epoch;

        // Act
        var pending = engine.CompleteAsync(notify: true);
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        try
        {
            if (action == "pause") await playback.PauseAsync();
            else if (action == "stop") await playback.StopAsync();
            else await playback.SelectEntryAsync(playback.Entries[2].Id, Token);
        }
        finally { release.TrySetResult(); }
        await pending;
        await engine.SignalAsync(epoch);

        // Assert
        Assert.Equal(action == "stop" ? (Guid?)null : action == "select" ? tracks[2].Id : tracks[preparing ? 1 : 0].Id, playback.SelectedTrack);
        Assert.Equal(action == "select", playback.IsPlaying);
        Assert.Equal(action == "select" ? 2 : 1, engine.Loads);
    }

    [Fact]
    public void Native_completion_ignores_duplicates_and_invalidated_playback_tokens()
    {
        // Arrange
        var completion = new AndroidPlaybackCompletion();
        var first = completion.Start();

        // Act
        var completed = completion.TryComplete(first);
        var duplicate = completion.TryComplete(first);
        var second = completion.Start();
        var stale = completion.TryComplete(first);
        completion.Invalidate();
        var paused = completion.TryComplete(second);

        // Assert
        Assert.True(completed);
        Assert.False(duplicate);
        Assert.False(stale);
        Assert.False(paused);
        Assert.False(completion.Ended);
    }

    private static Track[] Tracks(int count) => Enumerable.Range(1, count).Select(i => Fixture.Track(new(7, 1, "Synthetic source"), i)).ToArray();
    private static Task<PreparedMedia> Prepare(Track track, CancellationToken ct) => Task.FromResult(new PreparedMedia(track.Title, false, 1));
    private static Task Start(PlaybackCoordinator playback, Track[] tracks) => playback.StartQueueAsync(7,
        tracks.Select(t => t.Id).ToArray(), tracks[0].Id, (id, _) => Task.FromResult(tracks.FirstOrDefault(t => t.Id == id)), Prepare, ct: Token);

    private sealed class CompletionEngine : IAudioEngine, IAudioNotifications
    {
        private readonly AndroidPlaybackCompletion completion = new();
        private long token;
        private bool playing;
        private Func<long, string, double, Task>? notify;
        public long Epoch { get; private set; }
        public int Loads { get; private set; }
        public bool FailNextState { get; set; }
        public double Position { get; init; } = 9;
        public double Duration { get; init; } = 10;
        public Task ObserveAsync(long id, Func<long, string, double, Task> callback, CancellationToken ct)
        { Epoch = id; notify = callback; return Task.CompletedTask; }
        public Task SignalAsync(long epoch) => notify!(epoch, "ended", 0);
        public void LoseCompletion() => playing = false;
        public Task CompleteAsync(bool notify)
        {
            if (!completion.TryComplete(token)) return Task.CompletedTask;
            playing = false;
            return notify ? SignalAsync(Epoch) : Task.CompletedTask;
        }
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Loads++; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { playing = true; token = completion.Start(); return Task.CompletedTask; }
        public Task PauseAsync() { playing = false; completion.Invalidate(); return Task.CompletedTask; }
        public Task UnloadAsync() { playing = false; completion.Invalidate(); completion.ClearEnded(); return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public async Task<AudioState?> StateAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            if (FailNextState) { FailNextState = false; throw new IOException("The final native position sample was unavailable."); }
            completion.ObserveState(playing, Position, Duration);
            return new(Position, Duration, Duration, !playing, completion.Ended, 1);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
