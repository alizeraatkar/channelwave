using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.JSInterop;
using System.Text.Json;

namespace ChannelWave.Platform.Browser;

// Composition belongs in the web host; this adapter has no dependency on Razor UI.
public sealed class BrowserRuntime(BrowserBridge bridge, HttpClient http)
{
    public PlatformCapabilities Capabilities { get; } = new("Standalone browser", false, false, true, false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyMediaFile?> OpenOfflineMetadataAsync(Track track, CancellationToken ct)
    {
        var module = await bridge.ModuleAsync();
        var handle = await module.InvokeAsync<TagHandle?>("openOfflineMetadata", ct, track.Origin.AccountId, track.Id, Metadata.MediaKey(track));
        if (handle is null) return null;
        if (ct.IsCancellationRequested) { await module.InvokeVoidAsync("closeTagFile", handle.Token); ct.ThrowIfCancellationRequested(); }
        return new BrowserTagFile(module, track.Origin.AccountId, handle);
    }
    public async Task<byte[]> ReadMetadataRangeAsync(Track track, JsonElement file, long offset, int count, CancellationToken ct)
    {
        try
        {
            return await (await bridge.ModuleAsync()).InvokeAsync<byte[]>("readMetadataRange", ct, track.Origin.AccountId, track.Id,
                Metadata.MediaKey(track), file.GetProperty("id").GetInt32(), offset, count);
        }
        catch (JSException e) { throw new IOException("The cached metadata range is unavailable. " + e.Message.Split('\n')[0], e); }
    }
    public async Task<byte[]?> ReadEmbeddedArtworkAsync(Track track, JsonElement file, CancellationToken ct)
    {
        if (track.Origin.Protected) return null;
        var module = await bridge.ModuleAsync();
        var handle = await module.InvokeAsync<TagHandle>("openCachedTagFile", ct, track.Origin.AccountId, track.Id, file.GetProperty("id").GetInt32(), file.Id("size"));
        await using var reader = new BrowserTagFile(module, track.Origin.AccountId, handle);
        return ArtworkData.Embedded(await Id3Codec.ReadArtworkAsync(reader, ct));
    }
    public async Task<byte[]> ReadArtworkFileAsync(JsonElement file, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeAsync<byte[]>("readArtworkFile", ct, file.GetProperty("id").GetInt32(), file.Id("size"));
    public async Task<ITagFile> OpenTagFileAsync(Track track, JsonElement? file, CancellationToken ct)
    {
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        var module = await bridge.ModuleAsync();
        ct.ThrowIfCancellationRequested();
        // Wait for handle creation so a canceled editor can still release a late handle.
        var handle = await module.InvokeAsync<TagHandle>("openTagFile", track.Origin.AccountId, track.Id,
            file?.GetProperty("id").GetInt32(), file?.Id("size") ?? 0, Metadata.MediaKey(track));
        if (ct.IsCancellationRequested) { await module.InvokeVoidAsync("closeTagFile", handle.Token); ct.ThrowIfCancellationRequested(); }
        return new BrowserTagFile(module, track.Origin.AccountId, handle);
    }
    private sealed record TagHandle(string Token, long Length);
    private sealed class BrowserTagFile(IJSObjectReference module, long account, TagHandle handle) : ITagFile
    {
        private bool disposed;
        public long AccountId => account;
        public long Length => handle.Length;
        public async Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await module.InvokeAsync<byte[]>("readTagRange", ct, handle.Token, account, offset, count);
        }
        public async Task<string> ExportCopyAsync(Id3Rewrite rewrite, string name, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
            await module.InvokeVoidAsync("exportTagCopy", ct, handle.Token, account, rewrite.OriginalPrefixLength,
                rewrite.Prefix, rewrite.ReplaceLegacyFooter, rewrite.LegacyFooter, name);
            return "Tagged copy sent to your browser. Check your Downloads folder.";
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return; disposed = true; await module.InvokeVoidAsync("closeTagFile", handle.Token);
        }
    }
    public async Task CancelOfflineSaveAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("cancelOfflineSave");
    public async Task CancelExportAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("cancelExport");
    public async Task CancelTransferAsync(Guid operation) => await (await bridge.ModuleAsync()).InvokeVoidAsync("cancelTransfer", operation.ToString("N"));
    public async Task SaveOfflineTransferAsync(Track track, JsonElement file, Guid operation, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeVoidAsync("saveOffline", ct, JsonSerializer.Serialize(track, Json), file.GetProperty("id").GetInt32(), file.Id("size"), operation.ToString("N"));
    public async Task<string> ExportTransferAsync(JsonElement file, string name, Guid operation, CancellationToken ct)
    {
        await (await bridge.ModuleAsync()).InvokeVoidAsync("exportFile", ct, file.GetProperty("id").GetInt32(), name, file.Id("size"), operation.ToString("N"));
        return "Download sent to your browser. Check your Downloads folder.";
    }
    public async Task ValidateExportAsync(JsonElement file, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeVoidAsync("validateExportFile", ct, file.GetProperty("id").GetInt32(), file.Id("size"));
    public async Task<IReadOnlyList<OfflineCopy>> OfflineCopiesAsync(long? account, CancellationToken ct)
        => JsonSerializer.Deserialize<OfflineCopy[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("offlineCopies", ct, account), Json) ?? [];
    public async Task<StorageUsage> StorageUsageAsync(long account, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeAsync<StorageUsage>("storageUsage", ct, account);
    public async Task CancelMediaWorkAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("cancelMediaWork");
    public async Task<IReadOnlyList<Track>> OfflineTracksAsync(long? account, CancellationToken ct)
        => JsonSerializer.Deserialize<Track[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("offlineTracks", ct, account), Json) ?? [];
    public async Task SaveOfflineAsync(Track track, JsonElement file, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeVoidAsync("saveOffline", ct, JsonSerializer.Serialize(track, Json), file.GetProperty("id").GetInt32(), file.Id("size"));
    public async Task<PreparedMedia?> OpenOfflineAsync(Track track, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeAsync<PreparedMedia?>("openOffline", ct, track.Origin.AccountId, track.Id, Metadata.MediaKey(track));
    public async Task RemoveOfflineAsync(Track track, CancellationToken ct)
        => await (await bridge.ModuleAsync()).InvokeVoidAsync("removeOffline", ct, track.Origin.AccountId, track.Id);
    public async Task<bool> HasSavedSessionAsync(CancellationToken ct = default)
        => await (await bridge.ModuleAsync()).InvokeAsync<bool>("hasSavedTelegramSession", ct);
    public async Task<TelegramConfiguration> ConfigurationAsync()
    {
        using var response = await http.GetAsync("channelwave.local.json");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Developer configuration is missing. Copy channelwave.example.json to channelwave.local.json and configure the application API credentials.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(json.RootElement.GetProperty("apiId").GetInt32(), json.RootElement.GetProperty("apiHash").GetString()!, "tdlib", "tdlib", "");
    }
    public async Task<PreparedMedia> PrepareAsync(JsonElement completedFile, CancellationToken ct)
    {
        var module = await bridge.ModuleAsync();
        var media = await module.InvokeAsync<PreparedMedia>("prepareFile", ct, completedFile.GetProperty("id").GetInt32(), completedFile.Id("size"));
        var expected = completedFile.Id("size");
        if (media.Bytes <= 0 || expected > 0 && media.Bytes != expected)
        {
            await module.InvokeVoidAsync("releaseUnusedAudio", media.Location);
            throw new IOException("The downloaded browser file is incomplete. Download it again.");
        }
        return media;
    }
    public async Task ClearSessionAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("clearTelegramStorage");
    public async Task<PreparedMedia> StreamAsync(JsonElement file, string mime, CancellationToken ct)
        => (await (await bridge.ModuleAsync()).InvokeAsync<PreparedMedia>("streamFile", ct, file.GetProperty("id").GetInt32(), file.Id("size"), mime)) with { UsesNetworkRanges = true };
    public async Task<string> ExportAsync(JsonElement file, string name, CancellationToken ct)
    {
        await (await bridge.ModuleAsync()).InvokeVoidAsync("exportFile", ct, file.GetProperty("id").GetInt32(), name, file.Id("size"));
        return "Download sent to your browser. Check your Downloads folder.";
    }
}
