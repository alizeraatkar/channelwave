using System.Net;
using ChannelWave.Core;

namespace ChannelWave.Host.Maui;

internal interface IMobileCast : IAsyncDisposable
{
    Task<AudioOutput[]> DiscoverAsync(CancellationToken ct);
    Task<IPAddress> ConnectAsync(string id, CancellationToken ct);
    Task DisconnectAsync();
    Task LoadAsync(string url, string mime, Track track, double position, CancellationToken ct);
    Task PlayAsync(CancellationToken ct);
    Task PauseAsync();
    Task SeekAsync(double position);
    Task SetVolumeAsync(double volume);
    Task StopAsync();
    Task<AudioState?> StateAsync(CancellationToken ct);
}

internal sealed class MobileCastAudio(IAudioEngine local, IMobileCast cast,
    Func<IPAddress, string, string, ICastMediaLease>? createLease = null) : IAudioEngine, IAudioRouting, IAudioNotifications
{
    private readonly IAudioRouting localRoutes = (IAudioRouting)local;
    private AudioOutput? output;
    private IPAddress? receiver;
    private ICastMediaLease? lease;
    private Track? track;
    private PreparedMedia? media;
    private bool localLoaded, lossReported;
    private double volume = 1;
    private long epoch;
    private Func<long, string, double, Task>? notify;

    public async Task<AudioOutput> GetActiveOutputAsync(CancellationToken ct) =>
        output ?? await localRoutes.GetActiveOutputAsync(ct);

    public async Task<AudioOutputs> GetOutputsAsync(CancellationToken ct)
    {
        var routes = await localRoutes.GetOutputsAsync(ct);
        var receivers = await cast.DiscoverAsync(ct);
#if IOS
        const string castHelp = "Google Cast uses the same Wi-Fi and pauses when you leave the app.";
#else
        const string castHelp = "Google Cast requires a compatible device on the same Wi-Fi.";
#endif
        return routes with
        {
            Active = output ?? routes.Active,
            Devices = output is not null && routes.Devices.All(device => device.Id.Length != 0)
                ? [AudioOutput.Default, ..routes.Devices, ..receivers] : [..routes.Devices, ..receivers],
            Help = string.IsNullOrEmpty(routes.Help) ? castHelp : $"{routes.Help} {castHelp}"
        };
    }

    public async Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        if (id == output?.Id && output is not null) return;
        if (id?.StartsWith("cast:", StringComparison.Ordinal) != true)
        {
            if (output is not null)
            {
                // Validate an explicit local output before giving up the Cast session.
                if (id is not null) await localRoutes.SelectOutputAsync(id, position, playing, ct);
                await DisconnectAsync();
                if (media is not null)
                {
                    if (!localLoaded) { await local.LoadAsync(media, ct); localLoaded = true; }
                    await local.SeekAsync(position);
                }
                if (id is null) await localRoutes.SelectOutputAsync(id, position, playing, ct);
                await local.SetVolumeAsync(volume);
                if (playing && localLoaded) await local.PlayAsync(ct);
            }
            else await localRoutes.SelectOutputAsync(id, position, playing, ct);
            return;
        }

        if (media is null || track is null || track.Origin.Protected)
            throw new IOException("Play a song before choosing a Cast device.");
        var next = (await cast.DiscoverAsync(ct)).FirstOrDefault(device => device.Id == id)
            ?? throw new IOException("This Cast device is no longer available.");
        await local.PauseAsync();
        if (output is not null) await DisconnectAsync();
        try
        {
            receiver = await cast.ConnectAsync(id, ct);
            output = next;
            lossReported = false;
            await LoadRemoteAsync(media, track, position, ct);
            if (playing) await cast.PlayAsync(ct);
        }
        catch
        {
            await DisconnectAsync();
            if (!localLoaded) { await local.LoadAsync(media, CancellationToken.None); localLoaded = true; }
            await local.SeekAsync(position);
            await local.SetVolumeAsync(volume);
            throw;
        }
    }

    public async Task LoadAsync(PreparedMedia prepared, CancellationToken ct)
    {
        media = prepared;
        if (output is null)
        {
            await local.LoadAsync(prepared, ct);
            localLoaded = true;
        }
        else if (track is { } current)
        {
            try { await LoadRemoteAsync(prepared, current, 0, ct); }
            catch { await DisconnectAsync(); throw; }
        }
    }

    private async Task LoadRemoteAsync(PreparedMedia prepared, Track current, double position, CancellationToken ct)
    {
        var mime = current.Media.MimeType.ToLowerInvariant();
        if (prepared.IsBrowserUrl || current.Origin.Protected ||
            mime is not ("audio/mpeg" or "audio/mp4" or "audio/aac" or "audio/ogg" or "audio/wav" or "audio/webm" or "audio/flac"))
            throw new UnplayableTrackException("This recording cannot be sent to the Cast device. Choose a supported song or play it locally.");
        lease = createLease is null ? new CastMediaLease(receiver!, prepared.Location, mime)
            : createLease(receiver!, prepared.Location, mime);
        await cast.LoadAsync(lease.Url, mime, current, position, ct);
        await cast.SetVolumeAsync(volume);
    }

    public Task BeginSelectionAsync(Track selected, CancellationToken ct)
    {
        track = selected;
        return output is null ? local.BeginSelectionAsync(selected, ct) : Task.CompletedTask;
    }

    public Task SetTrackAsync(Track selected)
    {
        track = selected;
        return output is null ? local.SetTrackAsync(selected) : Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken ct) => output is null ? local.PlayAsync(ct) : cast.PlayAsync(ct);
    public Task PauseAsync() => output is null ? local.PauseAsync() : cast.PauseAsync();
    public Task SeekAsync(double seconds) => output is null ? local.SeekAsync(seconds) : cast.SeekAsync(seconds);
    public Task SetVolumeAsync(double value)
    {
        volume = value;
        return output is null ? local.SetVolumeAsync(value) : cast.SetVolumeAsync(value);
    }

    public async Task<AudioState?> StateAsync(CancellationToken ct = default)
    {
        if (output is null) return await local.StateAsync(ct);
        var state = await cast.StateAsync(ct);
        if (state is null && !lossReported)
        {
            lossReported = true;
            SignalLoss();
        }
        return state;
    }

    internal void SignalLoss()
    {
        lease?.Revoke();
        var handler = notify;
        var id = epoch;
        if (output is not null && handler is not null) _ = Task.Run(() => handler(id, "output-lost", 0));
    }

    public Task ObserveAsync(long playbackId, Func<long, string, double, Task> callback, CancellationToken ct)
    {
        epoch = playbackId;
        notify = callback;
        return local is IAudioNotifications notifications
            ? notifications.ObserveAsync(playbackId, (id, action, value) =>
                output is not null && action is ("ended" or "decode-error" or "output-lost")
                    ? Task.CompletedTask : callback(id, action, value), ct)
            : Task.CompletedTask;
    }

    private async Task RevokeMediaAsync()
    {
        var current = lease;
        lease = null;
        if (current is null) return;
        await current.DisposeAsync();
    }

    private async Task DisconnectAsync()
    {
        try
        {
            if (output is not null) await cast.StopAsync();
        }
        finally
        {
            try { await RevokeMediaAsync(); }
            finally
            {
                output = null;
                receiver = null;
                await cast.DisconnectAsync();
            }
        }
    }

    public async Task UnloadAsync()
    {
        try
        {
            // Track replacement unloads the file, not the receiver session.
            if (output is not null && lease is not null) await cast.PauseAsync();
        }
        finally
        {
            try { await RevokeMediaAsync(); }
            finally
            {
                media = null;
                track = null;
                localLoaded = false;
                await local.UnloadAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(); }
        finally { await cast.DisposeAsync(); await local.DisposeAsync(); }
    }
}
