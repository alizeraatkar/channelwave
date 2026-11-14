using ChannelWave.Telegram;
using ChannelWave.Telegram.Native;
try
{
    await using var session = new TelegramSession(new NativeTdTransport(args.FirstOrDefault()));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Console.WriteLine(await session.ProbeAsync(timeout.Token));
    var state = await session.Transport.SendAsync(Td.Query("getAuthorizationState"), timeout.Token);
    Console.WriteLine($"Authorization state: {state.Type()}");
    return state.Type() == "authorizationStateWaitTdlibParameters" ? 0 : 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Native initialization failed ({e.GetType().Name}). Verify the pinned tdjson binary and its runtime dependencies. No login was attempted.");
    return 1;
}
