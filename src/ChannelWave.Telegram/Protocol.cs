using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChannelWave.Telegram;

public interface ITdTransport : IAsyncDisposable
{
    ChannelReader<JsonElement> Updates { get; }
    Task StartAsync(CancellationToken ct = default);
    Task<JsonElement> SendAsync(JsonObject request, CancellationToken ct = default);
}

public sealed class TelegramException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public int? RetryAfter => System.Text.RegularExpressions.Regex.Match(Message, @"(?:FLOOD_WAIT_|retry after )(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, out var seconds) ? seconds : null;
}

public static class Td
{
    public const string Revision = "c15d3f5a5de6e3ba5839822c451152e5e18bb700";
    public static JsonObject Query(string type, params (string Key, object? Value)[] fields)
    {
        var node = new JsonObject { ["@type"] = type };
        foreach (var (key, value) in fields) node[key] = JsonSerializer.SerializeToNode(value);
        return node;
    }
    public static string Type(this JsonElement value) => value.TryGetProperty("@type", out var type) ? type.GetString() ?? "" : "";
    public static string Text(this JsonElement value, string key) => value.TryGetProperty(key, out var result) ? result.GetString() ?? "" : "";
    public static long Id(this JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        var id = item.ValueKind == JsonValueKind.String ? long.Parse(item.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : item.GetInt64();
        if (id is < -9007199254740991 or > 9007199254740991) throw new InvalidDataException("TDLib int53 is outside JavaScript's exact integer range.");
        return id;
    }
    public static bool Flag(this JsonElement value, string key) => value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.True;
    public static void ThrowIfError(JsonElement value)
    {
        if (value.Type() == "error") throw new TelegramException(value.GetProperty("code").GetInt32(), value.Text("message"));
    }
    public static string SafeError(Exception exception) => exception switch
    {
        TelegramException e => $"Telegram {e.Code}: {e.Message}",
        OperationCanceledException => "The operation was canceled or timed out. Retry when connected.",
        _ => exception.Message.Split('\n')[0].Trim()
    };
}

public sealed record TelegramHttpProxy(string Server, int Port);

public sealed record TelegramConfiguration(int ApiId, string ApiHash, string DatabaseDirectory, string FilesDirectory, string DatabaseEncryptionKey)
{
    public TelegramHttpProxy? HttpProxy { get; init; }

    public void Validate()
    {
        if (ApiId <= 0 || ApiHash.Length != 32 || !ApiHash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The developer must configure a valid Telegram API ID and hash before connecting.");
        if (HttpProxy is { } proxy && (string.IsNullOrWhiteSpace(proxy.Server) || proxy.Port is < 1 or > 65535))
            throw new InvalidOperationException("The Telegram HTTP proxy must have a server and a port between 1 and 65535.");
    }
}

public sealed record AuthorizationPrompt(string State, string Label, string? Delivery = null, int ResendAfter = 0, string? Link = null)
{
    public static AuthorizationPrompt From(JsonElement state) => state.Type() switch
    {
        "authorizationStateWaitTdlibParameters" => new("parameters", "Initializing Telegram"),
        "authorizationStateWaitPhoneNumber" => new("phone", "Phone number with country code"),
        "authorizationStateWaitCode" => new("code", "Verification code", state.GetProperty("code_info").GetProperty("type").Type(), state.GetProperty("code_info").GetProperty("timeout").GetInt32()),
        "authorizationStateWaitPassword" => new("password", "Two-step verification password"),
        "authorizationStateWaitEmailAddress" => new("email", "Email address requested by Telegram"),
        "authorizationStateWaitEmailCode" => new("emailCode", "Email verification code", state.GetProperty("code_info").Text("email_address_pattern")),
        "authorizationStateWaitOtherDeviceConfirmation" => new("qr", "Approve this session in Telegram", Link: state.Text("link")),
        "authorizationStateReady" => new("ready", "Connected"),
        "authorizationStateLoggingOut" => new("loggingOut", "Signing out"),
        "authorizationStateClosing" => new("closing", "Closing Telegram"),
        "authorizationStateClosed" => new("closed", "Session closed. Reopen to connect."),
        "authorizationStateWaitRegistration" => new("unsupported", "Create your account in an official Telegram client, then return."),
        "authorizationStateWaitPremiumPurchase" => new("unsupported", "Telegram requires a purchase step. Complete sign-in in an official client; this step is not supported here."),
        _ => new("unsupported", $"This Telegram authorization state needs an adapter update: {state.Type()}")
    };
}
