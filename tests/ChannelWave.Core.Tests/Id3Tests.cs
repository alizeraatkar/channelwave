using ChannelWave.Core;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class Id3Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Audio
    {
        get
        {
            var bytes = new byte[1251];
            for (var index = 0; index < 3; index++) { bytes[index * 417] = 0xff; bytes[index * 417 + 1] = 0xfb; bytes[index * 417 + 2] = 0x90; }
            return bytes;
        }
    }
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 128)]
    [InlineData(4, 0)]
    [InlineData(4, 128)]
    [InlineData(4, 16)]
    [InlineData(4, 144)]
    public async Task Unicode_artwork_and_opaque_frames_round_trip_without_changing_audio(int version, int flags)
    {
        // Arrange: private data deliberately contains bytes that require unsynchronisation.
        var unknown = Frame("XABC", [9, 0xff, 0xe2, 0xff, 0, 7], version, flags);
        var picture = Frame("APIC", [0, ..Encoding.ASCII.GetBytes("image/png"), 0, 4, 0, ..Png], version, flags);
        var bytes = Tagged(version, flags, Frame("TIT2", [0, ..Encoding.Latin1.GetBytes("Original")], version, flags), unknown, picture);
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var draft = before.Fields with { Title = "آهنگ كِي 🎵", Artist = "العربية فارسی", Album = "夜", Year = "2026", Genre = "Jazz", Track = "2/12", Disc = "1/2" };
        var rewrite = Id3Codec.Rewrite(before, draft, true, -1, Png);
        var exported = Apply(bytes, rewrite);
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(exported), Token);

        // Assert
        Assert.Null(before.ReadOnlyReason); Assert.Null(after.ReadOnlyReason);
        Assert.Equal(draft, after.Fields); Assert.Equal(version, after.Version); Assert.Equal(flags, after.Flags);
        Assert.Equal(before.Frames.Single(x => x.Id == "XABC").Raw, after.Frames.Single(x => x.Id == "XABC").Raw);
        Assert.Equal(Png, after.Artwork.Single(x => x.PictureType == 3).Data);
        Assert.Equal(before.Artwork.Single().Data, after.Artwork.Single(x => x.PictureType == 4).Data);
        Assert.Equal(bytes.AsSpan((int)before.PrefixLength).ToArray(), exported.AsSpan((int)after.PrefixLength).ToArray());
        Assert.Equal("Original", (await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token)).Fields.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Legacy_values_become_unicode_v23_and_unrelated_comments_survive(int minorVersion)
    {
        // Arrange
        var legacy = new byte[128]; "TAG"u8.CopyTo(legacy); Encoding.Latin1.GetBytes("Legacy title").CopyTo(legacy, 3);
        Encoding.Latin1.GetBytes("Keep this comment").CopyTo(legacy, 97); legacy[127] = 8;
        if (minorVersion == 1) legacy[126] = 7;
        else legacy[125] = 65;
        byte[] bytes = [..Audio, ..legacy];
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var rewrite = Id3Codec.Rewrite(before, before.Fields with { Title = "فارسی 🎵", Album = "New album" });
        var exported = Apply(bytes, rewrite); var after = await Id3Codec.ReadAsync(new MemoryTagFile(exported), Token);

        // Assert
        Assert.Null(after.ReadOnlyReason); Assert.Equal(3, after.Version); Assert.Equal("فارسی 🎵", after.Fields.Title);
        Assert.Equal("Jazz", after.Fields.Genre); Assert.Equal(before.Fields.Track, after.Fields.Track);
        Assert.Equal(legacy.AsSpan(97, 30).ToArray(), exported.AsSpan(exported.Length - 31, 30).ToArray());
        Assert.All(exported.AsSpan(exported.Length - 125, 30).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(Audio, exported.AsSpan((int)after.PrefixLength, Audio.Length).ToArray());
    }

    [Fact]
    public async Task Missing_tags_have_empty_fields_and_can_be_added_or_left_untouched()
    {
        // Arrange
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(Audio), Token);

        // Act
        var unchanged = Apply(Audio, Id3Codec.Rewrite(before, before.Fields with { }));
        var edited = Apply(Audio, Id3Codec.Rewrite(before, before.Fields with { Title = "New" }));
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(edited), Token);

        // Assert
        Assert.Null(before.ReadOnlyReason); Assert.Equal("No ID3 tags", before.Format); Assert.Equal(new Id3Fields(), before.Fields);
        Assert.Equal(Audio, unchanged); Assert.Equal("New", after.Fields.Title);
        Assert.Equal(Audio, edited.AsSpan((int)after.PrefixLength).ToArray());
    }

    [Fact]
    public async Task Clearing_tags_and_selected_artwork_preserves_other_frames()
    {
        // Arrange
        var bytes = Tagged(4, 0, Frame("TIT2", [3, 65], 4), Frame("TPE1", [3, 66], 4),
            Frame("APIC", [0, ..Encoding.ASCII.GetBytes("image/png"), 0, 3, 0, ..Png], 4),
            Frame("APIC", [0, ..Encoding.ASCII.GetBytes("image/png"), 0, 4, 0, ..Png], 4));
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(Apply(bytes, Id3Codec.Rewrite(before, before.Fields with { Title = "" }, true, before.Artwork[0].FrameIndex))), Token);

        // Assert
        Assert.Equal("", after.Fields.Title); Assert.Equal("B", after.Fields.Artist);
        Assert.Equal(4, Assert.Single(after.Artwork).PictureType);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("extended")]
    [InlineData("compressed")]
    [InlineData("truncated")]
    [InlineData("size")]
    [InlineData("encoding")]
    [InlineData("padding")]
    [InlineData("footer")]
    [InlineData("wav")]
    [InlineData("truncated-audio")]
    public async Task Unsafe_or_unsupported_files_are_read_only(string kind)
    {
        // Arrange
        var bytes = Tagged(4, kind == "footer" ? 16 : 0, Frame("TIT2", [3, 65], 4));
        switch (kind)
        {
            case "version": bytes[3] = 2; break;
            case "extended": bytes[5] = 64; break;
            case "compressed": bytes[19] = 8; break;
            case "truncated": bytes = bytes[..15]; break;
            case "size": bytes[6] = 255; break;
            case "encoding": bytes[20] = 99; break;
            case "padding": bytes[22] = 1; break;
            case "footer": bytes[26] = 0; break;
            case "wav": bytes = [..Encoding.ASCII.GetBytes("RIFF"), ..new byte[1000]]; break;
            case "truncated-audio": bytes = Audio[..421]; break;
        }

        // Act
        var document = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Assert
        Assert.NotNull(document.ReadOnlyReason);
        Assert.Throws<InvalidOperationException>(() => Id3Codec.Rewrite(document, document.Fields with { Title = "Edited" }));
    }

    [Fact]
    public async Task Per_frame_unsynchronisation_and_data_length_indicators_are_read_and_preserved()
    {
        // Arrange
        var frame = Frame("TIT2", [0, 0, 0, 4, 0, 65, 0xff, 0, 0xe0], 4); frame[9] = 3;
        var bytes = Tagged(4, 0, frame);
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(Apply(bytes, Id3Codec.Rewrite(before, before.Fields with { Album = "Added" }))), Token);

        // Assert
        Assert.Null(before.ReadOnlyReason); Assert.Equal("Aÿà", before.Fields.Title);
        Assert.Equal(before.Frames[0].Raw, after.Frames.Single(x => x.Id == "TIT2").Raw);
    }

    [Fact]
    public async Task Invalid_edits_and_large_metadata_are_rejected_before_export()
    {
        // Arrange
        var document = await Id3Codec.ReadAsync(new MemoryTagFile(Audio), Token);
        var huge = new byte[10]; "ID3"u8.CopyTo(huge); huge[3] = 3; Sync(huge.AsSpan(6), Id3Codec.MaxTagBytes);

        // Act
        var oversized = await Id3Codec.ReadAsync(new MemoryTagFile(huge), Token);

        // Assert
        Assert.NotNull(oversized.ReadOnlyReason);
        Assert.Throws<ArgumentException>(() => Id3Codec.Rewrite(document, document.Fields with { Track = "13/12" }));
        Assert.Throws<ArgumentException>(() => Id3Codec.Rewrite(document, document.Fields with { Year = "0000" }));
        Assert.Throws<ArgumentException>(() => Id3Codec.Rewrite(document, document.Fields with { Title = "bad\0text" }));
        Assert.Throws<ArgumentException>(() => Id3Codec.Rewrite(document, document.Fields, true, -1, [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => Id3Codec.ArtworkMime(new byte[Id3Codec.MaxArtworkBytes + 1]));
        var brokenPng = Png.ToArray(); brokenPng[52] ^= 1;
        Assert.Throws<ArgumentException>(() => Id3Codec.ArtworkMime(brokenPng));
    }

    [Fact]
    public async Task Native_export_streams_to_a_separate_file_and_cancellation_removes_partial_output()
    {
        // Arrange
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3"); var target = source + ".copy";
        await File.WriteAllBytesAsync(source, Audio, Token);
        try
        {
            await using var file = new NativeTagFile(7, source, (_, _, _, _) => throw new NotSupportedException());
            var document = await Id3Codec.ReadAsync(file, Token);
            var rewrite = Id3Codec.Rewrite(document, document.Fields with { Title = "Copy" });

            // Act
            await file.WriteCopyAsync(target, rewrite, Token);
            await using var output = new NativeTagFile(7, target, (_, _, _, _) => throw new NotSupportedException());
            var copy = await Id3Codec.ReadAsync(output, Token);

            // Assert
            Assert.Equal("Copy", copy.Fields.Title); Assert.Equal(Audio, await File.ReadAllBytesAsync(source, Token));
            await Assert.ThrowsAsync<IOException>(() => file.WriteCopyAsync(source, rewrite, Token));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.WriteCopyAsync(target + ".cancel", rewrite, cancel.Token));
            Assert.False(File.Exists(target + ".cancel"));
        }
        finally { File.Delete(source); File.Delete(target); }
    }

    private static byte[] Apply(byte[] original, Id3Rewrite rewrite) => [..rewrite.Prefix,
        ..original.AsSpan((int)rewrite.OriginalPrefixLength, original.Length - (int)rewrite.OriginalPrefixLength - (rewrite.ReplaceLegacyFooter ? 128 : 0)),
        ..(rewrite.ReplaceLegacyFooter ? rewrite.LegacyFooter ?? [] : [])];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Supported_text_encodings_remain_unchanged_when_another_field_is_edited(int encoding)
    {
        // Arrange
        var expected = encoding == 0 ? "Café" : "فارسی 🎵 夜";
        byte[] payload = encoding switch
        {
            0 => [0, ..Encoding.Latin1.GetBytes(expected)],
            1 => [1, 0xfe, 0xff, ..Encoding.BigEndianUnicode.GetBytes(expected)],
            2 => [2, ..Encoding.BigEndianUnicode.GetBytes(expected)],
            _ => [3, ..Encoding.UTF8.GetBytes(expected)]
        };
        var bytes = Tagged(4, 0, Frame("TIT2", payload, 4));
        var before = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(Apply(bytes, Id3Codec.Rewrite(before, before.Fields with { Album = "Added" }))), Token);

        // Assert
        Assert.Null(before.ReadOnlyReason); Assert.Equal(expected, before.Fields.Title);
        Assert.Equal(expected, after.Fields.Title); Assert.Equal(before.Frames[0].Raw, after.Frames.Single(x => x.Id == "TIT2").Raw);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Clearing_the_final_field_removes_the_empty_tag_and_preserves_all_audio(int version)
    {
        // Arrange
        var bytes = Tagged(version, version == 4 ? 16 : 0, Frame("TIT2", [0, 65], version));
        var document = await Id3Codec.ReadAsync(new MemoryTagFile(bytes), Token);

        // Act
        var exported = Apply(bytes, Id3Codec.Rewrite(document, document.Fields with { Title = "" }));
        var after = await Id3Codec.ReadAsync(new MemoryTagFile(exported), Token);

        // Assert
        Assert.Equal(Audio, exported); Assert.Null(after.ReadOnlyReason);
        Assert.Equal("No ID3 tags", after.Format); Assert.Equal("", after.Fields.Title);
    }

    [Fact]
    public async Task Cancellation_during_native_copy_removes_the_partial_file()
    {
        // Arrange
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3"); var target = source + ".copy";
        await using (var input = File.Create(source)) { input.SetLength(64 * 1024 * 1024); await input.WriteAsync(Audio, Token); }
        try
        {
            await using var file = new NativeTagFile(7, source, (_, _, _, _) => throw new NotSupportedException());
            var document = await Id3Codec.ReadAsync(file, Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

            // Act
            var copying = file.WriteCopyAsync(target, Id3Codec.Rewrite(document, document.Fields with { Title = "Canceled" }), cancellation.Token);
            cancellation.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
            Assert.False(File.Exists(target)); Assert.Equal(64 * 1024 * 1024, new FileInfo(source).Length);
        }
        finally { File.Delete(source); File.Delete(target); }
    }

    [Fact]
    public async Task Passive_artwork_reads_only_leading_metadata_and_prefers_the_front_cover()
    {
        var back = Frame("APIC", [0, ..Encoding.ASCII.GetBytes("image/png"), 0, 4, 0, ..Png], 4);
        var front = Frame("APIC", [0, ..Encoding.ASCII.GetBytes("image/png"), 0, 3, 0, ..Png], 4);
        var bytes = Tagged(4, 0, back, front); var file = new MemoryTagFile(bytes);
        var document = await Id3Codec.ReadArtworkAsync(file, Token);
        Assert.Equal(Png, ArtworkData.Embedded(document)); Assert.Null(document.LegacyFooter);
        Assert.Equal(2, file.Reads.Count);
        Assert.All(file.Reads, range => { Assert.Equal(0, range.Offset); Assert.True(range.Count <= document.PrefixLength); });
        var untagged = new MemoryTagFile(Audio);
        Assert.Null(ArtworkData.Embedded(await Id3Codec.ReadArtworkAsync(untagged, Token)));
        Assert.Equal((0L, 10), Assert.Single(untagged.Reads));
    }

    private static byte[] Frame(string id, byte[] payload, int version, int flags = 0)
    {
        if (version == 4 && (flags & 128) != 0) payload = Unsync(payload);
        var frame = new byte[10 + payload.Length]; Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        if (version == 4) Sync(frame.AsSpan(4, 4), payload.Length); else BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        if (version == 4 && (flags & 128) != 0) frame[9] = 2;
        payload.CopyTo(frame, 10); return frame;
    }
    private static byte[] Tagged(int version, int flags, params byte[][] frames)
    {
        byte[] body = [..frames.SelectMany(x => x), 0, 0, 0, 0];
        if (version == 3 && (flags & 128) != 0) body = Unsync(body);
        var header = new byte[10]; "ID3"u8.CopyTo(header); header[3] = (byte)version; header[5] = (byte)flags; Sync(header.AsSpan(6), body.Length);
        var footer = header.ToArray(); "3DI"u8.CopyTo(footer);
        return [..header, ..body, ..(version == 4 && (flags & 16) != 0 ? footer : []), ..Audio];
    }
    private static void Sync(Span<byte> bytes, int value) { for (var index = 3; index >= 0; index--) { bytes[index] = (byte)(value & 127); value >>= 7; } }
    private static byte[] Unsync(byte[] bytes)
    {
        var result = new List<byte>();
        for (var index = 0; index < bytes.Length; index++) { result.Add(bytes[index]); if (bytes[index] == 255 && (index + 1 == bytes.Length || bytes[index + 1] is 0 or >= 224)) result.Add(0); }
        return result.ToArray();
    }
    private sealed class MemoryTagFile(byte[] bytes) : ITagFile
    {
        public List<(long Offset, int Count)> Reads { get; } = [];
        public long AccountId => 7;
        public long Length => bytes.Length;
        public Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Reads.Add((offset, count)); return Task.FromResult(bytes.AsSpan((int)offset, count).ToArray()); }
        public Task<string> ExportCopyAsync(Id3Rewrite rewrite, string name, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
