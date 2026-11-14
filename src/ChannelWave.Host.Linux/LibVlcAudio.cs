using ChannelWave.Core;
using System.Runtime.InteropServices;

namespace ChannelWave.Host.Linux;

// libVLC 3 ABI. MPRIS/system controls remain a separate, explicitly disabled capability.
internal sealed partial class LibVlcAudio(bool discardAudio = false) : IAudioEngine, IAudioRouting, IAudioNotifications
{
    private const string Lib = "libvlc.so.5";
    private IntPtr instance, player;
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_new(int argc, IntPtr argv);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_release(IntPtr instance);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_media_player_new(IntPtr instance);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_player_release(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libvlc_media_new_path(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_release(IntPtr media);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_player_set_media(IntPtr player, IntPtr media);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_media_player_play(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_player_set_pause(IntPtr player, int pause);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void libvlc_media_player_set_time(IntPtr player, long milliseconds);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern long libvlc_media_player_get_time(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern long libvlc_media_player_get_length(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_media_player_is_playing(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_media_player_get_state(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_audio_get_volume(IntPtr player);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int libvlc_audio_set_volume(IntPtr player, int volume);
    public Task LoadAsync(PreparedMedia media, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (media.IsBrowserUrl || !File.Exists(media.Location)) throw new IOException("A complete local recording is required.");
        if (instance == IntPtr.Zero)
        {
            // Headless diagnostics decode through VLC's dummy output. Normal
            // playback always uses the real desktop audio device.
            var options = discardAudio ? new[] { "--quiet", "--aout=dummy" } : ["--quiet"];
            var strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            var args = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
            try
            {
                for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(args, i * IntPtr.Size, strings[i]);
                instance = libvlc_new(strings.Length, args);
            }
            finally { Marshal.FreeHGlobal(args); foreach (var option in strings) Marshal.FreeCoTaskMem(option); }
            if (instance == IntPtr.Zero) throw new InvalidOperationException("libVLC initialization failed.");
            CreatePlayer(IntPtr.Zero);
        }
        mediaPath = media.Location;
        pausedPosition = 0;
        SetMedia(media.Location, 0);
    }, ct);

    private void SetMedia(string path, double position)
    {
        var nativeMedia = libvlc_media_new_path(instance, path);
        if (nativeMedia == IntPtr.Zero) throw new IOException("libVLC could not open this recording.");
        try
        {
            if (position > 0) libvlc_media_add_option(nativeMedia, FormattableString.Invariant($":start-time={position:0.###}"));
            libvlc_media_player_set_media(player, nativeMedia);
        }
        finally { libvlc_media_release(nativeMedia); }
    }
    public Task PlayAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (player == IntPtr.Zero || mediaPath is null || libvlc_media_player_play(player) != 0)
            throw new IOException("Audio playback failed.");
        pausedPosition = null;
        return Task.CompletedTask;
    }
    public Task PauseAsync() { if (player != IntPtr.Zero) libvlc_media_player_set_pause(player, 1); return Task.CompletedTask; }
    public Task SeekAsync(double seconds)
    {
        if (pausedPosition.HasValue && mediaPath is { } path)
        {
            pausedPosition = Math.Max(0, seconds);
            SetMedia(path, pausedPosition.Value);
        }
        else if (player != IntPtr.Zero) libvlc_media_player_set_time(player, checked((long)(Math.Max(0, seconds) * 1000)));
        return Task.CompletedTask;
    }
    public Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (player == IntPtr.Zero) return Task.FromResult<AudioState?>(null);
        var state = libvlc_media_player_get_state(player); var length = Math.Max(0, libvlc_media_player_get_length(player) / 1000d);
        return Task.FromResult<AudioState?>(new(pausedPosition ?? Math.Max(0, libvlc_media_player_get_time(player) / 1000d), length, length, libvlc_media_player_is_playing(player) == 0, state == 6, Math.Clamp(libvlc_audio_get_volume(player) / 100d, 0, 1), state == 7 ? "Audio playback failed. Try Play again." : null, Buffering: state is 1 or 2));
    }
    public Task SetVolumeAsync(double volume) { outputVolume = (int)(Math.Clamp(volume, 0, 1) * 100); if (player != IntPtr.Zero) libvlc_audio_set_volume(player, outputVolume); return Task.CompletedTask; }
    public Task UnloadAsync()
    {
        if (player != IntPtr.Zero) { libvlc_media_player_stop(player); libvlc_media_player_set_media(player, IntPtr.Zero); }
        mediaPath = null;
        pausedPosition = null;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        StopDiscovery();
        if (player != IntPtr.Zero) libvlc_media_player_release(player);
        if (instance != IntPtr.Zero) libvlc_release(instance);
        player = instance = IntPtr.Zero;
        return ValueTask.CompletedTask;
    }
}
