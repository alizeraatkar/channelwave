using ChannelWave.Telegram;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class TelegramProxyTests
{
    [Fact]
    public async Task Proxy_waits_for_initialization_and_is_enabled_before_phone_sign_in()
    {
        // Arrange: TDLib cannot accept addProxy until parameter initialization completes.
        var initialized = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxyConfigured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport();
        transport.AsyncOverride = (query, _) => query["@type"]!.GetValue<string>() == "setTdlibParameters" ? initialized.Task : null;
        transport.Override = query =>
        {
            if (query["@type"]!.GetValue<string>() == "addProxy") proxyConfigured.TrySetResult();
            return null;
        };
        await using var session = new TelegramSession(transport);
        var configuration = new TelegramConfiguration(1, new string('0', 32), "test-only", "test-only", "")
        {
            HttpProxy = new("10.0.2.2", 10808)
        };
        var token = TestContext.Current.CancellationToken;

        // Act
        await session.ConnectAsync(configuration, token);
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() == "addProxy");
        initialized.SetResult(JsonSerializer.SerializeToElement(Td.Query("ok")));
        await proxyConfigured.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // Assert: the pinned schema expects a nested proxy object and transparent HTTP CONNECT.
        var request = Assert.Single(transport.Requests, q => q["@type"]!.GetValue<string>() == "addProxy");
        Assert.True(request["enable"]!.GetValue<bool>());
        Assert.Equal("proxy", request["proxy"]!["@type"]!.GetValue<string>());
        Assert.Equal("10.0.2.2", request["proxy"]!["server"]!.GetValue<string>());
        Assert.Equal(10808, request["proxy"]!["port"]!.GetValue<int>());
        Assert.Equal("proxyTypeHttp", request["proxy"]!["type"]!["@type"]!.GetValue<string>());
        Assert.False(request["proxy"]!["type"]!["http_only"]!.GetValue<bool>());
        Assert.Null(session.Error);
    }

    [Theory]
    [InlineData("", 10808)]
    [InlineData("10.0.2.2", 0)]
    [InlineData("10.0.2.2", 65536)]
    public async Task Invalid_proxy_is_rejected_before_starting_Telegram(string server, int port)
    {
        // Arrange
        var transport = new ScriptedTransport();
        await using var session = new TelegramSession(transport);
        var configuration = new TelegramConfiguration(1, new string('0', 32), "test-only", "test-only", "")
        {
            HttpProxy = new(server, port)
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(configuration, TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(transport.Requests);
    }
}
