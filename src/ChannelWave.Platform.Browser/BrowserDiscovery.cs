using ChannelWave.Core;
using Microsoft.JSInterop;
using System.Text.Json;

namespace ChannelWave.Platform.Browser;

public sealed partial class BrowserLibrary
{
    public async Task<LibraryOverview> OverviewAsync(long account, CancellationToken ct = default)
        => JsonSerializer.Deserialize<LibraryOverview>(await (await bridge.ModuleAsync()).InvokeAsync<string>("libraryOverview", ct, account), JsonOptions)!;
    public async Task<RecentTrack[]> RecentTracksPageAsync(long account, int offset, int limit, CancellationToken ct = default)
        => JsonSerializer.Deserialize<RecentTrack[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("recentListeningTracks", ct, account, Math.Clamp(limit, 1, 1000), Math.Max(0, offset)), JsonOptions)!;
    public async Task<LibraryGroup[]> GroupsAsync(long account, LibrarySearchRequest request, string kind, CancellationToken ct = default)
    {
        request.Validate();
        var matcher = new LibrarySearchMatcher(request.Criteria);
        var json = JsonSerializer.Serialize(new { request.Criteria, request.Favorites, request.Chat, request.Playlist,
            request.Sort, request.Order, request.ArtistKey, request.AlbumKey, request.OfflineIds,
            request.Offset, request.Limit, request.GroupText, matcher.Terms, matcher.TitleTerms, matcher.ArtistTerms, groups = kind }, JsonOptions);
        return JsonSerializer.Deserialize<LibraryGroup[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("searchLibraryAdvanced", ct, account, json), JsonOptions)!;
    }
    public async Task<RecentTrack[]> RecentTracksAsync(long account, int limit = 6, CancellationToken ct = default)
        => JsonSerializer.Deserialize<RecentTrack[]>(await (await bridge.ModuleAsync()).InvokeAsync<string>("recentListeningTracks", ct, account, Math.Clamp(limit, 1, 100)), JsonOptions)!;
}
