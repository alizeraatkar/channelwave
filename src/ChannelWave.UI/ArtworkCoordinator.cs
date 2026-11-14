using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.JSInterop;

namespace ChannelWave.UI;

/// <summary>Bounded, account-scoped artwork work independent of playback and audio transfers.</summary>
public sealed class ArtworkCoordinator(TelegramSession session, ILibraryRepository library, IRuntimeHost host, IJSRuntime js,
    PlaybackCoordinator playback) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(2, 2);
    private readonly Dictionary<string, Task<ArtworkData?>> requests = [];
    private readonly Dictionary<string, (Track Track, ArtworkData Image)> images = [];
    private readonly Dictionary<(long, long), Task<byte[]?>> channels = [];
    private CancellationTokenSource lifetime = new();
    private IJSObjectReference? module;
    private long? account;
    private bool initialized;
    public event Action<long, Guid>? Changed;
    private static string Key(Track track) => $"{track.Origin.AccountId}:{track.Id}:{Metadata.MediaKey(track)}";

    private void Initialize()
    {
        if (initialized) return; initialized = true; account = session.AccountId; session.Changed += SessionChanged;
    }
    private void SessionChanged()
    {
        if (session.AccountId != account) { CancelAll(); account = session.AccountId; Changed?.Invoke(0, Guid.Empty); }
    }
    public ArtworkData? Cached(Track track)
    {
        if (track.Origin.Protected || session.AccountId is { } active && active != track.Origin.AccountId) return null;
        lock (sync) return images.TryGetValue(Key(track), out var image) ? image.Image : track.Artwork?.Matches(track) == true ? track.Artwork : null;
    }
    public Task<ArtworkData?> GetAsync(Track track, bool offline, bool allowNetwork, bool refresh, bool useMetadataScan, CancellationToken ct)
    {
        Initialize();
        if (track.Origin.Protected) return Task.FromResult<ArtworkData?>(null);
        var cached = Cached(track);
        if (cached is not null && (!refresh || cached.Kind == ArtworkKind.Embedded)) return Task.FromResult<ArtworkData?>(cached);
        var key = Key(track) + $":{offline}:{allowNetwork}:{refresh}:{useMetadataScan}";
        lock (sync)
        {
            if (!requests.TryGetValue(key, out var pending) || refresh && pending.IsCompleted)
            {
                if (requests.Count >= 256) foreach (var finished in requests.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) requests.Remove(finished);
                pending = LoadAsync(track, offline, allowNetwork, refresh, useMetadataScan, lifetime.Token); requests[key] = pending;
            }
            return pending.WaitAsync(ct);
        }
    }
    private async Task<ArtworkData?> LoadAsync(Track track, bool offline, bool allowNetwork, bool refresh, bool useMetadataScan, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                // A completed metadata scan already checked the audio for embedded artwork.
                // Keep Telegram's thumbnail and source-photo fallback available.
                var current = offline || allowNetwork
                    ? await library.TrackAsync(track.Origin.AccountId, track.Id, ct) : null;
                if (current is { } && (current.Origin.Protected || !Metadata.IsSameMedia(track, current))) return null;
                var useStoredTags = useMetadataScan || current is not null && CachedMetadata.FromTrack(current) is not null;
                if (current?.Artwork is { Kind: ArtworkKind.Embedded } embeddedArtwork && embeddedArtwork.Matches(current)) return embeddedArtwork;
                if (offline)
                {
                    if (useStoredTags) return Cached(current ?? track);
                    await using var file = await host.OpenTagFileAsync(track, null, ct);
                    var bytes = ArtworkData.Embedded(await Id3Codec.ReadArtworkAsync(file, ct));
                    return bytes is null ? Cached(track) : await StoreAsync(track, bytes, ArtworkKind.Embedded, ct);
                }
                if (!allowNetwork || session.AccountId != track.Origin.AccountId) return Cached(track);
                var telegram = new TelegramArtwork(session); var resolved = await telegram.ResolveAsync(track, ct);
                if (resolved is null) return null;
                var local = resolved.Audio.GetProperty("local");
                if (!useStoredTags && (refresh || local.Flag("is_downloading_completed") || local.Id("downloaded_size") >= 10))
                {
                    try
                    {
                        var embedded = await host.ReadEmbeddedArtworkAsync(track, resolved.Audio, ct);
                        if (embedded is not null) return await StoreAsync(track, embedded, ArtworkKind.Embedded, ct);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested) { /* Missing cached ranges do not initiate audio transfers. */ }
                }
                if (Cached(track)?.Kind == ArtworkKind.Embedded) return Cached(track);
                if (resolved.Thumbnail is { } thumbnail)
                {
                    try
                    {
                        var file = await telegram.CompleteImageAsync(track.Origin.AccountId, thumbnail, ct);
                        return await StoreAsync(track, await host.ReadArtworkFileAsync(file, ct), ArtworkKind.AudioThumbnail, ct);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
                var source = (track.Origin.AccountId, track.Origin.ChatId);
                Task<byte[]?> sourceTask;
                lock (sync)
                {
                    if (!channels.TryGetValue(source, out sourceTask!))
                    {
                        if (channels.Count >= 64) channels.Clear();
                        sourceTask = ReadChannelAsync(telegram, track, ct); channels[source] = sourceTask;
                    }
                }
                var photo = await sourceTask;
                return photo is null ? Cached(track) : await StoreAsync(track, photo, ArtworkKind.ChannelPhoto, ct);
            }
            finally { gate.Release(); }
        }
        catch (Exception) { return Cached(track); } // Artwork failure must not stop listening or browsing.
    }
    private async Task<byte[]?> ReadChannelAsync(TelegramArtwork telegram, Track track, CancellationToken ct)
    {
        try
        {
            var photo = await telegram.ChannelPhotoAsync(track, ct);
            return photo is null ? null : await host.ReadArtworkFileAsync(await telegram.CompleteImageAsync(track.Origin.AccountId, photo.Value, ct), ct);
        }
        catch (Exception) { return null; }
    }
    private async Task<ArtworkData?> StoreAsync(Track track, byte[] bytes, ArtworkKind kind, CancellationToken ct)
    {
        Id3Codec.ArtworkMime(bytes); ct.ThrowIfCancellationRequested();
        module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, "./_content/ChannelWave.UI/artwork.js");
        var thumbnail = await module.InvokeAsync<byte[]>("thumbnailArtwork", ct, bytes); ct.ThrowIfCancellationRequested();
        var image = new ArtworkData(Metadata.MediaKey(track), kind, thumbnail); image.Validate(track);
        var updated = await library.SetArtworkAsync(track.Origin.AccountId, track.Id, image, ct); ct.ThrowIfCancellationRequested();
        if (updated.Artwork is null) return null;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested(); images[Key(track)] = (track, updated.Artwork);
            while (images.Count > ArtworkData.MaxCachedImages) images.Remove(images.MinBy(x => x.Value.Image.CachedAt).Key);
        }
        Changed?.Invoke(track.Origin.AccountId, track.Id);
        await playback.UpdateTrackMetadataAsync(updated, ct);
        return updated.Artwork;
    }
    public async Task ObserveEmbeddedBytesAsync(Track track, byte[] bytes, CancellationToken ct)
    {
        Initialize(); if (track.Origin.Protected) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try { await StoreAsync(track, bytes, ArtworkKind.Embedded, linked.Token); } catch (Exception) { }
    }
    public void CancelAll()
    {
        lock (sync) { lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); requests.Clear(); images.Clear(); channels.Clear(); }
    }
    public void RemoveSource(Source source)
    {
        // Cancel pending work before source erasure; canonical store writes also reject removed rows.
        lock (sync)
        {
            lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); requests.Clear(); channels.Remove((source.AccountId, source.ChatId));
            foreach (var key in images.Where(x => x.Value.Track.Origin.AccountId == source.AccountId && x.Value.Track.Origin.ChatId == source.ChatId).Select(x => x.Key).ToArray()) images.Remove(key);
        }
    }
    public async ValueTask DisposeAsync()
    {
        session.Changed -= SessionChanged; lifetime.Cancel();
        if (module is not null) await module.DisposeAsync(); lifetime.Dispose();
    }
}
