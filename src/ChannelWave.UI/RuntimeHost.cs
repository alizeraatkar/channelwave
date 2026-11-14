using ChannelWave.Core;
using ChannelWave.Telegram;
using System.Text.Json;

namespace ChannelWave.UI;

public interface IRuntimeHost
{
    PlatformCapabilities Capabilities { get; }
    bool UsesBrowserLinks => false;
    Task OpenTelegramLinkAsync(TelegramMessageLink link, CancellationToken ct = default)
        => throw new NotSupportedException("Opening Telegram is unavailable on this host.");
    Task<TelegramConfiguration> ConfigurationAsync();
    Task<bool> HasSavedSessionAsync(CancellationToken ct = default) => Task.FromResult(false);
    Task<PreparedMedia> PrepareAsync(JsonElement completedFile, CancellationToken ct);
    bool CanStream => false;
    Task<PreparedMedia> StreamAsync(JsonElement file, string mime, CancellationToken ct) => throw new NotSupportedException("Streaming is available in the browser app. Use Play on this device.");
    Task<string> ExportAsync(JsonElement completedFile, string fileName, CancellationToken ct);
    Task<string> ExportTransferAsync(JsonElement completedFile, string fileName, Guid operation, CancellationToken ct) => ExportAsync(completedFile, fileName, ct);
    async Task ValidateExportAsync(JsonElement completedFile, CancellationToken ct) => _ = await PrepareAsync(completedFile, ct);
    Task<ITagFile> OpenTagFileAsync(Track track, JsonElement? completedFile, CancellationToken ct);
    Task<IReadOnlyMediaFile?> OpenOfflineMetadataAsync(Track track, CancellationToken ct) => Task.FromResult<IReadOnlyMediaFile?>(null);
    async Task<byte[]> ReadMetadataRangeAsync(Track track, JsonElement file, long offset, int count, CancellationToken ct)
    {
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        var path = file.GetProperty("local").Text("path");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        input.Position = offset; var data = new byte[count]; await input.ReadExactlyAsync(data, ct); return data;
    }
    async Task<byte[]?> ReadEmbeddedArtworkAsync(Track track, JsonElement file, CancellationToken ct)
    {
        if (track.Origin.Protected || !file.TryGetProperty("local", out var local) || !local.Flag("is_downloading_completed")) return null;
        await using var reader = await OpenTagFileAsync(track, file, ct);
        return ArtworkData.Embedded(await Id3Codec.ReadArtworkAsync(reader, ct));
    }
    async Task<byte[]> ReadArtworkFileAsync(JsonElement file, CancellationToken ct)
    {
        var media = await PrepareAsync(file, ct);
        if (media.IsBrowserUrl || media.Bytes is <= 0 or > ArtworkData.MaxBytes) throw new IOException("Unsupported artwork size.");
        return await File.ReadAllBytesAsync(media.Location, ct);
    }
    bool CanSaveOffline => false;
    Task<IReadOnlyList<OfflineCopy>> OfflineCopiesAsync(long? account, CancellationToken ct) => Task.FromResult<IReadOnlyList<OfflineCopy>>([]);
    Task ClearOfflineAccountAsync(long account, CancellationToken ct) => Task.CompletedTask;
    Task<StorageUsage> StorageUsageAsync(long account, CancellationToken ct) => Task.FromResult(new StorageUsage(0, 0, 0));
    Task CancelExportAsync() => Task.CompletedTask;
    Task CleanupTemporaryExportsAsync(CancellationToken ct) => Task.CompletedTask;
    Task<IReadOnlyList<Track>> OfflineTracksAsync(long? account, CancellationToken ct) => Task.FromResult<IReadOnlyList<Track>>([]);
    Task SaveOfflineAsync(Track track, JsonElement completedFile, CancellationToken ct) => throw new NotSupportedException();
    Task SaveOfflineTransferAsync(Track track, JsonElement completedFile, Guid operation, CancellationToken ct) => SaveOfflineAsync(track, completedFile, ct);
    Task CancelTransferAsync(Guid operation, TransferPurpose purpose) => purpose == TransferPurpose.Offline ? CancelOfflineSaveAsync() : CancelExportAsync();
    Task<PreparedMedia?> OpenOfflineAsync(Track track, CancellationToken ct) => Task.FromResult<PreparedMedia?>(null);
    Task RemoveOfflineAsync(Track track, CancellationToken ct) => throw new NotSupportedException();
    Task CancelOfflineSaveAsync() => Task.CompletedTask;
    Task CancelMediaWorkAsync() => CancelOfflineSaveAsync();
    Task ClearSessionAsync();
}
