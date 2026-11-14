using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using ChannelWave.Telegram;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class MetadataScannerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Concurrent_requests_and_a_new_duplicate_reuse_the_saved_audio_result()
    {
        // Arrange
        await using var fixture = await ScanFixture.CreateAsync();
        var first = new Source(7, 1, "First");
        var second = new Source(7, 2, "Second");
        var track = Fixture.Track(first, 1);
        var duplicate = Fixture.Track(second, 1) with { Media = track.Media with { RuntimeId = 20 } };
        var protectedTrack = Fixture.Track(second, 2) with { Media = track.Media };
        protectedTrack = protectedTrack with { Origin = protectedTrack.Origin with { Protected = true } };
        await fixture.Library.SavePageAsync(first, [track], Token);
        await fixture.Library.SavePageAsync(second, [duplicate, protectedTrack], Token);
        await fixture.Library.SetLocalMetadataAsync(7, duplicate.Id, "My label", null, Token);
        var files = new Files { FailNotification = true };

        // Act
        await using (var scanner = new MetadataScanner(fixture.Session, fixture.Library, files))
            await Task.WhenAll(scanner.EnsureAsync(track, Token), scanner.EnsureAsync(duplicate, Token));
        var later = Fixture.Track(second, 3) with { Media = track.Media };
        await fixture.Library.RemoveSourceAsync(7, first.ChatId, Token);
        await fixture.Library.SavePageAsync(second, [later], Token);
        await using var reopened = new MetadataScanner(fixture.Session, fixture.Library, files);
        var reused = await reopened.EnsureAsync(later, Token);

        // Assert
        Assert.Equal(1, files.Opened);
        Assert.Equal(MetadataScanStatus.Completed, reused!.MetadataScan!.Status);
        Assert.Equal("My label", (await fixture.Library.TrackAsync(7, duplicate.Id, Token))!.Title);
        Assert.Null((await fixture.Library.TrackAsync(7, protectedTrack.Id, Token))!.MetadataScan);
        Assert.Empty(await fixture.Library.PendingMetadataAsync(7, DateTimeOffset.UtcNow, ct: Token));
    }

    [Fact]
    public async Task Explicit_inspection_saves_original_tags_for_the_automatic_worker()
    {
        // Arrange
        await using var fixture = await ScanFixture.CreateAsync();
        var source = new Source(7, 1, "Source");
        var track = Fixture.Track(source, 1);
        await fixture.Library.SavePageAsync(source, [track], Token);
        var files = new Files();
        await using var scanner = new MetadataScanner(fixture.Session, fixture.Library, files);

        // Act
        await scanner.InspectAsync(track, _ => Task.FromResult(new Id3Document(3, 0, 0, [], null,
            new() { Title = "Original MP3 title", Album = "Original album" }, [], [])), Token);
        var cached = await scanner.EnsureAsync(track, Token);

        // Assert
        Assert.Equal(0, files.Opened);
        Assert.Equal("Original MP3 title", cached!.Title);
        Assert.Equal("Original album", cached.EmbeddedMetadata!.Album);
    }

    [Fact]
    public async Task Worker_scans_beyond_visible_page_handles_new_music_and_persists_explicit_pause()
    {
        // Arrange
        await using var fixture = await ScanFixture.CreateAsync();
        var source = new Source(7, 1, "Source");
        var tracks = Enumerable.Range(1, 130).Select(i => Fixture.Track(source, i)).ToArray();
        await fixture.Library.SavePageAsync(source, tracks, Token);
        var files = new Files();
        await using (var scanner = new MetadataScanner(fixture.Session, fixture.Library, files, new(25, 0, 10)))
        {
            // Act
            await scanner.RestoreAsync(Token);
            await WaitAsync(() => scanner.Progress.Completed == 130);
            var added = Fixture.Track(source, 131);
            await fixture.Library.SavePageAsync(source, [added], Token);
            await WaitAsync(() => scanner.Progress.Completed == 131);
            await scanner.SetEnabledAsync(false, Token);
        }

        // Assert
        Assert.Equal(131, files.Opened);
        Assert.Empty(await fixture.Library.PendingMetadataAsync(7, DateTimeOffset.UtcNow, ct: Token));
        await using var restored = new MetadataScanner(fixture.Session, fixture.Library, files, new(25, 0, 10));
        await restored.RestoreAsync(Token); Assert.False(restored.Enabled);
        await fixture.Library.SavePageAsync(source, [Fixture.Track(source, 132)], Token);
        Assert.Equal(131, files.Opened);
        await restored.SetEnabledAsync(true, Token);
        await WaitAsync(() => files.Opened == 132);
    }

    [Fact]
    public async Task Failed_scans_retry_explicitly_and_account_changes_discard_inflight_results()
    {
        // Arrange
        await using var fixture = await ScanFixture.CreateAsync();
        var source = new Source(7, 1, "Source"); var track = Fixture.Track(source, 1);
        await fixture.Library.SavePageAsync(source, [track], Token);
        var files = new Files { Fail = true };
        await using var scanner = new MetadataScanner(fixture.Session, fixture.Library, files, new(25, 0, 10));

        // Act
        await scanner.RestoreAsync(Token);
        await WaitAsync(() => scanner.Progress.Failed == 1);
        files.Fail = false;
        await scanner.RetryFailedAsync(Token);
        await WaitAsync(() => scanner.Progress.Completed == 1);
        files.Hold = new TaskCompletionSource();
        var next = Fixture.Track(source, 2);
        await fixture.Library.SavePageAsync(source, [next], Token);
        await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        fixture.Transport.Channel.Writer.TryWrite(JsonSerializer.SerializeToElement(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateClosed")))));
        await WaitAsync(() => fixture.Session.AccountId is null);
        files.Hold.SetResult(); await scanner.SuspendAsync();

        // Assert
        var saved = await fixture.Library.SearchAsync(7, "", ct: Token);
        Assert.Equal(MetadataScanStatus.Completed, saved.Single(t => t.Id == track.Id).MetadataScan!.Status);
        Assert.Null(saved.Single(t => t.Id == next.Id).MetadataScan);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class Files : IMetadataSource
    {
        public int Opened;
        public bool Fail;
        public bool FailNotification;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Started { get; } = new();
        public async Task<IReadOnlyMediaFile> OpenAsync(Track track, CancellationToken ct)
        {
            Interlocked.Increment(ref Opened);
            if (Fail) throw new InvalidOperationException("Synthetic permanent failure.");
            if (Hold is { } hold) { Started.TrySetResult(); await hold.Task.WaitAsync(ct); }
            return new FileBytes(EmbeddedMetadataTests.FixtureBytes("flac"));
        }
        public Task ObserveAsync(Track track, byte[]? artwork, CancellationToken ct) =>
            FailNotification ? Task.FromException(new IOException("Synthetic notification failure.")) : Task.CompletedTask;
    }
    private sealed class FileBytes(byte[] data) : IReadOnlyMediaFile
    {
        public long AccountId => 7;
        public long Length => data.Length;
        public Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct) => Task.FromResult(data.AsSpan((int)offset, count).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ScanFixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "channelwave-scanner-" + Guid.NewGuid() + ".sqlite");
        public SqliteLibrary Library { get; }
        public TelegramSession Session { get; }
        public FakeTransport Transport { get; }
        private ScanFixture()
        {
            Transport = new(q => JsonSerializer.SerializeToElement(q["@type"]!.GetValue<string>() switch
            {
                "getOption" => Td.Query("optionValueString", ("value", q["name"]!.GetValue<string>() == "version" ? "1.8.68" : Td.Revision)),
                "getMe" => Td.Query("user", ("id", 7)),
                _ => Td.Query("authorizationStateReady")
            }));
            Session = new(Transport); Library = new(path);
        }
        public static async Task<ScanFixture> CreateAsync()
        {
            var fixture = new ScanFixture(); await fixture.Library.InitializeAsync(Token);
            await fixture.Session.ConnectAsync(new(1, new('0', 32), "fixture", "fixture", ""), Token);
            fixture.Transport.Channel.Writer.TryWrite(JsonSerializer.SerializeToElement(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady")))));
            await WaitAsync(() => fixture.Session.AccountId == 7); return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync(); await Library.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
