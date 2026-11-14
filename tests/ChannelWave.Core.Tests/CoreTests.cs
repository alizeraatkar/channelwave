using ChannelWave.Core;
using ChannelWave.Telegram;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("  Café   LIVE  ", "cafe live")]
    [InlineData("كِي يى", "کی یی")]
    [InlineData("آهنگ\u200cمن", "اهنگ من")]
    [InlineData(":) Café 🎵 كِي آهنگ\u200cمن", ":) cafe 🎵 کی اهنگ من")]
    public void Search_normalizes_without_changing_original(string original, string expected) => Assert.Equal(expected, Metadata.Normalize(original));

    [Fact]
    public void Title_preserves_version_and_uses_conservative_filename_fallback()
    {
        Assert.Equal("One (Live)", Metadata.Title(" One (Live) ", "two.mp3"));
        Assert.Equal("artist - remix", Metadata.Title("", "folder/artist - remix.mp3"));
        Assert.Equal("Unknown title", Metadata.Title("", ""));
    }

    [Theory]
    [InlineData("authorizationStateWaitPhoneNumber", "phone")]
    [InlineData("authorizationStateWaitPassword", "password")]
    [InlineData("authorizationStateWaitEmailAddress", "email")]
    [InlineData("authorizationStateReady", "ready")]
    [InlineData("authorizationStateLoggingOut", "loggingOut")]
    [InlineData("authorizationStateClosed", "closed")]
    [InlineData("authorizationStateWaitRegistration", "unsupported")]
    [InlineData("futureState", "unsupported")]
    public void Auth_follows_Telegram_state(string type, string expected) => Assert.Equal(expected, AuthorizationPrompt.From(JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["@type"] = type })).State);

    [Fact]
    public void Code_prompt_uses_actual_delivery_and_resend_time()
    {
        var prompt = AuthorizationPrompt.From(JsonDocument.Parse("""{"@type":"authorizationStateWaitCode","code_info":{"type":{"@type":"authenticationCodeTypeTelegramMessage"},"timeout":42}}""").RootElement);
        Assert.Equal("authenticationCodeTypeTelegramMessage", prompt.Delivery); Assert.Equal(42, prompt.ResendAfter);
    }

    [Fact]
    public void Identifiers_keep_int53_precision_and_reject_unsafe_numbers()
    {
        Assert.Equal(9007199254740991L, JsonDocument.Parse("""{"id":9007199254740991}""").RootElement.Id("id"));
        Assert.Equal(-1001234567890L, JsonDocument.Parse("""{"id":-1001234567890}""").RootElement.Id("id"));
        Assert.Throws<InvalidDataException>(() => JsonDocument.Parse("""{"id":9007199254740992}""").RootElement.Id("id"));
    }

    [Theory]
    [InlineData("FLOOD_WAIT_27", 27)]
    [InlineData("Too Many Requests: retry after 9", 9)]
    public void Rate_limits_expose_wait_seconds(string message, int seconds) => Assert.Equal(seconds, new TelegramException(429, message).RetryAfter);

    [Fact]
    public void Playlist_operations_do_not_mutate_previous_versions()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var original = Playlists.Add(Playlists.Add(Playlists.Create("  List  "), a), b);
        var changed = Playlists.Rename(Playlists.Move(original, 0, 1), "Next");
        Assert.Equal(new[] { a, b }, original.Tracks); Assert.Equal(new[] { b, a }, changed.Tracks);
        Assert.Equal("List", original.Name); Assert.Equal("Next", changed.Name);
        Assert.Throws<ArgumentException>(() => Playlists.Create("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Playlists.Move(original, 0, 3));
        Assert.Equal(new[] { a, b }, Playlists.Add(original, a).Tracks);
        Assert.Equal(new[] { b }, Playlists.Remove(original with { Tracks = [a, b, a] }, a).Tracks);
    }

    [Fact]
    public void Bulk_playlist_edits_and_duplicates_preserve_order_and_are_independent()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var original = Playlists.AddRange(Playlists.Create("Evening"), [a, b, a]);

        var expanded = Playlists.AddRange(original, [b, c]);
        var copy = Playlists.Duplicate(expanded, "Evening copy");
        var trimmed = Playlists.RemoveRange(copy, [a, c]);

        Assert.Equal(new[] { a, b }, original.Tracks);
        Assert.Equal(new[] { a, b, c }, expanded.Tracks);
        Assert.NotEqual(expanded.Id, copy.Id);
        Assert.Equal(expanded.Tracks, copy.Tracks);
        Assert.NotSame(expanded.Tracks, copy.Tracks);
        Assert.Equal(new[] { b }, trimmed.Tracks);
        Assert.Throws<ArgumentException>(() => Playlists.Rename(copy, "bad\nname"));
    }

    [Fact]
    public void Queue_handles_repeat_empty_and_shuffle_without_repeating_current()
    {
        var queue = new QueueSnapshot([Guid.NewGuid(), Guid.NewGuid()], 1);
        Assert.Null(QueueRules.Next(new([]), true)); Assert.Null(QueueRules.Next(queue, true));
        Assert.Equal(1, QueueRules.Next(queue with { Repeat = RepeatMode.One }, true));
        Assert.Equal(0, QueueRules.Next(queue with { Repeat = RepeatMode.All }, true));
        Assert.Equal(0, QueueRules.Next(queue with { Shuffle = true }, true));
    }

    [Fact]
    public void Cache_never_evicts_playing_or_pinned_files_even_over_budget()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[] { new CacheEntry("pin", 100, now, true, false), new CacheEntry("play", 100, now, false, true), new CacheEntry("old", 10, now.AddDays(-1), false, false), new CacheEntry("new", 10, now, false, false) };
        Assert.Equal(new[] { "old", "new" }, CachePolicy.Evictions(entries, 0).Select(x => x.Key));
    }

    [Fact]
    public async Task Old_download_cannot_replace_new_selection_even_if_it_ignores_cancellation()
    {
        var engine = new RecordingEngine(); await using var player = new PlaybackCoordinator(engine);
        var old = new TaskCompletionSource<PreparedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTask = player.SelectAsync(Guid.NewGuid(), _ => old.Task, TestContext.Current.CancellationToken);
        var selected = Guid.NewGuid(); await player.SelectAsync(selected, _ => Task.FromResult(new PreparedMedia("B", false, 1)), TestContext.Current.CancellationToken);
        old.SetResult(new("A", false, 1)); await oldTask;
        Assert.Equal(new[] { "B" }, engine.Loaded); Assert.Equal(selected, player.SelectedTrack); Assert.True(player.IsPlaying); Assert.True(player.CanResume);
    }

    [Fact]
    public async Task Pause_cancels_pending_download_and_restore_never_autoplays()
    {
        var engine = new RecordingEngine(); await using var player = new PlaybackCoordinator(engine);
        player.Restore(new([Guid.NewGuid()], 99, 12)); Assert.False(player.IsPlaying); Assert.False(player.CanResume); Assert.Equal(0, player.Queue.Index);
        var pending = player.SelectAsync(Guid.NewGuid(), async ct => { await Task.Delay(Timeout.Infinite, ct); return new("unreachable", false, 1); }, TestContext.Current.CancellationToken);
        await player.PauseAsync(); await pending;
        Assert.Empty(engine.Loaded); Assert.Equal(0, engine.PlayCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.ResumeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_tracks_cancels_late_downloads_clears_resume_and_prunes_queue()
    {
        var engine = new RecordingEngine(); await using var player = new PlaybackCoordinator(engine);
        var removed = Guid.NewGuid(); var kept = Guid.NewGuid();
        player.Restore(new([removed, kept, removed], 1, 12));
        var late = new TaskCompletionSource<PreparedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = player.SelectAsync(removed, _ => late.Task, TestContext.Current.CancellationToken);
        await player.RemoveTracksAsync([removed]); late.SetResult(new("late", false, 1)); await pending;
        Assert.Null(player.SelectedTrack); Assert.False(player.CanResume); Assert.False(player.IsPlaying); Assert.Empty(engine.Loaded);
        Assert.Equal(new[] { kept }, player.Queue.Tracks); Assert.Equal(0, player.Queue.Index); Assert.Equal(12, player.Queue.Position);
        await player.SelectAsync(kept, _ => Task.FromResult(new PreparedMedia("keep", false, 1)), TestContext.Current.CancellationToken);
        await player.RemoveTracksAsync([removed]); Assert.True(player.IsPlaying); Assert.True(player.CanResume);
        await player.RemoveTracksAsync([kept]); Assert.False(player.IsPlaying); Assert.False(player.CanResume); Assert.Empty(player.Queue.Tracks);
    }

    private sealed class RecordingEngine : IAudioEngine
    {
        public List<string> Loaded { get; } = []; public int PlayCount { get; private set; }
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Loaded.Add(media.Location); return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { PlayCount++; return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task SeekAsync(double seconds) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
