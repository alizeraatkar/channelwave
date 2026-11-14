using System.Net;
using ChannelWave.Core;
using ChannelWave.Host.Maui;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class MobileCastAudioTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("first")]
    public void Receiver_completion_without_media_info_is_not_a_disconnection_or_a_stale_new_track_completion(string? contentId)
    {
        // Arrange
        var state = new CastPlaybackState();
        state.BeginLoad("first");
        state.Observe(true, "first", new(12, 60, 60, false, false, .7));

        // Act
        var finished = state.Observe(true, contentId, new(0, 0, 0, true, true, 1));
        state.BeginLoad("second");
        var stale = state.Observe(true, null, new(0, 0, 0, true, true, 1));
        var ready = state.Observe(true, "second", new(0, 90, 90, true, false, .7));

        // Assert
        Assert.NotNull(finished); Assert.True(finished.Ended); Assert.Equal(60, finished.Position);
        Assert.NotNull(stale); Assert.False(stale.Ended); Assert.True(stale.Buffering);
        Assert.NotNull(ready); Assert.False(ready.Buffering); Assert.Equal(90, ready.Duration);
        Assert.Null(state.Observe(false, "second", ready));
    }

    [Fact]
    public void Connected_receiver_without_a_media_status_does_not_signal_output_loss()
    {
        // Arrange
        var state = new CastPlaybackState(); state.BeginLoad("current");
        state.Observe(true, "current", new(18, 60, 60, false, false, 1));

        // Act
        var pending = state.Observe(true, null, null);
        var otherLoad = state.Observe(true, "previous", new(60, 60, 60, true, true, 1));
        var failed = state.Observe(true, null, new(0, 0, 0, true, false, 1, Error: "Receiver error"));

        // Assert
        Assert.NotNull(pending); Assert.Equal(18, pending.Position); Assert.True(pending.Buffering);
        Assert.NotNull(otherLoad); Assert.False(otherLoad.Ended);
        Assert.NotNull(failed); Assert.Equal("Receiver error", failed.Error); Assert.False(failed.Buffering);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Next_song_keeps_the_receiver_and_revokes_the_previous_file_without_sending_stop(bool natural)
    {
        // Arrange
        var local = new LocalEngine(); var cast = new Receiver(); var leases = new List<Lease>();
        var engine = new MobileCastAudio(local, cast, (_, _, _) => { var lease = new Lease(); leases.Add(lease); return lease; });
        await using var player = new PlaybackCoordinator(engine);
        var tracks = new[] { Fixture.Track(new(7, 1, "Source"), 1), Fixture.Track(new(7, 1, "Source"), 2) };
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PreparedMedia> Prepare(Track track, CancellationToken ct)
        {
            if (track.Id == tracks[1].Id) { preparing.TrySetResult(); await release.Task.WaitAsync(ct); }
            return new(track.Title, false, 100);
        }
        await player.StartQueueAsync(7, tracks.Select(t => t.Id).ToArray(), tracks[0].Id,
            (id, _) => Task.FromResult<Track?>(tracks.FirstOrDefault(t => t.Id == id)), Prepare, ct: Token);
        await player.SelectOutputAsync("cast:test", Token);

        // Act: hold preparation to cover the interval between recordings.
        var next = natural ? player.AudioSignalAsync(local.Epoch, "ended") : player.SkipAsync(1, Token);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var outputBetweenTracks = await engine.GetActiveOutputAsync(Token);
        release.TrySetResult(); await next;

        // Assert
        Assert.True(outputBetweenTracks.Remote); Assert.True(player.ActiveOutput.Remote);
        Assert.Equal(tracks[1].Id, player.SelectedTrack); Assert.True(player.IsPlaying);
        Assert.Equal(1, cast.Connects); Assert.Equal(0, cast.Disconnects); Assert.Equal(0, cast.Stops);
        Assert.Equal(new[] { tracks[0].Id, tracks[1].Id }, cast.Loads);
        Assert.True(leases[0].Disposed); Assert.False(leases[1].Disposed); Assert.Equal(1, local.Plays);

        // Finishing the queue keeps the chosen receiver; explicit Stop releases it.
        await player.AudioSignalAsync(local.Epoch, "ended");
        Assert.True(player.ActiveOutput.Remote); Assert.Equal(0, cast.Disconnects);
        await player.StopAsync();
        Assert.Equal(1, cast.Disconnects); Assert.Equal(1, cast.Stops); Assert.True(leases[1].Disposed);
    }

    [Fact]
    public async Task Explicit_route_switch_stops_casting_and_returns_to_local_audio()
    {
        // Arrange
        var local = new LocalEngine(); var cast = new Receiver();
        await using var engine = new MobileCastAudio(local, cast, (_, _, _) => new Lease());
        var track = Fixture.Track(new(7, 1, "Source"), 1);
        await engine.BeginSelectionAsync(track, Token); await engine.LoadAsync(new("song", false, 100), Token);
        await engine.SelectOutputAsync("cast:test", 10, true, Token);

        // Act
        await engine.SelectOutputAsync("", 12, true, Token);

        // Assert
        Assert.False((await engine.GetActiveOutputAsync(Token)).Remote);
        Assert.Equal(1, cast.Disconnects); Assert.Equal(1, cast.Stops); Assert.Equal(1, local.Plays);
    }

    private sealed class Lease : ICastMediaLease
    {
        public string Url { get; } = "http://fixture/" + Guid.NewGuid();
        public bool Disposed { get; private set; }
        public void Revoke() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Receiver : IMobileCast
    {
        public int Connects, Disconnects, Stops;
        public List<Guid> Loads { get; } = [];
        private AudioState state = new(0, 60, 60, true, false, 1);
        public Task<AudioOutput[]> DiscoverAsync(CancellationToken ct) => Task.FromResult<AudioOutput[]>([new("cast:test", "Receiver", true)]);
        public Task<IPAddress> ConnectAsync(string id, CancellationToken ct) { Connects++; return Task.FromResult(IPAddress.Parse("192.168.1.2")); }
        public Task DisconnectAsync() { Disconnects++; return Task.CompletedTask; }
        public Task LoadAsync(string url, string mime, Track track, double position, CancellationToken ct) { Loads.Add(track.Id); state = state with { Position = position, Paused = true }; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { state = state with { Paused = false }; return Task.CompletedTask; }
        public Task PauseAsync() { state = state with { Paused = true }; return Task.CompletedTask; }
        public Task SeekAsync(double position) { state = state with { Position = position }; return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume) => Task.CompletedTask;
        public Task StopAsync() { Stops++; return Task.CompletedTask; }
        public Task<AudioState?> StateAsync(CancellationToken ct) => Task.FromResult<AudioState?>(state);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LocalEngine : IAudioEngine, IAudioRouting, IAudioNotifications
    {
        public long Epoch;
        public int Plays;
        private bool playing;
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken ct) { Plays++; playing = true; return Task.CompletedTask; }
        public Task PauseAsync() { playing = false; return Task.CompletedTask; }
        public Task UnloadAsync() => PauseAsync();
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public Task<AudioState?> StateAsync(CancellationToken ct = default) => Task.FromResult<AudioState?>(new(0, 60, 60, !playing, false, 1));
        public Task<AudioOutputs> GetOutputsAsync(CancellationToken ct) => Task.FromResult(AudioOutputs.SystemOnly);
        public Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct) => Task.CompletedTask;
        public Task ObserveAsync(long id, Func<long, string, double, Task> notify, CancellationToken ct) { Epoch = id; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
