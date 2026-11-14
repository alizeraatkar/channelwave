using System.Text.Json;
using ChannelWave.Core;
using ChannelWave.Telegram;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ChannelWave.UI;

public partial class Diagnostic
{
    private LibraryPresentationStore presentation = null!;
    private LibraryOverview? overview;
    private TelegramIdentity? profile;
    private string homeTab = "all";
    private bool focusHomeTab;
    private bool saveFilterOpen;
    private string filterName = "";
    private string? customizationError;
    private int restoredItemLimit = 100;
    private Guid? savedFilterId;
    private AppRoute Route => new(page);
    private string DisplayKey => savedFilterId is { } filterId ? $"filter:{filterId}"
        : page == "search" ? $"search:{browseMode}" : page == "playlist" ? $"playlist:{selectedPlaylist ?? playlist.Id}"
        : $"category:{page}:{browseMode}:{browseGroup}";
    private CollectionDisplay Display => presentation.Display(DisplayKey, page == "playlists" || GroupListing || page == "search" && browseMode == "playlists" ? CollectionDisplay.Grid : CollectionDisplay.List);
    private string ProfileName => (Session.Identity ?? profile)?.Name ?? "Your account";
    private string? ProfileUsername => (Session.Identity ?? profile)?.Username;
    private string ProfileInitials => string.Concat(ProfileName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(Initial));
    private RenderFragment PlaylistResults => builder =>
    {
        builder.OpenComponent<PlaylistCatalog>(0);
        builder.AddAttribute(1, "Playlists", AllPlaylists);
        builder.AddAttribute(2, "Query", searchCriteria.Text);
        builder.AddAttribute(3, "SearchMode", true);
        builder.AddAttribute(4, "OnSelect", EventCallback.Factory.Create<Guid>(this, SelectPlaylist));
        builder.AddAttribute(5, "Display", Display);
        builder.CloseComponent();
    };

    private void PresentationChanged()
    {
        if (!disposed) _ = InvokeAsync(() =>
        {
            if (HomeState.Account == presentation.Account)
            {
                var ids = presentation.Value.Tabs.Select(tab => tab.Id).ToHashSet();
                HomeState.RetainTabs(ids);
                if (!ids.Contains(homeTab))
                {
                    homeTab = HomeState.SelectedTab = "all";
                    focusHomeTab = page == "home";
                }
            }
            StateHasChanged();
        });
    }
    private async Task CacheProfileIdentityAsync(long account, CancellationToken ct)
    {
        if (Session.Identity is not { } identity || identity.AccountId != account || identity == profile || presentation.Account != account) return;
        try
        {
            await presentation.WriteAccountStateAsync(account, "profile-identity-v1", JsonSerializer.Serialize(identity), ct);
            if (!disposed && !cleanupPending && Session.Identity == identity && presentation.Account == account) profile = identity;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { if (!disposed && !cleanupPending) customizationError = Td.SafeError(e); }
    }
    private async Task RefreshPresentationAsync(long account, CancellationToken ct)
    {
        HomeState.Bind(account);
        await presentation.BindAsync(account, ct);
        var counts = await Library.OverviewAsync(account, ct);
        var copies = page == "library" && Host.CanSaveOffline ? await Host.OfflineCopiesAsync(account, ct) : null;
        var identity = Session.Identity;
        if (identity?.AccountId == account)
        {
            if (profile != identity) await presentation.WriteAccountStateAsync(account, "profile-identity-v1", JsonSerializer.Serialize(identity), ct);
        }
        else
        {
            var json = await Library.ReadStateAsync(account, "profile-identity-v1", ct);
            try { identity = json is null ? null : JsonSerializer.Deserialize<TelegramIdentity>(json); }
            catch (JsonException) { identity = null; }
        }
        if (cleanupPending || disposed || presentation.Account != account || (Session.AccountId ?? Playback.ListeningAccount) != account) return;
        presentationDirty = false;
        overview = counts; homeHasMusic = counts.Tracks > 0; profile = identity?.AccountId == account ? identity : null;
        if (copies is not null) offlineCopies = copies;
    }
    private async Task SelectHomeTab(string id)
    {
        if (navigation is not null) { await navigation.InvokeVoidAsync("captureHomeScroll"); await navigation.InvokeVoidAsync("capturePage"); }
        homeTab = HomeState.SelectedTab = id; focusPage = false; focusHomeTab = true;
    }
    private async Task ChangeDisplay(CollectionDisplay value)
    {
        try { await presentation.SetDisplayAsync(DisplayKey, value); }
        catch (Exception e) { customizationError = e.Message; }
    }
    private async Task Back()
    {
        if (navigation is not null && await navigation.InvokeAsync<bool>("goBack")) return;
        await NavigateBackAsync();
    }
    [JSInvokable] public Task NavigateBackAsync() => Navigate(page == "playlist" ? "playlists"
        : page is "tracks" or "offline" && browseGroup is not null
            ? "tracks~" + Uri.EscapeDataString(JsonSerializer.Serialize(BrowseState with { Group = null, Label = null })) : Route.Parent);
    private Task OpenCategory(string destination)
    {
        if (destination is "artists" or "albums")
            return Navigate("tracks~" + Uri.EscapeDataString(JsonSerializer.Serialize(new BrowsePreferences(destination, null, null, LibraryOrder.Title, new(), null))));
        return Navigate(destination);
    }
    private async Task OpenItem(LibraryItemReference item)
    {
        if (item.Kind == LibraryItemKind.Filter)
        {
            var filter = presentation.Value.Filters.FirstOrDefault(f => f.Id.ToString() == item.Key);
            if (filter is not null) await OpenSavedFilter(filter);
            return;
        }
        if (item.Kind == LibraryItemKind.Playlist && Guid.TryParse(item.Key, out var id)) { await SelectPlaylist(id); return; }
        if (item.Kind == LibraryItemKind.Track && Guid.TryParse(item.Key, out var trackId) && presentation.Account is { } account)
        {
            if (await Library.TrackAsync(account, trackId, lifetime.Token) is { } track) await PreviewHome(track);
            return;
        }
        await OpenMusicCredit(new(presentation.Account ?? 0, item.Kind == LibraryItemKind.Album ? "albums" : "artists", item.Key, item.Label));
    }
    private async Task PinCurrentCollection()
    {
        LibraryItemReference? item = page == "playlist" ? new(LibraryItemKind.Playlist, ActivePlaylist.Id.ToString(), ActivePlaylist.Name)
            : browseGroup is not null ? new(browseMode == "albums" ? LibraryItemKind.Album : LibraryItemKind.Artist, browseGroup, browseLabel ?? browseGroup) : null;
        if (item is null) return;
        try { await presentation.PinAsync(item); } catch (Exception e) { customizationError = e.Message; }
    }
    private async Task OpenSavedFilter(SavedLibraryFilter filter)
    {
        var query = filter.Query;
        var state = new BrowsePreferences(query.AlbumKey is not null ? "albums" : query.ArtistKey is not null ? "artists" : "tracks",
            query.AlbumKey ?? query.ArtistKey, filter.Name, query.Order ?? LibraryOrder.AddedNewest, query.Criteria, query.Chat,
            PlaylistId: query.PlaylistId, PlaylistOrder: query.Sort, FilterId: filter.Id);
        await Navigate((query.PlaylistId is null ? "tracks" : "playlist") + "~" + Uri.EscapeDataString(JsonSerializer.Serialize(state)));
    }
    private async Task SaveFilter()
    {
        try
        {
            var query = DiscoveryRequest(CurrentContext, null, 0, 100) with
            {
                Criteria = searchCriteria with { LikedOnly = searchCriteria.LikedOnly || page == "favorites", SavedOnly = searchCriteria.SavedOnly || page == "offline" },
                GroupText = null, Playlist = null, PlaylistId = page == "playlist" ? ActivePlaylist.Id : null
            };
            await presentation.SaveFilterAsync(filterName, query);
            saveFilterOpen = false; filterName = "";
        }
        catch (Exception e) { customizationError = e.Message; }
    }
    private async Task SearchKind(string kind)
    {
        browseMode = kind; browseGroup = browseLabel = null; loadedLimit = 100;
        await LoadLibraryAsync();
    }
}
