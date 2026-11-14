using System.Net;
using System.Diagnostics.CodeAnalysis;
using Android.Content;
using Android.Gms.Cast;
using Android.Gms.Cast.Framework;
using Android.Gms.Cast.Framework.Media;
using Android.Runtime;
using AndroidX.MediaRouter.Media;
using ChannelWave.Core;

namespace ChannelWave.Host.Maui;

[Register("dev/channelwave/app/ChannelWaveCastOptions")]
public sealed class ChannelWaveCastOptions : Java.Lang.Object, IOptionsProvider
{
    public CastOptions GetCastOptions(Context context) => new CastOptions.Builder()
        .SetReceiverApplicationId(CastMediaControlIntent.DefaultMediaReceiverApplicationId)!
        .SetResumeSavedSession(false)!
        .SetEnableReconnectionService(false)!
        .SetStopReceiverApplicationWhenEndingSession(true)!
        .SetCastMediaOptions(new CastMediaOptions.Builder().SetMediaSessionEnabled(false)!.Build())!
        .Build()!;

    public IList<SessionProvider>? GetAdditionalSessionProviders(Context context) => null;
}

internal sealed class AndroidCast : IMobileCast
{
    private readonly Context app = Android.App.Application.Context;
    private readonly DiscoveryCallback discovery = new();
    private CastContext? context;
    private MediaRouter? router;
    private CastSession? selected;
    private readonly CastPlaybackState playbackState = new();

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ChannelWaveCastOptions))]
    public Task<AudioOutput[]> DiscoverAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (context is null)
        {
            try { context = CastContext.GetSharedInstance(app); }
            catch (Java.Lang.Exception) { return []; }
        }
        if (context?.MergedSelector is not { } selector) return [];
        if (router is null)
        {
            router = MediaRouter.GetInstance(app);
            router?.AddCallback(selector, discovery, MediaRouter.CallbackFlagRequestDiscovery);
        }
        return router?.Routes.Where(route => route.IsEnabled && route.MatchesSelector(selector))
            .Select(route => new AudioOutput("cast:" + route.Id, route.Name ?? "Google Cast", true)).ToArray() ?? [];
    });

    public async Task<IPAddress> ConnectAsync(string id, CancellationToken ct)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var route = router?.Routes.FirstOrDefault(route => "cast:" + route.Id == id)
                ?? throw new IOException("The Cast device is no longer available.");
            route.Select();
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var receiver = await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if ("cast:" + router?.SelectedRoute?.Id != id ||
                        context?.SessionManager?.CurrentCastSession is not { IsConnected: true } session)
                        return null;
                    selected = session;
                    return IPAddress.TryParse(session.CastDevice?.InetAddress?.HostAddress, out var address) ? address : null;
                });
                if (receiver is not null) return receiver;
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("ChannelWave could not connect to the Cast device. Check that both devices are on the same Wi-Fi.");
        }
    }

    public Task DisconnectAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        context?.SessionManager?.EndCurrentSession(true);
        selected = null;
        playbackState.Clear();
    });

    public async Task LoadAsync(string url, string mime, Track track, double position, CancellationToken ct)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var metadata = new MediaMetadata(MediaMetadata.MediaTypeMusicTrack);
            metadata.PutString(MediaMetadata.KeyTitle, track.Title);
            metadata.PutString(MediaMetadata.KeyArtist, track.Performer);
            var media = new MediaInfo.Builder(url).SetContentType(mime)!.SetStreamType(MediaInfo.StreamTypeBuffered)!
                .SetMetadata(metadata)!.Build()!;
            var request = new MediaLoadRequestData.Builder().SetMediaInfo(media)!.SetAutoplay(Java.Lang.Boolean.False)!
                .SetCurrentTime((long)(position * 1000))!.Build()!;
            Client().Load(request);
            playbackState.BeginLoad(url);
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (await StateAsync(deadline.Token) is not { Error: null, Buffering: false })
                await Task.Delay(150, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("The Cast device could not open this recording.");
        }
    }

    public Task PlayAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var result = await Client().PlayAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (result?.Status?.IsSuccess != true) throw new IOException("The Cast device could not start playback.");
    });

    public Task PauseAsync() => MainThread.InvokeOnMainThreadAsync(() => { selected?.RemoteMediaClient?.Pause(); });
    public Task SeekAsync(double position) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        var seek = new MediaSeekOptions.Builder().SetPosition((long)(position * 1000))!.Build()!;
        selected?.RemoteMediaClient?.Seek(seek);
    });
    public Task StopAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (selected?.IsConnected == true) selected.RemoteMediaClient?.Stop();
        playbackState.Clear();
    });
    public Task SetVolumeAsync(double volume) => MainThread.InvokeOnMainThreadAsync(() => { selected?.RemoteMediaClient?.SetStreamVolume(volume); });

    public Task<AudioState?> StateAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync<AudioState?>(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (selected is not { IsConnected: true } || selected.RemoteMediaClient is not { } client)
            return playbackState.Observe(false, null, null);
        if (client.MediaStatus is not { } state)
            return playbackState.Observe(true, null, null);
        var duration = Math.Max(0, client.StreamDuration / 1000d);
        var ended = client.IdleReason == MediaStatus.IdleReasonFinished;
        var position = ended ? duration : Math.Max(0, client.ApproximateStreamPosition / 1000d);
        return playbackState.Observe(true, client.MediaInfo?.ContentId, new(position, duration, duration, !client.IsPlaying, ended, state.StreamVolume,
            client.IdleReason == MediaStatus.IdleReasonError ? "The Cast device stopped playback." : null,
            Buffering: client.IsBuffering, Muted: state.IsMute));
    });

    private RemoteMediaClient Client() => selected?.RemoteMediaClient
        ?? throw new IOException("The Cast device disconnected.");

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await MainThread.InvokeOnMainThreadAsync(() => { router?.RemoveCallback(discovery); discovery.Dispose(); });
    }

    private sealed class DiscoveryCallback : MediaRouter.Callback;
}
