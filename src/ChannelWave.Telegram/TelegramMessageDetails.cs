using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

public static class TelegramMessageReader
{
    public static TelegramMessageInfo Read(JsonElement message)
    {
        bool? channel = message.TryGetProperty("is_channel_post", out var post) && post.ValueKind is JsonValueKind.True or JsonValueKind.False ? post.GetBoolean() : null;
        string? kind = null;
        long? id = null;
        if (message.TryGetProperty("sender_id", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            kind = sender.Type() switch { "messageSenderUser" => "user", "messageSenderChat" => "chat", _ => null };
            if (kind is not null)
            {
                var key = kind == "user" ? "user_id" : "chat_id";
                if (sender.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    var senderId = sender.Id(key);
                    if (senderId != 0) id = senderId;
                }
            }
        }
        var signature = message.Text("author_signature");
        return new(channel, kind, id, AuthorSignature: string.IsNullOrWhiteSpace(signature) ? null : signature);
    }

    public static DateTimeOffset PostingDate(JsonElement message)
    {
        if (!message.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Number || !date.TryGetInt64(out var seconds) || seconds <= 0) return DateTimeOffset.UnixEpoch;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.UnixEpoch; }
    }

    public static async Task<TelegramMessageInfo> ResolveSenderAsync(ITdTransport transport, Source source,
        TelegramMessageInfo info, TelegramSession? session, CancellationToken ct)
    {
        if (info.SenderId is not { } id || info.SenderType is null) return info;
        if (info.SenderType == "chat" && id == source.ChatId)
            return info with { SenderName = source.Name, SenderResolved = true };
        try
        {
            if (session is not null) await session.WaitForRequestsAsync(ct);
            var sender = await transport.SendAsync(info.SenderType == "user"
                ? Td.Query("getUser", ("user_id", id)) : Td.Query("getChat", ("chat_id", id)), ct);
            Td.ThrowIfError(sender);
            var name = info.SenderType == "user" ? $"{sender.Text("first_name")} {sender.Text("last_name")}".Trim() : sender.Text("title");
            return info with { SenderName = string.IsNullOrWhiteSpace(name) ? null : name, SenderResolved = true };
        }
        catch (Exception e) when (e is TelegramException or IOException or TimeoutException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            DeferRateLimit(session, e);
            return info;
        }
    }

    public static void DeferRateLimit(TelegramSession? session, Exception error)
    {
        if (error is TelegramException { Code: 429 } rate)
            session?.DeferRequestsUntil(DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, rate.RetryAfter ?? 30)));
    }
}

public sealed record TelegramMessageDetailsResult(Track? Track, string? Notice = null, bool LinkDenied = false);

/// <summary>Refreshes only the indexed message, never its forwarded-from origin.</summary>
public sealed class TelegramMessageDetails(TelegramSession session, ILibraryRepository library)
{
    public async Task<TelegramMessageDetailsResult> RefreshAsync(Track track, CancellationToken ct = default)
    {
        var origin = track.Origin;
        void CheckAccount()
        {
            ct.ThrowIfCancellationRequested();
            if (session.AccountId != origin.AccountId) throw new OperationCanceledException("The Telegram account changed.", ct);
        }
        CheckAccount();
        await session.WaitForRequestsAsync(ct);
        JsonElement message;
        try
        {
            message = await session.Transport.SendAsync(Td.Query("getMessage", ("chat_id", origin.ChatId), ("message_id", origin.MessageId)), ct);
            Td.ThrowIfError(message);
        }
        catch (TelegramException e) when (e.Code is 400 or 403 or 404)
        {
            CheckAccount();
            var unavailable = await library.SetMessageInfoAsync(origin.AccountId, track.Id, origin.ChatId, origin.MessageId,
                new(LinkResolved: true), DateTimeOffset.UnixEpoch, ct);
            CheckAccount();
            return new(unavailable, "The message is unavailable or this account no longer has access.", true);
        }
        CheckAccount();
        if (message.Id("id") != origin.MessageId || message.TryGetProperty("chat_id", out _) && message.Id("chat_id") != origin.ChatId)
            throw new InvalidDataException("Telegram returned a different message.");
        var info = await TelegramMessageReader.ResolveSenderAsync(session.Transport,
            new(origin.AccountId, origin.ChatId, origin.SourceName), TelegramMessageReader.Read(message), session, ct);
        string? notice = null;
        var denied = false;
        try
        {
            CheckAccount();
            await session.WaitForRequestsAsync(ct);
            var properties = await session.Transport.SendAsync(Td.Query("getMessageProperties", ("chat_id", origin.ChatId), ("message_id", origin.MessageId)), ct);
            Td.ThrowIfError(properties);
            info = info with { LinkResolved = true };
            if (properties.Flag("can_get_link"))
            {
                var result = await session.Transport.SendAsync(Td.Query("getMessageLink", ("chat_id", origin.ChatId), ("message_id", origin.MessageId),
                    ("media_timestamp", 0), ("checklist_task_id", 0), ("poll_option_id", ""), ("for_album", false), ("in_message_thread", false)), ct);
                Td.ThrowIfError(result);
                var link = new TelegramMessageLink(result.Text("link"), result.Flag("is_public"));
                info = info with { Link = link.IsValid ? link : null };
            }
        }
        catch (Exception e) when (e is TelegramException or IOException or TimeoutException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            TelegramMessageReader.DeferRateLimit(session, e);
            denied = e is TelegramException { Code: 400 or 403 or 404 };
            info = info with { LinkResolved = denied, Link = null };
            notice = denied ? "Message link unavailable." : "Could not refresh the message link. Cached details are shown; reopen details to retry.";
        }
        CheckAccount();
        var updated = await library.SetMessageInfoAsync(origin.AccountId, track.Id, origin.ChatId, origin.MessageId,
            info, TelegramMessageReader.PostingDate(message), ct);
        CheckAccount();
        return new(updated, notice, denied);
    }
}
