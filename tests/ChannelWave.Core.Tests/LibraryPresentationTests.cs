using System.Text.Json;
using System.Reflection;
using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChannelWave.Core.Tests;

[Collection("Library search")]
public sealed class LibraryPresentationTests
{
    [Fact]
    public async Task Customization_survives_reload_without_removing_filters_or_other_accounts()
    {
        await WithLibrary(async db =>
        {
            var store = new LibraryPresentationStore(db);
            await store.BindAsync(7);
            var track = new LibraryItemReference(LibraryItemKind.Track, Guid.NewGuid().ToString(), "Song");
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.PinAsync(track)));
            Assert.Single(store.Value.Tabs, t => !t.IsDefault);
            await store.MoveTabAsync("liked", 0);
            await store.RemoveTabAsync("liked");
            await store.SetDisplayAsync("home:new", CollectionDisplay.List);
            await store.SaveFilterAsync("Saved favorites", new(new(LikedOnly: true, SavedOnly: true), Offset: 20, OfflineIds: [Guid.NewGuid()]));
            var filter = Assert.Single(store.Value.Filters);
            await store.PinAsync(new(LibraryItemKind.Filter, filter.Id.ToString(), filter.Name));
            await store.ShowFilterAsync(filter.Id, false);
            await store.BindAsync(8);
            Assert.Equal(5, store.Value.Tabs.Length);
            Assert.Empty(store.Value.Filters);
            await store.BindAsync(7);
            Assert.Equal("liked", store.Value.Tabs[0].Id);
            Assert.Equal(CollectionDisplay.List, store.Display("home:new", CollectionDisplay.Grid));
            Assert.Equal(CollectionDisplay.Grid, store.Display("home:liked", CollectionDisplay.Grid));
            Assert.False(Assert.Single(store.Value.Filters).ShowInLibrary);
            Assert.Contains(store.Value.Tabs, t => t.Item?.Key == filter.Id.ToString());
            Assert.Null(store.Value.Filters[0].Query.OfflineIds);
            Assert.Equal(0, store.Value.Filters[0].Query.Offset);
            await store.ShowFilterAsync(filter.Id, true);
            Assert.True(store.Value.Filters[0].ShowInLibrary);
        });
    }

    [Fact]
    public async Task Account_detachment_drains_writes_before_erasure()
    {
        await WithLibrary(async db =>
        {
            var store = new LibraryPresentationStore(db);
            await store.BindAsync(7);
            var writes = Enumerable.Range(0, 30).Select(i => store.SetDisplayAsync("home:" + i, CollectionDisplay.List)).ToArray();
            var pointer = store.RememberAccountAsync(7);
            await store.BindAsync(null);
            await db.ClearAccountAsync(7);
            await Task.WhenAll(writes);
            await pointer;
            Assert.Null(await db.ReadStateAsync(7, LibraryPresentationStore.StateKey));
            Assert.Null(await db.ReadStateAsync(0, "last-listening-account-v1"));
            Assert.Null(store.Account);
        });
    }

    [Fact]
    public async Task Erasure_removes_only_the_deleted_accounts_resume_pointer()
    {
        await WithLibrary(async db =>
        {
            await db.SaveStateAsync(0, "last-listening-account-v1", "8");
            await db.ClearAccountAsync(7);
            Assert.Equal("8", await db.ReadStateAsync(0, "last-listening-account-v1"));
            await db.ClearAccountAsync(8);
            Assert.Null(await db.ReadStateAsync(0, "last-listening-account-v1"));
        });
    }

    [Fact]
    public async Task Cancelled_customization_read_can_retry_for_the_same_account()
    {
        var repository = DispatchProxy.Create<ILibraryRepository, DelayedStateLibrary>();
        var fake = (DelayedStateLibrary)(object)repository;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Read = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };
        var store = new LibraryPresentationStore(repository);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var bind = store.BindAsync(7, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bind);
        fake.Read = _ => Task.FromResult<string?>(null);
        await store.BindAsync(7, TestContext.Current.CancellationToken);
        await store.SetDisplayAsync("home:new", CollectionDisplay.List);
        Assert.Equal(CollectionDisplay.List, store.Display("home:new", CollectionDisplay.Grid));
        Assert.NotNull(fake.Saved);
    }

    [Fact]
    public async Task Future_version_is_preserved_and_blocks_writes()
    {
        await WithLibrary(async db =>
        {
            var json = JsonSerializer.Serialize(LibraryPresentationPreferences.Empty with { Version = 99 });
            await db.SaveStateAsync(7, LibraryPresentationStore.StateKey, json);
            var store = new LibraryPresentationStore(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.BindAsync(7, TestContext.Current.CancellationToken));
            await store.BindAsync(7);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetDisplayAsync("home:new", CollectionDisplay.List));
            Assert.Equal(json, await db.ReadStateAsync(7, LibraryPresentationStore.StateKey));
        });
    }

    private static async Task WithLibrary(Func<SqliteLibrary, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "channelwave-presentation-" + Guid.NewGuid() + ".sqlite");
        try { await using var db = new SqliteLibrary(path); await db.InitializeAsync(TestContext.Current.CancellationToken); await test(db); }
        finally { SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Source_shortcuts_persist_reconcile_names_and_remove_only_missing_sources()
    {
        await WithLibrary(async db =>
        {
            var source = new Source(7, -100, "Night Radio");
            await db.SavePageAsync(source, [], TestContext.Current.CancellationToken);
            var store = new LibraryPresentationStore(db);
            await store.BindAsync(7);
            var item = new LibraryItemReference(LibraryItemKind.Source, "-100", source.Name);
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.PinAsync(item, 7)));
            var tab = Assert.Single(store.Value.Tabs, tab => tab.Item?.Kind == LibraryItemKind.Source);
            await store.MoveTabAsync(tab.Id, 1);
            await store.SetDisplayAsync("home:" + tab.Id, CollectionDisplay.List);
            await store.PinAsync(new(LibraryItemKind.Artist, "artist", "Artist"));
            await store.BindAsync(8);
            Assert.Equal(5, store.Value.Tabs.Length);
            await store.ReconcileSourceTabsAsync(7, []);
            await store.PinAsync(item, 7);
            Assert.Equal(5, store.Value.Tabs.Length);
            await store.BindAsync(7);
            Assert.Equal(tab.Id, store.Value.Tabs[1].Id);
            var renamed = source with { Name = "رادیو شب · Night Radio" };
            await store.ReconcileSourceTabsAsync(7, [renamed]);
            Assert.Equal(renamed.Name, store.Value.Tabs[1].Label);
            Assert.Equal(renamed.Name, store.Value.Tabs[1].Item!.Label);
            Assert.Equal(CollectionDisplay.List, store.Display("home:" + tab.Id, CollectionDisplay.Grid));
            var json = await db.ReadStateAsync(7, LibraryPresentationStore.StateKey);
            var notifications = 0;
            store.Changed += () => notifications++;
            await store.ReconcileSourceTabsAsync(7, [renamed]);
            await store.ReconcileSourceTabsAsync(7, [source with { AccountId = 8 }]);
            Assert.Equal(0, notifications);
            Assert.Equal(json, await db.ReadStateAsync(7, LibraryPresentationStore.StateKey));
            await db.RemoveSourceAsync(7, source.ChatId, TestContext.Current.CancellationToken);
            await store.ReconcileSourceTabsAsync(7, []);
            Assert.DoesNotContain(store.Value.Tabs, tab => tab.Item?.Kind == LibraryItemKind.Source);
            Assert.Equal(5, store.Value.Tabs.Count(tab => tab.IsDefault));
            Assert.Contains(store.Value.Tabs, tab => tab.Item?.Kind == LibraryItemKind.Artist);
            Assert.False(store.Value.Displays.ContainsKey("home:" + tab.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PinAsync(item, 7));
            await db.SavePageAsync(source, [], TestContext.Current.CancellationToken);
            await store.ReconcileSourceTabsAsync(7, [source]);
            Assert.DoesNotContain(store.Value.Tabs, tab => tab.Item?.Kind == LibraryItemKind.Source);
        });
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_source_keys_cannot_be_pinned(string key)
    {
        await WithLibrary(async db =>
        {
            var store = new LibraryPresentationStore(db);
            await store.BindAsync(7);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.PinAsync(new(LibraryItemKind.Source, key, "Source"), 7));
            Assert.Equal(5, store.Value.Tabs.Length);
        });
    }

    [Fact]
    public async Task Switching_accounts_during_source_validation_does_not_save_a_stale_shortcut()
    {
        var repository = DispatchProxy.Create<ILibraryRepository, DelayedStateLibrary>();
        var fake = (DelayedStateLibrary)(object)repository;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<Source?>();
        fake.Source = () => { entered.SetResult(); return finish.Task; };
        var store = new LibraryPresentationStore(repository);
        await store.BindAsync(7, TestContext.Current.CancellationToken);
        var pin = store.PinAsync(new(LibraryItemKind.Source, "-100", "Night Radio"), 7);
        await entered.Task;
        var bind = store.BindAsync(8, TestContext.Current.CancellationToken);
        finish.SetResult(new(7, -100, "Night Radio"));
        await Task.WhenAll(pin, bind);
        Assert.Null(fake.Saved);
        Assert.Equal(8, store.Account);
        Assert.Equal(5, store.Value.Tabs.Length);
    }

    public class DelayedStateLibrary : DispatchProxy
    {
        public Func<CancellationToken, Task<string?>> Read { get; set; } = _ => Task.FromResult<string?>(null);
        public string? Saved { get; private set; }
        public Func<Task<Source?>> Source { get; set; } = () => Task.FromResult<Source?>(null);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(ILibraryRepository.ReadStateAsync)) return Read((CancellationToken)args![2]!);
            if (method?.Name == nameof(ILibraryRepository.SourceAsync)) return Source();
            if (method?.Name == nameof(ILibraryRepository.SaveStateAsync)) { Saved = (string)args![2]!; return Task.CompletedTask; }
            throw new NotSupportedException(method?.Name);
        }
    }
}
