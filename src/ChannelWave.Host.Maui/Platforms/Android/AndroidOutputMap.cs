using System.Globalization;
using ChannelWave.Core;

namespace ChannelWave.Host.Maui;

internal enum AndroidOutputKind
{
    Other, Speaker, SafeSpeaker, Wired, Usb, Bluetooth, BluetoothSpeaker, HearingAid
}

internal sealed record AndroidEndpoint(int Id, AndroidOutputKind Kind, string? Name = null, string? Address = null);

internal sealed class AndroidOutputMap(IEnumerable<AndroidEndpoint> endpoints)
{
    private const string SpeakerId = "android:speaker";
    private readonly Route[] routes = endpoints
        .Where(device => device.Kind != AndroidOutputKind.Other)
        .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => MakeRoute(group.Key, group.OrderBy(device => device.Kind).ThenBy(device => device.Id).ToArray()))
        .ToArray();

    public AudioOutputs Snapshot(bool playing, int? actualId, string? preferredId) => new(
        (playing
            ? routes.FirstOrDefault(route => route.Endpoints.Any(device => device.Id == actualId))
            : routes.FirstOrDefault(route => route.Output.Id == preferredId))?.Output ?? AudioOutput.Default,
        [AudioOutput.Default, ..routes.Select(route => route.Output)], GroupAndroidOutputs: true);

    public int? Resolve(string? id) => routes.FirstOrDefault(route => route.Output.Id == id)?.Endpoints[0].Id;

    private static bool IsBluetooth(AndroidOutputKind kind) =>
        kind is AndroidOutputKind.Bluetooth or AndroidOutputKind.BluetoothSpeaker or AndroidOutputKind.HearingAid;

    private static string GroupKey(AndroidEndpoint device)
    {
        if (device.Kind is AndroidOutputKind.Speaker or AndroidOutputKind.SafeSpeaker) return SpeakerId;
        if (IsBluetooth(device.Kind) && device.Address?.Trim() is { Length: > 0 } address &&
            address is not ("00:00:00:00:00:00" or "02:00:00:00:00:00"))
            return "bt:" + address.ToUpperInvariant();
        return device.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static Route MakeRoute(string key, AndroidEndpoint[] endpoints)
    {
        if (key == SpeakerId) return new(new(key, "Phone speaker"), endpoints);
        var first = endpoints[0];
        var name = first.Kind switch
        {
            AndroidOutputKind.Wired => "Wired headphones",
            AndroidOutputKind.Usb => "USB audio",
            _ => endpoints.Select(device => device.Name?.Trim())
                .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? (first.Kind switch
                {
                    AndroidOutputKind.BluetoothSpeaker => "Bluetooth speaker",
                    AndroidOutputKind.HearingAid => "Bluetooth hearing aid",
                    _ => "Bluetooth headphones"
                })
        };
        // Keep hardware addresses inside the adapter. The UI only receives Android's endpoint ID.
        return new(new(first.Id.ToString(CultureInfo.InvariantCulture), name, Bluetooth: IsBluetooth(first.Kind)), endpoints);
    }

    private sealed record Route(AudioOutput Output, AndroidEndpoint[] Endpoints);
}
