using ChannelWave.Core;
using ChannelWave.Telegram;
using System.Text.Json;

namespace ChannelWave.UI;

public sealed class MediaStorageService(ILibraryRepository library, IRuntimeHost host, TelegramSession session, PlaybackCoordinator playback)
{
    private const string CacheKey = "media-cache:v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record CachedMedia(Track Track, long Bytes, DateTimeOffset LastUsed);
    private sealed record History(TransferJob[] Jobs);
    public async Task<StorageSettings> SettingsAsync(long account, CancellationToken ct = default)
    {
        var json = await library.ReadStateAsync(account, StorageSettings.Key, ct);
        return json is null ? StorageSettings.Default(host.UsesBrowserLinks) : JsonSerializer.Deserialize<StorageSettings>(json) ?? StorageSettings.Default(host.UsesBrowserLinks);
    }
    public async Task SetLimitAsync(long account, long limit, CancellationToken ct = default)
    {
        if (!StorageSettings.Limits.Contains(limit)) throw new ArgumentOutOfRangeException(nameof(limit));
        await library.SaveStateAsync(account, StorageSettings.Key, JsonSerializer.Serialize(new StorageSettings(limit)), ct);
        await TrimAsync(account, false, ct);
    }
    private async Task<CachedMedia[]> EntriesAsync(long account, CancellationToken ct)
    {
        var json = await library.ReadStateAsync(account, CacheKey, ct);
        return json is null ? [] : JsonSerializer.Deserialize<CachedMedia[]>(json) ?? [];
    }
    public async Task RegisterAsync(Track track, long bytes, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var rows = await EntriesAsync(track.Origin.AccountId, ct);
            await library.SaveStateAsync(track.Origin.AccountId, CacheKey, JsonSerializer.Serialize(rows.Where(c => Metadata.FileUseKey(c.Track) != Metadata.FileUseKey(track))
                .Append(new CachedMedia(track with { Artwork = null, EmbeddedMetadata = null }, bytes, DateTimeOffset.UtcNow))), ct);
        }
        finally { gate.Release(); }
    }
    public async Task<StorageUsage> UsageAsync(long account, CancellationToken ct = default)
    {
        var usage = await host.StorageUsageAsync(account, ct);
        if (!host.UsesBrowserLinks && session.AccountId == account)
        {
            var stats = await session.Transport.SendAsync(Td.Query("getStorageStatisticsFast"), ct);
            usage = usage with { TemporaryBytes = Math.Max(usage.TemporaryBytes, stats.Id("files_size")) };
        }
        return usage with { ProtectedBytes = usage.ProtectedBytes + (await EntriesAsync(account, ct)).Where(c => Playing(c.Track)).Sum(c => c.Bytes) };
    }
    private bool Playing(Track track) => playback.CurrentTrack is { } current && Metadata.FileUseKey(current) == Metadata.FileUseKey(track);
    public async Task TrimAsync(long account, bool clear, CancellationToken ct = default)
    {
        if (session.AccountId != account) return;
        await host.CleanupTemporaryExportsAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            var entries = (await EntriesAsync(account, ct)).ToList();
            var historyJson = await library.ReadStateAsync(account, TransferManager.JournalKey, ct);
            var history = historyJson is null ? [] : JsonSerializer.Deserialize<History>(historyJson)?.Jobs ?? [];
            var exports = history
                .Where(j => j.Purpose == TransferPurpose.Export && !j.Finished && j.State != TransferState.Failed).Select(j => Metadata.FileUseKey(j.Track)).ToHashSet();
            var budget = clear ? 0 : (await SettingsAsync(account, ct)).TemporaryLimit;
            var total = entries.Sum(c => c.Bytes);
            using var foreground = await session.MediaWork.ForegroundAsync(ct);
            foreach (var entry in entries.OrderBy(c => c.LastUsed).ToArray())
            {
                if (session.AccountId != account) return;
                if (total <= budget) break;
                if (Playing(entry.Track) || exports.Contains(Metadata.FileUseKey(entry.Track))) continue;
                using var lease = session.MediaWork.TryFile(Metadata.FileUseKey(entry.Track));
                if (lease is null) continue;
                if (Playing(entry.Track)) continue;
                JsonElement message;
                try { message = await session.Transport.SendAsync(Td.Query("getMessage", ("chat_id", entry.Track.Origin.ChatId), ("message_id", entry.Track.Origin.MessageId)), ct); }
                catch (TelegramException e) when (e.Code is 403 or 404)
                {
                    entries.Remove(entry); total -= entry.Bytes;
                    await library.SaveStateAsync(account, CacheKey, JsonSerializer.Serialize(entries), ct);
                    continue;
                }
                if (session.AccountId != account) return;
                if (message.TryGetProperty("content", out var content) && content.Type() == "messageAudio")
                {
                    var file = content.GetProperty("audio").GetProperty("audio");
                    await session.Transport.SendAsync(Td.Query("deleteFile", ("file_id", file.GetProperty("id").GetInt32())), ct);
                }
                else continue;
                entries.Remove(entry); total -= entry.Bytes;
                // Commit each deletion so interrupted cleanup does not claim a deleted file still exists.
                await library.SaveStateAsync(account, CacheKey, JsonSerializer.Serialize(entries), ct);
            }
            if (!host.UsesBrowserLinks)
            {
                if (session.AccountId != account) return;
                using var mutation = session.MediaWork.TryCacheMutation();
                if (mutation is null) return;
                // TDLib can also reclaim old cache files created before this app's ledger existed.
                // Exclude entire chats when any song there is in use; this is deliberately conservative.
                var excluded = history.Where(j => j.State is TransferState.Downloading or TransferState.Saving or TransferState.ReadyToExport or TransferState.Exporting)
                    .Select(j => j.Track.Origin.ChatId).ToHashSet();
                if (playback.CurrentTrack is { } playing && playing.Origin.AccountId == account) excluded.Add(playing.Origin.ChatId);
                await session.Transport.SendAsync(Td.Query("optimizeStorage", ("size", budget), ("ttl", clear ? 0 : -1),
                    ("count", clear ? 0 : -1), ("immunity_delay", 0), ("file_types", Array.Empty<object>()),
                    ("chat_ids", Array.Empty<long>()), ("exclude_chat_ids", excluded.ToArray()),
                    ("return_deleted_file_statistics", false), ("chat_limit", 0)), ct);
            }
        }
        finally { gate.Release(); }
    }
}
