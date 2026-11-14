using Android.App;
using Android.Content;
using Bitmap = Android.Graphics.Bitmap;
using Color = Android.Graphics.Color;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using ChannelWave.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ChannelWave.Host.Maui;

// Opt-in Debug test activity; excluded from ordinary app builds and all Release builds.
[Activity(Name = "dev.channelwave.app.PlaybackTests", Exported = true, Theme = "@style/Maui.SplashTheme")]
public sealed class PlaybackDeviceTests : Activity
{
    public PlaybackDeviceTests() { }
    public PlaybackDeviceTests(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }
    private string resultPath = "";
    private string ResultPath => resultPath;
    protected override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        resultPath = Path.Combine(CacheDir!.AbsolutePath, "channelwave-playback-test-result.txt");
        File.WriteAllText(ResultPath, "Playback device test runner started\n");
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        PlaybackCoordinator? playback = null;
        var passed = false;
        var fixture = Path.Combine(Path.GetDirectoryName(ResultPath)!, "channelwave-playback-test.mp3");
        try
        {
            Report("Starting main activity");
            var context = ApplicationContext!;
            var intent = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var activity = await StartMainAsync(intent);
            Report("Main activity is ready");
            await Until(() => Task.FromResult(activity.HasWindowFocus), "Activity did not reach the foreground");
            var services = IPlatformApplication.Current!.Services;
            playback = services.GetRequiredService<PlaybackCoordinator>();
            var engine = services.GetRequiredService<AndroidAudioEngine>();
            Report("Playback services resolved");
            await engine.SelectOutputAsync(AudioOutput.Default.Id, 0, false, CancellationToken.None);
            Require((await engine.GetOutputsAsync(CancellationToken.None)).Active == AudioOutput.Default,
                "System default could not be selected before loading audio");
            await using (var input = await FileSystem.OpenAppPackageFileAsync("channelwave-playback-test.mp3"))
            await using (var output = File.Create(fixture)) await input.CopyToAsync(output);
            Track Song(int id) => new(Guid.NewGuid(), new(0, -1, id, "Original test audio", false),
                new("synthetic", "synthetic-" + id, id, "test.mp3", "audio/mpeg", new FileInfo(fixture).Length),
                "Playback smoke test " + id, "ChannelWave tests", "Playback smoke test " + id, "ChannelWave tests", 30, DateTimeOffset.UnixEpoch);
            var first = Song(1); var second = Song(2);
            await playback.SelectAsync(first, [first, second], (_, _) => Task.FromResult(new PreparedMedia(fixture, false, new FileInfo(fixture).Length)));
            using var controller = new MediaController(context, engine.SessionToken);
            var notifications = (NotificationManager)context.GetSystemService(Android.Content.Context.NotificationService)!;
            await Until(async () => (await playback.StateAsync()) is { Paused: false, Position: > 0 }, "Audio did not start");
            await Until(async () => await ReadUi(activity, "document.querySelector('.music-player-info strong')?.textContent") == first.Title, "Initial player title is missing");
            Require(notifications.GetActiveNotifications()!.Any(n => n.Id == AndroidPlaybackService.NotificationId), "Media notification is missing");
            Require(controller.Metadata?.GetString(Android.Media.MediaMetadata.MetadataKeyTitle) == first.Title, "System track metadata is missing");
            Report("Playback and media notification are active");

            var outputs = await playback.GetOutputsAsync();
            if (outputs.Devices.FirstOrDefault(device => device.Bluetooth) is { } bluetooth &&
                outputs.Devices.FirstOrDefault(device => device.Name == "Phone speaker") is { } speaker)
            {
                foreach (var output in new[] { speaker, bluetooth })
                {
                    var position = (await playback.StateAsync())!.Position;
                    await playback.SelectOutputAsync(output.Id);
                    await Until(async () => (await playback.GetOutputsAsync()).Active.Id == output.Id &&
                        (await playback.StateAsync()) is { Paused: false } state && state.Position > position,
                        "Direct output switch did not keep the track playing on the requested device");
                }
                await playback.SelectOutputAsync(AudioOutput.Default.Id);
                Report("Direct speaker and Bluetooth switching preserved playback");
            }

            var beforeCover = (await playback.StateAsync())!.Position;
            first = first with { Artwork = new(Metadata.MediaKey(first), ArtworkKind.Embedded, Cover(Color.Red)) };
            await playback.UpdateTrackMetadataAsync(first);
            await Until(() => Task.FromResult(HasCover(controller, notifications, Color.Red)), "Late cover did not reach the media session and notification");
            Require((await playback.StateAsync()) is { Paused: false } covered && covered.Position >= beforeCover, "Cover update restarted or paused audio");
            Require(controller.Metadata?.GetBitmap(Android.Media.MediaMetadata.MetadataKeyArt) is not null, "System display artwork is missing");
            await playback.UpdateTrackMetadataAsync(first with { Origin = first.Origin with { AccountId = 42 }, Artwork = new(Metadata.MediaKey(first), ArtworkKind.Embedded, Cover(Color.Blue)) });
            Require(HasCover(controller, notifications, Color.Red), "Foreign-account cover replaced current artwork");
            Report("Late cover updates system artwork without changing playback or account ownership");

            var before = (await playback.StateAsync())!.Position;
            await MainThread.InvokeOnMainThreadAsync(() => context.StartActivity(new Intent(Intent.ActionMain).AddCategory(Intent.CategoryHome)!.SetFlags(ActivityFlags.NewTask)));
            await Task.Delay(1200);
            Require((await playback.StateAsync()) is { Paused: false } background && background.Position > before, "Background audio stopped");
            await MainThread.InvokeOnMainThreadAsync(activity.Finish);
            await Task.Delay(400);
            Report("Waiting for foreground user launch after activity destruction");
            activity = await StartMainAsync(intent);
            await Until(async () => await ReadUi(activity, "document.querySelector('.music-player-info strong')?.textContent") == first.Title, "Track did not return after activity destruction");
            Require(playback.CurrentTrack?.Id == first.Id && (await playback.StateAsync())!.Position > before, "Reopening reset playback");
            Require(HasCover(controller, notifications, Color.Red), "Recreating the activity lost system artwork");
            Report("Background playback and recreated player UI retain the same song");

            using var focus = new TestAudioFocus(context);
            await playback.SetVolumeAsync(0.4);
            await focus.RequestAsync(AudioFocus.GainTransient);
            await Until(async () => (await playback.StateAsync()) is { Paused: true }, "Temporary audio focus loss did not pause music");
            var interrupted = (await playback.StateAsync())!;
            Require(!playback.IsPlaying && playback.CurrentTrack?.Id == first.Id, "Interruption lost the selected song or playing state");
            Require(controller.PlaybackState?.State == PlaybackStateCode.Paused, "System media session did not reflect interruption");

            await focus.ReleaseAsync();
            await Until(async () => (await playback.StateAsync()) is { Paused: false } resumed && resumed.Position > interrupted.Position,
                "Music did not automatically resume after temporary audio focus returned");
            Require(playback.CurrentTrack?.Id == first.Id && (await playback.StateAsync())!.Volume == 0.4, "Resume changed the song or volume");
            Require(controller.PlaybackState?.State == PlaybackStateCode.Playing, "System media session did not reflect automatic resume");
            Report("Exclusive transient audio interruptions resume the same song, position and volume");

            var beforeNotification = (await playback.StateAsync())!;
            await focus.RequestAsync(AudioFocus.GainTransientMayDuck);
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(100);
                Require((await playback.StateAsync()) is { Paused: false, Volume: 0.4 }, "Notification paused music or changed the user's volume");
                Require(playback.IsPlaying && playback.CurrentTrack?.Id == first.Id, "Notification changed the playing song");
                Require(controller.PlaybackState?.State == PlaybackStateCode.Playing, "Notification changed the system media session to paused");
            }
            var duringNotification = (await playback.StateAsync())!;
            Require(duringNotification.Position > beforeNotification.Position, "Music did not advance during the notification");
            await playback.SetVolumeAsync(0.6);
            await focus.ReleaseAsync();
            await Until(async () => (await playback.StateAsync()) is { Paused: false, Volume: 0.6 } restored && restored.Position > duringNotification.Position,
                "Notification end did not preserve playback and the latest user volume");
            Require(playback.CurrentTrack?.Id == first.Id, "Notification end changed the song");
            Report("Duckable notifications keep the song advancing and preserve the user's latest volume after focus returns");

            await focus.RequestAsync(AudioFocus.GainTransientMayDuck);
            await Task.Delay(400);
            Require((await playback.StateAsync()) is { Paused: false }, "Notification paused music before manual Pause");
            await playback.PauseAsync();
            await focus.ReleaseAsync();
            await Task.Delay(400);
            Require((await playback.StateAsync()) is { Paused: true }, "Manual pause was overridden when notification ended");

            await focus.RequestAsync(AudioFocus.GainTransientMayDuck);
            await focus.ReleaseAsync();
            await Task.Delay(400);
            Require((await playback.StateAsync()) is { Paused: true }, "Notification resumed music that was already paused");
            await playback.ResumeAsync();

            await focus.RequestAsync(AudioFocus.Gain);
            await Until(async () => (await playback.StateAsync()) is { Paused: true }, "Permanent focus loss did not pause music");
            await focus.ReleaseAsync();
            await Task.Delay(400);
            Require((await playback.StateAsync()) is { Paused: true }, "Music resumed after permanent focus loss");
            await playback.ResumeAsync();
            await playback.SetVolumeAsync(1);
            Report("Manual pause, already-paused playback and permanent focus loss suppress automatic resume");

            controller.GetTransportControls()!.Pause();
            await Until(async () => (await playback.StateAsync()) is { Paused: true }, "System Pause failed");
            await Until(async () => await ReadUi(activity, "document.querySelector('.music-player-toggle')?.getAttribute('aria-label')") == "Resume playback", "UI did not reflect system Pause");
            controller.GetTransportControls()!.SeekTo(5000);
            await Until(async () => (await playback.StateAsync()) is { Position: >= 4.5 and <= 6 }, "System seeking failed");
            controller.GetTransportControls()!.Play();
            await Until(async () => (await playback.StateAsync()) is { Paused: false }, "System Play failed");
            controller.GetTransportControls()!.SkipToNext();
            await Until(async () => playback.CurrentTrack?.Id == second.Id && (await playback.StateAsync()) is { Paused: false }, "System Next failed");
            await Until(async () => await ReadUi(activity, "document.querySelector('.music-player-info strong')?.textContent") == second.Title, "UI did not reflect system Next");
            Require(controller.Metadata?.GetBitmap(Android.Media.MediaMetadata.MetadataKeyAlbumArt) is null && !HasNotificationCover(notifications), "Next song retained the previous cover");
            second = second with { Artwork = new(Metadata.MediaKey(second), ArtworkKind.Embedded, [1, 2, 3]) };
            await playback.UpdateTrackMetadataAsync(second);
            await Task.Delay(200);
            Require((await playback.StateAsync()) is { Paused: false } && !HasNotificationCover(notifications), "Invalid artwork disrupted playback or retained a cover");
            second = second with { Artwork = new(Metadata.MediaKey(second), ArtworkKind.ChannelPhoto, Cover(Color.Blue)) };
            await playback.UpdateTrackMetadataAsync(second);
            await Until(() => Task.FromResult(HasCover(controller, notifications, Color.Blue)), "Source-photo fallback did not reach system artwork");
            var protectedTrack = second with { Origin = second.Origin with { Protected = true } };
            await playback.UpdateTrackMetadataAsync(protectedTrack);
            await Until(() => Task.FromResult(controller.Metadata?.GetBitmap(Android.Media.MediaMetadata.MetadataKeyAlbumArt) is null && !HasNotificationCover(notifications)),
                "Protected song retained artwork");
            await playback.UpdateTrackMetadataAsync(second);
            await Until(() => Task.FromResult(HasCover(controller, notifications, Color.Blue)), "System artwork did not recover");
            Report("Track changes clear stale covers; invalid/protected covers fall back and valid source photos recover");
            Report("System Pause, Play, Seek and Next update the actual player and UI");

            await focus.RequestAsync(AudioFocus.GainTransientMayDuck);
            await Task.Delay(400);
            Require((await playback.StateAsync()) is { Paused: false }, "Notification paused music before Stop");
            controller.GetTransportControls()!.Stop();
            await Until(() => Task.FromResult(playback.CurrentTrack is null && !notifications.GetActiveNotifications()!.Any(n => n.Id == AndroidPlaybackService.NotificationId)), "Stop did not remove the media notification");
            await focus.ReleaseAsync();
            await Task.Delay(400);
            Require(playback.CurrentTrack is null && !playback.IsPlaying, "Music restarted after Stop during an interruption");
            Require(controller.Metadata is null, "Stop retained system metadata or artwork");
            Report("Stop clears the player and media notification");
            passed = true;
        }
        catch (Exception e)
        {
            Report("Android playback test failed: " + e);
        }
        finally
        {
            try { if (playback is not null) await playback.StopAsync(); if (File.Exists(fixture)) File.Delete(fixture); }
            catch (Exception e) { passed = false; Report("Cleanup failed: " + e.Message); }
        }
        Report(passed ? "RESULT: PASS" : "RESULT: FAIL");
        await MainThread.InvokeOnMainThreadAsync(Finish);
    }

    private void Report(string text) => File.AppendAllText(ResultPath, text + "\n");

    private static byte[] Cover(Color color)
    {
        using var bitmap = Bitmap.CreateBitmap(1024, 1024, Bitmap.Config.Argb8888!)!;
        bitmap.EraseColor(color);
        using var output = new MemoryStream();
        Require(bitmap.Compress(Bitmap.CompressFormat.Png!, 100, output), "Test cover could not be encoded");
        return output.ToArray();
    }

    private static bool HasNotificationCover(NotificationManager notifications)
        => notifications.GetActiveNotifications()!.SingleOrDefault(n => n.Id == AndroidPlaybackService.NotificationId)?.Notification?.GetLargeIcon() is not null;

    private static bool HasCover(MediaController controller, NotificationManager notifications, Color color)
    {
        using var bitmap = controller.Metadata?.GetBitmap(Android.Media.MediaMetadata.MetadataKeyAlbumArt);
        if (bitmap is null || bitmap.Width > 512 || bitmap.Height > 512 || bitmap.GetPixel(0, 0) != color.ToArgb()) return false;
        var notification = notifications.GetActiveNotifications()!.SingleOrDefault(n => n.Id == AndroidPlaybackService.NotificationId)?.Notification;
        using var drawable = notification?.GetLargeIcon()?.LoadDrawable(Android.App.Application.Context) as Android.Graphics.Drawables.BitmapDrawable;
        return drawable?.Bitmap is { } icon && icon.GetPixel(0, 0) == color.ToArgb();
    }

    private sealed class TestAudioFocus(Context context) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly AudioManager manager = (AudioManager)context.GetSystemService(Context.AudioService)!;
        private AudioFocusRequestClass? request;

        public Task RequestAsync(AudioFocus gain) => MainThread.InvokeOnMainThreadAsync(() =>
        {
            using var attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!;
            request = new AudioFocusRequestClass.Builder(gain).SetAudioAttributes(attributes)!
                .SetOnAudioFocusChangeListener(this, new Handler(Looper.MainLooper!))!.Build()!;
            Require(manager.RequestAudioFocus(request) == AudioFocusRequest.Granted, "Test could not acquire competing audio focus");
        });

        public Task ReleaseAsync() => MainThread.InvokeOnMainThreadAsync(Release);

        private void Release()
        {
            if (request is null) return;
            manager.AbandonAudioFocusRequest(request);
            request.Dispose(); request = null;
        }

        public void OnAudioFocusChange(AudioFocus focusChange) { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) MainThread.InvokeOnMainThreadAsync(Release).GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
    }

    private async Task<Activity> StartMainAsync(Intent intent)
    {
        ApplicationContext!.StartActivity(intent);
        await Until(() => Task.FromResult(Platform.CurrentActivity is MainActivity main && main.HasWindowFocus), "Main activity did not open");
        return Platform.CurrentActivity!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<Task<bool>> condition, string error)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(error); await Task.Delay(100); }
    }
    private static Android.Webkit.WebView? FindWebView(Android.Views.View view)
    {
        if (view is Android.Webkit.WebView browser) return browser;
        if (view is ViewGroup group) for (var i = 0; i < group.ChildCount; i++) if (FindWebView(group.GetChildAt(i)!) is { } found) return found;
        return null;
    }
    private static async Task<string?> ReadUi(Activity activity, string expression)
    {
        using var callback = new JavascriptResult();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var browser = FindWebView(activity.Window!.DecorView);
            if (browser is null) callback.Value.TrySetResult("null");
            else browser.EvaluateJavascript(expression, callback);
        });
        return JsonSerializer.Deserialize<string>(await callback.Value.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    private sealed class JavascriptResult : Java.Lang.Object, IValueCallback
    {
        internal TaskCompletionSource<string> Value { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnReceiveValue(Java.Lang.Object? value) => Value.TrySetResult(value?.ToString() ?? "null");
    }
}
