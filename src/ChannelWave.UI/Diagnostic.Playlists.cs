using System.Text.Json;
using ChannelWave.Core;
using Microsoft.AspNetCore.Components;

namespace ChannelWave.UI;

public partial class Diagnostic
{
    private Playlist[] playlists = [];
    private Guid? selectedPlaylist;
    private long? playlistAccount;
    private PlaylistSort playlistSort;
    private bool playlistSaving;
    private (long Account, string Json, Playlist Value)? parsedPlaylist;
    private (long Account, string Json, Playlist[] Value)? parsedPlaylists;
    private Playlist ActivePlaylist => playlists.FirstOrDefault(p => p.Id == selectedPlaylist)
        ?? (selectedPlaylist is null || selectedPlaylist == playlist.Id ? playlist : new(selectedPlaylist.Value, "Unavailable playlist", []));
    private IReadOnlyList<Playlist> AllPlaylists => [playlist, .. playlists];

    private async Task SelectPlaylist(Guid id)
    {
        if (playlistSaving) return;
        selectedPlaylist = id;
        playlistSort = PlaylistSort.Custom;
        await Navigate("playlist");
    }

    private Task SortPlaylist(PlaylistSort value)
    {
        playlistSort = value;
        loadedLimit = 100;
        return LoadLibraryAsync();
    }

    private async Task<IReadOnlyList<Track>> ReadPlaylistTracks()
    {
        if (Session.AccountId is not { } account || cleanupPending) return [];
        var list = ActivePlaylist;
        return await Library.SearchAsync(account, new LibrarySearchRequest(searchCriteria, Chat: selectedChannel, Playlist: list.Tracks,
            Sort: playlistSort, Limit: Math.Max(1, list.Tracks.Length)), lifetime.Token);
    }
    private async Task<IReadOnlyList<Track>> ReadOfflinePlaylistSnapshot()
    {
        if (Session.AccountId is not { } account || cleanupPending) return [];
        var members = ActivePlaylist.Tracks;
        return await Library.SearchAsync(account, new LibrarySearchRequest(new(), Playlist: members,
            Limit: Math.Max(1, members.Length)), lifetime.Token);
    }

    private async Task<Guid[]> ReadQueueTrackIdsAsync(CancellationToken ct)
    {
        if (cleanupPending || disposed) return [];
        var context = CurrentContext;
        var savedIds = (await Host.OfflineCopiesAsync(context.Account, ct)).Where(c => c.Health == OfflineHealth.Saved).Select(c => c.Track.Id).ToArray();
        var request = DiscoveryRequest(context, savedIds, 0, int.MaxValue);
        if (context.View == "offline") return LibrarySearch.Select(offlineTracks.Select(LibrarySearchRow.FromTrack), request, ct);
        if (context.Account is not { } account) return [];
        var ids = await Library.QueueTrackIdsAsync(account, request, ct);
        return context == CurrentContext ? ids : [];
    }

    private void ResetPlaylists()
    {
        parsedPlaylist = null; parsedPlaylists = null;
        playlist = Playlists.Create("Listening list");
        playlists = [];
        selectedPlaylist = null;
        playlistAccount = null;
        playlistSort = PlaylistSort.Custom;
    }

    private async Task EditPlaylist(PlaylistEdit edit)
    {
        if (playlistSaving || busy || cleanupPending || Session.AccountId is not { } account)
            throw new InvalidOperationException("Wait for the current operation to finish before changing playlists.");
        playlistSaving = busy = true;
        try
        {
            // Read the durable version before each edit; background source removal can prune it.
            var legacyJson = await Library.ReadStateAsync(account, "playlist", lifetime.Token);
            var legacy = legacyJson is null ? playlist : JsonSerializer.Deserialize<Playlist>(legacyJson)!;
            var json = await Library.ReadStateAsync(account, "playlists", lifetime.Token);
            var lists = json is null ? [] : JsonSerializer.Deserialize<Playlist[]>(json)!;
            var original = edit.PlaylistId == legacy.Id ? legacy : lists.FirstOrDefault(p => p.Id == edit.PlaylistId);
            Playlist? changed;
            switch (edit.Action)
            {
                case PlaylistAction.Create:
                    changed = Playlists.Create(edit.Name ?? "");
                    break;
                case PlaylistAction.Duplicate:
                    changed = Playlists.Duplicate(original ?? throw new InvalidOperationException("This playlist no longer exists."), edit.Name ?? "");
                    break;
                default:
                    if (original is null) throw new InvalidOperationException("This playlist no longer exists.");
                    changed = edit.Action switch
                    {
                        PlaylistAction.Rename => Playlists.Rename(original, edit.Name ?? ""),
                        PlaylistAction.Delete when original.Id != legacy.Id => null,
                        PlaylistAction.Clear => original with { Tracks = [] },
                        PlaylistAction.Add => Playlists.AddRange(original, edit.Tracks ?? []),
                        PlaylistAction.Remove => Playlists.RemoveRange(original, edit.Tracks ?? []),
                        PlaylistAction.Move => Playlists.Move(original, Array.IndexOf(original.Tracks, edit.TrackId),
                            Array.IndexOf(original.Tracks, edit.TrackId) + edit.Direction),
                        _ => throw new InvalidOperationException("The listening list cannot be deleted.")
                    };
                    break;
            }
            if (edit.Action == PlaylistAction.Add && edit.Tracks is { Length: > 0 } ids)
            {
                var owned = await Library.ReadViewAsync(account, "", false, null, ids, ids.Length, lifetime.Token);
                if (owned.Count != ids.Distinct().Count()) throw new InvalidOperationException("Some selected songs are no longer in this account's library. Refresh and try again.");
            }
            if (Session.AccountId != account || cleanupPending) throw new OperationCanceledException();
            var isLegacy = original?.Id == legacy.Id && edit.Action is not (PlaylistAction.Create or PlaylistAction.Duplicate);
            var next = lists.Where(p => p.Id != edit.PlaylistId || edit.Action is PlaylistAction.Create or PlaylistAction.Duplicate).ToList();
            if (!isLegacy && changed is not null) next.Add(changed);
            // Preserve collection order when editing an existing list.
            if (!isLegacy && original is not null && changed is not null && edit.Action is not (PlaylistAction.Create or PlaylistAction.Duplicate))
            {
                next.Remove(changed);
                next.Insert(Array.FindIndex(lists, p => p.Id == original.Id), changed);
            }
            await Library.SaveStateAsync(account, isLegacy ? "playlist" : "playlists",
                isLegacy ? JsonSerializer.Serialize(changed!) : JsonSerializer.Serialize(next), lifetime.Token);
            if (Session.AccountId != account || cleanupPending) return;
            ++refreshVersion;
            if (isLegacy) playlist = changed!; else playlists = next.ToArray();
            HomeState.Invalidate();
            if (edit.Action is PlaylistAction.Create or PlaylistAction.Duplicate)
            {
                selectedPlaylist = changed!.Id;
                playlistSort = PlaylistSort.Custom;
                await Navigate("playlist");
            }
            else
            {
                if (changed is null && selectedPlaylist == edit.PlaylistId) selectedPlaylist = null;
                await Refresh();
            }
        }
        finally { playlistSaving = busy = false; await InvokeAsync(StateHasChanged); }
    }
}
