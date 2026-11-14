using ChannelWave.Core;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class AudioRoutingTests
{
    [Fact]
    public async Task Switching_output_keeps_the_track_and_disconnect_pauses_at_the_current_position()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new RoutingEngine();
        await using var player = new PlaybackCoordinator(engine);
        var song = Guid.NewGuid();
        await player.SelectAsync(song, _ => Task.FromResult(new PreparedMedia("test", false, 1)), ct);
        await player.SeekAsync(19);
        await player.SelectOutputAsync("cast:test", ct);

        Assert.Equal(song, player.SelectedTrack);
        Assert.True(player.ActiveOutput.Remote);
        Assert.True(player.IsPlaying);
        Assert.Equal(19, engine.Position);

        await player.AudioSignalAsync(engine.PlaybackId, "output-lost");
        Assert.Equal(AudioOutput.Default, player.ActiveOutput);
        Assert.Equal(song, player.SelectedTrack);
        Assert.False(player.IsPlaying);
        Assert.Equal(19, (await player.StateAsync(ct))!.Position);
    }

    [Fact]
    public async Task Selecting_a_new_song_cancels_a_pending_output_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new RoutingEngine { DelayTransfer = true };
        await using var player = new PlaybackCoordinator(engine);
        static Task<PreparedMedia> Prepare(CancellationToken _) => Task.FromResult(new PreparedMedia("test", false, 1));
        await player.SelectAsync(Guid.NewGuid(), Prepare, ct);

        var transfer = player.SelectOutputAsync("cast:test", ct);
        await engine.TransferStarted.Task.WaitAsync(ct);
        var next = Guid.NewGuid();
        await player.SelectAsync(next, Prepare, ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        Assert.Equal(next, player.SelectedTrack);
        Assert.Equal(AudioOutput.Default, player.ActiveOutput);
    }

    private sealed class RoutingEngine : IAudioEngine, IAudioRouting, IAudioNotifications
    {
        public bool DelayTransfer { get; init; }
        public TaskCompletionSource TransferStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long PlaybackId { get; private set; }
        public double Position { get; private set; }
        private bool playing;
        private AudioOutput active = AudioOutput.Default;

        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Position = 0; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { playing = true; return Task.CompletedTask; }
        public Task PauseAsync() { playing = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { Position = seconds; return Task.CompletedTask; }
        public Task<AudioState?> StateAsync(CancellationToken ct = default) =>
            Task.FromResult<AudioState?>(new(Position, 60, 60, !playing, false, 1));
        public Task<AudioOutputs> GetOutputsAsync(CancellationToken ct) => Task.FromResult(new AudioOutputs(active, [AudioOutput.Default]));

        public async Task SelectOutputAsync(string? id, double position, bool resume, CancellationToken ct)
        {
            if (DelayTransfer && id == "cast:test")
            {
                TransferStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            active = id == "cast:test" ? new(id, "Test receiver", true) : AudioOutput.Default;
            Position = position;
            playing = resume;
        }

        public Task ObserveAsync(long playbackId, Func<long, string, double, Task> notify, CancellationToken ct)
        {
            PlaybackId = playbackId;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
