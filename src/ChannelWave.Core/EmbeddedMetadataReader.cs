using System.Buffers.Binary;
using System.Text;

namespace ChannelWave.Core;

/// <summary>Read-only container inspection. Seeks over audio payloads; never requests a full download.</summary>
public static class EmbeddedMetadataReader
{
    public const int Version = 1;
    public const int MaxBytes = 32 * 1024 * 1024;
    public const int MaxOperations = 128;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<MetadataReadResult> ReadAsync(IReadOnlyMediaFile input, CancellationToken ct = default)
    {
        var file = new BoundedFile(input);
        try
        {
            var header = await file.ReadRangeAsync(0, (int)Math.Min(12, file.Length), ct);
            EmbeddedMetadata fields; byte[]? artwork = null;
            if (header.AsSpan().StartsWith("ID3"u8) || header.Length >= 2 && header[0] == 0xff && (header[1] & 0xe0) == 0xe0)
            {
                var id3 = await Id3Codec.ReadMetadataAsync(file, ct);
                return FromId3(id3);
            }
            else if (header.AsSpan().StartsWith("fLaC"u8)) (fields, artwork) = await ReadFlacAsync(file, ct);
            else if (header.AsSpan().StartsWith("OggS"u8)) (fields, artwork) = await ReadOggAsync(file, ct);
            else if (header.Length >= 8 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8)) (fields, artwork) = await ReadMp4Async(file, ct);
            else return Unsupported("Unsupported audio container.");
            return Normalize(fields, artwork);
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException or ArgumentException or EndOfStreamException)
        {
            return Unsupported(e.Message);
        }
    }

    public static MetadataReadResult FromId3(Id3Document document)
    {
        if (document.ReadOnlyReason is { } reason) return Unsupported(reason);
        var fields = document.Fields;
        return Normalize(new(fields.Title, fields.Artist, fields.Album, fields.Year, fields.Genre, fields.Track, fields.Disc),
            ArtworkData.Embedded(document));
    }

    private static MetadataReadResult Normalize(EmbeddedMetadata fields, byte[]? artwork)
    {
        fields = new(Clean(fields.Title), Clean(fields.Artist), Clean(fields.Album), Clean(fields.Year),
            Clean(fields.Genre), Clean(fields.Track), Clean(fields.Disc));
        return new(fields, artwork, fields == new EmbeddedMetadata() && artwork is null
            ? MetadataScanStatus.NoTags : MetadataScanStatus.Completed);
    }

    private static string Clean(string value) => new string(value.Where(c => !char.IsControl(c)).Take(1000).ToArray()).Trim();
    private static MetadataReadResult Unsupported(string reason) => new(new(), null, MetadataScanStatus.Unsupported, reason);

    private sealed class BoundedFile(IReadOnlyMediaFile input) : IReadOnlyMediaFile
    {
        private int operations;
        private long bytes;
        public long AccountId => input.AccountId;
        public long Length => input.Length;
        public async Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (offset < 0 || count < 0 || offset > Length - count) throw new InvalidDataException("Metadata points outside the file.");
            if (++operations > MaxOperations || (bytes += count) > MaxBytes) throw new InvalidDataException("Metadata scan exceeds its bounded read budget.");
            var data = await input.ReadRangeAsync(offset, count, ct);
            if (data.Length != count) throw new InvalidDataException("Incomplete metadata range.");
            return data;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(EmbeddedMetadata, byte[]?)> ReadFlacAsync(BoundedFile file, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? cover = null; var front = false; long offset = 4;
        for (var blocks = 0; blocks < 128; blocks++)
        {
            var header = await file.ReadRangeAsync(offset, 4, ct);
            var kind = header[0] & 0x7f; var length = header[1] << 16 | header[2] << 8 | header[3]; offset += 4;
            if (offset > file.Length - length) throw new InvalidDataException("Truncated FLAC metadata block.");
            if (kind is 4 or 6)
            {
                if (kind == 4) ReadComments(await file.ReadRangeAsync(offset, length, ct), fields, ref cover, ref front);
                else if (length <= Id3Codec.MaxArtworkBytes + 4096)
                    SelectPicture(await file.ReadRangeAsync(offset, length, ct), ref cover, ref front);
            }
            offset += length;
            if ((header[0] & 0x80) != 0) return (FromComments(fields), cover);
        }
        throw new InvalidDataException("Too many FLAC metadata blocks.");
    }

    private static EmbeddedMetadata FromComments(Dictionary<string, string> fields)
    {
        string Get(string name) => fields.GetValueOrDefault(name, "");
        string Number(string name, string total) => Get(name) is { Length: > 0 } n && !n.Contains('/') && Get(total) is { Length: > 0 } t ? $"{n}/{t}" : Get(name);
        return new(Get("TITLE"), Get("ARTIST") is { Length: > 0 } a ? a : Get("ALBUMARTIST"), Get("ALBUM"),
            Get("DATE") is { Length: > 0 } d ? d : Get("YEAR"), Get("GENRE"), Number("TRACKNUMBER", "TRACKTOTAL"), Number("DISCNUMBER", "DISCTOTAL"));
    }

    private static void ReadComments(byte[] data, Dictionary<string, string> fields, ref byte[]? cover, ref bool front)
    {
        var cursor = new Cursor(data);
        cursor.Take(cursor.LengthLittle()); // vendor
        var count = cursor.LengthLittle();
        if (count > 10000) throw new InvalidDataException("Too many Vorbis comments.");
        for (var i = 0; i < count; i++)
        {
            var comment = Utf8.GetString(cursor.Take(cursor.LengthLittle()));
            var split = comment.IndexOf('='); if (split < 1) continue;
            var name = comment[..split]; var value = comment[(split + 1)..];
            if (name.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > (Id3Codec.MaxArtworkBytes + 4096L) * 4 / 3 + 4) continue;
                try { SelectPicture(Convert.FromBase64String(value), ref cover, ref front); }
                catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException) { }
            }
            else if (name is not { Length: > 64 } && !fields.ContainsKey(name)) fields[name] = Clean(value);
        }
    }

    private static void SelectPicture(byte[] data, ref byte[]? cover, ref bool front)
    {
        var cursor = new Cursor(data); var type = cursor.LengthBig();
        cursor.Take(cursor.LengthBig()); cursor.Take(cursor.LengthBig()); // MIME and description
        cursor.Take(16); // dimensions, depth, indexed colors
        var length = cursor.LengthBig();
        if (length > Id3Codec.MaxArtworkBytes) return;
        var picture = cursor.Take(length).ToArray();
        try { Id3Codec.ArtworkMime(picture); }
        catch (ArgumentException) { return; }
        if (cover is null || !front && type == 3) { cover = picture; front = type == 3; }
    }

    private static async Task<(EmbeddedMetadata, byte[]?)> ReadOggAsync(BoundedFile file, CancellationToken ct)
    {
        long offset = 0; uint? serial = null; uint sequence = 0;
        using var packet = new MemoryStream();
        var codec = ""; var packetNumber = 0;
        while (offset < file.Length)
        {
            var header = await file.ReadRangeAsync(offset, 27, ct);
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8) || header[4] != 0) throw new InvalidDataException("Invalid Ogg page.");
            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            var pageSequence = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
            serial ??= pageSerial;
            if (serial != pageSerial || pageSequence != sequence++ || ((header[5] & 1) != 0) != (packet.Length > 0))
                throw new InvalidDataException("Unsupported multiplexed or discontinuous Ogg stream.");
            var lacing = await file.ReadRangeAsync(offset + 27, header[26], ct);
            var length = lacing.Sum(x => (int)x);
            var body = await file.ReadRangeAsync(offset + 27 + lacing.Length, length, ct); var position = 0;
            foreach (var segment in lacing)
            {
                if (packet.Length + segment > MaxBytes) throw new InvalidDataException("Ogg comment exceeds the metadata budget.");
                packet.Write(body, position, segment); position += segment;
                if (segment == 255) continue;
                var data = packet.ToArray(); packet.SetLength(0);
                if (packetNumber++ == 0)
                {
                    codec = data.AsSpan().StartsWith("OpusHead"u8) ? "opus" : data.AsSpan().StartsWith(new byte[] { 1, 118, 111, 114, 98, 105, 115 }) ? "vorbis" : "";
                    if (codec.Length == 0) throw new InvalidDataException("Unsupported Ogg audio codec.");
                }
                else
                {
                    var prefix = codec == "opus" ? 8 : 7;
                    if (codec == "opus" ? !data.AsSpan().StartsWith("OpusTags"u8) : !data.AsSpan().StartsWith(new byte[] { 3, 118, 111, 114, 98, 105, 115 }))
                        throw new InvalidDataException("Missing Ogg comment header.");
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); byte[]? cover = null; var front = false;
                    ReadComments(data[prefix..], fields, ref cover, ref front);
                    return (FromComments(fields), cover);
                }
            }
            offset += 27 + lacing.Length + length;
        }
        throw new InvalidDataException("Truncated Ogg comment packet.");
    }

    private static async Task<(EmbeddedMetadata, byte[]?)> ReadMp4Async(BoundedFile file, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(); byte[]? cover = null;
        async Task Walk(long start, long end, int depth)
        {
            if (depth > 8) throw new InvalidDataException("MP4 metadata nesting is too deep.");
            for (var offset = start; offset < end;)
            {
                var header = await file.ReadRangeAsync(offset, 8, ct);
                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header); var prefix = 8;
                var kind = Encoding.Latin1.GetString(header, 4, 4);
                if (size == 1) { size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(await file.ReadRangeAsync(offset + 8, 8, ct))); prefix = 16; }
                if (size == 0) size = end - offset;
                if (size < prefix || size > end - offset) throw new InvalidDataException("Invalid MP4 atom size.");
                var payload = offset + prefix; var atomEnd = offset + size;
                if (kind is "moov" or "udta" or "ilst" or "trak" or "mdia" or "minf" or "stbl") await Walk(payload, atomEnd, depth + 1);
                else if (kind is "enca" or "drms") throw new InvalidDataException("Encrypted MP4 audio metadata is unsupported.");
                else if (kind == "stsd")
                {
                    if (atomEnd - payload < 8) throw new InvalidDataException("Truncated MP4 sample description.");
                    await Walk(payload + 8, atomEnd, depth + 1);
                }
                else if (kind == "meta")
                {
                    if (atomEnd - payload < 4) throw new InvalidDataException("Truncated MP4 metadata header.");
                    var flags = await file.ReadRangeAsync(payload, 4, ct);
                    if (flags.Any(x => x != 0)) throw new InvalidDataException("Unsupported MP4 metadata version.");
                    await Walk(payload + 4, atomEnd, depth + 1);
                }
                else if (kind is "©nam" or "©ART" or "aART" or "©alb" or "©day" or "©gen" or "trkn" or "disk" or "covr")
                {
                    for (var child = payload; child < atomEnd;)
                    {
                        if (atomEnd - child < 16) throw new InvalidDataException("Truncated MP4 metadata value.");
                        var dataHeader = await file.ReadRangeAsync(child, 16, ct);
                        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(dataHeader);
                        if (dataSize < 16 || dataSize > atomEnd - child) throw new InvalidDataException("Invalid MP4 metadata value.");
                        if (dataHeader.AsSpan(4, 4).SequenceEqual("data"u8))
                        {
                            var count = checked((int)dataSize - 16);
                            if (kind != "covr" || count <= Id3Codec.MaxArtworkBytes)
                            {
                                var data = await file.ReadRangeAsync(child + 16, count, ct);
                                var type = BinaryPrimitives.ReadUInt32BigEndian(dataHeader.AsSpan(8));
                                if (kind == "covr")
                                {
                                    try { Id3Codec.ArtworkMime(data); cover ??= data; } catch (ArgumentException) { }
                                }
                                else if (kind is "trkn" or "disk")
                                {
                                    if (data.Length < 6) throw new InvalidDataException("Truncated MP4 track/disc metadata.");
                                    var number = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)); var total = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
                                    values[kind] = total > 0 ? $"{number}/{total}" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                }
                                else if (type is 1 or 2) values.TryAdd(kind, type == 1 ? Utf8.GetString(data) : new UnicodeEncoding(true, false, true).GetString(data));
                            }
                        }
                        child += dataSize;
                    }
                }
                offset = atomEnd; // mdat and other audio payloads are never read
            }
        }
        await Walk(0, file.Length, 0);
        return (new(values.GetValueOrDefault("©nam", ""), values.GetValueOrDefault("©ART", values.GetValueOrDefault("aART", "")),
            values.GetValueOrDefault("©alb", ""), values.GetValueOrDefault("©day", ""), values.GetValueOrDefault("©gen", ""),
            values.GetValueOrDefault("trkn", ""), values.GetValueOrDefault("disk", "")), cover);
    }

    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;
        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > remaining.Length) throw new InvalidDataException("Truncated metadata field.");
            var value = remaining[..count]; remaining = remaining[count..]; return value;
        }
        public int LengthLittle() => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));
        public int LengthBig() => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)));
    }
}
