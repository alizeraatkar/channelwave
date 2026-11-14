using ChannelWave.Core;

namespace ChannelWave.Host.Maui;

// Foreground probe adapters. Background and system controls are explicitly disabled until tested.
public sealed partial class NativeAudio : IAudioEngine
{
#if WINDOWS
    private readonly global::Windows.Media.Playback.MediaPlayer player = new() { AutoPlay = false };
    private global::Windows.Media.Playback.MediaPlaybackItem? item;
    private global::Windows.Storage.Streams.InMemoryRandomAccessStream? coverStream;
    public async Task LoadAsync(PreparedMedia media, CancellationToken ct)
    {
        if (media.IsBrowserUrl) throw new InvalidOperationException("Native playback requires a local file.");
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(media.Location);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(global::Windows.Media.Playback.MediaPlayer _, object args) => ready.TrySetResult();
        void Failed(global::Windows.Media.Playback.MediaPlayer _, global::Windows.Media.Playback.MediaPlayerFailedEventArgs args) => ready.TrySetException(new IOException("Windows could not decode this recording."));
        player.MediaOpened += Opened; player.MediaFailed += Failed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() => ready.TrySetCanceled(timeout.Token));
        try { item = new(global::Windows.Media.Core.MediaSource.CreateFromStorageFile(file)); player.Source = item; await ready.Task; }
        finally { player.MediaOpened -= Opened; player.MediaFailed -= Failed; }
    }
    public async Task SetTrackAsync(Track track)
    {
        var selected = item;
        if (selected is null) return;
        var properties = selected.GetDisplayProperties();
        properties.ClearAll(); properties.Type = global::Windows.Media.MediaPlaybackType.Music;
        properties.MusicProperties.Title = track.Title; properties.MusicProperties.Artist = track.Performer;
        properties.MusicProperties.AlbumTitle = track.Origin.SourceName;
        global::Windows.Storage.Streams.InMemoryRandomAccessStream? next = null;
        try
        {
            if (track.Artwork?.Matches(track) == true)
            {
                Id3Codec.ArtworkMime(track.Artwork.Data);
                next = new();
                using var writer = new global::Windows.Storage.Streams.DataWriter(next);
                writer.WriteBytes(track.Artwork.Data); await writer.StoreAsync(); writer.DetachStream(); next.Seek(0);
                properties.Thumbnail = global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(next);
            }
        }
        catch (Exception) { next?.Dispose(); next = null; properties.Thumbnail = null; }
        if (!ReferenceEquals(item, selected)) { next?.Dispose(); return; }
        selected.ApplyDisplayProperties(properties);
        coverStream?.Dispose(); coverStream = next;
    }
    public Task UnloadAsync()
    {
        player.Pause(); player.Source = null; item = null;
        coverStream?.Dispose(); coverStream = null;
        return Task.CompletedTask;
    }
    public Task PlayAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); player.Play(); return Task.CompletedTask; }
    public Task PauseAsync() { player.Pause(); return Task.CompletedTask; }
    public Task SeekAsync(double seconds) { player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds)); return Task.CompletedTask; }
    public Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var session = player.PlaybackSession;
        return Task.FromResult<AudioState?>(new(session.Position.TotalSeconds, session.NaturalDuration.TotalSeconds, session.NaturalDuration.TotalSeconds, session.PlaybackState != global::Windows.Media.Playback.MediaPlaybackState.Playing, session.NaturalDuration.TotalSeconds > 0 && session.Position >= session.NaturalDuration, player.Volume, Buffering: session.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Buffering));
    }
    public Task SetVolumeAsync(double volume) { player.Volume = Math.Clamp(volume, 0, 1); return Task.CompletedTask; }
    public async ValueTask DisposeAsync() { await UnloadAsync(); player.Dispose(); }
#elif ANDROID
    private readonly Android.Media.MediaPlayer player = new();
    private bool loaded;
    public Task LoadAsync(PreparedMedia media, CancellationToken ct) => Task.Run(() => { ct.ThrowIfCancellationRequested(); if (media.IsBrowserUrl) throw new InvalidOperationException("Native playback requires a local file."); player.Reset(); loaded = false; player.SetDataSource(media.Location); player.Prepare(); loaded = true; }, ct);
    public Task PlayAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!loaded) throw new InvalidOperationException("Choose a recording first."); player.Start(); return Task.CompletedTask; }
    public Task PauseAsync() { if (loaded && player.IsPlaying) player.Pause(); return Task.CompletedTask; }
    public Task SeekAsync(double seconds) { if (loaded) player.SeekTo((int)(Math.Max(0, seconds) * 1000)); return Task.CompletedTask; }
    public Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<AudioState?>(loaded ? new(player.CurrentPosition / 1000d, player.Duration / 1000d, player.Duration / 1000d, !player.IsPlaying, player.Duration > 0 && player.CurrentPosition >= player.Duration, volume) : null);
    }
    private double volume = 1;
    public Task SetVolumeAsync(double value) { volume = Math.Clamp(value, 0, 1); player.SetVolume((float)volume, (float)volume); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { player.Release(); player.Dispose(); return ValueTask.CompletedTask; }
#elif IOS || MACCATALYST
    private AVFoundation.AVPlayer? player;
    public async Task LoadAsync(PreparedMedia media, CancellationToken ct)
    {
        if (media.IsBrowserUrl) throw new InvalidOperationException("Native playback requires a local file.");
        await MainThread.InvokeOnMainThreadAsync(ConfigureAppleAudio);
        player?.Pause(); player?.Dispose(); player = AVFoundation.AVPlayer.FromUrl(Foundation.NSUrl.FromFilename(media.Location));
        player.AllowsExternalPlayback = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (player.Status == AVFoundation.AVPlayerStatus.Unknown) await Task.Delay(50, deadline.Token);
        if (player.Status == AVFoundation.AVPlayerStatus.Failed) throw new IOException("Apple's audio engine could not open this recording.");
    }
    public Task PlayAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (player is null) throw new InvalidOperationException("Choose a recording first."); player.Play(); return Task.CompletedTask; }
    public Task PauseAsync() { player?.Pause(); return Task.CompletedTask; }
    public Task SeekAsync(double seconds) { player?.Seek(CoreMedia.CMTime.FromSeconds(Math.Max(0, seconds), 1000)); return Task.CompletedTask; }
    public Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (player is null) return Task.FromResult<AudioState?>(null);
        var duration = player.CurrentItem?.Duration.Seconds ?? 0; var position = player.CurrentTime.Seconds;
        return Task.FromResult<AudioState?>(new(double.IsFinite(position) ? position : 0, double.IsFinite(duration) ? duration : 0, double.IsFinite(duration) ? duration : 0, player.Rate == 0, duration > 0 && position >= duration, player.Volume));
    }
    public Task SetVolumeAsync(double volume) { if (player is not null) player.Volume = (float)Math.Clamp(volume, 0, 1); return Task.CompletedTask; }
    public Task UnloadAsync() { player?.Pause(); player?.Dispose(); player = null; return Task.CompletedTask; }
    public async ValueTask DisposeAsync() { routeObserver?.Dispose(); await UnloadAsync(); }
#endif
}
