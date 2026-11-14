using System.Text.Json;

namespace ChannelWave.Telegram;

public sealed record TelegramIdentity(long AccountId, string Name, string? Username)
{
    public static TelegramIdentity From(JsonElement user)
    {
        var name = $"{user.Text("first_name")} {user.Text("last_name")}".Trim();
        string? username = null;
        if (user.TryGetProperty("usernames", out var names) && names.ValueKind == JsonValueKind.Object
            && names.TryGetProperty("active_usernames", out var active) && active.GetArrayLength() > 0)
            username = active[0].GetString();
        return new(user.Id("id"), name.Length > 0 ? name : "Your account", username);
    }
}
