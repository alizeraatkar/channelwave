using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using System.Text.Json;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class TransferStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Track Song(long account = 700) => new(Guid.NewGuid(), new(account, -100, 1, "Synthetic source", false),
        new("remote", "unique", 1, "test.wav", "audio/wav", 64), "Song", "Artist", "Song", "Artist", 1, DateTimeOffset.UtcNow);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task<SqliteLibrary> Store(string root)
    {
        var library = new SqliteLibrary(Path.Combine(root, "library.db")); await library.InitializeAsync(Token); return library;
    }
    private static string Root() => Path.Combine(Path.GetTempPath(), "channelwave-transfer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Interrupted_offline_intent_recovers_and_repeated_enqueue_deduplicates()
    {
        // Arrange
        await using var library = await Store(Root()); var song = Song();
        var blocked = new Worker { Prepare = async (_, ct) => await Task.Delay(Timeout.Infinite, ct) };
        var first = new TransferManager(library, blocked); await first.BindAsync(700, Token);

        // Act
        var original = await first.EnqueueAsync([song], TransferPurpose.Offline, ct: Token);
        var duplicate = await first.EnqueueAsync([song], TransferPurpose.Offline, ct: Token);
        await Until(() => blocked.Starts == 1); await first.DisposeAsync();
        var completing = new Worker(); await using var second = new TransferManager(library, completing);
        await second.BindAsync(700, Token); await Until(() => second.Jobs.Single().State == TransferState.Completed);

        // Assert
        Assert.Equal(original, duplicate); Assert.Single(second.Jobs); Assert.Equal(1, completing.Starts);
        Assert.Equal(original[0], second.Jobs.Single().Id);
    }

    [Fact]
    public async Task Cancellation_is_durable_and_next_job_runs_without_overlap()
    {
        // Arrange
        await using var library = await Store(Root());
        var worker = new Worker { Prepare = async (_, ct) => await Task.Delay(Timeout.Infinite, ct) };
        await using var manager = new TransferManager(library, worker); await manager.BindAsync(700, Token);
        var ids = await manager.EnqueueAsync([Song(), Song()], TransferPurpose.Offline, ct: Token);
        await Until(() => worker.Starts == 1);

        // Act
        await manager.CancelAsync(ids[0], Token);
        await Until(() => worker.Starts == 2);
        await manager.CancelAsync(ids[1], Token);
        await manager.BindAsync(null, Token);
        await manager.BindAsync(700, Token);

        // Assert
        Assert.All(manager.Jobs, j => Assert.Equal(TransferState.Canceled, j.State));
        Assert.Equal(1, worker.MaxConcurrent); Assert.Equal(2, worker.Starts);
    }

    [Fact]
    public async Task Export_crash_recovery_requires_explicit_save_and_does_not_repeat_handoff()
    {
        // Arrange
        await using var library = await Store(Root()); var song = Song(); var worker = new Worker();
        var job = new TransferJob(Guid.NewGuid(), song, TransferPurpose.Export, TransferState.Exporting, DateTimeOffset.UtcNow);
        await library.SaveStateAsync(700, TransferManager.JournalKey, JsonSerializer.Serialize(new { Version = 1, Jobs = new[] { job } }), Token);
        await using var manager = new TransferManager(library, worker);

        // Act
        await manager.BindAsync(700, Token);
        var recovered = manager.Jobs.Single();
        await manager.ExportAsync(recovered.Id, Token);

        // Assert
        Assert.Equal(TransferState.ReadyToExport, recovered.State); Assert.Contains("Check your files", recovered.Error);
        Assert.Equal(0, worker.Starts); Assert.Equal(1, worker.Exports);
        Assert.Equal(TransferState.Completed, manager.Jobs.Single().State);
    }

    [Fact]
    public async Task Account_switch_and_source_removal_cannot_revive_old_intent()
    {
        // Arrange
        await using var library = await Store(Root()); var worker = new Worker { Prepare = async (_, ct) => await Task.Delay(Timeout.Infinite, ct) };
        await using var manager = new TransferManager(library, worker); await manager.BindAsync(700, Token);
        await manager.EnqueueAsync([Song()], TransferPurpose.Offline, ct: Token); await Until(() => worker.Starts == 1);

        // Act
        await manager.RemoveSourceAsync(-100, Token);
        await manager.BindAsync(701, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnqueueAsync([Song()], TransferPurpose.Offline, ct: Token));
        await manager.BindAsync(700, Token);

        // Assert
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public async Task Native_saved_copy_survives_cache_deletion_and_detects_same_length_corruption()
    {
        // Arrange
        var root = Root(); await using var library = await Store(root); var song = Song();
        await library.SavePageAsync(new(700, -100, "Synthetic source"), [song], Token);
        var temporary = Path.Combine(root, "temporary.wav"); await File.WriteAllBytesAsync(temporary, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), Token);
        var first = new NativeOfflineStore(root, library); await first.SaveAsync(song, temporary, Token);

        // Act
        File.Delete(temporary);
        var reopened = new NativeOfflineStore(root, library);
        var media = await reopened.OpenAsync(song, Token);
        await File.WriteAllBytesAsync(reopened.AudioPath(700, song.Id), new byte[64], Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.OpenAsync(song, Token));

        // Assert
        Assert.NotNull(media); Assert.Equal(64, media.Bytes);
        Assert.Equal(OfflineHealth.Damaged, (await reopened.CopiesAsync(700, Token)).Single().Health);
        File.Delete(reopened.AudioPath(700, song.Id));
        Assert.Equal(OfflineHealth.Missing, (await reopened.CopiesAsync(700, Token)).Single().Health);
    }

    [Fact]
    public async Task Native_account_erasure_and_incomplete_copy_never_remove_another_account()
    {
        // Arrange
        var root = Root(); await using var library = await Store(root); var a = Song(); var b = Song(701);
        await library.SavePageAsync(new(700, -100, "Source A"), [a], Token);
        await library.SavePageAsync(new(701, -100, "Source B"), [b], Token);
        var source = Path.Combine(root, "input.wav"); await File.WriteAllBytesAsync(source, new byte[64], Token);
        var offline = new NativeOfflineStore(root, library);
        await offline.SaveAsync(a, source, Token); await offline.SaveAsync(b, source, Token);

        // Act
        await offline.ClearAccountAsync(700, Token); await library.ClearAccountAsync(700, Token);
        await File.WriteAllBytesAsync(source, new byte[32], Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => offline.SaveAsync(b, source, Token));
        await library.SetFavoriteAsync(701, b.Id, true, Token);

        // Assert
        Assert.Empty(await offline.CopiesAsync(700, Token));
        Assert.NotNull(await offline.OpenAsync(b, Token));
        Assert.True((await offline.CopiesAsync(701, Token)).Single().Track.Favorite);
    }

    [Fact]
    public void Offline_pins_do_not_consume_the_temporary_cache_budget()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        CacheEntry[] entries = [new("saved", 9000, now, true, false), new("cache", 100, now, false, false)];

        // Act
        var evictions = CachePolicy.Evictions(entries, 100);

        // Assert
        Assert.Empty(evictions);
    }

    [Fact]
    public async Task Persisted_rate_limit_deadline_is_respected_after_reopening()
    {
        // Arrange
        await using var library = await Store(Root()); var song = Song(); var worker = new Worker();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var job = new TransferJob(Guid.NewGuid(), song, TransferPurpose.Offline, TransferState.Waiting, DateTimeOffset.UtcNow, RetryAt: deadline, Error: "Waiting for Telegram");
        await library.SaveStateAsync(700, TransferManager.JournalKey, JsonSerializer.Serialize(new { Version = 1, Jobs = new[] { job } }), Token);
        await using var manager = new TransferManager(library, worker);

        // Act
        await manager.BindAsync(700, Token);
        await Task.Delay(400, Token);

        // Assert
        Assert.Equal(0, worker.Starts); Assert.Equal(deadline, manager.Jobs.Single().RetryAt);
        Assert.Equal(TransferState.Waiting, manager.Jobs.Single().State);
    }

    [Fact]
    public async Task Cache_cleanup_lease_never_overlaps_a_file_user()
    {
        // Arrange
        var coordinator = new MediaWorkCoordinator();
        var file = await coordinator.FileAsync("700:source:message", Token);

        // Act
        var blocked = coordinator.TryCacheMutation();
        file.Dispose();
        using var mutation = coordinator.TryCacheMutation();
        var anotherFile = coordinator.TryFile("700:another:message");

        // Assert
        Assert.Null(blocked); Assert.NotNull(mutation); Assert.Null(anotherFile);
    }

    [Fact]
    public async Task Native_export_publishes_only_complete_files_and_keeps_existing_exports()
    {
        // Arrange
        var root = Root(); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.wav"); var destination = Path.Combine(root, "export.wav");
        await File.WriteAllBytesAsync(source, new byte[64], Token);

        // Act
        await NativeFileCopy.ExportAsync(source, destination, Token);
        await Assert.ThrowsAsync<IOException>(() => NativeFileCopy.ExportAsync(source, destination, Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeFileCopy.ExportAsync(source, destination + ".canceled", canceled.Token));

        // Assert
        Assert.Equal(64, new FileInfo(destination).Length);
        Assert.False(File.Exists(destination + ".canceled"));
        Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
    }

    [Fact]
    public async Task Signing_out_interrupts_export_before_waiting_for_its_lifecycle_lock()
    {
        // Arrange
        await using var library = await Store(Root());
        var worker = new Worker { Handoff = async ct => { await Task.Delay(Timeout.Infinite, ct); return "unreachable"; } };
        var job = new TransferJob(Guid.NewGuid(), Song(), TransferPurpose.Export, TransferState.ReadyToExport, DateTimeOffset.UtcNow);
        await library.SaveStateAsync(700, TransferManager.JournalKey, JsonSerializer.Serialize(new { Version = 1, Jobs = new[] { job } }), Token);
        await using var manager = new TransferManager(library, worker); await manager.BindAsync(700, Token);

        // Act
        var exporting = manager.ExportAsync(job.Id, Token); await Until(() => worker.Exports == 1);
        await manager.BindAsync(null, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        await exporting;

        // Assert
        Assert.Null(manager.Account); Assert.Empty(manager.Jobs); Assert.Equal(1, worker.Exports);
    }

    [Fact]
    public void Forwarded_recordings_share_a_file_lease_but_different_accounts_do_not()
    {
        // Arrange
        var original = Song();
        var forwarded = original with { Id = Guid.NewGuid(), Origin = original.Origin with { ChatId = -200, MessageId = 99 }, Media = original.Media with { RuntimeId = 100 } };
        var otherAccount = forwarded with { Origin = forwarded.Origin with { AccountId = 701 } };

        // Act
        var originalKey = Metadata.FileUseKey(original);
        var forwardedKey = Metadata.FileUseKey(forwarded);

        // Assert
        Assert.Equal(originalKey, forwardedKey);
        Assert.NotEqual(originalKey, Metadata.FileUseKey(otherAccount));
    }

    private sealed class Worker : ITransferWorker
    {
        public Func<TransferJob, CancellationToken, Task>? Prepare { get; init; }
        public Func<CancellationToken, Task<string>>? Handoff { get; init; }
        public int Starts, Exports, MaxConcurrent;
        private int concurrent;
        public async Task PrepareAsync(TransferJob job, IProgress<TransferProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts); var running = Interlocked.Increment(ref concurrent); MaxConcurrent = Math.Max(MaxConcurrent, running);
            try { if (Prepare is not null) await Prepare(job, ct); progress.Report(new(64, 64)); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        public Task<string> ExportAsync(TransferJob job, CancellationToken ct) { Exports++; return Handoff?.Invoke(ct) ?? Task.FromResult("Saved synthetic file"); }
        public Task CancelAsync(TransferJob job) => Task.CompletedTask;
    }
}
