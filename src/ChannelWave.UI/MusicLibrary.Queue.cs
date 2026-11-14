using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.AspNetCore.Components;

namespace ChannelWave.UI;

public partial class MusicLibrary
{
    public async Task HomeActionAsync(HomeMusicAction action)
    {
        actionError = null;
        switch (action.Action)
        {
            case "favorite": await FavoriteAsync(action.Track); break;
            case "offline": await ToggleOfflineAsync(action.Track); break;
            case "download": await DownloadAsync(action.Track); break;
            case "add": await Add(action.Track); break;
            case "playlist": ChoosePlaylists(action.Track); break;
            case "edit": await EditAsync(action.Track); break;
            case "next": await PlayNextAsync(action.Track); break;
            case "queue": await EnqueueAsync(action.Track); break;
        }
        if (actionError is not null) throw new InvalidOperationException(actionError);
        await InvokeAsync(StateHasChanged);
    }
    [Parameter] public Func<CancellationToken, Task<Guid[]>>? ReadQueue { get; set; }
    private bool queueOpen;
    private void OpenQueue() => queueOpen = true;

    private async Task PlayCollectionAsync(Track? track, bool offlineOnly, bool shuffle = false)
    {
        var startingPlayback = false;
        try
        {
            var context = ViewportContext;
            var ids = ReadQueue is null ? Visible.Select(t => t.Id).ToArray() : await ReadQueue(lifetime.Token);
            if (context != ViewportContext || lifetime.IsCancellationRequested) return;
            var account = track?.Origin.AccountId ?? Session.AccountId ?? OfflineTracks.FirstOrDefault()?.Origin.AccountId;
            if (account is null || ids.Length == 0) { actionError = "No songs match this collection. Clear a filter or add music."; return; }
            var selected = track?.Id ?? ids[shuffle ? Random.Shared.Next(ids.Length) : 0];
            var resolver = Media; var owner = account.Value;
            playingOfflineOnly = offlineOnly; playError = actionError = null; followingPlayback = previewTrack is not null;
            startingPlayback = true;
            await Playback.StartQueueAsync(owner, ids, selected, (id, ct) => resolver.ResolveAsync(owner, id, ct),
                (next, ct) => resolver.PrepareAsync(next, offlineOnly, ct), offlineOnly, shuffle, lifetime.Token);
            Playback.SetContext(new(View, CollectionLabel ?? (View == "playlist" ? Playlist.Name : View == "offline" ? "Offline" : SearchMode ? "Search results" : "Your library"), View == "playlist" ? Playlist.Id : null));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (startingPlayback && current is not null) playError = Td.SafeError(e);
            else actionError = Td.SafeError(e);
        }
    }

    private Task PlayNextAsync(Track track) => AddQueueAsync(track, true);
    private Task EnqueueAsync(Track track) => AddQueueAsync(track, false);
    private Task AddQueueAsync(Track track, bool next)
    {
        try
        {
            var resolver = Media; var owner = track.Origin.AccountId; var offline = View == "offline";
            Playback.Enqueue(track, (id, ct) => resolver.ResolveAsync(owner, id, ct),
                (item, ct) => resolver.PrepareAsync(item, offline, ct), next, offline);
            feedback = next ? $"{track.Title} will play next." : $"Added {track.Title} to queue.";
        }
        catch (Exception e) { actionError = Td.SafeError(e); }
        return Task.CompletedTask;
    }
}
