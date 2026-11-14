using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ChannelWave.Core;

public sealed record Id3Fields
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Year { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Track { get; set; } = "";
    public string Disc { get; set; } = "";
}

public sealed record Id3Artwork(int FrameIndex, string MimeType, byte PictureType, string Description, byte[] Data);
public sealed record Id3Frame(string Id, ushort Flags, byte[] Raw, byte[] Content);
public sealed record Id3Document(byte Version, byte Flags, long PrefixLength, byte[] OriginalPrefix,
    byte[]? LegacyFooter, Id3Fields Fields, IReadOnlyList<Id3Artwork> Artwork, IReadOnlyList<Id3Frame> Frames,
    string? ReadOnlyReason = null)
{
    public string Format => PrefixLength > 0 ? $"ID3v2.{Version}" : LegacyFooter is null ? "No ID3 tags" : LegacyFooter[125] == 0 && LegacyFooter[126] != 0 ? "ID3v1.1" : "ID3v1";
}

/// <summary>MP3-only metadata reader and conservative, frame-preserving copy writer.</summary>
public static class Id3Codec
{
    // Match the thumbnail policy before any platform image decoder allocates pixels.
    public const long MaxArtworkPixels = 64L * 1024 * 1024;
    public const int MaxTagBytes = 32 * 1024 * 1024;
    public const int MaxArtworkBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private static readonly UnicodeEncoding StrictBigEndian = new(true, false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly string[] Genres = ("Blues|Classic Rock|Country|Dance|Disco|Funk|Grunge|Hip-Hop|Jazz|Metal|New Age|Oldies|Other|Pop|R&B|Rap|Reggae|Rock|Techno|Industrial|Alternative|Ska|Death Metal|Pranks|Soundtrack|Euro-Techno|Ambient|Trip-Hop|Vocal|Jazz+Funk|Fusion|Trance|Classical|Instrumental|Acid|House|Game|Sound Clip|Gospel|Noise|AlternRock|Bass|Soul|Punk|Space|Meditative|Instrumental Pop|Instrumental Rock|Ethnic|Gothic|Darkwave|Techno-Industrial|Electronic|Pop-Folk|Eurodance|Dream|Southern Rock|Comedy|Cult|Gangsta|Top 40|Christian Rap|Pop/Funk|Jungle|Native American|Cabaret|New Wave|Psychadelic|Rave|Showtunes|Trailer|Lo-Fi|Tribal|Acid Punk|Acid Jazz|Polka|Retro|Musical|Rock & Roll|Hard Rock|Folk|Folk-Rock|National Folk|Swing|Fast Fusion|Bebob|Latin|Revival|Celtic|Bluegrass|Avantgarde|Gothic Rock|Progressive Rock|Psychedelic Rock|Symphonic Rock|Slow Rock|Big Band|Chorus|Easy Listening|Acoustic|Humour|Speech|Chanson|Opera|Chamber Music|Sonata|Symphony|Booty Bass|Primus|Porn Groove|Satire|Slow Jam|Club|Tango|Samba|Folklore|Ballad|Power Ballad|Rhythmic Soul|Freestyle|Duet|Punk Rock|Drum Solo|A capella|Euro-House|Dance Hall|Goa|Drum & Bass|Club-House|Hardcore|Terror|Indie|BritPop|Negerpunk|Polsk Punk|Beat|Christian Gangsta Rap|Heavy Metal|Black Metal|Crossover|Contemporary Christian|Christian Rock|Merengue|Salsa|Thrash Metal|Anime|JPop|SynthPop").Split('|');

    public static Task<Id3Document> ReadAsync(ITagFile file, CancellationToken ct = default) => ReadCoreAsync(file, false, ct);
    public static Task<Id3Document> ReadMetadataAsync(IReadOnlyMediaFile file, CancellationToken ct = default) => ReadCoreAsync(file, false, ct, true);
    // Passive streaming artwork inspection reads only the already-cached leading tag.
    public static Task<Id3Document> ReadArtworkAsync(ITagFile file, CancellationToken ct = default) => ReadCoreAsync(file, true, ct);
    private static async Task<Id3Document> ReadCoreAsync(IReadOnlyMediaFile file, bool artworkOnly, CancellationToken ct, bool metadataOnly = false)
    {
        var fields = new Id3Fields(); var frames = new List<Id3Frame>(); var pictures = new List<Id3Artwork>();
        byte version = 3, flags = 0; long prefixLength = 0; byte[] prefix = [], body = []; byte[]? legacy = null;
        string? reason = null;
        try
        {
            if (file.Length < 4) throw new InvalidDataException("This file is too short to contain MP3 audio.");
            if (!artworkOnly && file.Length >= 128)
            {
                var tail = await ReadExactAsync(file, file.Length - 128, 128, ct);
                if (tail.AsSpan(0, 3).SequenceEqual("TAG"u8)) { legacy = tail; ReadLegacy(tail, fields); }
            }
            var header = await ReadExactAsync(file, 0, (int)Math.Min(10, file.Length), ct);
            if (header.AsSpan(0, 3).SequenceEqual("ID3"u8))
            {
                if (header.Length != 10) throw new InvalidDataException("The ID3 header is truncated.");
                version = header[3]; flags = header[5];
                var size = ReadSync(header.AsSpan(6, 4));
                var footerSize = version == 4 && (flags & 0x10) != 0 ? 10 : 0;
                prefixLength = 10L + size + footerSize;
                if (prefixLength > MaxTagBytes || prefixLength > file.Length - (legacy is null ? 0 : 128))
                    throw new InvalidDataException("The ID3 tag is truncated or exceeds the 32 MiB limit.");
                prefix = await ReadExactAsync(file, 0, (int)prefixLength, ct);
                if (version is not (3 or 4)) throw new InvalidDataException($"ID3v2.{version} is read-only. Editing supports ID3v2.3 and ID3v2.4.");
                if (header[4] != 0 || (flags & ~(version == 4 ? 0x90 : 0x80)) != 0)
                    throw new InvalidDataException("Extended headers, experimental tags, and unsupported header flags are read-only.");
                if (footerSize != 0 && (!prefix.AsSpan(prefix.Length - 10, 3).SequenceEqual("3DI"u8)
                    || !prefix.AsSpan(prefix.Length - 7, 7).SequenceEqual(header.AsSpan(3, 7))))
                    throw new InvalidDataException("The ID3 footer does not match its header.");
                body = prefix.AsSpan(10, size).ToArray();
                if (version == 3 && (flags & 0x80) != 0) body = Deunsync(body);
                for (var offset = 0; offset < body.Length;)
                {
                    if (body[offset] == 0)
                    {
                        if (body.AsSpan(offset).ContainsAnyExcept((byte)0)) throw new InvalidDataException("Nonzero data follows ID3 padding.");
                        break;
                    }
                    if (body.Length - offset < 10) throw new InvalidDataException("An ID3 frame header is truncated.");
                    var id = Encoding.ASCII.GetString(body, offset, 4);
                    if (id.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9'))) throw new InvalidDataException("An ID3 frame identifier is malformed.");
                    var frameSize = version == 4 ? ReadSync(body.AsSpan(offset + 4, 4)) : ReadSize(body.AsSpan(offset + 4, 4));
                    if (frameSize < 1 || frameSize > body.Length - offset - 10) throw new InvalidDataException("An ID3 frame size is malformed.");
                    var frameFlags = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset + 8, 2));
                    var raw = body.AsSpan(offset, 10 + frameSize).ToArray();
                    var content = raw.AsSpan(10).ToArray();
                    var supported = version == 4 ? (frameFlags & ~0x0003) == 0 : frameFlags == 0;
                    if (!supported) reason ??= "Compressed, encrypted, grouped, read-only, or unsupported frame flags cannot be safely rewritten.";
                    else
                    {
                        if (version == 4 && ((frameFlags & 2) != 0 || (flags & 0x80) != 0)) content = Deunsync(content);
                        if (version == 4 && (frameFlags & 1) != 0)
                        {
                            if (content.Length < 4 || ReadSync(content.AsSpan(0, 4)) != content.Length - 4)
                                throw new InvalidDataException("The frame data length indicator is malformed.");
                            content = content.AsSpan(4).ToArray();
                        }
                        ReadField(id, content, fields);
                        if (id == "APIC") pictures.Add(ReadPicture(content, frames.Count));
                    }
                    frames.Add(new(id, frameFlags, raw, content)); offset += raw.Length;
                }
            }
            if (!artworkOnly && !metadataOnly)
            {
                var audioEnd = file.Length - (legacy is null ? 0 : 128);
                if (prefixLength + 4 > audioEnd) throw new InvalidDataException("No MP3 audio follows the tags.");
                var audio = await ReadExactAsync(file, prefixLength, (int)Math.Min(4096, audioEnd - prefixLength), ct);
                if (!IsMp3(audio)) throw new InvalidDataException("Unsupported audio format. Tag editing supports MP3 files only.");
            }
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or ArgumentException or OverflowException)
        {
            reason = e is DecoderFallbackException ? "An ID3 text encoding is malformed; this file is read-only." : e.Message;
        }
        return new(version, flags, prefixLength, prefix, legacy, fields, pictures, frames, reason);
    }

    private static async Task<byte[]> ReadExactAsync(IReadOnlyMediaFile file, long offset, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var bytes = await file.ReadRangeAsync(offset, count, ct);
        if (bytes.Length != count) throw new InvalidDataException("The file supplied an incomplete metadata range.");
        return bytes;
    }

    private static bool IsMp3(ReadOnlySpan<byte> bytes)
    {
        // Require two contiguous MPEG Layer III frames to avoid accepting a renamed WAV or a stray sync word.
        if (bytes.Length < 4) return false;
        var length = Mp3FrameLength(bytes);
        if (length <= 0 || length + 4 > bytes.Length) return false;
        var second = Mp3FrameLength(bytes[length..]);
        return second > 0 && length + second <= bytes.Length;
    }

    private static int Mp3FrameLength(ReadOnlySpan<byte> header)
    {
        if (header[0] != 0xff || (header[1] & 0xe0) != 0xe0 || (header[1] & 6) != 2) return 0;
        var version = (header[1] >> 3) & 3; var rate = (header[2] >> 2) & 3; var bitrate = header[2] >> 4;
        if (version == 1 || rate == 3 || bitrate is 0 or 15) return 0;
        int[] rates = [44100, 48000, 32000];
        int[] bitrates = version == 3 ? [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320] : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return (version == 3 ? 144000 : 72000) * bitrates[bitrate] / (rates[rate] / (version == 3 ? 1 : version == 2 ? 2 : 4)) + ((header[2] >> 1) & 1);
    }

    private static void ReadField(string id, byte[] content, Id3Fields fields)
    {
        if (id is not ("TIT2" or "TPE1" or "TALB" or "TYER" or "TDRC" or "TCON" or "TRCK" or "TPOS")) return;
        var value = DecodeText(content);
        switch (id)
        {
            case "TIT2": fields.Title = value; break;
            case "TPE1": fields.Artist = value; break;
            case "TALB": fields.Album = value; break;
            case "TYER" or "TDRC": fields.Year = value.Length >= 4 ? value[..4] : value; break;
            case "TCON": fields.Genre = value; break;
            case "TRCK": fields.Track = value; break;
            case "TPOS": fields.Disc = value; break;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> content)
    {
        if (content.Length < 1) throw new InvalidDataException("An ID3 text frame is empty.");
        var encoding = content[0]; var data = content[1..];
        var value = DecodeString(encoding, data);
        return value.TrimEnd('\0');
    }

    private static string DecodeString(byte encoding, ReadOnlySpan<byte> data) => encoding switch
    {
        0 => Encoding.Latin1.GetString(data),
        1 when data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe => StrictUtf16.GetString(data[2..]),
        1 when data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff => StrictBigEndian.GetString(data[2..]),
        1 when data.Length == 0 => "",
        2 => StrictBigEndian.GetString(data),
        3 => StrictUtf8.GetString(data),
        _ => throw new InvalidDataException("An ID3 text encoding is unsupported or missing its byte-order mark.")
    };

    private static Id3Artwork ReadPicture(byte[] content, int index)
    {
        if (content.Length < 5) throw new InvalidDataException("An artwork frame is truncated.");
        var mimeEnd = Array.IndexOf(content, (byte)0, 1);
        if (mimeEnd < 0 || mimeEnd + 2 >= content.Length) throw new InvalidDataException("An artwork frame is malformed.");
        var mime = Encoding.Latin1.GetString(content, 1, mimeEnd - 1);
        var start = mimeEnd + 2; var end = start; var wide = content[0] is 1 or 2;
        while (end < content.Length && !(content[end] == 0 && (!wide || end + 1 < content.Length && content[end + 1] == 0))) end += wide ? 2 : 1;
        var dataStart = end + (wide ? 2 : 1);
        if (dataStart > content.Length) throw new InvalidDataException("An artwork description is unterminated.");
        return new(index, mime, content[mimeEnd + 1], DecodeString(content[0], content.AsSpan(start, end - start)), content.AsSpan(dataStart).ToArray());
    }

    public static string ArtworkMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxArtworkBytes) throw new ArgumentException("Artwork must be between 1 byte and 10 MiB.");
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && ValidPng(bytes)) return "image/png";
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9)
        {
            var dimensions = false;
            for (var offset = 2; offset + 4 < bytes.Length;)
            {
                if (bytes[offset++] != 0xff) break;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset + 3 >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker == 0xd9) break;
                var size = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (size < 2 || offset + size > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 && size >= 11)
                {
                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                    if ((long)height * width > MaxArtworkPixels) throw new ArgumentException("Artwork exceeds the supported pixel budget.");
                    if (height > 0 && width > 0 && bytes[offset + 7] is > 0 and <= 4
                        && size == 8 + 3 * bytes[offset + 7]) dimensions = true;
                }
                if (marker == 0xda)
                {
                    if (dimensions && size >= 8 && bytes[offset + 2] is > 0 and <= 4 && size == 6 + 2 * bytes[offset + 2]
                        && offset + size < bytes.Length - 2) return "image/jpeg";
                    break;
                }
                offset += size;
            }
        }
        throw new ArgumentException("Choose a valid JPEG or PNG image.");
    }

    private static bool ValidPng(ReadOnlySpan<byte> bytes)
    {
        var header = false; var image = false;
        for (var offset = 8; offset <= bytes.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) return false;
            var size = (int)length; var type = bytes.Slice(offset + 4, 4); var data = bytes.Slice(offset + 8, size);
            var crc = uint.MaxValue;
            foreach (var b in bytes.Slice(offset + 4, size + 4)) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + size, 4))) return false;
            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8) || size != 13
                    || BinaryPrimitives.ReadUInt32BigEndian(data[..4]) is 0 or > 32768
                    || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) is 0 or > 32768
                    || (long)BinaryPrimitives.ReadUInt32BigEndian(data[..4]) * BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) > MaxArtworkPixels
                    || data[10] != 0 || data[11] != 0 || data[12] > 1) return false;
                if (!(data[9] switch { 0 => data[8] is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => data[8] is 8 or 16, 3 => data[8] is 1 or 2 or 4 or 8, _ => false })) return false;
                header = true;
            }
            else if (type.SequenceEqual("IHDR"u8)) return false;
            if (type.SequenceEqual("IDAT"u8)) image |= size > 0;
            if (type.SequenceEqual("IEND"u8)) return image && size == 0 && offset + 12 == bytes.Length;
            offset += size + 12;
        }
        return false;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320 : 0);
            table[index] = value;
        }
        return table;
    }

    /// <param name="artworkIndex">Existing frame index, or -1 to add a front cover.</param>
    /// <param name="changeArtwork">False preserves every artwork frame; true replaces/removes the selected one.</param>
    public static Id3Rewrite Rewrite(Id3Document document, Id3Fields edited, bool changeArtwork = false, int artworkIndex = -1, byte[]? artwork = null)
    {
        if (document.ReadOnlyReason is not null) throw new InvalidOperationException(document.ReadOnlyReason);
        var version = document.Version; var original = document.Fields;
        var changes = new Dictionary<string, string>();
        void Change(string id, string before, string after)
        {
            if (before == after) return;
            if (after.Length > 10000 || after.Any(char.IsControl)) throw new ArgumentException("Tag text must be at most 10,000 characters without control characters.");
            changes.Add(id, after);
        }
        Change("TIT2", original.Title, edited.Title); Change("TPE1", original.Artist, edited.Artist);
        Change("TALB", original.Album, edited.Album); Change(version == 4 ? "TDRC" : "TYER", original.Year, edited.Year);
        Change("TCON", original.Genre, edited.Genre); Change("TRCK", original.Track, edited.Track); Change("TPOS", original.Disc, edited.Disc);
        if (original.Year != edited.Year && edited.Year.Length != 0 && (edited.Year.Length != 4 || !uint.TryParse(edited.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 1 or > 9999))
            throw new ArgumentException("Year must be empty or four digits from 0001 to 9999.");
        ValidateNumber(original.Track, edited.Track); ValidateNumber(original.Disc, edited.Disc);
        if (changeArtwork && artworkIndex != -1 && !document.Artwork.Any(x => x.FrameIndex == artworkIndex)) throw new ArgumentException("Artwork selection is no longer available.");
        string? artworkMime = changeArtwork && artwork is not null ? ArtworkMime(artwork) : null;
        if (changes.Count == 0 && !changeArtwork)
            return new(document.PrefixLength, document.OriginalPrefix, false, null);

        using var body = new MemoryStream(); var written = new HashSet<string>();
        for (var index = 0; index < document.Frames.Count; index++)
        {
            var frame = document.Frames[index];
            if (changeArtwork && index == artworkIndex) continue;
            // Either year spelling is replaced when the year changes; other date detail is untouched.
            var id = frame.Id is "TYER" or "TDRC" && original.Year != edited.Year ? version == 4 ? "TDRC" : "TYER" : frame.Id;
            if (changes.TryGetValue(id, out var text))
            {
                if (written.Add(id) && text.Length > 0) body.Write(RenderFrame(id, EncodeText(text, version), version, document.Flags));
            }
            else body.Write(frame.Raw);
        }
        foreach (var (id, text) in changes)
            if (written.Add(id) && text.Length > 0) body.Write(RenderFrame(id, EncodeText(text, version), version, document.Flags));
        if (artworkMime is not null)
        {
            var previous = document.Artwork.FirstOrDefault(x => x.FrameIndex == artworkIndex);
            using var picture = new MemoryStream(); picture.WriteByte(version == 4 ? (byte)3 : (byte)1);
            picture.Write(Encoding.ASCII.GetBytes(artworkMime)); picture.WriteByte(0); picture.WriteByte(previous?.PictureType ?? 3);
            var description = EncodeText(previous?.Description ?? "", version); picture.Write(description.AsSpan(1));
            picture.WriteByte(0); if (version == 3) picture.WriteByte(0);
            picture.Write(artwork!); body.Write(RenderFrame("APIC", picture.ToArray(), version, document.Flags));
        }
        // ID3v1-only fields become Unicode-capable v2 fields, including unchanged values.
        if (document.PrefixLength == 0 && document.LegacyFooter is not null)
        {
            foreach (var (id, value) in new Dictionary<string, string> { ["TIT2"] = edited.Title, ["TPE1"] = edited.Artist, ["TALB"] = edited.Album, ["TYER"] = edited.Year, ["TCON"] = edited.Genre, ["TRCK"] = edited.Track })
                if (written.Add(id) && value.Length != 0) body.Write(RenderFrame(id, EncodeText(value, version), version, document.Flags));
        }
        if (body.Length == 0)
        {
            // A valid ID3v2 tag contains at least one frame. Clearing the final
            // field/picture removes the leading tag instead of emitting padding alone.
            var clearedLegacy = document.LegacyFooter?.ToArray();
            if (clearedLegacy is not null) UpdateLegacy(clearedLegacy, original, edited);
            return new(document.PrefixLength, [], clearedLegacy is not null, clearedLegacy);
        }
        var hasFooter = version == 4 && (document.Flags & 0x10) != 0;
        if (!hasFooter) body.Write(new byte[256]);
        var rendered = body.ToArray(); if (version == 3 && (document.Flags & 0x80) != 0) rendered = Unsync(rendered);
        if (rendered.Length + 20 > MaxTagBytes) throw new ArgumentException("The edited tag exceeds the 32 MiB limit.");
        var header = new byte[10]; "ID3"u8.CopyTo(header); header[3] = version; header[5] = document.Flags; WriteSync(header.AsSpan(6, 4), rendered.Length);
        using var output = new MemoryStream(); output.Write(header); output.Write(rendered);
        if (hasFooter) { var footer = header.ToArray(); "3DI"u8.CopyTo(footer); output.Write(footer); }
        var legacy = document.LegacyFooter?.ToArray();
        if (legacy is not null) UpdateLegacy(legacy, original, edited);
        return new(document.PrefixLength, output.ToArray(), legacy is not null, legacy);
    }

    private static void ValidateNumber(string original, string value)
    {
        if (original == value || value.Length == 0) return;
        var parts = value.Split('/');
        if (parts.Length > 2 || parts.Any(x => !uint.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 999999)
            || parts.Length == 2 && uint.Parse(parts[0], CultureInfo.InvariantCulture) > uint.Parse(parts[1], CultureInfo.InvariantCulture))
            throw new ArgumentException("Track and disc must be a positive number or number/total, with number no greater than total.");
    }

    private static byte[] EncodeText(string value, byte version) => version == 4
        ? [(byte)3, ..StrictUtf8.GetBytes(value)] : [(byte)1, (byte)0xff, (byte)0xfe, ..StrictUtf16.GetBytes(value)];

    private static byte[] RenderFrame(string id, byte[] content, byte version, byte tagFlags)
    {
        var unsync = version == 4 && (tagFlags & 0x80) != 0;
        if (unsync) content = Unsync(content);
        var frame = new byte[10 + content.Length]; Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        if (version == 4) WriteSync(frame.AsSpan(4, 4), content.Length); else BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), content.Length);
        if (unsync) frame[9] = 2; content.CopyTo(frame, 10); return frame;
    }

    private static int ReadSize(ReadOnlySpan<byte> bytes)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("An ID3 frame is too large.");
    }

    private static int ReadSync(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var b in bytes) { if (b >= 128) throw new InvalidDataException("An ID3 size is not sync-safe."); value = (value << 7) | b; }
        return value;
    }

    private static void WriteSync(Span<byte> bytes, int value)
    {
        for (var index = 3; index >= 0; index--) { bytes[index] = (byte)(value & 0x7f); value >>= 7; }
    }

    private static byte[] Deunsync(byte[] bytes)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < bytes.Length; index++)
        {
            output.WriteByte(bytes[index]);
            if (bytes[index] == 0xff && index + 1 < bytes.Length && bytes[index + 1] == 0) index++;
        }
        return output.ToArray();
    }

    private static byte[] Unsync(byte[] bytes)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < bytes.Length; index++)
        {
            output.WriteByte(bytes[index]);
            if (bytes[index] == 0xff && (index + 1 == bytes.Length || bytes[index + 1] == 0 || bytes[index + 1] >= 0xe0)) output.WriteByte(0);
        }
        return output.ToArray();
    }

    private static void ReadLegacy(byte[] bytes, Id3Fields fields)
    {
        string Text(int offset, int size) => Encoding.Latin1.GetString(bytes, offset, size).TrimEnd('\0', ' ');
        fields.Title = Text(3, 30); fields.Artist = Text(33, 30); fields.Album = Text(63, 30); fields.Year = Text(93, 4);
        fields.Genre = bytes[127] < Genres.Length ? Genres[bytes[127]] : "";
        if (bytes[125] == 0 && bytes[126] != 0) fields.Track = bytes[126].ToString(CultureInfo.InvariantCulture);
    }

    private static void UpdateLegacy(byte[] bytes, Id3Fields original, Id3Fields edited)
    {
        void Text(int offset, int size, string before, string after)
        {
            if (before == after) return;
            bytes.AsSpan(offset, size).Clear();
            if (after.Length <= size && after.All(c => c <= 255)) Encoding.Latin1.GetBytes(after).CopyTo(bytes, offset);
        }
        Text(3, 30, original.Title, edited.Title); Text(33, 30, original.Artist, edited.Artist);
        Text(63, 30, original.Album, edited.Album); Text(93, 4, original.Year, edited.Year);
        if (original.Genre != edited.Genre) { var genre = Array.IndexOf(Genres, edited.Genre); bytes[127] = genre >= 0 ? (byte)genre : (byte)255; }
        if (original.Track != edited.Track && bytes[125] == 0 && bytes[126] != 0)
            bytes[126] = byte.TryParse(edited.Track.Split('/')[0], out var track) ? track : (byte)0;
    }
}
