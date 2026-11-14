using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;

namespace ChannelWave.Host.Maui;

[Service(Name = "dev.channelwave.app.PlaybackService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AndroidPlaybackService : Service
{
    internal const string Play = "dev.channelwave.app.PLAY";
    internal const string Pause = "dev.channelwave.app.PAUSE";
    internal const string Stop = "dev.channelwave.app.STOP";
    internal const string Next = "dev.channelwave.app.NEXT";
    internal const string Previous = "dev.channelwave.app.PREVIOUS";
    internal const int NotificationId = 1001;
    private AndroidAudioEngine Engine => IPlatformApplication.Current!.Services.GetRequiredService<AndroidAudioEngine>();

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!Engine.HasMedia) { StopSelf(); return StartCommandResult.NotSticky; }
        Engine.AttachService(this);
        switch (intent?.Action)
        {
            case Play: Engine.RunControl(p => p.ResumeAsync()); break;
            case Pause: Engine.RunControl(p => p.PauseAsync()); break;
            case Stop: Engine.RunControl(p => p.StopAsync()); break;
            case Next: Engine.RunControl(p => p.SkipAsync(1)); break;
            case Previous: Engine.RunControl(p => p.SkipAsync(-1)); break;
        }
        // Process death must never restart or autoplay an old account's music.
        return StartCommandResult.NotSticky;
    }

    internal void Publish(Notification notification) => StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);

    internal void Finish()
    {
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    public override void OnDestroy()
    {
        Engine.DetachService(this);
        base.OnDestroy();
    }
}
