using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class EmbeddedMetadataTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("flac")]
    [InlineData("opus")]
    [InlineData("vorbis")]
    [InlineData("m4a")]
    [InlineData("mp3")]
    [InlineData("aac")]
    public async Task Common_containers_read_unicode_fields_and_embedded_covers(string format)
    {
        // Arrange
        var bytes = FixtureBytes(format);
        await using var file = new MemoryFile(bytes);

        // Act
        var result = await EmbeddedMetadataReader.ReadAsync(file, Token);

        // Assert
        Assert.Equal(MetadataScanStatus.Completed, result.Status);
        Assert.Equal("آهنگ 🎵", result.Metadata.Title); Assert.Equal("Artist", result.Metadata.Artist);
        Assert.Equal("Album", result.Metadata.Album); Assert.Equal("2026", result.Metadata.Year);
        Assert.Equal("Jazz", result.Metadata.Genre); Assert.Equal("2/12", result.Metadata.Track); Assert.Equal("1/2", result.Metadata.Disc);
        Assert.Equal(Png, result.Artwork);
        Assert.True(file.Requests.Count <= EmbeddedMetadataReader.MaxOperations);
    }

    [Fact]
    public async Task Tail_located_m4a_metadata_seeks_past_audio_without_fetching_the_payload()
    {
        // Arrange
        var prefix = Atom("ftyp", "M4A "u8.ToArray()); var media = Atom("mdat", new byte[2 * 1024 * 1024]);
        var metadata = FixtureBytes("m4a")[prefix.Length..];
        await using var file = new MemoryFile([..prefix, ..media, ..metadata]);

        // Act
        var result = await EmbeddedMetadataReader.ReadAsync(file, Token);

        // Assert
        Assert.Equal("آهنگ 🎵", result.Metadata.Title);
        Assert.True(file.Requests.Sum(x => x.Count) < 4096);
        Assert.DoesNotContain(file.Requests, r => r.Offset >= prefix.Length + 8 && r.Offset < prefix.Length + media.Length);
    }

    [Fact]
    public async Task Malformed_and_over_budget_containers_are_recorded_without_unbounded_reads()
    {
        // Arrange
        byte[][] malformed = ["fLaC\x84\xff\xff\xff"u8.ToArray(), "OggS"u8.ToArray(),
            [..Atom("ftyp", "M4A "u8.ToArray()), ..Enumerable.Range(0, 140).SelectMany(_ => Atom("free", []))]];

        // Act
        var results = new List<MetadataReadResult>();
        foreach (var bytes in malformed) results.Add(await EmbeddedMetadataReader.ReadAsync(new MemoryFile(bytes), Token));

        // Assert
        Assert.All(results, r => { Assert.Equal(MetadataScanStatus.Unsupported, r.Status); Assert.NotEmpty(r.Reason!); });
        var oversized = new byte[10]; "ID3"u8.CopyTo(oversized); oversized[3] = 4; oversized[6] = 127;
        var result = await EmbeddedMetadataReader.ReadAsync(new MemoryFile(oversized), Token);
        Assert.Equal(MetadataScanStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task Untagged_aac_has_no_tags_and_cancellation_propagates()
    {
        // Arrange
        await using var file = new MemoryFile([0xff, 0xf1, 0x50, 0x80, 0, 0, 0, 0, 0, 0]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        // Act
        var result = await EmbeddedMetadataReader.ReadAsync(file, Token);

        // Assert
        Assert.Equal(MetadataScanStatus.NoTags, result.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EmbeddedMetadataReader.ReadAsync(file, canceled.Token));
    }

    [Fact]
    public async Task Scans_preserve_edits_survive_restart_and_reject_stale_media_removed_rows_and_other_accounts()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-metadata-" + Guid.NewGuid() + ".sqlite");
        var source = new Source(7, 1, "Source"); var track = Fixture.Track(source, 1); var key = Metadata.MediaKey(track);
        try
        {
            await using (var db = new SqliteLibrary(path))
            {
                await db.InitializeAsync(Token); await db.SavePageAsync(source, [track], Token);
                await db.SetLocalMetadataAsync(7, track.Id, "Manual", null, Token); await db.SetFavoriteAsync(7, track.Id, true, Token);

                // Act
                await db.SetMetadataScanAsync(7, track.Id, key, new("Embedded", "Artist", "Album"), new(key, 1, MetadataScanStatus.Completed), Token);
                await db.SavePageAsync(source, [track with { Id = Guid.NewGuid(), OriginalTitle = "Fresh source" }], Token);
            }
            await using var reopened = new SqliteLibrary(path);
            var updated = Assert.Single(await reopened.SearchAsync(7, "manual", ct: Token));

            // Assert
            Assert.True(updated.Favorite); Assert.Equal("Artist", updated.Performer); Assert.Equal("Fresh source", updated.OriginalTitle);
            Assert.Empty(await reopened.PendingMetadataAsync(7, DateTimeOffset.UtcNow, ct: Token));
            var automatic = await reopened.SetLocalMetadataAsync(7, track.Id, null, null, Token); Assert.Equal("Embedded", automatic.Title);
            Assert.Null(await reopened.SetMetadataScanAsync(8, track.Id, key, new("Wrong"), new(key, 1, MetadataScanStatus.Completed), Token));
            var changed = track with { Media = track.Media with { Size = track.Media.Size + 1 } };
            await reopened.SavePageAsync(source, [changed], Token);
            Assert.Null(Assert.Single(await reopened.PendingMetadataAsync(7, DateTimeOffset.UtcNow, ct: Token)).EmbeddedMetadata);
            Assert.Null(await reopened.SetMetadataScanAsync(7, track.Id, key, new("Stale"), new(key, 1, MetadataScanStatus.Completed), Token));
            await reopened.RemoveSourceAsync(7, 1, Token);
            Assert.Null(await reopened.SetMetadataScanAsync(7, track.Id, key, new("Removed"), new(key, 1, MetadataScanStatus.Completed), Token));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Pending_scan_pagination_skips_finished_tracks_and_obeys_retry_deadlines()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "channelwave-pending-" + Guid.NewGuid() + ".sqlite");
        try
        {
            await using var db = new SqliteLibrary(path); await db.InitializeAsync(Token);
            var source = new Source(7, 1, "Source");
            var tracks = Enumerable.Range(1, 1050).Select(i => Fixture.Track(source, i)).ToArray();
            await db.SavePageAsync(source, tracks, Token);
            var now = DateTimeOffset.UtcNow;
            foreach (var track in tracks.Take(1005))
            {
                var key = Metadata.MediaKey(track);
                await db.SetMetadataScanAsync(7, track.Id, key, null, new(key, 1, MetadataScanStatus.NoTags), Token);
            }

            // Act
            var pending = await db.PendingMetadataAsync(7, now, 50, Token);
            var failed = pending[0]; var mediaKey = Metadata.MediaKey(failed);
            await db.SetMetadataScanAsync(7, failed.Id, mediaKey, null, new(mediaKey, 1, MetadataScanStatus.Failed, 1, now.AddMinutes(1)), Token);

            // Assert
            Assert.Equal(45, pending.Count);
            Assert.DoesNotContain(await db.PendingMetadataAsync(7, now, ct: Token), t => t.Id == failed.Id);
            Assert.Contains(await db.PendingMetadataAsync(7, now.AddMinutes(2), ct: Token), t => t.Id == failed.Id);
            await db.RetryMetadataAsync(7, Token);
            Assert.Contains(await db.PendingMetadataAsync(7, now, ct: Token), t => t.Id == failed.Id);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Foreground_preempts_and_drains_background_cancellation_before_handoff()
    {
        // Arrange
        var work = new MediaWorkCoordinator(); var started = new TaskCompletionSource(); var drained = new TaskCompletionSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var background = work.BackgroundAsync(async ct =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return 1; }
            finally { drained.TrySetResult(); }
        }, lifetime.Token);
        await started.Task.WaitAsync(Token);

        // Act
        using var foreground = await work.ForegroundAsync(Token);

        // Assert
        Assert.True(drained.Task.IsCompletedSuccessfully); Assert.False(background.IsCompleted);
        lifetime.Cancel(); foreground.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
    }

    private static byte[] Number(int value, bool little = false)
    {
        var bytes = new byte[4]; if (little) BinaryPrimitives.WriteInt32LittleEndian(bytes, value); else BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes;
    }
    private static byte[] Comments()
    {
        string[] values = ["TITLE=آهنگ 🎵", "ARTIST=Artist", "ALBUM=Album", "DATE=2026", "GENRE=Jazz", "TRACKNUMBER=2", "TRACKTOTAL=12", "DISCNUMBER=1", "DISCTOTAL=2", "METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(Picture())];
        return [..Number(0, true), ..Number(values.Length, true), ..values.SelectMany(v => { var b = Encoding.UTF8.GetBytes(v); return Number(b.Length, true).Concat(b); })];
    }
    private static byte[] Picture() => [..Number(3), ..Number(9), .."image/png"u8, ..Number(0), ..new byte[16], ..Number(Png.Length), ..Png];
    private static byte[] Atom(string kind, byte[] data) => [..Number(data.Length + 8), ..Encoding.Latin1.GetBytes(kind), ..data];
    private static byte[] Value(string kind, string value) => Atom(kind, Atom("data", [..Number(1), ..Number(0), ..Encoding.UTF8.GetBytes(value)]));
    public static byte[] FixtureBytes(string format)
    {
        if (format == "flac") { var data = Comments(); return [.."fLaC"u8, 0x84, (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, ..data]; }
        if (format is "opus" or "vorbis")
        {
            byte[] first = format == "opus" ? [.."OpusHead"u8, ..new byte[11]] : [1, .."vorbis"u8, ..new byte[23]];
            byte[] second = format == "opus" ? [.."OpusTags"u8, ..Comments()] : [3, .."vorbis"u8, ..Comments(), 1];
            byte[] Page(byte[] packet, int sequence)
            {
                var lacing = Enumerable.Repeat((byte)255, packet.Length / 255).Append((byte)(packet.Length % 255)).ToArray();
                var header = new byte[27]; "OggS"u8.CopyTo(header); header[5] = sequence == 0 ? (byte)2 : (byte)0;
                Number(sequence, true).CopyTo(header, 18); header[26] = (byte)lacing.Length;
                return [..header, ..lacing, ..packet];
            }
            return [..Page(first, 0), ..Page(second, 1)];
        }
        if (format == "m4a") return [..Atom("ftyp", "M4A "u8.ToArray()), ..Atom("moov", Atom("udta", Atom("meta", [0, 0, 0, 0, ..Atom("ilst", [
            ..Value("©nam", "آهنگ 🎵"), ..Value("©ART", "Artist"), ..Value("©alb", "Album"), ..Value("©day", "2026"), ..Value("©gen", "Jazz"),
            ..Atom("trkn", Atom("data", [..new byte[8], 0, 0, 0, 2, 0, 12, 0, 0])), ..Atom("disk", Atom("data", [..new byte[8], 0, 0, 0, 1, 0, 2])),
            ..Atom("covr", Atom("data", [..Number(14), ..Number(0), ..Png]))])])) )];
        var original = new TagMemoryFile([0xff, 0xfb, 0x90, ..new byte[414], 0xff, 0xfb, 0x90, ..new byte[414]]);
        var document = Id3Codec.ReadAsync(original).GetAwaiter().GetResult();
        var rewrite = Id3Codec.Rewrite(document, new() { Title = "آهنگ 🎵", Artist = "Artist", Album = "Album", Year = "2026", Genre = "Jazz", Track = "2/12", Disc = "1/2" }, true, -1, Png);
        return [..rewrite.Prefix, ..new byte[160]];
    }

    private sealed class MemoryFile(byte[] data) : IReadOnlyMediaFile
    {
        public List<(long Offset, int Count)> Requests { get; } = [];
        public long AccountId => 7;
        public long Length => data.Length;
        public Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Requests.Add((offset, count)); return Task.FromResult(data.AsSpan((int)offset, count).ToArray()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class TagMemoryFile(byte[] data) : ITagFile
    {
        public long AccountId => 7;
        public long Length => data.Length;
        public Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct) => Task.FromResult(data.AsSpan((int)offset, count).ToArray());
        public Task<string> ExportCopyAsync(Id3Rewrite rewrite, string fileName, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
