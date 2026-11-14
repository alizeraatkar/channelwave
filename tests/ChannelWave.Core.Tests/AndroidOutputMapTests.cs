using ChannelWave.Core;
using ChannelWave.Host.Maui;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class AndroidOutputMapTests
{
    [Fact]
    public void Phone_endpoints_use_one_selectable_speaker_and_hide_internal_routes()
    {
        var map = new AndroidOutputMap([
            new(1, AndroidOutputKind.Other, "Pixel 8"),
            new(2, AndroidOutputKind.SafeSpeaker, "Pixel 8"),
            new(3, AndroidOutputKind.Speaker, "Pixel 8"),
            new(4, AndroidOutputKind.Other, "Pixel 8")
        ]);

        var outputs = map.Snapshot(playing: true, actualId: 2, preferredId: null);

        Assert.Equal(["System default", "Phone speaker"], outputs.Devices.Select(device => device.Name));
        Assert.Equal(outputs.Devices[1], outputs.Active);
        Assert.Equal(3, map.Resolve(outputs.Active.Id));
        Assert.Null(map.Resolve("4"));
    }

    [Fact]
    public void Bluetooth_profiles_merge_by_address_and_keep_separate_same_name_devices()
    {
        var map = new AndroidOutputMap([
            new(1, AndroidOutputKind.Bluetooth, "Headphones", "AA:AA:AA:AA:AA:AA"),
            new(2, AndroidOutputKind.Bluetooth, "Headphones", "aa:aa:aa:aa:aa:aa"),
            new(3, AndroidOutputKind.Bluetooth, "Headphones", "BB:BB:BB:BB:BB:BB"),
            new(4, AndroidOutputKind.BluetoothSpeaker)
        ]);

        var outputs = map.Snapshot(playing: true, actualId: 2, preferredId: "3");

        Assert.Equal(["Headphones", "Headphones", "Bluetooth speaker"],
            outputs.Devices.Where(device => device.Bluetooth).Select(device => device.Name));
        Assert.Equal("1", outputs.Active.Id);
        Assert.Equal(3, map.Resolve("3"));
    }

    [Fact]
    public void Disconnected_output_cannot_be_selected_or_reported_as_active()
    {
        var connected = new AndroidOutputMap([new(1, AndroidOutputKind.Usb)]);
        var previous = connected.Snapshot(playing: false, actualId: null, preferredId: "1").Active;
        var disconnected = new AndroidOutputMap([new(2, AndroidOutputKind.Speaker)]);

        Assert.Equal("USB audio", previous.Name);
        Assert.Null(disconnected.Resolve(previous.Id));
        Assert.Equal(AudioOutput.Default,
            disconnected.Snapshot(playing: false, actualId: null, preferredId: previous.Id).Active);
        Assert.Equal("Phone speaker",
            disconnected.Snapshot(playing: true, actualId: 2, preferredId: previous.Id).Active.Name);
    }
}
