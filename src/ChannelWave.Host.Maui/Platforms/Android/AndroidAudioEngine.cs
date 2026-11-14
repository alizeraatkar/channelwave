using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using ChannelWave.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ChannelWave.Host.Maui;

public sealed class AndroidAudioEngine : IAudioEngine, IAudioNotifications, ChannelWave.Core.IAudioRouting
{
    private string? preferredOutput;
    private PreparedMedia? loadedMedia;

    private AudioDeviceInfo[] OutputDevices() => audioManager.GetDevices(GetDevicesTargets.Outputs) ?? [];

    private static AndroidOutputMap MapOutputs(AudioDeviceInfo[] devices) => new(devices.Select(device =>
    {
        var kind = device.Type switch
        {
            AudioDeviceType.BuiltinSpeaker => AndroidOutputKind.Speaker,
            var type when OperatingSystem.IsAndroidVersionAtLeast(30) && type == AudioDeviceType.BuiltinSpeakerSafe
                => AndroidOutputKind.SafeSpeaker,
            AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => AndroidOutputKind.Wired,
            AudioDeviceType.UsbDevice or AudioDeviceType.UsbAccessory or AudioDeviceType.UsbHeadset => AndroidOutputKind.Usb,
            AudioDeviceType.BluetoothA2dp => AndroidOutputKind.Bluetooth,
            var type when OperatingSystem.IsAndroidVersionAtLeast(31) && type == AudioDeviceType.BleHeadset
                => AndroidOutputKind.Bluetooth,
            var type when OperatingSystem.IsAndroidVersionAtLeast(31) && type == AudioDeviceType.BleSpeaker
                => AndroidOutputKind.BluetoothSpeaker,
            AudioDeviceType.HearingAid => AndroidOutputKind.HearingAid,
            _ => AndroidOutputKind.Other
        };
        var name = device.ProductName;
        if (string.Equals(name?.Trim(), Build.Model?.Trim(), StringComparison.OrdinalIgnoreCase)) name = null;
        return new AndroidEndpoint(device.Id, kind, name, device.Address);
    }));

    public Task<AudioOutputs> GetOutputsAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        var playing = loaded && player.IsPlaying;
        return MapOutputs(OutputDevices()).Snapshot(playing, playing ? player.RoutedDevice?.Id : null, preferredOutput);
    });

    public async Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        var reload = await MainThread.InvokeOnMainThreadAsync<OutputReload?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var device = id == AudioOutput.Default.Id ? null : FindOutput(id);
            if (id != AudioOutput.Default.Id && device is null)
                throw new IOException("This audio output is no longer available. Choose another device.");
            var previous = preferredOutput;
            var requested = device is null ? null : id;
            if (!loaded || (device is null && player.PreferredDevice is null) ||
                player.SetPreferredDevice(device))
            {
                preferredOutput = requested;
                return null;
            }
            if (loadedMedia is null) throw OutputSwitchFailed();
            preferredOutput = requested;
            return new OutputReload(loadedMedia, previous, requested, player.CurrentPosition / 1000d, player.IsPlaying);
        });

        if (reload is not { } state) return;
        // Android's offloaded audio track can reject route changes until it has stopped and flushed.
        // Prepare a fresh track, apply the output before it starts, and restore the playback position.
        try
        {
            await LoadAsync(state.Media, ct);
            if (preferredOutput != state.Requested) throw OutputSwitchFailed();
            await SeekAsync(state.Position);
            ct.ThrowIfCancellationRequested();
            if (state.Playing) await PlayAsync(ct);
        }
        catch
        {
            preferredOutput = state.Previous;
            await LoadAsync(state.Media, CancellationToken.None);
            await SeekAsync(state.Position);
            if (state.Playing && !ct.IsCancellationRequested) await PlayAsync(CancellationToken.None);
            throw;
        }
    }

    private static IOException OutputSwitchFailed() =>
        new("Android couldn’t switch to this output. Try again or choose another device.");

    private sealed record OutputReload(PreparedMedia Media, string? Previous, string? Requested, double Position, bool Playing);

    private AudioDeviceInfo? FindOutput(string? id)
    {
        var devices = OutputDevices();
        var nativeId = MapOutputs(devices).Resolve(id);
        return devices.FirstOrDefault(device => device.Id == nativeId);
    }

    private long playbackId;
    private Func<long, string, double, Task>? notify;
    public Task ObserveAsync(long id, Func<long, string, double, Task> callback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); playbackId = id; notify = callback; return Task.CompletedTask;
    }
    private const string ChannelId = "channelwave-playback";
    private readonly Context context = Android.App.Application.Context;
    private readonly MediaPlayer player = new();
    private readonly MediaSession session;
    private readonly AudioManager audioManager;
    private readonly AudioFocusRequestClass focusRequest;
    private readonly FocusListener focusListener;
    private readonly NoisyReceiver noisyReceiver;
    private readonly SessionCallback callback;
    private readonly CancellationTokenSource lifetime = new();
    private AndroidPlaybackService? service;
    private TaskCompletionSource? serviceReady;
    private Track? track;
    private (long Account, Guid Track, string Media)? artworkOwner;
    private ArtworkData? requestedArtwork;
    private Bitmap? cover;
    private CancellationTokenSource? artworkRequest;
    private long artworkVersion;
    private bool loaded, disposed;
    private readonly AndroidPlaybackCompletion completion = new();
    private EventHandler? completionHandler;
    private bool ended => completion.Ended;
    private bool resumeOnFocusGain;
    private bool ducked;
    private double volume = 1;
    private string? error;
    private readonly Task polling;
    private PlaybackCoordinator Playback => IPlatformApplication.Current!.Services.GetRequiredService<PlaybackCoordinator>();
    internal MediaSession.Token SessionToken => session.SessionToken!;
    internal bool HasMedia => loaded || Playback.IsPreparing || Playback.ActiveOutput.Remote && Playback.CanResume;
    private bool IsAudible => Playback.ActiveOutput.Remote ? Playback.IsPlaying : loaded && player.IsPlaying;

    public AndroidAudioEngine()
    {
        var attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!;
        player.SetAudioAttributes(attributes);
        player.SetWakeMode(context, WakeLockFlags.Partial);
        player.Error += Failed;
        player.RoutingChanged += (_, _) =>
        {
            if (notify is { } callback) _ = callback(playbackId, "output-changed", 0);
        };
        audioManager = (AudioManager)context.GetSystemService(Context.AudioService)!;
        focusListener = new(this);
        focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(attributes)!.SetOnAudioFocusChangeListener(focusListener, new Handler(Looper.MainLooper!))!
            .SetWillPauseWhenDucked(false)!.Build()!;
        session = new MediaSession(context, "ChannelWave");
        callback = new(this);
        session.SetCallback(callback, new Handler(Looper.MainLooper!));
        session.SetSessionActivity(OpenApp());
        noisyReceiver = new(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) context.RegisterReceiver(noisyReceiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy), ReceiverFlags.NotExported);
        else context.RegisterReceiver(noisyReceiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Music playback", NotificationImportance.Low)
        {
            Description = "The song playing in ChannelWave and its playback controls",
            LockscreenVisibility = NotificationVisibility.Public
        });
        polling = PollAsync();
    }

    public async Task LoadAsync(PreparedMedia media, CancellationToken ct)
    {
        if (media.IsBrowserUrl) throw new InvalidOperationException("Native playback requires a local file.");
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ready(object? sender, EventArgs args) { prepared.TrySetResult(); }
        void PrepareFailed(object? sender, MediaPlayer.ErrorEventArgs args) { args.Handled = true; prepared.TrySetException(new UnplayableTrackException("Android could not decode this recording.")); }
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                resumeOnFocusGain = ducked = false;
                loadedMedia = null;
                InvalidateCompletion(); completion.ClearEnded();
                player.Reset(); loaded = false; error = null;
                player.Prepared += Ready; player.Error += PrepareFailed;
                player.SetDataSource(media.Location); player.PrepareAsync();
            });
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                // The native player has an audio sink only after a data source has been prepared.
                var device = preferredOutput is { } output ? FindOutput(output) : null;
                if (device is null || !player.SetPreferredDevice(device))
                {
                    preferredOutput = null;
                    if (player.PreferredDevice is not null) player.SetPreferredDevice(null);
                }
                loadedMedia = media;
                loaded = true;
            });
        }
        catch { await MainThread.InvokeOnMainThreadAsync(() => { InvalidateCompletion(); player.Reset(); loaded = false; }); throw; }
        finally { await MainThread.InvokeOnMainThreadAsync(() => { player.Prepared -= Ready; player.Error -= PrepareFailed; }); }
    }

    public Task SetTrackAsync(Track selected) => MainThread.InvokeOnMainThreadAsync(() => { track = selected; UpdateSession(); });

    public async Task BeginSelectionAsync(Track selected, CancellationToken ct)
    {
        await SetTrackAsync(selected);
        await EnsureServiceAsync(ct);
    }

    public async Task PlayAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!loaded) throw new InvalidOperationException("Choose a recording first.");
        await EnsureServiceAsync(ct);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            resumeOnFocusGain = ducked = false;
            if (audioManager.RequestAudioFocus(focusRequest) != AudioFocusRequest.Granted) throw new IOException("Another app currently has audio focus. Try Resume again.");
            if (ended) player.SeekTo(0);
            error = null; ApplyVolume(); player.Start(); ObserveCompletion();
            session.Active = true; UpdateSession();
        });
    }

    private async Task EnsureServiceAsync(CancellationToken ct)
    {
        if (service is not null) return;
        serviceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await MainThread.InvokeOnMainThreadAsync(() => context.StartForegroundService(new Intent(context, typeof(AndroidPlaybackService))));
        try { await serviceReady.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { await MainThread.InvokeOnMainThreadAsync(() => context.StopService(new Intent(context, typeof(AndroidPlaybackService)))); throw; }
    }

    private void FinishService()
    {
        var owner = service;
        if (owner is null) return;
        Playback.Changed -= PlaybackChanged; service = null; session.Active = false;
        owner.Finish();
    }

    public Task PauseAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        InvalidateCompletion();
        resumeOnFocusGain = ducked = false;
        if (loaded && player.IsPlaying) player.Pause();
        if (loaded) ApplyVolume();
        audioManager.AbandonAudioFocusRequest(focusRequest);
        UpdateSession();
        if (!loaded && !Playback.IsPreparing) FinishService();
    });

    public Task UnloadAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        resumeOnFocusGain = ducked = false;
        loadedMedia = null;
        InvalidateCompletion(); completion.ClearEnded();
        player.Reset(); loaded = false; error = null; track = null;
        audioManager.AbandonAudioFocusRequest(focusRequest);
        if (!Playback.IsPreparing) { session.Active = false; FinishService(); UpdateSession(); }
        else UpdateSession();
    });

    public async Task SeekAsync(double seconds)
    {
        if (!loaded) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CompletedSeek(object? sender, EventArgs args) => ready.TrySetResult();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            player.SeekComplete += CompletedSeek;
            player.SeekTo((int)Math.Clamp(seconds * 1000, 0, player.Duration)); completion.ClearEnded(); UpdateSession();
        });
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { await MainThread.InvokeOnMainThreadAsync(() => player.SeekComplete -= CompletedSeek); }
    }

    public Task SetVolumeAsync(double value) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        volume = Math.Clamp(value, 0, 1); ApplyVolume();
    });

    private void ApplyVolume()
    {
        // Keep temporary attenuation separate from the user's saved volume.
        var output = (float)(volume * (ducked ? 0.35 : 1));
        player.SetVolume(output, output);
    }

    public Task<AudioState?> StateAsync(CancellationToken ct = default) => MainThread.InvokeOnMainThreadAsync<AudioState?>(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!loaded) return error is null ? null : new(0, track?.Duration ?? 0, 0, true, false, volume, error);
        var position = player.CurrentPosition / 1000d;
        var duration = player.Duration / 1000d;
        var playing = player.IsPlaying;
        if (error is null) completion.ObserveState(playing, position, duration);
        return new(position, duration, duration, !playing, ended, volume, error);
    });

    internal void AttachService(AndroidPlaybackService owner)
    {
        if (!ReferenceEquals(service, owner)) { service = owner; Playback.Changed += PlaybackChanged; }
        session.Active = true;
        UpdateSession();
        owner.Publish(BuildNotification());
        serviceReady?.TrySetResult();
    }

    internal void DetachService(AndroidPlaybackService owner)
    {
        if (!ReferenceEquals(service, owner)) return;
        Playback.Changed -= PlaybackChanged; service = null; session.Active = false;
        if (loaded && !disposed) RunControl(async p => { await p.PauseAsync(); await p.CheckpointAsync(); });
    }

    private void PlaybackChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (disposed || service is null) return;
            UpdateSession();
            if (!loaded && !Playback.IsPreparing) FinishService();
        });
    }

    internal async void RunControl(Func<PlaybackCoordinator, Task> command)
    {
        try { await command(Playback); }
        catch (Exception e) { await MainThread.InvokeOnMainThreadAsync(() => { error = e.Message.Split('\n')[0]; UpdateSession(); }); }
    }

    private void InvalidateCompletion()
    {
        completion.Invalidate();
        if (completionHandler is not null) player.Completion -= completionHandler;
        completionHandler = null;
    }

    private void ObserveCompletion()
    {
        InvalidateCompletion();
        var token = completion.Start();
        var id = playbackId;
        var handler = notify;
        completionHandler = (_, _) => Completed(token, id, handler);
        player.Completion += completionHandler;
    }

    private void Completed(long token, long id, Func<long, string, double, Task>? handler)
    {
        if (disposed || !loaded || !completion.TryComplete(token)) return;
        resumeOnFocusGain = ducked = false;
        audioManager.AbandonAudioFocusRequest(focusRequest); UpdateSession();
        if (handler is not null) _ = handler(id, "ended", 0);
    }

    private void Failed(object? sender, MediaPlayer.ErrorEventArgs args)
    {
        InvalidateCompletion();
        resumeOnFocusGain = ducked = false;
        args.Handled = true; loaded = false; error = "Android could not play this recording.";
        audioManager.AbandonAudioFocusRequest(focusRequest); UpdateSession();
        if (notify is { } callback) _ = callback(playbackId, "decode-error", 0);
    }

    private void UpdateSession()
    {
        if (disposed) return;
        var selected = Playback.CurrentTrack ?? track;
        UpdateArtwork(selected);
        var playing = IsAudible;
        var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, selected?.Title ?? "ChannelWave")!
            .PutString(MediaMetadata.MetadataKeyArtist, selected?.Performer ?? "")!
            .PutString(MediaMetadata.MetadataKeyAlbum, selected?.Origin.SourceName ?? "")!
            .PutLong(MediaMetadata.MetadataKeyDuration, loaded ? player.Duration : (selected?.Duration ?? 0) * 1000L)!;
        if (cover is not null)
        {
            metadata.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, cover);
            metadata.PutBitmap(MediaMetadata.MetadataKeyArt, cover);
        }
        session.SetMetadata(selected is null ? null : metadata.Build());
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause | PlaybackState.ActionStop | PlaybackState.ActionSeekTo;
        if (Playback.HasAdjacent(-1)) actions |= PlaybackState.ActionSkipToPrevious;
        if (Playback.HasAdjacent(1)) actions |= PlaybackState.ActionSkipToNext;
        var state = selected is null ? PlaybackStateCode.None : error is not null ? PlaybackStateCode.Error : Playback.IsPreparing ? PlaybackStateCode.Buffering
            : playing ? PlaybackStateCode.Playing : ended ? PlaybackStateCode.Stopped : PlaybackStateCode.Paused;
        var position = Playback.ActiveOutput.Remote ? (long)(Playback.Queue.Position * 1000) : loaded ? player.CurrentPosition : 0;
        session.SetPlaybackState(new PlaybackState.Builder().SetActions(actions)!
            .SetState(state, position, playing ? 1 : 0, SystemClock.ElapsedRealtime())!.Build());
        if (service is not null) ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(AndroidPlaybackService.NotificationId, BuildNotification());
    }

    private void UpdateArtwork(Track? selected)
    {
        (long, Guid, string)? owner = selected is null ? null : (selected.Origin.AccountId, selected.Id, Metadata.MediaKey(selected));
        var image = selected?.Artwork is { } candidate && candidate.Matches(selected) ? candidate : null;
        if (artworkOwner == owner && ReferenceEquals(requestedArtwork, image)) return;
        artworkOwner = owner; requestedArtwork = image;
        var version = ++artworkVersion;
        artworkRequest?.Cancel(); artworkRequest?.Dispose(); artworkRequest = null;
        cover?.Dispose(); cover = null;
        if (image is null) return;
        artworkRequest = new();
        _ = LoadArtworkAsync(image, version, artworkRequest.Token);
    }

    private async Task LoadArtworkAsync(ArtworkData image, long version, CancellationToken ct)
    {
        Bitmap? decoded = null;
        try
        {
            decoded = await Task.Run(() => DecodeArtwork(image.Data), ct);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (disposed || ct.IsCancellationRequested || version != artworkVersion) return;
                cover = decoded; decoded = null;
                UpdateSession();
            });
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { /* Invalid covers must never interrupt playback or system controls. */ }
        finally { decoded?.Dispose(); }
    }

    private static Bitmap? DecodeArtwork(byte[] data)
    {
        Id3Codec.ArtworkMime(data);
        using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using var probe = BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 512) sample *= 2;
        using var options = new BitmapFactory.Options { InSampleSize = sample };
        return BitmapFactory.DecodeByteArray(data, 0, data.Length, options);
    }

    private Notification BuildNotification()
    {
        var selected = Playback.CurrentTrack ?? track;
        var playing = IsAudible;
        var builder = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_music)!
            .SetContentTitle(selected?.Title ?? "ChannelWave")!
            .SetContentText(selected?.Performer ?? "Preparing audio…")!
            .SetSubText(selected?.Origin.SourceName)!
            .SetContentIntent(OpenApp())!.SetDeleteIntent(Control(AndroidPlaybackService.Stop, 4))!
            .SetVisibility(NotificationVisibility.Public)!.SetOnlyAlertOnce(true)!.SetShowWhen(false)!
            .SetOngoing(playing || Playback.IsPreparing)!.SetCategory(Notification.CategoryTransport)!;
        if (cover is not null) builder.SetLargeIcon(cover);
        var compact = new List<int>(); var index = 0;
        if (Playback.HasAdjacent(-1)) { builder.AddAction(Action(Android.Resource.Drawable.IcMediaPrevious, "Previous", AndroidPlaybackService.Previous, 0)); compact.Add(index++); }
        builder.AddAction(Action(playing ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay,
            playing ? "Pause" : "Play", playing ? AndroidPlaybackService.Pause : AndroidPlaybackService.Play, 1)); compact.Add(index++);
        if (Playback.HasAdjacent(1)) { builder.AddAction(Action(Android.Resource.Drawable.IcMediaNext, "Next", AndroidPlaybackService.Next, 2)); compact.Add(index++); }
        builder.AddAction(Action(Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop", AndroidPlaybackService.Stop, 3));
        builder.SetStyle(new Notification.MediaStyle().SetMediaSession(session.SessionToken)!.SetShowActionsInCompactView(compact.ToArray()));
        return builder.Build();
    }

    private Notification.Action Action(int icon, string title, string command, int request)
        => new Notification.Action.Builder(Icon.CreateWithResource(context, icon), title, Control(command, request)).Build()!;

    private PendingIntent Control(string command, int request) => PendingIntent.GetService(context, request,
        new Intent(context, typeof(AndroidPlaybackService)).SetAction(command), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

    private PendingIntent OpenApp() => PendingIntent.GetActivity(context, 10, new Intent(context, typeof(MainActivity))
        .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop), PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await MainThread.InvokeOnMainThreadAsync(() => { if (!disposed && service is not null && IsAudible) UpdateSession(); }); }
        catch (System.OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await UnloadAsync(); disposed = true; lifetime.Cancel(); await polling;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            artworkRequest?.Cancel(); artworkRequest?.Dispose(); cover?.Dispose(); cover = null;
            context.UnregisterReceiver(noisyReceiver); player.Error -= Failed;
            player.Release(); player.Dispose(); session.Release(); session.Dispose();
            focusRequest.Dispose(); focusListener.Dispose(); noisyReceiver.Dispose(); callback.Dispose();
        });
        lifetime.Dispose();
    }

    private sealed class SessionCallback(AndroidAudioEngine owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.RunControl(p => p.ResumeAsync());
        public override void OnPause() => owner.RunControl(p => p.PauseAsync());
        public override void OnStop() => owner.RunControl(p => p.StopAsync());
        public override void OnSeekTo(long pos) => owner.RunControl(p => p.SeekAsync(pos / 1000d));
        public override void OnSkipToNext() => owner.RunControl(p => p.SkipAsync(1));
        public override void OnSkipToPrevious() => owner.RunControl(p => p.SkipAsync(-1));
    }

    private void AudioFocusChanged(AudioFocus change)
    {
        if (Playback.ActiveOutput.Remote) return;
        if (disposed) return;
        switch (change)
        {
            case AudioFocus.LossTransient:
                // Keep our focus request registered so Android can return focus after the interruption.
                if (loaded && player.IsPlaying)
                {
                    InvalidateCompletion();
                    resumeOnFocusGain = true;
                    player.Pause();
                }
                UpdateSession();
                RunControl(p => p.StateAsync());
                break;
            case AudioFocus.LossTransientCanDuck:
                // Android normally ducks media itself; handle devices that deliver the callback too.
                if (loaded && player.IsPlaying)
                {
                    ducked = true;
                    ApplyVolume();
                }
                break;
            case AudioFocus.Gain:
                ducked = false;
                if (loaded) ApplyVolume();
                if (!resumeOnFocusGain) return;
                resumeOnFocusGain = false;
                if (!loaded || ended || error is not null) return;
                player.Start(); ObserveCompletion();
                UpdateSession();
                RunControl(p => p.StateAsync());
                break;
            case AudioFocus.Loss:
                resumeOnFocusGain = ducked = false;
                RunControl(p => p.PauseAsync());
                break;
        }
    }

    private sealed class FocusListener(AndroidAudioEngine owner) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus change) => owner.AudioFocusChanged(change);
    }

    private sealed class NoisyReceiver(AndroidAudioEngine owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy && !owner.Playback.ActiveOutput.Remote)
                owner.RunControl(p => p.PauseAsync());
        }
    }
}
