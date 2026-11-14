using System.Globalization;
using System.Text.Json;

namespace ChannelWave.Core;

public enum LibraryItemKind { Artist, Album, Playlist, Track, Filter, Source = 5 }
public enum CollectionDisplay { Grid, List }
public sealed record LibraryItemReference(LibraryItemKind Kind, string Key, string Label);
public sealed record HomeTab(string Id, string Label, LibraryItemReference? Item = null)
{
    public bool IsDefault => Item is null;
}
public sealed record SavedLibraryFilter(Guid Id, string Name, LibrarySearchRequest Query, bool ShowInLibrary = true);
public sealed record LibraryPresentationPreferences(int Version, HomeTab[] Tabs, SavedLibraryFilter[] Filters,
    Dictionary<string, CollectionDisplay> Displays)
{
    public static HomeTab[] DefaultTabs => [new("all", "All"), new("recent", "Recently Played"), new("new", "New"), new("playlists", "Playlists"), new("liked", "Liked")];
    public static LibraryPresentationPreferences Empty => new(1, DefaultTabs, [], []);
}

/// <summary>Serializes account-owned UI writes and drains them before account cleanup.</summary>
public sealed class LibraryPresentationStore(ILibraryRepository library)
{
    public const string StateKey = "library-presentation-v1";
    private readonly SemaphoreSlim writes = new(1, 1);
    private long generation;
    private bool writable = true;
    private bool loaded = true;
    private long? requestedAccount;
    public long? Account { get; private set; }
    public LibraryPresentationPreferences Value { get; private set; } = LibraryPresentationPreferences.Empty;
    public event Action? Changed;

    public async Task BindAsync(long? account, CancellationToken ct = default)
    {
        if (Account == account && requestedAccount == account && loaded) return;
        requestedAccount = account;
        var owner = Interlocked.Increment(ref generation);
        await writes.WaitAsync(ct);
        try
        {
            if (owner != generation) return;
            Account = account;
            writable = false;
            loaded = false;
            Value = LibraryPresentationPreferences.Empty;
            if (account is { } id)
            {
                var json = await library.ReadStateAsync(id, StateKey, ct);
                if (json is not null)
                {
                    var saved = JsonSerializer.Deserialize<LibraryPresentationPreferences>(json);
                    if (saved is { Version: > 1 })
                    {
                        loaded = true;
                        throw new InvalidOperationException("These library preferences need a newer ChannelWave version.");
                    }
                    if (saved is not null) Value = Normalize(saved);
                }
            }
            if (owner != generation) return;
            writable = true;
            loaded = true;
        }
        catch (JsonException) { if (owner == generation) { Value = LibraryPresentationPreferences.Empty; writable = loaded = true; } }
        finally { writes.Release(); }
        Changed?.Invoke();
    }

    public CollectionDisplay Display(string key, CollectionDisplay fallback) => Value.Displays.GetValueOrDefault(key, fallback);
    public Task SetDisplayAsync(string key, CollectionDisplay display) => ChangeAsync(value => value with { Displays = new(value.Displays) { [key] = display } });
    public Task PinAsync(LibraryItemReference item, long? expectedAccount = null) => ChangeAsync(value => value.Tabs.Any(t => t.Item?.Kind == item.Kind && t.Item.Key == item.Key)
        ? value : value with { Tabs = [.. value.Tabs, new(Guid.NewGuid().ToString("N"), item.Label, item)] }, expectedAccount,
        item.Kind == LibraryItemKind.Source ? async account =>
        {
            if (!long.TryParse(item.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chat)
                || await library.SourceAsync(account, chat) is not { } source || source.AccountId != account)
                throw new InvalidOperationException("This source is no longer in your library.");
        } : null);

    public Task ReconcileSourceTabsAsync(long account, IReadOnlyList<Source> sources)
    {
        // Only an authoritative list for this account may remove its shortcuts.
        if (sources.Any(source => source.AccountId != account)) return Task.CompletedTask;
        var names = sources.ToDictionary(source => source.ChatId.ToString(CultureInfo.InvariantCulture), source => source.Name);
        return ChangeAsync(value =>
        {
            var tabs = value.Tabs.Where(tab => tab.Item?.Kind != LibraryItemKind.Source || names.ContainsKey(tab.Item.Key))
                .Select(tab => tab.Item is { Kind: LibraryItemKind.Source } item
                    ? tab with { Label = names[item.Key], Item = item with { Label = names[item.Key] } } : tab).ToArray();
            if (tabs.SequenceEqual(value.Tabs)) return value;
            var removed = value.Tabs.ExceptBy(tabs.Select(tab => tab.Id), tab => tab.Id).Select(tab => "home:" + tab.Id).ToHashSet();
            return value with { Tabs = tabs, Displays = value.Displays.Where(pair => !removed.Contains(pair.Key)).ToDictionary() };
        }, account);
    }
    public Task RemoveTabAsync(string id) => ChangeAsync(value => value with { Tabs = value.Tabs.Where(t => t.Id != id || t.IsDefault).ToArray() });
    public Task MoveTabAsync(string id, int destination) => ChangeAsync(value =>
    {
        var tabs = value.Tabs.ToList();
        var index = tabs.FindIndex(t => t.Id == id);
        if (index < 0) return value;
        var tab = tabs[index];
        tabs.RemoveAt(index);
        tabs.Insert(Math.Clamp(destination, 0, tabs.Count), tab);
        return value with { Tabs = tabs.ToArray() };
    });
    public Task SaveFilterAsync(string name, LibrarySearchRequest query) => ChangeAsync(value =>
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("Choose a name between 1 and 100 characters.");
        query.Validate();
        return value with { Filters = [.. value.Filters, new(Guid.NewGuid(), name, query with { Offset = 0, Limit = 100, OfflineIds = null })] };
    });
    public Task ShowFilterAsync(Guid id, bool show) => ChangeAsync(value => value with
    {
        Filters = value.Filters.Select(filter => filter.Id == id ? filter with { ShowInLibrary = show } : filter).ToArray()
    });

    public Task WriteAccountStateAsync(long account, string key, string value, CancellationToken ct = default)
        => WriteStateAsync(account, account, key, value, ct);

    public Task RememberAccountAsync(long account, CancellationToken ct = default)
        => WriteStateAsync(account, 0, "last-listening-account-v1", account.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

    private async Task WriteStateAsync(long account, long storageAccount, string key, string value, CancellationToken ct)
    {
        var owner = generation;
        await writes.WaitAsync(ct);
        try
        {
            if (owner == generation && Account == account)
                await library.SaveStateAsync(storageAccount, key, value, ct);
        }
        finally { writes.Release(); }
    }

    private async Task ChangeAsync(Func<LibraryPresentationPreferences, LibraryPresentationPreferences> change,
        long? expectedAccount = null, Func<long, Task>? validate = null)
    {
        var owner = generation;
        if (expectedAccount is { } expected && Account != expected) return;
        var account = Account ?? throw new InvalidOperationException("Connect an account before customizing your library.");
        await writes.WaitAsync();
        try
        {
            if (owner != generation || Account != account) return;
            if (!writable) throw new InvalidOperationException("These library preferences could not be loaded. They have not been changed.");
            if (validate is not null) await validate(account);
            if (owner != generation || Account != account) return;
            var changed = change(Value);
            if (ReferenceEquals(changed, Value)) return;
            var next = Normalize(changed);
            await library.SaveStateAsync(account, StateKey, JsonSerializer.Serialize(next));
            Value = next;
        }
        finally { writes.Release(); }
        Changed?.Invoke();
    }

    private static LibraryPresentationPreferences Normalize(LibraryPresentationPreferences value)
    {
        var defaults = LibraryPresentationPreferences.DefaultTabs;
        var tabs = (value.Tabs ?? []).Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id)
            && (t.Item is { } item && Enum.IsDefined(item.Kind) && !string.IsNullOrWhiteSpace(item.Key) && !defaults.Any(d => d.Id == t.Id)
                || t.Item is null && defaults.Any(d => d.Id == t.Id)))
            .Select(t => t.IsDefault ? defaults.First(d => d.Id == t.Id) : t)
            .DistinctBy(t => t.Id).DistinctBy(t => t.Item is null ? t.Id : $"{t.Item.Kind}:{t.Item.Key}").ToList();
        foreach (var tab in defaults) if (!tabs.Any(t => t.Id == tab.Id)) tabs.Add(tab);
        return new(1, tabs.ToArray(), (value.Filters ?? []).Where(f => f is not null && f.Id != Guid.Empty && f.Query?.Criteria is not null && !string.IsNullOrWhiteSpace(f.Name)).DistinctBy(f => f.Id).ToArray(),
            (value.Displays ?? []).Where(d => Enum.IsDefined(d.Value)).ToDictionary());
    }
}

public sealed record LibraryOverview(int Tracks, int Artists, int Albums);
