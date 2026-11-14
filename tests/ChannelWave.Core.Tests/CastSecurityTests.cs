using System.Net;
using ChannelWave.Host.Maui;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class CastSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    public void Cast_listener_rejects_non_private_receivers_before_binding(string address)
    {
        // Arrange.
        var file = Path.GetTempFileName();
        try
        {
            // Act / Assert.
            Assert.Throws<IOException>(() => new CastMediaLease(IPAddress.Parse(address), file, "audio/mpeg"));
        }
        finally { File.Delete(file); }
    }
}
