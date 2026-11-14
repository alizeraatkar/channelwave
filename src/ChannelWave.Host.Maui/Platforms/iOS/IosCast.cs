#if CHANNELWAVE_IOS_CAST
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChannelWave.Core;
using Foundation;
using UIKit;

namespace ChannelWave.Host.Maui;

internal sealed class IosCast : IMobileCast
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? mediaUrl;
    private bool initialized;
    private NSObject? backgroundObserver;

    public void ObserveBackground(Action onBackground)
    {
        backgroundObserver = UIApplication.Notifications.ObserveDidEnterBackground((_, _) =>
        {
            if (mediaUrl is not null) cw_cast_stop();
            onBackground();
        });
    }

    public Task<AudioOutput[]> DiscoverAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        initialized = true;
        return JsonSerializer.Deserialize<AudioOutput[]>(TakeString(cw_cast_devices()) ?? "[]", Json) ?? [];
    });

    public async Task<IPAddress> ConnectAsync(string id, CancellationToken ct)
    {
        var identifier = id[5..];
        var started = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            RequireForeground();
            return cw_cast_connect(identifier);
        });
        if (started == 0) throw new IOException("The Cast device is no longer available.");
        return await WaitAsync(async () => await MainThread.InvokeOnMainThreadAsync(() =>
            IPAddress.TryParse(TakeString(cw_cast_receiver(identifier)), out var ip) ? ip : null),
            "Could not connect to the Cast device. Check local network access.", ct);
    }

    public async Task LoadAsync(string url, string mime, Track track, double position, CancellationToken ct)
    {
        var started = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            RequireForeground();
            return cw_cast_load(url, mime, track.Title, track.Performer, position);
        });
        if (started == 0) throw new IOException("The Cast device disconnected.");
        mediaUrl = url;
        await WaitAsync(() => StateAsync(ct), "The Cast device could not open this recording.", ct);
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> read, string message, CancellationToken ct) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (await read() is { } result) return result;
                await Task.Delay(150, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException(message); }
    }

    public Task<AudioState?> StateAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        var state = mediaUrl is { } url ? TakeString(cw_cast_state(url)) : null;
        return state is null ? null : JsonSerializer.Deserialize<AudioState>(state, Json);
    });

    public Task PlayAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        RequireForeground();
        cw_cast_play();
    });
    public Task PauseAsync() => MainThread.InvokeOnMainThreadAsync(cw_cast_pause);
    public Task SeekAsync(double position) => MainThread.InvokeOnMainThreadAsync(() => cw_cast_seek(position));
    public Task SetVolumeAsync(double volume) => MainThread.InvokeOnMainThreadAsync(() => cw_cast_volume(volume));
    public Task StopAsync() => MainThread.InvokeOnMainThreadAsync(() => { if (mediaUrl is not null) cw_cast_stop(); mediaUrl = null; });
    public Task DisconnectAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (initialized) cw_cast_disconnect();
        mediaUrl = null;
    });

    public async ValueTask DisposeAsync()
    {
        backgroundObserver?.Dispose();
        await DisconnectAsync();
    }

    private static string? TakeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(pointer); }
        finally { cw_cast_free(pointer); }
    }

    private static void RequireForeground()
    {
        if (UIApplication.SharedApplication.ApplicationState == UIApplicationState.Background)
            throw new IOException("Open ChannelWave before starting Google Cast.");
    }

    [DllImport("__Internal")] private static extern IntPtr cw_cast_devices();
    [DllImport("__Internal")] private static extern int cw_cast_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport("__Internal")] private static extern IntPtr cw_cast_receiver([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport("__Internal")] private static extern int cw_cast_load([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mime, [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string artist, double position);
    [DllImport("__Internal")] private static extern IntPtr cw_cast_state([MarshalAs(UnmanagedType.LPUTF8Str)] string url);
    [DllImport("__Internal")] private static extern void cw_cast_play();
    [DllImport("__Internal")] private static extern void cw_cast_pause();
    [DllImport("__Internal")] private static extern void cw_cast_stop();
    [DllImport("__Internal")] private static extern void cw_cast_seek(double position);
    [DllImport("__Internal")] private static extern void cw_cast_volume(double volume);
    [DllImport("__Internal")] private static extern void cw_cast_disconnect();
    [DllImport("__Internal")] private static extern void cw_cast_free(IntPtr value);
}
#endif
