#if WINDOWS
using ChannelWave.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ChannelWave.Host.Maui;

public sealed partial class NativeAudio : IAudioRouting
{
    private static async Task<DeviceInformationCollection> AudioDevicesAsync(CancellationToken ct) =>
        await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(ct);

    public async Task<AudioOutputs> GetOutputsAsync(CancellationToken ct)
    {
        var devices = await AudioDevicesAsync(ct);
        var outputs = devices.Select(device => new AudioOutput(device.Id, device.Name)).ToArray();
        var active = outputs.FirstOrDefault(device => device.Id == player.AudioDevice?.Id);
        return new(active ?? AudioOutput.Default, [AudioOutput.Default, ..outputs]);
    }

    public async Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        if (id == AudioOutput.Default.Id)
        {
            player.AudioDevice = null;
            return;
        }
        var devices = await AudioDevicesAsync(ct);
        player.AudioDevice = devices.FirstOrDefault(device => device.Id == id)
            ?? throw new IOException("The audio output is unavailable. Refresh the list and try again.");
    }
}
#endif
