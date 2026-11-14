using System.Text.Json.Serialization;

namespace ChannelWave.Core;

public sealed record TelegramMessageLink(string Url, bool IsPublic)
{
    [JsonIgnore] public bool IsValid => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort && uri.AbsolutePath.Length > 1;
}

public sealed record TelegramMessageInfo(bool? IsChannelPost = null, string? SenderType = null, long? SenderId = null,
    string? SenderName = null, string? AuthorSignature = null, bool SenderResolved = false,
    TelegramMessageLink? Link = null, bool LinkResolved = false)
{
    [JsonIgnore] public string Attribution => IsChannelPost == true
        ? Nonempty(AuthorSignature) ?? (SenderType == "user" ? Nonempty(SenderName) : null) ?? "Not provided by Telegram"
        : Nonempty(SenderName) is { } name
            ? Nonempty(AuthorSignature) is { } signature && signature != name ? $"{name} · {signature}" : name
            : Nonempty(AuthorSignature) ?? "Unknown sender";

    // A fresh message can authoritatively remove its signature. An unresolved name
    // or link lookup, however, must not discard an already available value.
    public static TelegramMessageInfo? Merge(TelegramMessageInfo? previous, TelegramMessageInfo? incoming)
    {
        if (incoming is null) return previous;
        if (previous is null) return incoming;
        var senderSnapshot = incoming.IsChannelPost.HasValue || incoming.SenderType is not null;
        var sameSender = !senderSnapshot || incoming.SenderType == previous.SenderType && incoming.SenderId == previous.SenderId;
        return incoming with
        {
            IsChannelPost = incoming.IsChannelPost ?? previous.IsChannelPost,
            SenderType = senderSnapshot ? incoming.SenderType : previous.SenderType,
            SenderId = senderSnapshot ? incoming.SenderId : previous.SenderId,
            SenderName = incoming.SenderResolved ? incoming.SenderName : incoming.SenderName ?? (sameSender ? previous.SenderName : null),
            SenderResolved = incoming.SenderResolved || sameSender && previous.SenderResolved,
            AuthorSignature = incoming.IsChannelPost.HasValue ? incoming.AuthorSignature : incoming.AuthorSignature ?? previous.AuthorSignature,
            Link = incoming.LinkResolved ? incoming.Link : incoming.Link ?? previous.Link,
            LinkResolved = incoming.LinkResolved || previous.LinkResolved
        };
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
