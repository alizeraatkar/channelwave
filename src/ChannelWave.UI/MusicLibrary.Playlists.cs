using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.AspNetCore.Components;

namespace ChannelWave.UI;

public partial class MusicLibrary
{
    [Parameter] public IReadOnlyList<Playlist> Playlists { get; set; } = [];
    [Parameter] public PlaylistSort PlaylistSort { get; set; }
    [Parameter] public EventCallback<PlaylistEdit> OnPlaylistEdit { get; set; }
    [Parameter] public EventCallback<Guid> OnPlaylistSelect { get; set; }
    [Parameter] public EventCallback<PlaylistSort> OnPlaylistSort { get; set; }
    [Parameter] public Func<Task<IReadOnlyList<Track>>>? ReadPlaylist { get; set; }
    private readonly HashSet<Guid> selectedTracks = [];
    private Guid[]? membershipTracks;
    private HashSet<Guid> playlistMembership = [];
    private Dictionary<Guid, int> playlistPositions = [];
    private bool selecting, savingPlaylistEdit, playlistPickerOpen, loadingPlaylist;
    private Guid[] pickerTracks = [];
    private string? pickerError;
    private LibraryDialog? playlistPicker;
    private (long? Account, string View, Guid Playlist, bool Visible)? selectionContext;

    private void ResetPlaylistContext()
    {
        // Playlist operations replace the track array. Build membership once per
        // version instead of scanning a large playlist for every rendered row.
        if (!ReferenceEquals(membershipTracks, Playlist.Tracks))
        {
            membershipTracks = Playlist.Tracks;
            playlistMembership = Playlist.Tracks.ToHashSet();
            playlistPositions = Playlist.Tracks.Select((id, index) => (id, index)).DistinctBy(x => x.id).ToDictionary(x => x.id, x => x.index);
        }
        var next = (Session.AccountId, View, Playlist.Id, ShowContent);
        if (selectionContext != next)
        {
            selecting = playlistPickerOpen = false;
            selectedTracks.Clear(); pickerTracks = []; pickerError = null;
        }
        selectionContext = next;
    }
    private void ToggleSelection() { selecting = !selecting; selectedTracks.Clear(); }
    private void SelectTrack(Guid id) { if (!selectedTracks.Add(id)) selectedTracks.Remove(id); }
    private void SelectShown() { foreach (var track in Visible) selectedTracks.Add(track.Id); }
    private Task ChoosePlaylist(Track track) { ChoosePlaylists(track); return Task.CompletedTask; }
    private void ChoosePlaylists(Track? track)
    {
        pickerTracks = track is null ? selectedTracks.ToArray() : [track.Id];
        pickerError = null; playlistPickerOpen = true;
    }
    private void ClosePlaylistPicker() { playlistPickerOpen = false; pickerError = null; }
    private async Task ManagePlaylists()
    {
        if (playlistPicker is not null) await playlistPicker.CloseAsync();
        await ViewChanged.InvokeAsync("playlist");
    }
    private async Task AddToPlaylist(Playlist list)
    {
        if (savingPlaylistEdit || pickerTracks.Length == 0) return;
        savingPlaylistEdit = true; pickerError = null; feedback = actionError = null;
        try
        {
            var count = pickerTracks.Count(id => !list.Tracks.Contains(id));
            await OnPlaylistEdit.InvokeAsync(new(PlaylistAction.Add, list.Id, Tracks: pickerTracks));
            if (playlistPicker is not null) await playlistPicker.CloseAsync();
            selectedTracks.Clear();
            feedback = $"Added {count} {(count == 1 ? "song" : "songs")} to {list.Name}.";
        }
        catch (Exception e) { pickerError = Td.SafeError(e); }
        finally { savingPlaylistEdit = false; }
    }
    private bool CanMove(Track track, int direction)
    {
        var index = playlistPositions.GetValueOrDefault(track.Id, -1);
        return PlaylistSort == PlaylistSort.Custom && !Criteria.IsActive && channel.Length == 0
            && index >= 0 && index + direction >= 0 && index + direction < Playlist.Tracks.Length;
    }
    private Task MoveTrack(Track track, int direction) => SavePlaylistEdit(new(PlaylistAction.Move, Playlist.Id, TrackId: track.Id, Direction: direction), "Playlist order saved.");
    private Task RemoveSelected() => SavePlaylistEdit(new(PlaylistAction.Remove, Playlist.Id, Tracks: selectedTracks.ToArray()), "Selected songs removed from this playlist.");
    private async Task SavePlaylistEdit(PlaylistEdit edit, string message)
    {
        if (savingPlaylistEdit) return;
        savingPlaylistEdit = true; feedback = actionError = null;
        try { await OnPlaylistEdit.InvokeAsync(edit); selectedTracks.Clear(); feedback = message; }
        catch (Exception e) { actionError = Td.SafeError(e); }
        finally { savingPlaylistEdit = false; }
    }
    private async Task PlayPlaylist(bool shuffle)
    {
        if (loadingPlaylist) return;
        loadingPlaylist = true; playError = actionError = null;
        try
        {
            await PlayCollectionAsync(null, false, shuffle);
        }
        catch (Exception e) { actionError = Td.SafeError(e); }
        finally { loadingPlaylist = false; }
    }
}
