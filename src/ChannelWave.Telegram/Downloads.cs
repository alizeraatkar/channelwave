using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed class TelegramDownloads(TelegramSession session)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<long, long>? Progress;
    public async Task<JsonElement> ResolveAsync(Track track, CancellationToken ct)
    {
        if (session.AccountId != track.Origin.AccountId) throw new InvalidOperationException("The recording belongs to another account.");
        var message = await session.Transport.SendAsync(Td.Query("getMessage", ("chat_id", track.Origin.ChatId), ("message_id", track.Origin.MessageId)), ct);
        if (message.GetProperty("content").Type() != "messageAudio") throw new InvalidOperationException("This message no longer contains audio.");
        if (!message.Flag("can_be_saved")) throw new InvalidOperationException("This source restricts saving. Playback and download are unavailable in this client.");
        return message.GetProperty("content").GetProperty("audio").GetProperty("audio").Clone();
    }
    public async Task<JsonElement> CompleteAsync(Track track, CancellationToken ct)
    {
        using var mediaWork = await session.MediaWork.ForegroundAsync(ct);
        if (session.AccountId != track.Origin.AccountId) throw new InvalidOperationException("The recording belongs to another account.");
        await gate.WaitAsync(ct);
        try
        {
            // File IDs are session locators. Re-resolve the original message before every request.
            var file = await ResolveAsync(track, ct);
            // Reissuing downloadFile can invalidate tdweb's completed persistent cache.
            // ResolveAsync has already rechecked account and message permissions.
            if (file.GetProperty("local").Flag("is_downloading_completed"))
            {
                Progress?.Invoke(file.Id("size"), file.Id("size"));
                return file;
            }
            var id = file.GetProperty("id").GetInt32();
            var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(JsonElement candidate)
            {
                if (candidate.GetProperty("id").GetInt32() != id) return;
                var local = candidate.GetProperty("local");
                Progress?.Invoke(local.Id("downloaded_size"), candidate.Id("expected_size"));
                if (local.Flag("is_downloading_completed")) completed.TrySetResult(candidate.Clone());
            }
            void Update(JsonElement update) { if (update.Type() == "updateFile") Observe(update.GetProperty("file")); }
            session.Update += Update;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var registration = timeout.Token.Register(() => completed.TrySetCanceled(timeout.Token));
            try
            {
                // Subscribe before sending: completion can arrive before the initial response.
                Observe(await session.Transport.SendAsync(Td.Query("downloadFile", ("file_id", id), ("priority", 16), ("offset", 0), ("limit", 0), ("synchronous", false)), timeout.Token));
                return await completed.Task;
            }
            catch (OperationCanceledException)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await session.Transport.SendAsync(Td.Query("cancelDownloadFile", ("file_id", id), ("only_if_pending", false)), cancelTimeout.Token); } catch (Exception) { }
                throw;
            }
            finally { session.Update -= Update; }
        }
        finally { gate.Release(); }
    }
}
