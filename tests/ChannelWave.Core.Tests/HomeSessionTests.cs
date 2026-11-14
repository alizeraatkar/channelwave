using ChannelWave.UI;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class HomeSessionTests
{
    private static HomeCollectionSnapshot Snapshot(bool more = false) => new([], [], more);

    [Fact]
    public async Task Removing_a_tab_discards_cached_rows_and_scroll_and_resets_selection()
    {
        var state = new HomeSessionState();
        var removed = state.Get(700, "source", false, "query");
        await state.ReadAsync(removed, false, (_, _) => Task.FromResult(Snapshot()));
        state.SelectedTab = "source";
        state.RememberScroll(700, "source:Grid", 400);
        state.RememberScroll(700, "new:Grid", 200);
        state.RetainTabs(new HashSet<string> { "all", "new" });
        Assert.Equal("all", state.SelectedTab);
        Assert.Equal(0, state.Scroll("source:Grid"));
        Assert.Equal(200, state.Scroll("new:Grid"));
        Assert.NotSame(removed, state.Get(700, "source", false, "query"));
    }

    [Fact]
    public async Task Metadata_patches_default_rows_without_reading_but_rechecks_custom_query_membership()
    {
        var session = new HomeSessionState();
        var track = new ChannelWave.Core.Track(Guid.NewGuid(), new(700, 1, 1, "Source", false),
            new("remote", "unique", 1, "song.mp3", "audio/mpeg", 100), "Title", "Artist", "Title", "Artist", 60, DateTimeOffset.UnixEpoch);
        var initial = new HomeCollectionSnapshot([track with { Favorite = true }], []);
        var defaults = session.Get(700, "new", false, "query");
        var custom = session.Get(700, "artist-pin", false, "query");
        await session.ReadAsync(defaults, false, (_, _) => Task.FromResult(initial));
        await session.ReadAsync(custom, false, (_, _) => Task.FromResult(initial));

        session.UpdateTracks([track with { Title = "Updated title" }]);
        await session.ReadAsync(defaults, false, (_, _) => throw new InvalidOperationException("Metadata should patch default rows"));
        var customReads = 0;
        await session.ReadAsync(custom, false, (_, _) => { customReads++; return Task.FromResult(Snapshot()); });

        Assert.Null(defaults.Error);
        Assert.Equal("Updated title", Assert.Single(defaults.Snapshot!.Items).Title);
        Assert.True(Assert.Single(defaults.Snapshot.Items).Favorite);
        Assert.Equal(1, customReads);
        Assert.Empty(custom.Snapshot!.Items);

        session.UpdateTracks([track with { Title = "Local correction", TitleOverride = "Local correction" }]);
        session.UpdateTracks([track with { Title = "Older scan", EmbeddedMetadata = new(Title: "Older scan") }], automatic: true);
        Assert.Equal("Local correction", Assert.Single(defaults.Snapshot.Items).Title);
        Assert.True(Assert.Single(defaults.Snapshot.Items).Favorite);
    }

    [Fact]
    public async Task Concurrent_views_share_one_read_and_warm_views_do_not_read_again()
    {
        var session = new HomeSessionState();
        var entry = session.Get(700, "new", false, "query");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HomeCollectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<HomeCollectionSnapshot> Read(HomeCollectionSnapshot? _, CancellationToken ct)
        { Interlocked.Increment(ref calls); started.TrySetResult(); return finish.Task; }

        var first = session.ReadAsync(entry, false, Read);
        await started.Task;
        var second = session.ReadAsync(session.Get(700, "new", false, "query"), false, Read);
        finish.SetResult(Snapshot());
        await Task.WhenAll(first, second);
        await session.ReadAsync(entry, false, Read);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.NotNull(entry.Snapshot);
        Assert.False(entry.Loading);
    }

    [Fact]
    public async Task Invalidated_read_cannot_overwrite_a_newer_snapshot_even_if_adapter_ignores_cancellation()
    {
        var session = new HomeSessionState();
        var entry = session.Get(700, "new", false, "query");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new TaskCompletionSource<HomeCollectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = session.ReadAsync(entry, false, (_, _) => { started.SetResult(); return old.Task; });
        await started.Task;

        session.Invalidate();
        var fresh = Snapshot(true);
        await session.ReadAsync(entry, false, (_, _) => Task.FromResult(fresh));
        old.SetResult(Snapshot());
        await stale;

        Assert.Same(fresh, entry.Snapshot);
        Assert.False(entry.Loading);
    }

    [Fact]
    public async Task Account_change_cancels_reads_and_erases_selected_tab_scroll_and_cached_rows()
    {
        var session = new HomeSessionState();
        var entry = session.Get(700, "new", false, "query");
        session.SelectedTab = "new";
        session.RememberScroll(700, "new:List", 1200);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HomeCollectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.ReadAsync(entry, false, (_, ct) => { started.SetResult(ct); return finish.Task; });
        var token = await started.Task;

        session.Bind(null);
        session.Bind(701);
        finish.SetResult(Snapshot());
        await pending;

        Assert.True(token.IsCancellationRequested);
        Assert.Null(entry.Snapshot);
        Assert.Equal("all", session.SelectedTab);
        Assert.Equal(0, session.Scroll("new:List"));
        session.RememberScroll(700, "new:List", 800);
        Assert.Equal(0, session.Scroll("new:List"));
        Assert.Null(session.Get(701, "new", false, "query").Snapshot);
        Assert.Null(session.Get(700, "new", false, "query").Snapshot);
    }

    [Fact]
    public async Task Failed_page_keeps_visible_rows_and_can_retry_while_recent_invalidation_preserves_other_tabs()
    {
        var session = new HomeSessionState();
        var entry = session.Get(700, "new", false, "query");
        var initial = Snapshot(true);
        await session.ReadAsync(entry, false, (_, _) => Task.FromResult(initial));

        await session.ReadAsync(entry, true, (_, _) => throw new IOException("Retry this page"));
        Assert.Same(initial, entry.Snapshot);
        Assert.Equal("Retry this page", entry.Error);
        Assert.True(entry.FailedAppend);
        await session.ReadAsync(entry, true, (_, _) => Task.FromResult(Snapshot()));
        session.Invalidate("recent");
        await session.ReadAsync(entry, false, (_, _) => throw new InvalidOperationException("Warm New should not read"));

        Assert.Null(entry.Error);
        Assert.False(entry.FailedAppend);
        Assert.False(entry.Snapshot!.More);
    }
}
