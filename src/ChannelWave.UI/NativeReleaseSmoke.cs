using System.Text.Json;
using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace ChannelWave.UI;

/// <summary>Explicit, credential-free native release diagnostic. Never signs in.</summary>
public static class NativeReleaseSmoke
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var report = Environment.GetEnvironmentVariable("CHANNELWAVE_SMOKE_REPORT") ?? throw new InvalidOperationException("Smoke report path is required.");
        var stage = "native-initialization";
        try
        {
            // WinRT StorageFile requires a normalized Windows path, just as
            // MauiRuntimeHost supplies for real downloaded media.
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("CHANNELWAVE_SMOKE_AUDIO") ?? throw new InvalidOperationException("Synthetic audio path is required."));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var session = services.GetRequiredService<TelegramSession>();
            var engine = await session.ProbeAsync(timeout.Token);
            stage = "audio-load";
            var audio = services.GetRequiredService<IAudioEngine>();
            await audio.LoadAsync(new(path, false, new FileInfo(path).Length), timeout.Token);
            stage = "audio-playback";
            await audio.SetVolumeAsync(0);
            await audio.PlayAsync(timeout.Token);
            AudioState? state;
            do
            {
                await Task.Delay(100, timeout.Token);
                state = await audio.StateAsync(timeout.Token);
                if (state?.Error is { } error) throw new IOException(error);
            } while (state is null || state.Duration <= 0 || state.Position <= 0);
            await audio.PauseAsync();
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { result = "pass", engine, duration = state.Duration, position = state.Position }));
            await audio.DisposeAsync();
            return 0;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { result = "fail", stage, errorType = e.GetType().Name, error = Td.SafeError(e) }));
            return 1;
        }
    }
}
