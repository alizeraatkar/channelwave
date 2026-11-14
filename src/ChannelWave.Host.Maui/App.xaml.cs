namespace ChannelWave.Host.Maui;

using ChannelWave.Telegram;
using Microsoft.Extensions.DependencyInjection;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
#if CHANNELWAVE_LIBRARY_PERFORMANCE
        return LibraryPerformanceFixture.CreateWindow();
#else
#if WINDOWS
        // Package diagnostics must never initialize account UI or the user's
        // configuration/secure-storage cleanup paths.
        if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
            return new Window(new ContentPage()) { Title = "ChannelWave package verification" };
#endif
        var window = new Window(new MainPage()) { Title = "ChannelWave.Host.Maui" };
        window.Stopped += async (_, _) => await UpdateActivityAsync(false);
        window.Resumed += async (_, _) => await UpdateActivityAsync(true);
        return window;
#endif
	}

    private static async Task UpdateActivityAsync(bool active)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        var sync = services.GetRequiredService<ChannelSync>();
        try
        {
            if (!active)
            {
                var playback = services.GetRequiredService<ChannelWave.Core.PlaybackCoordinator>();
                if (!services.GetRequiredService<ChannelWave.UI.IRuntimeHost>().Capabilities.BackgroundAudio) await playback.PauseAsync();
                await playback.CheckpointAsync();
            }
            if (active && services.GetRequiredService<TelegramSession>().AccountId is not null) await sync.RestoreAsync();
            else if (!active) await sync.PauseAsync(preserveResume: true);
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("ChannelWave sync lifecycle: " + e.GetType().Name); }
    }
}
