using System.Runtime.InteropServices;
using ChannelWave.Core;

namespace ChannelWave.Host.Linux;

internal sealed partial class LibVlcAudio
{
    private const int RendererAdded = 0x502, RendererRemoved = 0x503;
    private readonly object renderersGate = new();
    private readonly Dictionary<IntPtr, AudioOutput> renderers = [];
    private IntPtr discovery, activeRenderer;
    private AudioOutput activeOutput = AudioOutput.Default;
    private NativeCallback? discoveryCallback;
    private Func<long, string, double, Task>? notifyRoute;
    private long routeEpoch;
    private string? mediaPath;
    private double? pausedPosition;
    private int outputVolume = 100;
    private bool discoveryAttempted;

    public Task ObserveAsync(long playbackId, Func<long, string, double, Task> notify, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        routeEpoch = playbackId;
        notifyRoute = notify;
        return Task.CompletedTask;
    }

    public Task<AudioOutputs> GetOutputsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (discardAudio || player == IntPtr.Zero) return Task.FromResult(AudioOutputs.SystemOnly);
        StartDiscovery();
        var devices = LocalDevices();
        lock (renderersGate)
            return Task.FromResult(new AudioOutputs(activeOutput, [AudioOutput.Default, ..devices, ..renderers.Values],
                Help: discovery == IntPtr.Zero
                    ? "Chromecast discovery requires libVLC’s mDNS renderer plugin."
                    : "Choose a Chromecast on the same network. Refresh to look for more devices."));
    }

    private List<AudioOutput> LocalDevices()
    {
        var devices = new List<AudioOutput>();
        var first = libvlc_audio_output_device_enum(player);
        try
        {
            for (var current = first; current != IntPtr.Zero;)
            {
                var node = Marshal.PtrToStructure<DeviceNode>(current);
                var id = Marshal.PtrToStringUTF8(node.Id);
                if (!string.IsNullOrEmpty(id)) devices.Add(new(id, Marshal.PtrToStringUTF8(node.Description) ?? "Audio device"));
                current = node.Next;
            }
        }
        finally { if (first != IntPtr.Zero) libvlc_audio_output_device_list_release(first); }
        return devices;
    }

    public async Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (discardAudio || player == IntPtr.Zero || id == activeOutput.Id) return;
        AudioOutput? next = id == "" ? AudioOutput.Default : LocalDevices().FirstOrDefault(output => output.Id == id);
        IntPtr renderer = IntPtr.Zero;
        lock (renderersGate)
        {
            if (next is null)
            {
                foreach (var candidate in renderers)
                {
                    if (candidate.Value.Id != id) continue;
                    next = candidate.Value;
                    renderer = libvlc_renderer_item_hold(candidate.Key);
                    break;
                }
            }
        }
        if (next is null) throw new IOException("This audio output is no longer available.");
        try
        {
            if (renderer != IntPtr.Zero || activeRenderer != IntPtr.Zero || id == "")
            {
                var old = player;
                libvlc_media_player_stop(old);
                player = IntPtr.Zero;
                libvlc_media_player_release(old);
                CreatePlayer(renderer);
                if (mediaPath is { } path)
                {
                    pausedPosition = position;
                    SetMedia(path, position);
                }
            }
            if (!next.Remote && next.Id.Length > 0) libvlc_audio_output_device_set(player, IntPtr.Zero, next.Id);
            activeOutput = next;
            activeRenderer = renderer;
            if (playing && pausedPosition.HasValue) await PlayAsync(ct);
        }
        catch
        {
            if (player != IntPtr.Zero) libvlc_media_player_release(player);
            player = IntPtr.Zero;
            activeOutput = AudioOutput.Default;
            activeRenderer = IntPtr.Zero;
            CreatePlayer(IntPtr.Zero);
            if (mediaPath is { } path)
            {
                pausedPosition = position;
                SetMedia(path, position);
            }
            throw;
        }
        finally { if (renderer != IntPtr.Zero) libvlc_renderer_item_release(renderer); }
    }

    private void CreatePlayer(IntPtr renderer)
    {
        player = libvlc_media_player_new(instance);
        if (player == IntPtr.Zero) throw new IOException("libVLC could not create an audio player.");
        if (renderer != IntPtr.Zero && libvlc_media_player_set_renderer(player, renderer) != 0)
            throw new IOException("libVLC could not connect to the Chromecast.");
        libvlc_audio_set_volume(player, outputVolume);
    }

    private void StartDiscovery()
    {
        if (discoveryAttempted) return;
        discoveryAttempted = true;
        discovery = libvlc_renderer_discoverer_new(instance, "microdns_renderer");
        if (discovery == IntPtr.Zero) return;
        var events = libvlc_renderer_discoverer_event_manager(discovery);
        discoveryCallback = OnRendererChanged;
        if (libvlc_event_attach(events, RendererAdded, discoveryCallback, IntPtr.Zero) != 0 ||
            libvlc_event_attach(events, RendererRemoved, discoveryCallback, IntPtr.Zero) != 0 ||
            libvlc_renderer_discoverer_start(discovery) != 0)
            StopDiscovery();
    }

    private void OnRendererChanged(IntPtr pointer, IntPtr data)
    {
        var change = Marshal.PtrToStructure<RendererEvent>(pointer);
        if (change.Item == IntPtr.Zero) return;
        if (change.Type == RendererAdded && Marshal.PtrToStringUTF8(libvlc_renderer_item_type(change.Item)) == "chromecast")
        {
            lock (renderersGate)
            {
                if (renderers.ContainsKey(change.Item)) return;
                libvlc_renderer_item_hold(change.Item);
                renderers.Add(change.Item, new($"cast:{Guid.NewGuid():N}",
                    Marshal.PtrToStringUTF8(libvlc_renderer_item_name(change.Item)) ?? "Chromecast", true));
            }
        }
        else if (change.Type == RendererRemoved)
        {
            lock (renderersGate)
            {
                if (!renderers.Remove(change.Item)) return;
                libvlc_renderer_item_release(change.Item);
            }
            if (change.Item == activeRenderer)
            {
                var epoch = routeEpoch;
                _ = Task.Run(() => notifyRoute?.Invoke(epoch, "output-lost", 0) ?? Task.CompletedTask);
            }
        }
    }

    private void StopDiscovery()
    {
        if (discovery == IntPtr.Zero) return;
        libvlc_renderer_discoverer_stop(discovery);
        libvlc_renderer_discoverer_release(discovery);
        discovery = IntPtr.Zero;
        lock (renderersGate)
        {
            foreach (var item in renderers.Keys) libvlc_renderer_item_release(item);
            renderers.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNode { public IntPtr Next, Id, Description; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RendererEvent { public int Type; public IntPtr Sender, Item; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCallback(IntPtr pointer, IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_add_option(IntPtr media, [MarshalAs(UnmanagedType.LPUTF8Str)] string option);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_player_stop(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_audio_output_device_enum(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_audio_output_device_list_release(IntPtr list);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_audio_output_device_set(IntPtr player, IntPtr module, [MarshalAs(UnmanagedType.LPUTF8Str)] string device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_renderer_discoverer_new(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_renderer_discoverer_start(IntPtr discovery);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_renderer_discoverer_stop(IntPtr discovery);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_renderer_discoverer_release(IntPtr discovery);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_renderer_discoverer_event_manager(IntPtr discovery);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_event_attach(IntPtr events, int type, NativeCallback callback, IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_media_player_set_renderer(IntPtr player, IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_renderer_item_hold(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_renderer_item_release(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_renderer_item_type(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_renderer_item_name(IntPtr renderer);
}
