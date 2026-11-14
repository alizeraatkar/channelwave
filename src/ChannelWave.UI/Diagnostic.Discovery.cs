using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ChannelWave.Core;

namespace ChannelWave.UI;

public partial class Diagnostic
{
    private string browseMode = "tracks";
    private string? browseGroup, browseLabel;
    private LibraryOrder generalOrder = LibraryOrder.AddedNewest;
    private LibraryGroup[] browseGroups = [];
    private Dictionary<Guid, Track> groupCovers = [];
    private long? browseAccount;
    private bool routeHasBrowse, homeHasMusic;
    private long homeRevision => HomeState.Revision;
    private RenderFragment GroupResults => builder => {
        builder.OpenComponent<LibraryGroups>(0); builder.AddAttribute(1, "Mode", browseMode); builder.AddAttribute(2, "Groups", browseGroups);
        builder.AddAttribute(3, "Covers", groupCovers); builder.AddAttribute(6, "Display", Display); builder.AddAttribute(4, "Loading", libraryLoadState is LibraryLoadState.Refreshing or LibraryLoadState.LoadingMore);
        builder.AddAttribute(5, "OnOpen", EventCallback.Factory.Create<LibraryGroup>(this, OpenBrowseGroup));
        builder.AddAttribute(7, "CanLoadMore", canLoadMore); builder.AddAttribute(8, "OnLoadMore", EventCallback.Factory.Create(this, LoadMoreMusic)); builder.CloseComponent();
    };
    private bool GroupListing => page is "tracks" or "search" && browseMode is "artists" or "albums" && browseGroup is null;
    private sealed record BrowsePreferences(string Mode, string? Group, string? Label, LibraryOrder Order, LibrarySearchCriteria Criteria, long? Source, string HomeTab = "all", Guid? PlaylistId = null, PlaylistSort PlaylistOrder = PlaylistSort.Custom, Guid? FilterId = null);
    private BrowsePreferences BrowseState => new(browseMode, browseGroup, browseLabel, generalOrder, searchCriteria, selectedChannel, homeTab, selectedPlaylist, playlistSort, savedFilterId);
    private string DiscoveryContext => JsonSerializer.Serialize(BrowseState);
    private string CurrentRoute => page is "home" or "playlist" or "search" or "tracks" or "favorites" or "offline"
        ? page + "~" + Uri.EscapeDataString(DiscoveryContext) : page;
    private EventCallback<MusicCreditDestination> CreditNavigation => EventCallback.Factory.Create<MusicCreditDestination>(this, OpenMusicCredit);
    private async Task OpenMusicCredit(MusicCreditDestination destination)
    {
        if (!cleanupVerified || cleanupPending || disposed || (Session.AccountId ?? Playback.ListeningAccount) != destination.Account) return;
        if (music is not null) await music.CloseMusicDialogsAsync();
        var state = new BrowsePreferences(destination.Kind, destination.Key, destination.Label,
            destination.Kind == "albums" ? LibraryOrder.Album : LibraryOrder.Title, new(), null);
        await Navigate((Session.AccountId is null ? "offline" : "tracks") + "~" + Uri.EscapeDataString(JsonSerializer.Serialize(state)));
    }

    private void ApplyBrowseState(BrowsePreferences state)
    {
        if (state.Criteria is null) throw new ArgumentException("Missing library filter state.");
        var criteria = state.Criteria with { Text = state.Criteria.Text ?? "", Title = state.Criteria.Title ?? "", Artist = state.Criteria.Artist ?? "" };
        criteria.Validate();
        browseMode = state.Mode is "artists" or "albums" or "playlists" ? state.Mode : "tracks";
        browseGroup = browseMode == "tracks" ? null : state.Group;
        browseLabel = browseGroup is null && state.FilterId is null ? null : state.Label;
        generalOrder = Enum.IsDefined(state.Order) ? state.Order : LibraryOrder.AddedNewest;
        searchCriteria = criteria; selectedChannel = state.Source; homeTab = state.HomeTab; selectedPlaylist = state.PlaylistId;
        playlistSort = Enum.IsDefined(state.PlaylistOrder) ? state.PlaylistOrder : PlaylistSort.Custom;
        savedFilterId = state.FilterId;
    }
    private async Task RestoreBrowseAsync(long account, CancellationToken ct)
    {
        if (browseAccount == account) return;
        browseGroups = []; groupCovers = []; homeHasMusic = false;
        var saved = await Library.ReadStateAsync(account, "library-browse-v1", ct);
        if (!routeHasBrowse)
        {
            browseMode = "tracks"; browseGroup = browseLabel = null; generalOrder = LibraryOrder.AddedNewest;
            if (saved is not null)
                try { if (JsonSerializer.Deserialize<BrowsePreferences>(saved) is { } state) ApplyBrowseState(state with { Criteria = searchCriteria, Source = selectedChannel, HomeTab = homeTab, PlaylistId = selectedPlaylist, FilterId = savedFilterId }); }
                catch (Exception e) when (e is JsonException or ArgumentException) { }
        }
        browseAccount = account; routeHasBrowse = false;
    }
    private LibrarySearchRequest DiscoveryRequest(LibraryContext context, Guid[]? offlineIds, int offset, int limit)
        => new(GroupListing && page == "search" ? context.Criteria with { Text = "" } : context.Criteria, context.View == "favorites", context.Chat,
            context.View == "playlist" ? ActivePlaylist.Tracks : null, context.Sort, offset, limit,
            context.View == "playlist" ? null : context.Order,
            context.Page is "tracks" or "offline" or "search" && context.Mode == "artists" ? context.Group : null,
            context.Page is "tracks" or "offline" or "search" && context.Mode == "albums" ? context.Group : null,
            context.Criteria.SavedOnly ? offlineIds ?? [] : null, GroupText: GroupListing && page == "search" ? context.Criteria.Text : null);

    private async Task ChangeBrowseMode(string mode)
    {
        browseMode = mode; browseGroup = browseLabel = null; loadedLimit = 100; focusPage = true;
        generalOrder = mode == "artists" ? LibraryOrder.Title : LibraryOrder.AddedNewest;
        await LoadLibraryAsync();
    }
    private async Task OpenBrowseGroup(LibraryGroup group)
    {
        if (navigation is not null) await navigation.InvokeVoidAsync("capturePage");
        browseGroup = group.Key; browseLabel = group.Label; loadedLimit = 100; focusPage = true;
        generalOrder = browseMode == "albums" ? LibraryOrder.Album : LibraryOrder.Title;
        await LoadLibraryAsync();
    }
    private Task ChangeGeneralOrder(LibraryOrder order) { generalOrder = order; loadedLimit = 100; return LoadLibraryAsync(); }
    private Task PreviewHome(Track track) { music?.PreviewHomeTrack(track); return Task.CompletedTask; }
    private Task HomeAction(HomeMusicAction action) => music?.HomeActionAsync(action) ?? Task.CompletedTask;
    private Task PlayBrowse(bool shuffle) => music?.PlayBrowseAsync(shuffle) ?? Task.CompletedTask;
}
