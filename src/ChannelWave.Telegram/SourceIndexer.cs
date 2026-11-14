using ChannelWave.Core;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ChannelWave.Telegram;

public sealed class SourceIndexer(ITdTransport transport, ILibraryRepository repository, TelegramSession? session = null)
{
    private readonly ConcurrentDictionary<(long Account, long Chat), SemaphoreSlim> gates = new();
    public event Action<long, long>? Changed;
    public async Task<Source> SelectAsync(Source source, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd((source.AccountId, source.ChatId), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var saved = await repository.SourceAsync(source.AccountId, source.ChatId, ct);
            if (saved is not null) return saved;
            await repository.SavePageAsync(source, [], ct);
            return source;
        }
        finally { gate.Release(); }
    }
    public async Task<Guid[]> UnindexAsync(long account, long chat, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd((account, chat), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { return await repository.RemoveSourceAsync(account, chat, ct); }
        finally { gate.Release(); }
    }
    public async Task<Source> IndexPageAsync(Source source, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd((source.AccountId, source.ChatId), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Manual and background requests share this coordinator and always use the committed cursor.
            var committed = await repository.SourceAsync(source.AccountId, source.ChatId, ct);
            if (committed is not null) source = committed with { Name = source.Name };
            if (source.HistoryComplete) return source;
            if (session is not null) await session.WaitForRequestsAsync(ct);
            var page = await transport.SendAsync(Td.Query("searchChatMessages", ("chat_id", source.ChatId), ("topic_id", null),
                ("query", ""), ("sender_id", null), ("from_message_id", source.HistoryCursor), ("offset", 0),
                ("limit", 100), ("filter", Td.Query("searchMessagesFilterAudio"))), ct);
            var next = page.Id("next_from_message_id");
            if (next != 0 && next == source.HistoryCursor) throw new InvalidDataException("Telegram repeated its pagination cursor. Retry this source later.");
            var tracks = page.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("content").Type() == "messageAudio")
                .Where(m => source.Sync?.DeletedMessages?.Contains(m.Id("id")) != true)
                .Select(m => ParseTrack(source, m)).DistinctBy(x => x.Identity).ToArray();
            var senders = new Dictionary<(string?, long?), TelegramMessageInfo>();
            for (var i = 0; i < tracks.Length; i++)
            {
                var info = tracks[i].Origin.Message!;
                var key = (info.SenderType, info.SenderId);
                if (!senders.TryGetValue(key, out var resolved))
                {
                    resolved = await TelegramMessageReader.ResolveSenderAsync(transport, source, info, session, ct);
                    senders[key] = resolved;
                }
                // Names can be shared across a page; signatures belong to each message.
                tracks[i] = tracks[i] with { Origin = tracks[i].Origin with { Message = info with { SenderName = resolved.SenderName, SenderResolved = resolved.SenderResolved } } };
            }
            var updated = source with { HistoryCursor = next, HistoryComplete = next == 0, LatestMessageId = Math.Max(source.LatestMessageId, tracks.Select(t => t.Origin.MessageId).DefaultIfEmpty().Max()), RefreshedAt = DateTimeOffset.UtcNow, Error = null };
            ct.ThrowIfCancellationRequested();
            if (session is not null && session.AccountId != source.AccountId) throw new OperationCanceledException("The Telegram account changed.", ct);
            await repository.SavePageAsync(updated, tracks, ct);
            Changed?.Invoke(source.AccountId, source.ChatId);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async Task ReconcilePageAsync(long account, long chat, bool audit, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd((account, chat), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await repository.SourceAsync(account, chat, ct) is not { HistoryComplete: true } source) return;
            var sync = source.Sync ?? new();
            if (audit && sync.AuditedAt > DateTimeOffset.UtcNow.AddDays(-1) && sync.AuditCursor == 0 && !sync.AuditVerifying) return;
            CheckAccount(account, ct);
            if (session is not null) await session.WaitForRequestsAsync(ct);
            if (audit && sync.AuditVerifying)
            {
                var ids = await repository.SourceMessageIdsAsync(account, chat, sync.VerificationCursor, ct);
                var tracks = new List<Track>(); var unavailable = new List<long>();
                if (ids.Length > 0)
                {
                    var response = await transport.SendAsync(Td.Query("getMessages", ("chat_id", chat), ("message_ids", ids)), ct);
                    Td.ThrowIfError(response);
                    var messages = response.GetProperty("messages").EnumerateArray().ToArray();
                    if (messages.Length != ids.Length) throw new InvalidDataException("Telegram returned an incomplete message verification response.");
                    for (var i = 0; i < ids.Length; i++)
                    {
                        var message = messages[i];
                        if (message.ValueKind == JsonValueKind.Null) { unavailable.Add(ids[i]); continue; }
                        ValidateMessage(message, chat, ids[i]);
                        if (message.GetProperty("content").Type() != "messageAudio") unavailable.Add(ids[i]);
                        else if (sync.DeletedMessages?.Contains(ids[i]) != true) tracks.Add(ParseTrack(source, message));
                    }
                }
                sync = ids.Length < 100 ? sync with { AuditVerifying = false, VerificationCursor = 0, AuditedAt = DateTimeOffset.UtcNow, Status = "watching" }
                    : sync with { VerificationCursor = ids[^1], Status = "auditing" };
                CheckAccount(account, ct);
                await repository.ApplySourceChangesAsync(source with { Sync = sync with { RetryAt = null }, Error = null }, tracks, unavailable, ct: ct);
            }
            else
            {
                var cursor = audit ? sync.AuditCursor : sync.CatchUpCursor;
                if (!audit && cursor == 0) sync = sync with { CatchUpBoundary = source.LatestMessageId, CatchUpHead = 0 };
                var page = await transport.SendAsync(Td.Query("searchChatMessages", ("chat_id", chat), ("topic_id", null), ("query", ""),
                    ("sender_id", null), ("from_message_id", cursor), ("offset", 0), ("limit", 100), ("filter", Td.Query("searchMessagesFilterAudio"))), ct);
                Td.ThrowIfError(page);
                var messages = page.GetProperty("messages").EnumerateArray().ToArray();
                var next = page.Id("next_from_message_id");
                if (next != 0 && next == cursor) throw new InvalidDataException("Telegram repeated its pagination cursor.");
                var tracks = messages.Where(m => m.GetProperty("content").Type() == "messageAudio" && sync.DeletedMessages?.Contains(m.Id("id")) != true)
                    .Select(m => { ValidateMessage(m, chat, m.Id("id")); return ParseTrack(source, m); }).DistinctBy(t => t.Identity).ToArray();
                var head = Math.Max(sync.CatchUpHead, messages.Select(m => m.Id("id")).DefaultIfEmpty().Max());
                var finished = next == 0 || !audit && messages.Any(m => m.Id("id") <= sync.CatchUpBoundary);
                if (audit) sync = sync with { AuditCursor = finished ? 0 : next, AuditVerifying = finished, VerificationCursor = 0, Status = "auditing" };
                else sync = sync with { CatchUpCursor = finished ? 0 : next, CatchUpHead = finished ? 0 : head, Inaccessible = false, Status = finished ? "watching" : "catchingUp" };
                var updated = source with { Sync = sync with { RetryAt = null }, Error = null, RefreshedAt = DateTimeOffset.UtcNow,
                    LatestMessageId = !audit && finished ? Math.Max(source.LatestMessageId, head) : source.LatestMessageId };
                CheckAccount(account, ct);
                await repository.ApplySourceChangesAsync(updated, tracks, [], ct: ct);
            }
            Changed?.Invoke(account, chat);
        }
        catch (TelegramException e) when (e.Code == 403)
        {
            CheckAccount(account, ct);
            if (await repository.SourceAsync(account, chat, ct) is { } source)
                await repository.ApplySourceChangesAsync(source with { Error = "Access unavailable. Retry after restoring access in Telegram.",
                    Sync = (source.Sync ?? new()) with { Inaccessible = true, Status = "unavailable" } }, [], [], allUnavailable: true, ct: ct);
            Changed?.Invoke(account, chat);
        }
        finally { gate.Release(); }
    }

    public async Task ApplyUpdateAsync(long account, JsonElement update, CancellationToken ct = default)
    {
        var type = update.Type();
        var chat = type == "updateNewMessage" ? update.GetProperty("message").Id("chat_id") : update.Id("chat_id");
        if (chat == 0) return;
        var gate = gates.GetOrAdd((account, chat), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CheckAccount(account, ct);
            if (await repository.SourceAsync(account, chat, ct) is not { } source) return;
            var sync = source.Sync ?? new();
            if (type == "updateDeleteMessages")
            {
                // Evicting TDLib's cache is not a deletion from Telegram.
                if (update.Flag("from_cache")) return;
                var ids = update.GetProperty("message_ids").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                if (update.Flag("is_permanent")) sync = sync with { DeletedMessages = (sync.DeletedMessages ?? []).Concat(ids).Distinct().ToArray() };
                await repository.ApplySourceChangesAsync(source with { Sync = sync }, [], ids, ct: ct);
            }
            else if (type == "updateChatTitle")
                await repository.ApplySourceChangesAsync(source with { Name = update.Text("title") }, [], [], ct: ct);
            else
            {
                var id = type == "updateNewMessage" ? update.GetProperty("message").Id("id") : update.Id("message_id");
                if (sync.DeletedMessages?.Contains(id) == true) return;
                if (session is not null) await session.WaitForRequestsAsync(ct);
                JsonElement message;
                try
                {
                    message = await transport.SendAsync(Td.Query("getMessage", ("chat_id", chat), ("message_id", id)), ct);
                    Td.ThrowIfError(message);
                }
                catch (TelegramException e) when (e.Code == 404 || e.Code == 400 && e.Message.Contains("Message not found", StringComparison.OrdinalIgnoreCase))
                {
                    CheckAccount(account, ct);
                    await repository.ApplySourceChangesAsync(source, [], [id], ct: ct);
                    Changed?.Invoke(account, chat); return;
                }
                ValidateMessage(message, chat, id); CheckAccount(account, ct);
                if (message.TryGetProperty("sending_state", out var sending) && sending.ValueKind == JsonValueKind.Object) return;
                var tracks = message.GetProperty("content").Type() == "messageAudio" ? new[] { ParseTrack(source, message) } : [];
                await repository.ApplySourceChangesAsync(source with { RefreshedAt = DateTimeOffset.UtcNow,
                    Sync = sync with { Inaccessible = false, Status = "watching", RetryAt = null }, Error = null }, tracks, tracks.Length == 0 ? [id] : [], ct: ct);
            }
            Changed?.Invoke(account, chat);
        }
        finally { gate.Release(); }
    }

    public async Task RecordFailureAsync(long account, long chat, string error, DateTimeOffset? retryAt, bool inaccessible, CancellationToken ct)
    {
        var gate = gates.GetOrAdd((account, chat), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            CheckAccount(account, ct);
            if (await repository.SourceAsync(account, chat, ct) is not { } source) return;
            await repository.ApplySourceChangesAsync(source with { Error = error,
                Sync = (source.Sync ?? new()) with { RetryAt = retryAt, Inaccessible = inaccessible, Status = inaccessible ? "unavailable" : "waiting" } },
                [], [], allUnavailable: inaccessible, ct: ct);
            Changed?.Invoke(account, chat);
        }
        finally { gate.Release(); }
    }

    private void CheckAccount(long account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session is not null && session.AccountId != account) throw new OperationCanceledException("The Telegram account changed.", ct);
    }

    private static void ValidateMessage(JsonElement message, long chat, long id)
    {
        if (message.Id("id") != id || message.TryGetProperty("chat_id", out _) && message.Id("chat_id") != chat)
            throw new InvalidDataException("Telegram returned a different message.");
    }

    public static Track ParseTrack(Source source, JsonElement message)
    {
        var audio = message.GetProperty("content").GetProperty("audio"); var file = audio.GetProperty("audio"); var remote = file.GetProperty("remote");
        return new(Guid.NewGuid(), new(source.AccountId, source.ChatId, message.Id("id"), source.Name, !message.Flag("can_be_saved"), TelegramMessageReader.Read(message)),
            new(remote.Text("id"), remote.Text("unique_id"), file.GetProperty("id").GetInt32(), audio.Text("file_name"), audio.Text("mime_type"), file.Id("size")),
            audio.Text("title"), audio.Text("performer"), Metadata.Title(audio.Text("title"), audio.Text("file_name")),
            string.IsNullOrWhiteSpace(audio.Text("performer")) ? "Unknown artist" : audio.Text("performer"), audio.GetProperty("duration").GetInt32(),
            TelegramMessageReader.PostingDate(message));
    }
}
