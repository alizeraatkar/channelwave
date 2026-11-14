using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace ChannelWave.Host.Maui;

internal static class LibraryPerformanceFixture
{
    private const int Account = 700;
    private const int TrackCount = 10_000;
    private const string LogTag = "ChannelWavePerf";
    private static readonly ActivityListener Listener = new()
    {
        ShouldListenTo = source => source.Name is "ChannelWave.Sqlite.Search" or "ChannelWave.UI.Library",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStarted = activity => activity.SetTag("startThread", Environment.CurrentManagedThreadId),
        ActivityStopped = activity => Android.Util.Log.Info(LogTag, JsonSerializer.Serialize(new
        {
            source = activity.Source.Name,
            operation = activity.DisplayName,
            startedUtc = activity.StartTimeUtc,
            durationMs = activity.Duration.TotalMilliseconds,
            thread = activity.GetTagItem("startThread")
        }))
    };

    public static Window CreateWindow()
    {
        var status = new Label { Text = "Preparing synthetic music…" };
        var loading = new ContentPage { Content = status };
        var window = new Window(loading) { Title = "ChannelWave performance fixture" };
        loading.Loaded += Prepare;
        return window;

        async void Prepare(object? sender, EventArgs args)
        {
            loading.Loaded -= Prepare;
            try
            {
                var services = IPlatformApplication.Current!.Services;
                var withMetadata = Platform.CurrentActivity?.Intent?.GetBooleanExtra("channelwave.performance.metadata", false) == true;
                ActivitySource.AddActivityListener(Listener);
                await Task.Run(() => SeedAsync(services, withMetadata));
                Android.Util.Log.Info(LogTag, $"Fixture ready. UI thread {Environment.CurrentManagedThreadId}. Metadata {withMetadata}.");
                window.Page = new MainPage();
            }
            catch (Exception error)
            {
                Android.Util.Log.Error(LogTag, error.ToString());
                status.Text = error.Message;
            }
        }
    }

    private static async Task SeedAsync(IServiceProvider services, bool withMetadata)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var library = services.GetRequiredService<ILibraryRepository>();
        await library.InitializeAsync(ct);
        await library.ClearAccountAsync(Account, ct);
        var source = new Source(Account, -100123, "Performance fixture", HistoryComplete: true);
        var publication = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tracks = Enumerable.Range(1, TrackCount).Select(index =>
        {
            var title = $"Performance song {index:D5}";
            var artist = $"Fixture artist {index % 100:D2}";
            return new Track(new Guid(index, 0, 0, new byte[8]), new(Account, source.ChatId, 100_000 + index, source.Name, false),
                new($"remote-{index}", $"media-{index}", index, $"track-{index}.flac", "audio/flac", 100),
                title, artist, title, artist, 180, publication);
        });
        foreach (var batch in tracks.Chunk(500)) await library.SavePageAsync(source, batch, ct);
        await library.SaveStateAsync(Account, "automatic-metadata-v1", withMetadata ? "true" : "false", ct);
        await library.SaveStateAsync(Account, "library-browse-v1", """{"Mode":"tracks","Order":6,"Criteria":{}}""", ct);

        var session = services.GetRequiredService<TelegramSession>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (session.AccountId == Account) ready.TrySetResult(); }
        session.Changed += Changed;
        try
        {
            await session.ConnectAsync(new(1, new('0', 32), "fixture", "fixture", ""), ct);
            await ready.Task.WaitAsync(ct);
        }
        finally { session.Changed -= Changed; }
    }

    public sealed class FixtureTransport : ITdTransport
    {
        internal Func<JsonObject, CancellationToken, Task<JsonElement?>>? MediaRequest { get; set; }
        private readonly Channel<JsonElement> updates = Channel.CreateUnbounded<JsonElement>();
        public ChannelReader<JsonElement> Updates => updates.Reader;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<JsonElement> SendAsync(JsonObject request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (MediaRequest is { } mediaRequest && await mediaRequest(request, ct) is { } media) return media;
            var type = request["@type"]!.GetValue<string>();
            if (type is "getAuthorizationState" or "close")
            {
                var state = Td.Query(type == "close" ? "authorizationStateClosed" : "authorizationStateReady");
                updates.Writer.TryWrite(JsonSerializer.SerializeToElement(Td.Query("updateAuthorizationState", ("authorization_state", state))));
            }
            var result = type switch
            {
                "getOption" => Td.Query("optionValueString", ("value", request["name"]!.GetValue<string>() == "version" ? "1.8.68" : Td.Revision)),
                "getMe" => Td.Query("user", ("id", Account), ("first_name", "Performance fixture")),
                "getChat" => Td.Query("chat", ("id", -100123), ("title", "Performance fixture")),
                "getChats" => Td.Query("chats", ("chat_ids", Array.Empty<long>())),
                _ => Td.Query("ok")
            };
            return JsonSerializer.SerializeToElement(result);
        }

        public ValueTask DisposeAsync()
        {
            updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class FixtureMetadataSource : IMetadataSource
    {
        public async Task<IReadOnlyMediaFile> OpenAsync(Track track, CancellationToken ct)
        {
            await Task.Delay(125, ct);
            // A FLAC Vorbis comment block is enough for the real bounded tag reader.
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var comment = Encoding.UTF8.GetBytes($"TITLE={track.OriginalTitle} enriched");
            writer.Write("fLaC"u8);
            writer.Write(new byte[] { 0x84, 0, 0, checked((byte)(12 + comment.Length)) });
            writer.Write(0); // Empty vendor string.
            writer.Write(1); // One comment.
            writer.Write(comment.Length);
            writer.Write(comment);
            return new FixtureFile(stream.ToArray());
        }

        public Task ObserveAsync(Track track, byte[]? artwork, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixtureFile(byte[] bytes) : IReadOnlyMediaFile
    {
        public long AccountId => Account;
        public long Length => bytes.Length;
        public Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(bytes.AsSpan(checked((int)offset), count).ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
