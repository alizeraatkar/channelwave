using ChannelWave.Telegram;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class SecuritySignoutTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TelegramConfiguration Configuration => new(1, new string('0', 32), "saved-profile", "saved-files", "test-only-key");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_initializes_saved_profile_without_identity_or_background_updates_and_waits_for_closure(bool offline)
    {
        // Arrange: opening the encrypted profile reaches ready, but closure is deliberately delayed.
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedTransport { OfflineLogout = offline };
        transport.Override = query =>
        {
            if (query["@type"]!.GetValue<string>() == "setTdlibParameters")
                transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
            return null;
        };
        transport.AsyncOverride = (query, _) =>
        {
            var type = query["@type"]!.GetValue<string>();
            if (type != (offline ? "destroy" : "logOut")) return null;
            requested.TrySetResult();
            return Task.FromResult(JsonSerializer.SerializeToElement(Td.Query("ok")));
        };
        await using var session = new TelegramSession(transport);
        var published = new List<string>();
        session.Update += update => published.Add(update.Type());

        // Act: a ready session during cleanup must never start ordinary account restoration.
        var recovery = session.RecoverLogOutAsync(Configuration, Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Assert: receiving an ok response is insufficient to erase session storage.
        Assert.False(recovery.IsCompleted);
        Assert.Null(session.AccountId);
        Assert.False(session.IsResolvingAccount);
        Assert.Empty(published);
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() == "getMe");
        var parameters = Assert.Single(transport.Requests, q => q["@type"]!.GetValue<string>() == "setTdlibParameters");
        Assert.Equal(Configuration.DatabaseDirectory, parameters["database_directory"]!.GetValue<string>());
        Assert.Equal(Configuration.DatabaseEncryptionKey, parameters["database_encryption_key"]!.GetValue<string>());

        transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateClosed"))));
        Assert.Equal(!offline, await recovery.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("closed", session.Authorization.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Configuration, Token));
    }

    [Fact]
    public async Task Recovery_does_not_report_erasure_when_profile_initialization_fails_and_can_retry()
    {
        // Arrange: a profile that cannot yet be opened must not be treated as destroyed.
        var unavailable = true;
        var transport = new ScriptedTransport();
        transport.Override = query =>
        {
            if (query["@type"]!.GetValue<string>() != "setTdlibParameters") return null;
            if (unavailable) throw new IOException("Synthetic saved-profile initialization failure");
            transport.Push(Td.Query("updateAuthorizationState", ("authorization_state", Td.Query("authorizationStateReady"))));
            return Td.Query("ok");
        };
        await using var session = new TelegramSession(transport);

        // Act
        await Assert.ThrowsAsync<IOException>(() => session.RecoverLogOutAsync(Configuration, Token));

        // Assert, then recover the same pending cleanup after the transient failure clears.
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() is "logOut" or "destroy" or "getMe");
        unavailable = false;
        Assert.True(await session.RecoverLogOutAsync(Configuration, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("closed", session.Authorization.State);
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() == "getMe");
    }

    [Fact]
    public async Task Recovery_rejects_unverified_engine_before_opening_saved_profile()
    {
        // Arrange
        var transport = new ScriptedTransport { WrongRevision = true };
        await using var session = new TelegramSession(transport);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RecoverLogOutAsync(Configuration, Token));

        // Assert
        Assert.DoesNotContain(transport.Requests, q => q["@type"]!.GetValue<string>() is "setTdlibParameters" or "logOut" or "destroy" or "getMe");
        Assert.Null(session.AccountId);
    }
}
