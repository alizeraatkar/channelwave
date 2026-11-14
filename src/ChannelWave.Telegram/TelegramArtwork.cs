using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed record ArtworkMessage(JsonElement Audio, JsonElement? Thumbnail);

public sealed class TelegramArtwork(TelegramSession session)
{
    public async Task<ArtworkMessage?> ResolveAsync(Track track, CancellationToken ct)
    {
        Check(track.Origin.AccountId); if (track.Origin.Protected) return null;
        await session.WaitForRequestsAsync(ct);
        var message = await session.Transport.SendAsync(Td.Query("getMessage", ("chat_id", track.Origin.ChatId), ("message_id", track.Origin.MessageId)), ct);
        Check(track.Origin.AccountId);
        if (!message.Flag("can_be_saved") || message.GetProperty("content").Type() != "messageAudio") return null;
        var audio = message.GetProperty("content").GetProperty("audio"); var file = audio.GetProperty("audio");
        if (file.TryGetProperty("remote", out var remote) && !string.IsNullOrEmpty(track.Media.UniqueId) && remote.Text("unique_id") != track.Media.UniqueId) return null;
        JsonElement? thumbnail = audio.TryGetProperty("album_cover_thumbnail", out var cover) && cover.ValueKind == JsonValueKind.Object
            && cover.TryGetProperty("file", out var image) ? image.Clone() : null;
        return new(file.Clone(), thumbnail);
    }
    public async Task<JsonElement?> ChannelPhotoAsync(Track track, CancellationToken ct)
    {
        Check(track.Origin.AccountId); if (track.Origin.Protected) return null;
        await session.WaitForRequestsAsync(ct);
        var chat = await session.Transport.SendAsync(Td.Query("getChat", ("chat_id", track.Origin.ChatId)), ct);
        Check(track.Origin.AccountId);
        if (chat.Flag("has_protected_content")) return null;
        return chat.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object
            && photo.TryGetProperty("small", out var small) ? small.Clone() : null;
    }
    public async Task<JsonElement> CompleteImageAsync(long account, JsonElement file, CancellationToken ct)
    {
        Check(account);
        if (file.Id("size") is <= 0 or > ArtworkData.MaxBytes) throw new InvalidDataException("Artwork image exceeds the 512 KiB transfer limit.");
        if (file.TryGetProperty("local", out var local) && local.Flag("is_downloading_completed")) return file;
        var id = file.GetProperty("id").GetInt32();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var completed = await session.Transport.SendAsync(Td.Query("downloadFile", ("file_id", id), ("priority", 4), ("offset", 0), ("limit", 0), ("synchronous", true)), timeout.Token);
            Check(account);
            if (completed.GetProperty("id").GetInt32() != id || !completed.GetProperty("local").Flag("is_downloading_completed")
                || completed.Id("size") is <= 0 or > ArtworkData.MaxBytes) throw new IOException("Artwork image is incomplete.");
            return completed;
        }
        catch (OperationCanceledException)
        {
            if (session.AccountId == account)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await session.Transport.SendAsync(Td.Query("cancelDownloadFile", ("file_id", id), ("only_if_pending", true)), cancel.Token); } catch (Exception) { }
            }
            throw;
        }
    }
    private void Check(long account) { if (session.AccountId != account) throw new InvalidOperationException("Artwork belongs to another account."); }
}
