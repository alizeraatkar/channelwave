using ChannelWave.Core;
using ChannelWave.Telegram;
using System.Text.Json;

namespace ChannelWave.UI;

public sealed class TelegramTransferWorker(TelegramSession session, ILibraryRepository library, IRuntimeHost host,
    PlaybackCoordinator playback, MediaStorageService storage) : ITransferWorker
{
    private readonly object operationLock = new();
    private readonly Dictionary<Guid, Guid> operations = [];
    private Guid Begin(Guid job)
    {
        var operation = Guid.NewGuid();
        lock (operationLock) operations[job] = operation;
        return operation;
    }
    private void End(Guid job, Guid operation)
    {
        lock (operationLock) if (operations.GetValueOrDefault(job) == operation) operations.Remove(job);
    }
    private bool Active(Track track) => playback.CurrentTrack is { } current && Metadata.FileUseKey(current) == Metadata.FileUseKey(track)
        && (playback.CanResume || playback.IsPreparing);
    private async Task<Track> ResolveAsync(TransferJob job, CancellationToken ct)
    {
        if (session.AccountId != job.Track.Origin.AccountId) throw new TransferWaitException("Reconnect Telegram to continue.", TimeSpan.FromSeconds(2));
        var track = await library.TrackAsync(job.Track.Origin.AccountId, job.Track.Id, ct)
            ?? throw new InvalidOperationException("This song or its source was removed.");
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        if (!track.Available) throw new InvalidOperationException("This Telegram message is unavailable.");
        if (Metadata.MediaKey(track) != Metadata.MediaKey(job.Track)) throw new InvalidOperationException("The recording changed. Add the updated song to Downloads again.");
        if (Active(track)) throw new TransferWaitException("Waiting for playback of this song to stop.", TimeSpan.FromSeconds(1));
        return track;
    }
    public async Task PrepareAsync(TransferJob job, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var operation = Begin(job.Id);
        try { await PrepareCoreAsync(job, progress, operation, ct); }
        finally { End(job.Id, operation); }
    }
    private async Task PrepareCoreAsync(TransferJob job, IProgress<TransferProgress> progress, Guid operation, CancellationToken ct)
    {
        var track = await ResolveAsync(job, ct);
        using var foreground = await session.MediaWork.ForegroundAsync(ct);
        using var lease = await session.MediaWork.FileAsync(Metadata.FileUseKey(track), ct);
        if (Active(track)) throw new TransferWaitException("Waiting for playback of this song to stop.", TimeSpan.FromSeconds(1));
        try
        {
            if (job.Purpose == TransferPurpose.Offline)
            {
                var saved = await host.OpenOfflineAsync(track, ct);
                if (saved is not null) { progress.Report(new(saved.Bytes, saved.Bytes)); return; }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.JSInterop.JSException) { /* Replace a damaged pin with a verified copy. */ }
        var downloads = new TelegramDownloads(session);
        downloads.Progress += (bytes, total) => progress.Report(new(bytes, total));
        try
        {
            await session.WaitForRequestsAsync(ct);
            var file = await downloads.CompleteAsync(track, ct);
            if (session.AccountId != track.Origin.AccountId) throw new TransferWaitException("Reconnect the original Telegram account to continue.", TimeSpan.FromSeconds(2));
            await storage.RegisterAsync(track, file.Id("size"), ct);
            if (job.Purpose == TransferPurpose.Offline)
            {
                progress.Report(new(file.Id("size"), file.Id("size"), "Saving offline"));
                await host.SaveOfflineTransferAsync(track, file, operation, ct);
            }
            else
            {
                // Validate actual bytes before claiming an export is ready; this does not save a copy externally.
                await host.ValidateExportAsync(file, ct);
            }
        }
        catch (TelegramException e) when (e.Code == 429)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(1, e.RetryAfter ?? 30));
            session.DeferRequestsUntil(DateTimeOffset.UtcNow + delay);
            throw new TransferRetryException("Waiting for Telegram's download limit.", delay);
        }
        catch (TelegramException e) when (e.Code >= 500) { throw new IOException("Telegram is temporarily unavailable.", e); }
        catch (IOException e) when ((e.HResult & 0xffff) is 112 or 39 or 28) { throw new InvalidOperationException("Not enough device space. Manage storage, then retry saving this song.", e); }
        catch (Microsoft.JSInterop.JSException e) { throw new InvalidOperationException("Could not save this song. Free storage or reconnect, then retry. " + e.Message.Split('\n')[0], e); }
    }
    public async Task<string> ExportAsync(TransferJob job, CancellationToken ct)
    {
        var operation = Begin(job.Id);
        try { return await ExportCoreAsync(job, operation, ct); }
        finally { End(job.Id, operation); }
    }
    private async Task<string> ExportCoreAsync(TransferJob job, Guid operation, CancellationToken ct)
    {
        var track = await ResolveAsync(job, ct);
        using var foreground = await session.MediaWork.ForegroundAsync(ct);
        using var lease = await session.MediaWork.FileAsync(Metadata.FileUseKey(track), ct);
        var file = await new TelegramDownloads(session).CompleteAsync(track, ct);
        ct.ThrowIfCancellationRequested();
        if (session.AccountId != track.Origin.AccountId) throw new TransferWaitException("Reconnect the original Telegram account to export this song.", TimeSpan.FromSeconds(2));
        return await host.ExportTransferAsync(file, track.Media.FileName, operation, ct);
    }
    public Task CancelAsync(TransferJob job)
    {
        Guid operation;
        lock (operationLock) operation = operations.GetValueOrDefault(job.Id);
        return operation == Guid.Empty ? Task.CompletedTask : host.CancelTransferAsync(operation, job.Purpose);
    }
}
