using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using ChannelWave.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using ChannelWave.Telegram;
using ChannelWave.UI;
using ChannelWave.Storage.Sqlite;

namespace ChannelWave.Host.Maui;

// Synthetic, opt-in emulator checks. Never included in Release builds.
[Activity(Name = "dev.channelwave.app.CompletionTests", Exported = true, Theme = "@style/Maui.SplashTheme")]
public sealed class PlaybackCompletionDeviceTests : Activity
{
    public PlaybackCompletionDeviceTests() { }
    public PlaybackCompletionDeviceTests(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }
    private readonly object reportGate = new();
    private string reportPath = "";
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        reportPath = Path.Combine(CacheDir!.AbsolutePath, "completion-result.txt");
        File.WriteAllText(reportPath, "Starting completion test\n");
        var scenario = Intent?.GetStringExtra("scenario") ?? "all";
        _ = Task.Run(() => RunAsync(scenario));
    }

    private void Report(string message)
    {
        lock (reportGate) File.AppendAllText(reportPath, $"{DateTime.UtcNow:O} {message}\n");
    }

    private async Task RunAsync(string scenario)
    {
        PlaybackCoordinator? playback = null;
        LibraryPerformanceFixture.FixtureTransport? transport = null;
        Action? changed = null;
        try
        {
            var launch = new Intent(ApplicationContext!, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            await MainThread.InvokeOnMainThreadAsync(() => ApplicationContext!.StartActivity(launch));
            await Task.Delay(7000);
            var services = IPlatformApplication.Current!.Services;
            playback = services.GetRequiredService<PlaybackCoordinator>();
            var engine = services.GetRequiredService<AndroidAudioEngine>();
            await playback.StopAsync();
            var path = Path.Combine(CacheDir!.AbsolutePath, scenario == "mp3" ? "completion-tone.mp3" : "completion-tone.wav");
            if (scenario == "mp3")
            {
                await using var input = await FileSystem.OpenAppPackageFileAsync("channelwave-playback-test.mp3");
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            else WriteTone(path, scenario == "tiny" ? 0.04 : 1);
            var tracks = Enumerable.Range(1, 3).Select(i => new Track(Guid.NewGuid(), new(700, -5, i, "Completion fixture", false),
                new("completion", "completion-" + i, i, "tone.wav", "audio/wav", new FileInfo(path).Length),
                "Completion " + i, "Synthetic", "Completion " + i, "Synthetic", 1, DateTimeOffset.UnixEpoch)).ToArray();
            var starts = new List<Guid>();
            var library = services.GetRequiredService<ILibraryRepository>();
            var mediaResolver = services.GetRequiredService<PlaybackMediaResolver>();
            var realMedia = scenario is "offline" or "download";
            if (realMedia)
            {
                await library.SavePageAsync(new(700, -5, "Completion fixture", HistoryComplete: true), tracks, CancellationToken.None);
                if (scenario == "offline")
                {
                    var offline = services.GetRequiredService<NativeOfflineStore>();
                    foreach (var track in tracks) await offline.SaveAsync(track, path, CancellationToken.None);
                }
                else
                {
                    transport = (LibraryPerformanceFixture.FixtureTransport)services.GetRequiredService<ITdTransport>();
                    JsonElement FileValue(int id, bool completed) => JsonSerializer.SerializeToElement(new
                    {
                        id, size = new FileInfo(path).Length, expected_size = new FileInfo(path).Length,
                        local = new { path = completed ? path : "", is_downloading_completed = completed, downloaded_size = completed ? new FileInfo(path).Length : 0 },
                        remote = new { id = "completion", unique_id = "completion-" + id }
                    });
                    transport.MediaRequest = async (request, ct) =>
                    {
                        var type = request["@type"]?.GetValue<string>();
                        if (type == "getMessage" && request["chat_id"]?.GetValue<long>() == -5)
                            return JsonSerializer.SerializeToElement(new
                            {
                                can_be_saved = true,
                                content = new Dictionary<string, object>
                                {
                                    ["@type"] = "messageAudio",
                                    ["audio"] = new { audio = FileValue(checked((int)request["message_id"]!.GetValue<long>()), false) }
                                }
                            });
                        if (type == "downloadFile")
                        {
                            await Task.Delay(500, ct);
                            return FileValue(request["file_id"]!.GetValue<int>(), true);
                        }
                        return null;
                    };
                }
            }
            var adjusted = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = Path.Combine(CacheDir!.AbsolutePath, "completion-go");
            if (File.Exists(go)) File.Delete(go);
            Report("READY: SCREEN");
            while (!File.Exists(go)) await Task.Delay(100);
            async Task<PreparedMedia> Prepare(Track track, CancellationToken ct)
            {
                if (scenario == "slow") await Task.Delay(900, ct);
                lock (starts) starts.Add(track.Id);
                Report($"PREPARE {Array.FindIndex(tracks, t => t.Id == track.Id)} count={starts.Count}");
                return realMedia ? await mediaResolver.PrepareAsync(track, scenario == "offline", ct) : new(path, false, new FileInfo(path).Length);
            }
            changed = () =>
            {
                Report($"CHANGED selected={Array.FindIndex(tracks, t => t.Id == playback.SelectedTrack)} playing={playback.IsPlaying} preparing={playback.IsPreparing} error={playback.Error}");
                if (playback.IsPlaying && !playback.IsPreparing) ready.TrySetResult();
                if (playback.IsPlaying && !playback.IsPreparing && adjusted != starts.Count)
                {
                    adjusted = starts.Count;
                    _ = AdjustAsync();
                }
                lock (starts)
                {
                    if (scenario == "off" ? starts.Count == 3 && !playback.IsPlaying && !playback.IsPreparing : starts.Count >= (scenario == "missing" ? 6 : 12) && playback.IsPlaying && !playback.IsPreparing)
                        done.TrySetResult();
                }
            };
            async Task AdjustAsync()
            {
                try
                {
                    if (scenario == "mp3")
                    {
                        var state = await engine.StateAsync();
                        await playback.SeekAsync(Math.Max(0, state!.Duration - 0.6));
                    }
                    if (scenario == "resume")
                    {
                        await Task.Delay(180);
                        await playback.PauseAsync();
                        await Task.Delay(150);
                        await playback.ResumeAsync();
                    }
                    if (scenario == "missing")
                    {
                        // Deliberately drop only the native event delivery. The real player
                        // continues to EOF; monitoring must recover the completion.
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            var native = (Android.Media.MediaPlayer)typeof(AndroidAudioEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                            var handler = (EventHandler?)typeof(AndroidAudioEngine).GetField("completionHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);
                            if (handler is not null) native.Completion -= handler;
                        });
                        Report("FAULT: native Completion notification suppressed");
                    }
                }
                catch (Exception e) { done.TrySetException(e); }
            }
            playback.Changed += changed;
            await playback.StartQueueAsync(700, tracks.Select(t => t.Id).ToArray(), tracks[0].Id,
                (id, ct) => realMedia ? mediaResolver.ResolveAsync(700, id, ct) : Task.FromResult(tracks.FirstOrDefault(t => t.Id == id)), Prepare).WaitAsync(TimeSpan.FromSeconds(15));
            if (scenario != "off")
            {
                playback.CycleRepeat(); // One
                if (scenario != "one") playback.CycleRepeat(); // All
                if (scenario == "shuffle") playback.SetShuffle(true);
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(scenario == "missing" ? 20 : 65));
            await playback.PauseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            lock (starts)
            {
                if (scenario == "one" && starts.Any(id => id != tracks[0].Id)) throw new InvalidOperationException("Repeat one changed track");
                if (scenario is "off" or "all" or "slow")
                    for (var i = 0; i < starts.Count; i++)
                        if (starts[i] != tracks[i % 3].Id) throw new InvalidOperationException("Queue order skipped a track");
            }
            Report($"RESULT: PASS starts={starts.Count}");
        }
        catch (Exception error)
        {
            Report(error.ToString());
            if (playback is not null)
            {
                foreach (var name in new[] { "generation", "loadedGeneration", "queueRequest", "completionArmed", "commands" })
                {
                    var value = typeof(PlaybackCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(playback);
                    Report($"DIAGNOSTIC {name}={(value is SemaphoreSlim gate ? gate.CurrentCount : value)}");
                }
                Report($"DIAGNOSTIC playing={playback.IsPlaying} preparing={playback.IsPreparing} error={playback.Error}");
            }
            Report("RESULT: FAIL");
        }
        finally
        {
            if (transport is not null) transport.MediaRequest = null;
            if (playback is not null)
            {
                if (changed is not null) playback.Changed -= changed;
                try { await playback.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception e) { Report("Cleanup: " + e.Message); }
            }
            await MainThread.InvokeOnMainThreadAsync(Finish);
        }
    }

    private static void WriteTone(string path, double seconds)
    {
        const int rate = 16000;
        var samples = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 500));
    }
}
