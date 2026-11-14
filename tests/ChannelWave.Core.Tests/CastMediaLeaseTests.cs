using ChannelWave.Host.Maui;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class CastMediaLeaseTests
{
    [Theory]
    [InlineData("bytes=0-0", 0, 0)]
    [InlineData("bytes=5-", 5, 9)]
    [InlineData("bytes=-2", 8, 9)]
    [InlineData("bytes=-20", 0, 9)]
    [InlineData("bytes=8-20", 8, 9)]
    public void Valid_ranges_never_extend_past_the_recording(string header, long expectedStart, long expectedEnd)
    {
        Assert.True(CastMediaLease.TryRange(header, 10, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("bytes=10-")]
    [InlineData("bytes=2-1")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=9223372036854775808-")]
    [InlineData("items=0-1")]
    public void Unsupported_ranges_are_rejected(string header) =>
        Assert.False(CastMediaLease.TryRange(header, 10, out _, out _));
}
