using ChannelWave.Core;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class PlaybackLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Track_and_sequence_survive_ui_detachment_and_external_controls()
    {
        // Arrange: the UI subscribes while choosing a song, then leaves.
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var first = Fixture.Track(new(7, -1001, "Synthetic source"), 1);
        var second = Fixture.Track(new(7, -1001, "Synthetic source"), 2);
        void Render() { }
        playback.Changed += Render;
        await playback.SelectAsync(first, [first, second], (track, _) => Task.FromResult(new PreparedMedia(track.Title, false, 1)), Token);
        playback.Changed -= Render;

        // Act: a system media control selects the next song while there is no UI.
        await playback.SkipAsync(1, Token);
        await playback.PauseAsync();

        // Assert: a new UI reads the actual selection and paused state, without preparing it again.
        Assert.Equal(second, playback.CurrentTrack);
        Assert.Equal(second.Id, playback.SelectedTrack);
        Assert.Equal(new[] { first, second }, playback.PlayingSequence);
        Assert.Equal(new[] { first, second }, engine.Metadata);
        Assert.False(playback.IsPlaying);
        Assert.True(playback.CanResume);
        Assert.False(playback.IsPreparing);
        Assert.True((await playback.StateAsync(Token))!.Paused);
        await playback.ResumeAsync(Token);
        Assert.True(playback.IsPlaying);
        Assert.Equal(2, engine.Loads);
    }

    [Fact]
    public async Task Late_preparation_cannot_replace_current_track_or_system_metadata()
    {
        // Arrange
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var old = Fixture.Track(new(7, -1001, "Synthetic source"), 1);
        var current = Fixture.Track(new(7, -1001, "Synthetic source"), 2);
        var late = new TaskCompletionSource<PreparedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = playback.SelectAsync(old, [old], (_, _) => late.Task, Token);

        // Act
        await playback.SelectAsync(current, [current], (_, _) => Task.FromResult(new PreparedMedia("current", false, 1)), Token);
        late.SetResult(new("old", false, 1));
        await pending;

        // Assert
        Assert.Equal(current, playback.CurrentTrack);
        Assert.Equal(current, Assert.Single(engine.Metadata));
        Assert.Equal(current, Assert.Single(playback.PlayingSequence));
        Assert.True(playback.IsPlaying);
        Assert.False(playback.IsPreparing);
    }

    [Fact]
    public async Task Removing_a_playing_source_clears_restorable_track_and_system_selection()
    {
        // Arrange
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var track = Fixture.Track(new(7, -1001, "Synthetic source"), 1);
        await playback.SelectAsync(track, [track], (_, _) => Task.FromResult(new PreparedMedia("current", false, 1)), Token);

        // Act
        await playback.RemoveTracksAsync([track.Id]);

        // Assert
        Assert.Null(playback.CurrentTrack);
        Assert.Null(playback.SelectedTrack);
        Assert.Empty(playback.PlayingSequence);
        Assert.False(playback.CanResume);
        Assert.False(playback.IsPlaying);
        Assert.False(engine.Playing);
    }

    [Fact]
    public async Task Corrected_metadata_updates_system_labels_and_queue_without_reloading_or_seeking_audio()
    {
        // Arrange
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var track = Fixture.Track(new(7, -1001, "Source"), 1);
        await playback.SelectAsync(track, [track], (_, _) => Task.FromResult(new PreparedMedia("file", false, 1)), Token);
        var updated = Metadata.ApplyOverrides(track, "Local title", "Local artist");

        // Act
        await playback.UpdateTrackMetadataAsync(updated, Token);

        // Assert
        Assert.Equal(updated, playback.CurrentTrack); Assert.Equal(updated, Assert.Single(playback.PlayingSequence));
        Assert.Equal(updated, engine.Metadata.Last()); Assert.Equal(1, engine.Loads);
        Assert.True(playback.IsPlaying); Assert.Equal(12, (await playback.StateAsync(Token))!.Position);
        await playback.UpdateTrackMetadataAsync(updated with { Origin = updated.Origin with { AccountId = 8 }, Title = "Wrong account" }, Token);
        Assert.Equal(updated, playback.CurrentTrack); Assert.Equal(2, engine.Metadata.Count);
    }

    [Fact]
    public async Task Corrections_while_buffering_are_not_overwritten_by_download_completion()
    {
        // Arrange
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var track = Fixture.Track(new(7, -1001, "Source"), 1);
        var ready = new TaskCompletionSource<PreparedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = playback.SelectAsync(track, [track], (_, _) => ready.Task, Token);
        var corrected = Metadata.ApplyOverrides(track, "New title", "New artist");

        // Act
        await playback.UpdateTrackMetadataAsync(corrected, Token);
        ready.SetResult(new("file", false, 1)); await pending;

        // Assert
        Assert.Equal(corrected, engine.Metadata.Last()); Assert.Equal(corrected, playback.CurrentTrack);
        Assert.True(playback.IsPlaying); Assert.Equal(1, engine.Loads);
    }

    [Fact]
    public async Task Local_playback_releases_metadata_work_and_streaming_holds_it_until_end_or_stop()
    {
        // Arrange
        var work = new MediaWorkCoordinator(); var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine, work);
        var track = Fixture.Track(new(7, 1, "Source"), 1);

        // Act
        await playback.SelectAsync(track, [track], (_, _) => Task.FromResult(new PreparedMedia("local", false, 1)), Token);
        var local = await work.BackgroundAsync(_ => Task.FromResult(42), Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        await playback.SelectAsync(track, [track], (_, _) => Task.FromResult(new PreparedMedia("stream", true, 1, true)), Token);
        var background = work.BackgroundAsync(_ => Task.FromResult(43), Token);

        // Assert
        Assert.Equal(42, local); Assert.False(background.IsCompleted);
        engine.Ended = true; await playback.StateAsync(Token);
        Assert.Equal(43, await background.WaitAsync(TimeSpan.FromSeconds(2), Token));
        await playback.SeekAsync(0);
        var afterSeek = work.BackgroundAsync(_ => Task.FromResult(44), Token); Assert.False(afterSeek.IsCompleted);
        await playback.StopAsync(); Assert.Equal(44, await afterSeek.WaitAsync(TimeSpan.FromSeconds(2), Token));
    }

    [Fact]
    public async Task Pausing_canceled_preparation_releases_its_metadata_transfer_lease()
    {
        // Arrange
        var work = new MediaWorkCoordinator(); await using var playback = new PlaybackCoordinator(new LifecycleEngine(), work);
        var started = new TaskCompletionSource(); var track = Fixture.Track(new(7, 1, "Source"), 1);
        var selection = playback.SelectAsync(track, [track], async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new("never", false, 1); }, Token);
        await started.Task.WaitAsync(Token);

        // Act
        await playback.PauseAsync(); await selection;

        // Assert
        Assert.Equal(1, await work.BackgroundAsync(_ => Task.FromResult(1), Token).WaitAsync(TimeSpan.FromSeconds(2), Token));
    }

    [Fact]
    public async Task Delayed_artwork_updates_current_metadata_without_restarting_audio_and_clears_on_stop()
    {
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var track = Fixture.Track(new(7, 1, "Source"), 1);
        await playback.SelectAsync(track, [track], (_, _) => Task.FromResult(new PreparedMedia("file", false, 1)), Token);
        var covered = track with { Artwork = new(Metadata.MediaKey(track), ArtworkKind.Embedded, [0xff, 0xd8, 0xff]) };

        await playback.UpdateTrackMetadataAsync(covered, Token);

        Assert.Equal(covered.Artwork, engine.CurrentMetadata?.Artwork);
        Assert.Equal(covered, Assert.Single(playback.PlayingSequence));
        Assert.Equal(1, engine.Loads); Assert.True(playback.IsPlaying);
        Assert.Equal(12, (await playback.StateAsync(Token))!.Position);
        await playback.PauseAsync(); await playback.ResumeAsync(Token); await playback.SeekAsync(3);
        Assert.Equal(covered.Artwork, engine.CurrentMetadata?.Artwork);
        await playback.StopAsync();
        await playback.UpdateTrackMetadataAsync(covered, Token);
        Assert.Null(engine.CurrentMetadata); Assert.Null(playback.CurrentTrack);
    }

    [Fact]
    public async Task Late_artwork_cannot_replace_another_track_account_or_replaced_recording()
    {
        var engine = new LifecycleEngine();
        await using var playback = new PlaybackCoordinator(engine);
        var old = Fixture.Track(new(7, 1, "Source"), 1);
        var current = Fixture.Track(new(7, 1, "Source"), 2);
        Task<PreparedMedia> Prepare(Track _, CancellationToken ct) => Task.FromResult(new PreparedMedia("file", false, 1));
        await playback.SelectAsync(old, [old, current], Prepare, Token);
        await playback.SkipAsync(1, Token);
        var covered = old with { Artwork = new(Metadata.MediaKey(old), ArtworkKind.Embedded, [0xff, 0xd8, 0xff]) };
        await playback.UpdateTrackMetadataAsync(covered, Token);
        Assert.Equal(current, engine.CurrentMetadata);
        await playback.UpdateTrackMetadataAsync(current with { Origin = current.Origin with { AccountId = 8 }, Artwork = covered.Artwork }, Token);
        Assert.Equal(current, engine.CurrentMetadata);

        var replacement = old with { Media = old.Media with { UniqueId = "replacement" } };
        await playback.SelectAsync(replacement, [replacement], Prepare, Token);
        await playback.UpdateTrackMetadataAsync(covered, Token);
        Assert.Equal(replacement, engine.CurrentMetadata);
        Assert.Equal(replacement, Assert.Single(playback.PlayingSequence));
    }

    private sealed class LifecycleEngine : IAudioEngine
    {
        public List<Track> Metadata { get; } = [];
        public Track? CurrentMetadata { get; private set; }
        public int Loads { get; private set; }
        public bool Playing { get; private set; }
        public bool Ended { get; set; }
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Loads++; return Task.CompletedTask; }
        public Task SetTrackAsync(Track track) { Metadata.Add(track); CurrentMetadata = track; return Task.CompletedTask; }
        public Task UnloadAsync() { Playing = false; CurrentMetadata = null; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { Playing = true; return Task.CompletedTask; }
        public Task PauseAsync() { Playing = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public Task<AudioState?> StateAsync(CancellationToken ct = default) => Task.FromResult<AudioState?>(new(12, 90, 90, !Playing, Ended, 1));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
