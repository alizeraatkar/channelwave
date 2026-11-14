using ChannelWave.Core;
using ChannelWave.Telegram;
using ChannelWave.UI;
using ChannelWave.Storage.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelWave.Host.Linux;

internal sealed class LinuxRuntimeHost(string root, NativeOfflineStore offline) : IRuntimeHost
{
    public bool CanSaveOffline => true;
    public Task<IReadOnlyList<OfflineCopy>> OfflineCopiesAsync(long? account, CancellationToken ct) => offline.CopiesAsync(account, ct);
    public async Task<IReadOnlyList<Track>> OfflineTracksAsync(long? account, CancellationToken ct) => (await offline.CopiesAsync(account, ct)).Select(c => c.Track).ToArray();
    public Task SaveOfflineAsync(Track track, JsonElement file, CancellationToken ct) => offline.SaveAsync(track, file.GetProperty("local").Text("path"), ct);
    public Task<PreparedMedia?> OpenOfflineAsync(Track track, CancellationToken ct) => offline.OpenAsync(track, ct);
    public Task RemoveOfflineAsync(Track track, CancellationToken ct) => offline.RemoveAsync(track, ct);
    public Task ClearOfflineAccountAsync(long account, CancellationToken ct) => offline.ClearAccountAsync(account, ct);
    public Task<StorageUsage> StorageUsageAsync(long account, CancellationToken ct) => NativeStorageUsage.ReadAsync(root, offline, account, ct);
    public async Task<IReadOnlyMediaFile?> OpenOfflineMetadataAsync(Track track, CancellationToken ct)
    {
        var media = await offline.OpenAsync(track, ct);
        return media is null ? null : new NativeTagFile(track.Origin.AccountId, media.Location, ExportTagsAsync);
    }
    public Task OpenTelegramLinkAsync(TelegramMessageLink link, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!link.IsValid) throw new ArgumentException("Invalid Telegram message link.");
        using var process = Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
        if (process is null) throw new IOException("Could not open Telegram. Try again on this device.");
        return Task.CompletedTask;
    }
    public PlatformCapabilities Capabilities { get; } = new("Linux · Photino", false, false, true, false);
    public async Task<bool> HasSavedSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = await SecretTool(["lookup", "application", "channelwave", "purpose", "tdlib-key"]);
        ct.ThrowIfCancellationRequested();
        return !string.IsNullOrWhiteSpace(key);
    }
    public async Task<TelegramConfiguration> ConfigurationAsync()
    {
        var configPath = Path.Combine(root, "channelwave.local.json");
        if (!File.Exists(configPath)) configPath = Path.Combine(AppContext.BaseDirectory, "telegram.json");
        if (!File.Exists(configPath)) throw new InvalidOperationException("Telegram application configuration is missing. Install an official release or configure channelwave.local.json in the ChannelWave application-data directory.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var key = await SecretTool(["lookup", "application", "channelwave", "purpose", "tdlib-key"]);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await SecretTool(["store", "--label=ChannelWave Telegram database", "application", "channelwave", "purpose", "tdlib-key"], key);
        }
        return new(document.RootElement.GetProperty("apiId").GetInt32(), document.RootElement.GetProperty("apiHash").GetString()!, Path.Combine(root, "telegram"), Path.Combine(root, "files"), key.Trim());
    }
    private static async Task<string> SecretTool(string[] args, string? secret = null)
    {
        var info = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Linux Secret Service is required to protect sessions.");
        if (secret is not null) await process.StandardInput.WriteLineAsync(secret);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(); throw new InvalidOperationException("Unlock your Linux keyring and try again."); }
        await errors;
        if (process.ExitCode != 0 && (secret is not null || process.ExitCode != 1)) throw new InvalidOperationException("Unlock your Linux keyring before connecting Telegram.");
        return await output;
    }
    public Task<PreparedMedia> PrepareAsync(JsonElement file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var path = file.GetProperty("local").Text("path"); var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || file.Id("size") > 0 && info.Length != file.Id("size")) throw new IOException("Downloaded file is missing or incomplete.");
        return Task.FromResult(new PreparedMedia(path, false, info.Length));
    }
    public async Task<string> ExportAsync(JsonElement file, string name, CancellationToken ct)
    {
        var media = await PrepareAsync(file, ct);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ChannelWave");
        Directory.CreateDirectory(folder);
        var safeName = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "audio";
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + "-" + safeName);
        await NativeFileCopy.ExportAsync(media.Location, destination, ct);
        return "Saved to " + destination;
    }
    public async Task<ITagFile> OpenTagFileAsync(Track track, JsonElement? file, CancellationToken ct)
    {
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        var media = file is { } downloaded ? await PrepareAsync(downloaded, ct)
            : await offline.OpenAsync(track, ct) ?? throw new IOException("The saved offline copy is unavailable.");
        return new NativeTagFile(track.Origin.AccountId, media.Location, ExportTagsAsync);
    }
    private static async Task<string> ExportTagsAsync(NativeTagFile file, Id3Rewrite rewrite, string name, CancellationToken ct)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ChannelWave");
        Directory.CreateDirectory(folder);
        var safe = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safe)) safe = "tagged.mp3";
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + "-" + safe);
        await file.WriteCopyAsync(destination, rewrite, ct);
        return "Tagged copy saved to " + destination;
    }
    public async Task ClearSessionAsync() => await SecretTool(["clear", "application", "channelwave", "purpose", "tdlib-key"]);
}
