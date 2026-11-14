using ChannelWave.Core;
using ChannelWave.Telegram;
using ChannelWave.UI;
using ChannelWave.Storage.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelWave.Host.Maui;

public sealed class MauiRuntimeHost(NativeOfflineStore offline, TelegramSession session) : IRuntimeHost
{
    public bool CanSaveOffline => OperatingSystem.IsAndroid();
    public Task<IReadOnlyList<OfflineCopy>> OfflineCopiesAsync(long? account, CancellationToken ct) => offline.CopiesAsync(account, ct);
    public async Task<IReadOnlyList<Track>> OfflineTracksAsync(long? account, CancellationToken ct) => (await offline.CopiesAsync(account, ct)).Select(c => c.Track).ToArray();
    public Task SaveOfflineAsync(Track track, JsonElement file, CancellationToken ct) => offline.SaveAsync(track, file.GetProperty("local").Text("path"), ct);
    public Task<PreparedMedia?> OpenOfflineAsync(Track track, CancellationToken ct) => offline.OpenAsync(track, ct);
    public Task RemoveOfflineAsync(Track track, CancellationToken ct) => offline.RemoveAsync(track, ct);
    private static string ExportRoot(long account) => Path.Combine(FileSystem.CacheDirectory, "exports", account.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public async Task ClearOfflineAccountAsync(long account, CancellationToken ct)
    {
        await offline.ClearAccountAsync(account, ct);
        var folder = ExportRoot(account);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Invalid export staging path.");
                File.Delete(file);
            }
    }
    public Task CleanupTemporaryExportsAsync(CancellationToken ct)
    {
        if (session.AccountId is not { } account) return Task.CompletedTask;
        var folder = ExportRoot(account);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
                    try { File.Delete(file); } catch (IOException) { /* The receiving application may still own the file. */ }
            }
        return Task.CompletedTask;
    }
    public Task<StorageUsage> StorageUsageAsync(long account, CancellationToken ct) => NativeStorageUsage.ReadAsync(Path.Combine(FileSystem.AppDataDirectory, "ChannelWave"), offline, account, ct, ExportRoot(account));
    public async Task<IReadOnlyMediaFile?> OpenOfflineMetadataAsync(Track track, CancellationToken ct)
    {
        var media = await offline.OpenAsync(track, ct);
        return media is null ? null : new NativeTagFile(track.Origin.AccountId, media.Location, ExportTagsAsync);
    }
    public async Task OpenTelegramLinkAsync(TelegramMessageLink link, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!link.IsValid) throw new ArgumentException("Invalid Telegram message link.");
        if (!await Launcher.Default.OpenAsync(new Uri(link.Url))) throw new IOException("Could not open Telegram. Try again on this device.");
    }
    public PlatformCapabilities Capabilities { get; } = new("Native · MAUI Hybrid", OperatingSystem.IsAndroid(), OperatingSystem.IsAndroid(), OperatingSystem.IsAndroid(), false);
    public async Task<bool> HasSavedSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = await SecureStorage.GetAsync("channelwave-tdlib-key");
        ct.ThrowIfCancellationRequested();
        return !string.IsNullOrWhiteSpace(key);
    }
    public async Task<TelegramConfiguration> ConfigurationAsync()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "channelwave.local.json");
        using var stream = File.Exists(path) ? File.OpenRead(path) : await FileSystem.OpenAppPackageFileAsync("telegram.json");
        using var document = await JsonDocument.ParseAsync(stream);
        var root = Path.Combine(FileSystem.AppDataDirectory, "ChannelWave");
        var key = await SecureStorage.GetAsync("channelwave-tdlib-key");
        if (key is null) { key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); await SecureStorage.SetAsync("channelwave-tdlib-key", key); }
        return new(document.RootElement.GetProperty("apiId").GetInt32(), document.RootElement.GetProperty("apiHash").GetString()!, Path.Combine(root, "telegram"), Path.Combine(root, "files"), key)
        {
            HttpProxy = document.RootElement.TryGetProperty("httpProxy", out var proxy)
                ? new(proxy.GetProperty("server").GetString()!, proxy.GetProperty("port").GetInt32()) : null
        };
    }
    public Task<PreparedMedia> PrepareAsync(JsonElement file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var info = new FileInfo(file.GetProperty("local").Text("path"));
        if (!info.Exists || info.Length == 0 || file.Id("size") > 0 && info.Length != file.Id("size")) throw new IOException("The downloaded file is missing or incomplete.");
        return Task.FromResult(new PreparedMedia(info.FullName, false, info.Length));
    }
    public Task ClearSessionAsync() { SecureStorage.Remove("channelwave-tdlib-key"); return Task.CompletedTask; }
    public async Task<ITagFile> OpenTagFileAsync(Track track, JsonElement? file, CancellationToken ct)
    {
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        var media = file is { } downloaded ? await PrepareAsync(downloaded, ct)
            : await offline.OpenAsync(track, ct) ?? throw new IOException("The saved offline copy is unavailable.");
        return new NativeTagFile(track.Origin.AccountId, media.Location, ExportTagsAsync);
    }
    private static async Task<string> ExportTagsAsync(NativeTagFile file, Id3Rewrite rewrite, string name, CancellationToken ct)
    {
        var folder = Path.Combine(ExportRoot(file.AccountId), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var safe = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safe)) safe = "tagged.mp3";
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        var destination = Path.Combine(folder, safe);
        try
        {
            await file.WriteCopyAsync(destination, rewrite, ct); ct.ThrowIfCancellationRequested();
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Save tagged MP3 copy", File = new ShareFile(destination) });
            return "Choose a destination for the tagged copy in your device's share / save sheet.";
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
    public async Task<string> ExportAsync(JsonElement file, string name, CancellationToken ct)
    {
        var media = await PrepareAsync(file, ct);
        var account = session.AccountId ?? throw new InvalidOperationException("Reconnect Telegram to export this song.");
        var folder = Path.Combine(ExportRoot(account), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var safeName = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "audio";
        foreach (var invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        var destination = Path.Combine(folder, safeName);
        await NativeFileCopy.ExportAsync(media.Location, destination, ct);
        ct.ThrowIfCancellationRequested();
        await Share.Default.RequestAsync(new ShareFileRequest { Title = "Save audio to your device", File = new ShareFile(destination) });
        return "Choose a destination in your device's share / save sheet.";
    }
}
