using System.Buffers.Binary;
using ChannelWave.Core;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class ArtworkSecurityTests
{
    [Fact]
    public void Large_declared_pixel_count_is_rejected_before_platform_decoding()
    {
        // Arrange: a structurally valid, CRC-correct header within the byte/dimension
        // limits formerly accepted a billion pixels. Pixel allocation must never start.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");
        Assert.Equal("image/png", Id3Codec.ArtworkMime(png));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 32768);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 32768);
        uint crc = uint.MaxValue;
        foreach (var value in png.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), crc ^ uint.MaxValue);

        // Act / Assert: shared validation protects all hosts and metadata paths.
        Assert.Throws<ArgumentException>(() => Id3Codec.ArtworkMime(png));
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0x80, 0, 0x80, 0, 1, 1, 0x11, 0,
            0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0, 42, 0xff, 0xd9];
        Assert.Throws<ArgumentException>(() => Id3Codec.ArtworkMime(jpeg));
    }
}
