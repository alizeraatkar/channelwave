using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChannelWave.Platform.Browser;

public sealed class BrowserBridge(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? module;
    public Task<IJSObjectReference> ModuleAsync() => module ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/ChannelWave.Platform.Browser/channelwave.js").AsTask();
    public async ValueTask DisposeAsync() { if (module is not null) await (await module).DisposeAsync(); }
}
public sealed class BrowserTdTransport(BrowserBridge bridge) : ITdTransport
{
    private readonly Channel<JsonElement> updates = Channel.CreateUnbounded<JsonElement>();
    private DotNetObjectReference<BrowserTdTransport>? reference;
    public ChannelReader<JsonElement> Updates => updates.Reader;
    public async Task StartAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        reference ??= DotNetObjectReference.Create(this);
        await (await bridge.ModuleAsync()).InvokeVoidAsync("startTelegram", timeout.Token, reference);
    }
    [JSInvokable]
    public Task OnUpdate(string json)
    {
        using var document = JsonDocument.Parse(json); updates.Writer.TryWrite(document.RootElement.Clone()); return Task.CompletedTask;
    }
    public async Task<JsonElement> SendAsync(JsonObject request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await (await bridge.ModuleAsync()).InvokeAsync<string>("sendTelegram", timeout.Token, request.ToJsonString());
        using var document = JsonDocument.Parse(result); var value = document.RootElement.Clone(); Td.ThrowIfError(value); return value;
    }
    public async ValueTask DisposeAsync()
    {
        if (reference is null) return;
        try { await (await bridge.ModuleAsync()).InvokeVoidAsync("closeTelegram"); }
        finally { reference.Dispose(); reference = null; updates.Writer.TryComplete(); }
    }
}
public sealed partial class BrowserLibrary(BrowserBridge bridge) : ILibraryRepository, IListeningStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<Track?> SetMessageInfoAsync(long account, Guid id, long chat, long message, TelegramMessageInfo info,
        DateTimeOffset publishedAt, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("setMessageInfo", ct, account, id, chat, message,
            JsonSerializer.Serialize(info, JsonOptions), publishedAt);
        return json is null ? null : JsonSerializer.Deserialize<Track>(json, JsonOptions);
    }
    public async Task InitializeAsync(CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeVoidAsync("openLibrary", ct);
    public async Task SavePageAsync(Source source, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        if (tracks.Any(t => t.Origin.AccountId != source.AccountId || t.Origin.ChatId != source.ChatId)) throw new InvalidOperationException("Cross-account/source write rejected.");
        await (await bridge.ModuleAsync()).InvokeVoidAsync("savePage", ct, JsonSerializer.Serialize(source, JsonOptions), JsonSerializer.Serialize(tracks, JsonOptions));
    }
    public async Task ApplySourceChangesAsync(Source source, IReadOnlyList<Track> tracks, IReadOnlyCollection<long> unavailable,
        bool allUnavailable = false, CancellationToken ct = default)
        => await (await bridge.ModuleAsync()).InvokeVoidAsync("savePage", ct, JsonSerializer.Serialize(source, JsonOptions),
            JsonSerializer.Serialize(tracks, JsonOptions), unavailable, allUnavailable, true);
    public async Task<Track?> TrackAsync(long account, Guid id, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("readTrack", ct, account, id);
        return json is null ? null : JsonSerializer.Deserialize<Track>(json, JsonOptions);
    }
    public async Task<long[]> SourceMessageIdsAsync(long account, long chat, long before = 0, CancellationToken ct = default)
        => await (await bridge.ModuleAsync()).InvokeAsync<long[]>("sourceMessageIds", ct, account, chat, before);
    public async Task<Guid[]> QueueTrackIdsAsync(long account, LibrarySearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var matcher = new LibrarySearchMatcher(request.Criteria);
        var payload = JsonSerializer.Serialize(new { request.Criteria, request.Favorites, request.Chat, request.Playlist,
            request.Sort, request.Order, request.ArtistKey, request.AlbumKey, request.OfflineIds, Offset = 0, Limit = int.MaxValue, matcher.Terms, matcher.TitleTerms, matcher.ArtistTerms, idsOnly = true }, JsonOptions);
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("searchLibraryAdvanced", ct, account, payload);
        return JsonSerializer.Deserialize<Guid[]>(json, JsonOptions)!;
    }
    public async Task<IReadOnlyList<Track>> SearchAsync(long account, string query, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("searchLibrary", ct, account, Metadata.Normalize(query), offset, Math.Clamp(limit, 1, 1000));
        return JsonSerializer.Deserialize<Track[]>(json, JsonOptions)!;
    }
    public async Task<IReadOnlyList<Track>> SearchAsync(long account, LibrarySearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var matcher = new LibrarySearchMatcher(request.Criteria);
        var payload = JsonSerializer.Serialize(new { request.Criteria, request.Favorites, request.Chat, request.Playlist,
            request.Sort, request.Order, request.ArtistKey, request.AlbumKey, request.OfflineIds, request.Offset, request.Limit, matcher.Terms, matcher.TitleTerms, matcher.ArtistTerms }, JsonOptions);
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("searchLibraryAdvanced", ct, account, payload);
        return JsonSerializer.Deserialize<Track[]>(json, JsonOptions)!;
    }
    public async Task<IReadOnlyList<Source>> SourcesAsync(long account, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("readSources", ct, account);
        return JsonSerializer.Deserialize<Source[]>(json, JsonOptions)!;
    }
    public async Task<Source?> SourceAsync(long account, long chat, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("readSource", ct, account, chat);
        return json is null ? null : JsonSerializer.Deserialize<Source>(json, JsonOptions);
    }
    public async Task<Guid[]> RemoveSourceAsync(long account, long chat, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("removeSource", ct, account, chat);
        return JsonSerializer.Deserialize<Guid[]>(json, JsonOptions)!;
    }
    public async Task SetFavoriteAsync(long account, Guid id, bool favorite, CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeVoidAsync("favorite", ct, account, id, favorite);
    public async Task<Track> SetLocalMetadataAsync(long account, Guid id, string? title, string? performer, CancellationToken ct = default)
    {
        title = Metadata.ValidateOverride(title); performer = Metadata.ValidateOverride(performer);
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("setLocalMetadata", ct, account, id, title, performer);
        return JsonSerializer.Deserialize<Track>(json, JsonOptions)!;
    }
    public async Task<Track> SetArtworkAsync(long account, Guid id, ArtworkData artwork, CancellationToken ct = default)
    {
        if (artwork.Data.Length > ArtworkData.MaxBytes) throw new ArgumentException("Artwork thumbnail exceeds 512 KiB.");
        Id3Codec.ArtworkMime(artwork.Data);
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("setArtwork", ct, account, id, JsonSerializer.Serialize(artwork, JsonOptions));
        return JsonSerializer.Deserialize<Track>(json, JsonOptions)!;
    }
    public async Task<IReadOnlyList<Track>> PendingMetadataAsync(long account, DateTimeOffset now, int limit = 50, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("pendingMetadata", ct, account, now.ToUnixTimeMilliseconds(), Math.Clamp(limit, 1, 1000), EmbeddedMetadataReader.Version);
        return JsonSerializer.Deserialize<Track[]>(json, JsonOptions)!;
    }
    public async Task<Track?> SetMetadataScanAsync(long account, Guid id, string mediaKey, EmbeddedMetadata? metadata, MetadataScan scan, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("setMetadataScan", ct, account, id, mediaKey,
            JsonSerializer.Serialize(metadata, JsonOptions), JsonSerializer.Serialize(scan, JsonOptions));
        return json is null ? null : JsonSerializer.Deserialize<Track>(json, JsonOptions);
    }
    public async Task RetryMetadataAsync(long account, CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeVoidAsync("retryMetadata", ct, account);
    public async Task<CachedMetadata?> FindEmbeddedMetadataAsync(Track track, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string?>("findEmbeddedMetadata", ct,
            JsonSerializer.Serialize(track with { Artwork = null }, JsonOptions), EmbeddedMetadataReader.Version);
        return json is null ? null : JsonSerializer.Deserialize<CachedMetadata>(json, JsonOptions);
    }
    public async Task<Track[]> SaveEmbeddedMetadataAsync(Track track, CachedMetadata metadata, CancellationToken ct = default)
    {
        var json = await (await bridge.ModuleAsync()).InvokeAsync<string>("saveEmbeddedMetadata", ct,
            JsonSerializer.Serialize(track with { Artwork = null }, JsonOptions),
            JsonSerializer.Serialize(metadata with { Artwork = null }, JsonOptions), EmbeddedMetadataReader.Version);
        return JsonSerializer.Deserialize<Track[]>(json, JsonOptions)!;
    }
    public async Task SaveStateAsync(long account, string key, string value, CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeVoidAsync("saveState", ct, account, key, value);
    public async Task<string?> ReadStateAsync(long account, string key, CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeAsync<string?>("readState", ct, account, key);
    public async Task ClearAccountAsync(long account, CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeVoidAsync("clearAccount", ct, account);
    public async ValueTask DisposeAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("clearSearchCache");
}
public sealed class BrowserAudio(BrowserBridge bridge) : IAudioEngine, IAudioNotifications, IAudioRouting
{
    public async Task<AudioOutputs> GetOutputsAsync(CancellationToken ct) =>
        await (await bridge.ModuleAsync()).InvokeAsync<AudioOutputs>("getAudioOutputs", ct);
    public async Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        var message = await (await bridge.ModuleAsync()).InvokeAsync<string?>("selectAudioOutput", ct, id);
        if (message is not null) throw new IOException(message);
    }
    private DotNetObjectReference<BrowserAudio>? reference;
    private Func<long, string, double, Task>? notify;
    public async Task ObserveAsync(long playbackId, Func<long, string, double, Task> callback, CancellationToken ct)
    {
        notify = callback; reference ??= DotNetObjectReference.Create(this);
        await (await bridge.ModuleAsync()).InvokeVoidAsync("observeAudio", ct, reference, playbackId);
    }
    [JSInvokable] public Task OnAudioSignal(long playbackId, string action, double value) => notify?.Invoke(playbackId, action, value) ?? Task.CompletedTask;
    public async Task SetTrackAsync(Track track)
    {
        string? artwork = null;
        try { if (track.Artwork?.Matches(track) == true) artwork = track.Artwork.DataUri; }
        catch (ArgumentException) { }
        await (await bridge.ModuleAsync()).InvokeVoidAsync("setAudioMetadata", track.Title, track.Performer, track.Origin.SourceName, artwork);
    }
    public async Task LoadAsync(PreparedMedia media, CancellationToken ct)
    {
        if (!media.IsBrowserUrl) throw new InvalidOperationException("A native file path cannot be played in a browser.");
        try { await (await bridge.ModuleAsync()).InvokeVoidAsync("loadAudio", ct, media.Location); }
        catch (JSException e) when (e.Message.Contains("cannot decode", StringComparison.OrdinalIgnoreCase)) { throw new UnplayableTrackException("This browser cannot decode this recording."); }
    }
    public async Task PlayAsync(CancellationToken ct) => await (await bridge.ModuleAsync()).InvokeVoidAsync("playAudio", ct);
    public async Task PauseAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("pauseAudio");
    public async Task UnloadAsync() => await (await bridge.ModuleAsync()).InvokeVoidAsync("clearAudio");
    public async Task SeekAsync(double seconds) => await (await bridge.ModuleAsync()).InvokeVoidAsync("seekAudio", seconds);
    public async Task<AudioState?> StateAsync(CancellationToken ct = default) => await (await bridge.ModuleAsync()).InvokeAsync<AudioState>("audioState", ct);
    public async Task SetVolumeAsync(double volume) => await (await bridge.ModuleAsync()).InvokeVoidAsync("volumeAudio", volume);
    public async Task ReleaseUnusedAsync(PreparedMedia media) => await (await bridge.ModuleAsync()).InvokeVoidAsync("releaseUnusedAudio", media.Location);
    public async ValueTask DisposeAsync() { await (await bridge.ModuleAsync()).InvokeVoidAsync("clearAudio"); reference?.Dispose(); }
}
