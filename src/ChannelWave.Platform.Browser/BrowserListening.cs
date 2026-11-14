using ChannelWave.Core;
using Microsoft.JSInterop;
using System.Text.Json;

namespace ChannelWave.Platform.Browser;

public sealed partial class BrowserLibrary
{
    public async Task<ListeningState> ReadListeningStateAsync(long account, CancellationToken ct = default)
        => JsonSerializer.Deserialize<ListeningState>(await (await bridge.ModuleAsync()).InvokeAsync<string>("readListeningState", ct, account), JsonOptions)!;
    public async Task<long> CommitListeningAsync(ListeningCommit commit, CancellationToken ct = default)
        => await (await bridge.ModuleAsync()).InvokeAsync<long>("commitListening", ct,
            JsonSerializer.Serialize(new { commit, buckets = commit.Events.SelectMany(ListeningBucket.From).ToArray() }, JsonOptions));
    public async Task<ListeningEvent?> ListenAsync(long account, Guid id, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("readListen", ct, account, id.ToString("N"));
        return json is null ? null : JsonSerializer.Deserialize<ListeningEvent>(json, JsonOptions);
    }
    public async Task<ListeningPage> HistoryAsync(long account, ListeningQuery query, CancellationToken ct = default)
        => JsonSerializer.Deserialize<ListeningPage>(await (await bridge.ModuleAsync()).InvokeAsync<string>("listeningHistory", ct, account,
            JsonSerializer.Serialize(query, JsonOptions)), JsonOptions)!;
    public async Task<ListeningBucket[]> BucketsAsync(long account, long from, long to, string? after, CancellationToken ct = default)
        => JsonSerializer.Deserialize<ListeningBucket[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("listeningBuckets", ct, account, from, to, after), JsonOptions)!;
    public async Task<ListeningState> DeleteListeningAsync(long account, Guid? id, long? olderThan = null, CancellationToken ct = default)
        => JsonSerializer.Deserialize<ListeningState>(await (await bridge.ModuleAsync()).InvokeAsync<string>("deleteListening", ct, account, id?.ToString("N"), olderThan), JsonOptions)!;
}
