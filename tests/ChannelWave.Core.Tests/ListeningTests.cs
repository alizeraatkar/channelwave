using ChannelWave.Core;
using ChannelWave.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class ListeningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Track Track(int duration = 180) => Fixture.Track(new(7, -1001, "Synthetic source"), 1) with
    { Duration = duration, OriginalPerformer = "Known credit", Performer = "Known credit" };
    private sealed class Clock : TimeProvider
    {
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-02T12:00:00Z").AddMilliseconds(milliseconds);
        public void Advance(int ms = 1000) => milliseconds += ms;
    }
    private static AudioState Audio(double position, double duration = 180, bool paused = false) => new(position, duration, duration, paused, false, 1);

    [Theory]
    [InlineData(180, 29, false)]
    [InlineData(180, 30, true)]
    [InlineData(20, 9, false)]
    [InlineData(20, 10, true)]
    [InlineData(0, 30, true)]
    public void Active_time_qualifies_once_at_the_duration_threshold(int duration, int seconds, bool qualified)
    {
        // Arrange
        var clock = new Clock(); var recorder = new ListeningRecorder(clock); var track = Track(duration);
        recorder.Observe(0, track, Guid.NewGuid(), Audio(0, duration), true, false);

        // Act
        for (var i = 1; i <= seconds; i++) { clock.Advance(); recorder.Observe(i, track, null, Audio(i, duration), true, false); }

        // Assert
        Assert.Equal(seconds * 1000d, recorder.Current!.ActiveMilliseconds);
        Assert.Equal(qualified, recorder.Current.QualifiedAt is not null);
        Assert.Equal(seconds * 1000d, recorder.Current.Spans.Sum(x => x.ActiveMilliseconds));
    }

    [Fact]
    public void Seeks_pauses_buffering_mute_and_duplicate_samples_do_not_inflate_time()
    {
        // Arrange
        var clock = new Clock(); var recorder = new ListeningRecorder(clock); var track = Track();
        recorder.Observe(0, track, null, Audio(0), true, false);
        clock.Advance(); recorder.Observe(1, track, null, Audio(1), true, false);

        // Act
        clock.Advance(); recorder.Observe(2, track, null, Audio(121) with { Seeking = true }, true, false);
        clock.Advance(); recorder.Observe(3, track, null, Audio(121, paused: true), true, false);
        clock.Advance(600_000); recorder.Observe(4, track, null, Audio(121), true, false);
        clock.Advance(); recorder.Observe(5, track, null, Audio(122) with { Buffering = true }, true, false);
        clock.Advance(); recorder.Observe(6, track, null, Audio(123) with { Muted = true }, true, false);
        recorder.Observe(6, track, null, Audio(999), true, false);
        recorder.Break(); recorder.Observe(7, track, null, Audio(123), true, false);
        clock.Advance(); recorder.Observe(8, track, null, Audio(124), true, false);

        // Assert
        Assert.Equal(2000, recorder.Current!.ActiveMilliseconds);
        Assert.Null(recorder.Current.QualifiedAt);
        Assert.True(recorder.Current.HasGaps);
    }

    [Fact]
    public void Interrupted_resume_reuses_listen_and_natural_repeat_starts_a_new_one()
    {
        // Arrange
        var clock = new Clock(); var recorder = new ListeningRecorder(clock); var track = Track(20); var entry = Guid.NewGuid();
        recorder.Observe(0, track, entry, Audio(0, 20), true, false);
        for (var i = 1; i <= 10; i++) { clock.Advance(); recorder.Observe(i, track, entry, Audio(i, 20), true, false); }
        var saved = recorder.Current!;

        // Act
        recorder = new(clock); recorder.Restore(saved); clock.Advance(500_000);
        recorder.Observe(0, track, entry, Audio(10, 20), true, false);
        clock.Advance(); recorder.Observe(1, track, entry, Audio(11, 20), true, false);
        var resumed = recorder.Finish("completed")!;
        recorder.Observe(2, track, entry, Audio(0, 20), true, false);
        clock.Advance(); recorder.Observe(3, track, entry, Audio(1, 20), true, false);

        // Assert
        Assert.Equal(saved.Id, resumed.Id); Assert.Equal(saved.QualifiedAt, resumed.QualifiedAt);
        Assert.Equal(11_000, resumed.ActiveMilliseconds); Assert.True(resumed.Resumed);
        Assert.NotEqual(saved.Id, recorder.Current!.Id); Assert.Equal(entry, recorder.Current.EntryId);
    }

    [Fact]
    public async Task Sqlite_commits_are_atomic_account_scoped_and_deletion_recomputes_statistics()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-listening-{Guid.NewGuid()}.db");
        try
        {
            await using var store = new SqliteLibrary(path); await store.InitializeAsync(Token);
            var track = ListeningTrack.From(Track()); var start = DateTimeOffset.Parse("2026-11-02T23:59:40Z").ToUnixTimeMilliseconds();
            var listen = new ListeningEvent(Guid.NewGuid(), 7, Guid.NewGuid(), track, start, start + 40_000, 40_000, 30_000,
                start + 30_000, 0, 40, "completed", [new(start, start + 40_000, 40_000)]);

            // Act
            var revision = await store.CommitListeningAsync(new(7, 0, 0, new(), null, [listen]), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitListeningAsync(new(7, 0, 0, new(), null, [listen]), Token));
            await store.CommitListeningAsync(new(7, revision, 0, new(), null, [listen]), Token);
            var stats = await store.StatisticsAsync(7, start, start + 40_001, TimeZoneInfo.Utc, Token);
            var other = await store.StatisticsAsync(8, start, start + 40_001, TimeZoneInfo.Utc, Token);
            await store.DeleteListeningAsync(7, listen.Id, ct: Token);
            var cleared = await store.StatisticsAsync(7, start, start + 40_001, TimeZoneInfo.Utc, Token);

            // Assert
            Assert.Equal(40_000, stats.Milliseconds); Assert.Equal(1, stats.Plays); Assert.Equal(1, stats.Tracks); Assert.Equal(1, stats.Artists);
            Assert.Equal(new[] { 20_000d, 20_000d }, stats.Days.Select(x => x.Milliseconds));
            Assert.Equal(new[] { 0, 1 }, stats.Days.Select(x => x.Plays));
            Assert.Equal(0, other.Milliseconds); Assert.Equal(0, cleared.Milliseconds); Assert.Equal(0, cleared.Plays);
            Assert.Null(await store.ListenAsync(7, listen.Id, Token));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Queue_identity_modes_navigation_and_position_restore_without_loading_or_playing()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-restore-{Guid.NewGuid()}.db");
        var track = Track(); var second = track with { Id = Guid.NewGuid(), Origin = track.Origin with { MessageId = 2 } };
        var tracks = new[] { track, second }; QueueEntry[] entries; Guid? current;
        try
        {
            await using (var store = new SqliteLibrary(path))
            {
                await store.InitializeAsync(Token); var engine = new Engine();
                await using var playback = new PlaybackCoordinator(engine, listeningStore: store);
                await playback.StartQueueAsync(7, [track.Id, second.Id, track.Id], track.Id,
                    (id, _) => Task.FromResult(tracks.FirstOrDefault(x => x.Id == id)), (_, _) => Task.FromResult(new PreparedMedia("synthetic", false, 1)), ct: Token);
                playback.SetShuffle(true); playback.CycleRepeat(); await playback.SetVolumeAsync(.4); await playback.SetMutedAsync(true);
                engine.Position = 12.345; await playback.PauseAsync(); await playback.CheckpointAsync();
                entries = playback.Entries.ToArray(); current = playback.CurrentEntryId;
            }

            // Act
            await using var reopened = new SqliteLibrary(path); await reopened.InitializeAsync(Token); var restoredEngine = new Engine();
            await using var restored = new PlaybackCoordinator(restoredEngine, listeningStore: reopened);
            await restored.BindListeningAsync(7, (id, _) => Task.FromResult(tracks.FirstOrDefault(x => x.Id == id)),
                (_, _) => Task.FromResult(new PreparedMedia("synthetic", false, 1)), Token);

            // Assert
            Assert.Equal(entries, restored.Entries); Assert.Equal(current, restored.CurrentEntryId);
            Assert.Equal(12.345, restored.Queue.Position); Assert.True(restored.Queue.Shuffle); Assert.Equal(RepeatMode.One, restored.Queue.Repeat);
            Assert.Equal(.4, restored.OutputVolume); Assert.True(restored.Muted); Assert.True(restored.RestoredPaused);
            Assert.Equal(0, restoredEngine.Loads); Assert.Equal(0, restoredEngine.Plays);
            await restored.ResumeAsync(Token); Assert.Equal(1, restoredEngine.Loads); Assert.Equal(1, restoredEngine.Plays);
            Assert.Equal(12.345, restoredEngine.Position); Assert.Equal(0, restoredEngine.Volume);
            await restored.StopAsync(); Assert.Null((await reopened.ReadListeningStateAsync(7, Token)).Player);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Snapshot_rejects_foreign_accounts_duplicate_entries_and_checksum_damage()
    {
        // Arrange
        var track = ListeningTrack.From(Track()); var entry = new QueueEntry(Guid.NewGuid(), track.Id);
        var snapshot = new SavedPlayerSnapshot(1, 7, [entry], entry.Id, [entry.Id], [entry.Id], 0, 3, RepeatMode.Off, false, 1, false, false, new(), track, null);

        // Act
        var valid = PlaybackEnvelope.Create(snapshot);

        // Assert
        Assert.NotNull(valid.Read(7)); Assert.Null(valid.Read(8));
        Assert.Null((valid with { Checksum = "broken" }).Read(7));
        Assert.Null(PlaybackEnvelope.Create(snapshot with { Entries = [entry, entry] }).Read(7));
        Assert.Throws<NotSupportedException>(() => (valid with { Version = 2 }).Read(7));
    }

    [Fact]
    public void Local_date_boundaries_respect_daylight_saving_and_inclusive_custom_end_dates()
    {
        // Arrange
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");

        // Act
        var autumn = ListeningPeriods.Range("custom", now, zone, new(2026, 11, 1), new(2026, 11, 1));
        var spring = ListeningPeriods.Range("custom", now, zone, new(2026, 3, 8), new(2026, 3, 8));

        // Assert
        Assert.Equal(25 * 3_600_000L, autumn.To - autumn.From);
        Assert.Equal(23 * 3_600_000L, spring.To - spring.From);
    }
    private sealed class Engine : IAudioEngine
    {
        public int Loads, Plays;
        public double Position, Volume = 1;
        private bool paused = true;
        public Task LoadAsync(PreparedMedia media, CancellationToken ct) { Loads++; Position = 0; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct) { Plays++; paused = false; return Task.CompletedTask; }
        public Task PauseAsync() { paused = true; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { Position = seconds; return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume) { Volume = volume; return Task.CompletedTask; }
        public Task<AudioState?> StateAsync(CancellationToken ct = default) => Task.FromResult<AudioState?>(new(Position, 180, 180, paused, false, Volume));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Deletion_fences_old_writes_and_cannot_restore_the_deleted_active_attempt()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-delete-listen-{Guid.NewGuid()}.db");
        try
        {
            await using var store = new SqliteLibrary(path); await store.InitializeAsync(Token);
            var reference = ListeningTrack.From(Track()); var entry = new QueueEntry(Guid.NewGuid(), reference.Id);
            var listen = new ListeningEvent(Guid.NewGuid(), 7, entry.Id, reference, 1000, 41_000, 40_000, 30_000, 31_000,
                0, 40, "active", [new(1000, 41_000, 40_000)]);
            var snapshot = PlaybackEnvelope.Create(new(1, 7, [entry], entry.Id, [entry.Id], [entry.Id], 0, 40,
                RepeatMode.Off, false, 1, false, false, new(), reference, listen.Id));
            await store.CommitListeningAsync(new(7, 0, 0, new(), snapshot, [listen]), Token);
            await store.CommitListeningAsync(new(7, 1, 0, new(), snapshot, [listen]), Token);

            // Act
            var state = await store.DeleteListeningAsync(7, listen.Id, ct: Token);

            // Assert
            Assert.True(state.Player!.Read(7)!.Suppressed); Assert.True(state.PreviousPlayer!.Read(7)!.Suppressed);
            Assert.Null(state.Player.Read(7)!.ActiveListen); Assert.Equal(entry.Id, state.Player.Read(7)!.CurrentEntry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitListeningAsync(new(7, 2, 0, new(), snapshot, [listen]), Token));
            Assert.Empty((await store.HistoryAsync(7, new(), Token)).Items);
            await store.CommitListeningAsync(new(8, 0, 0, new(), null, [listen with { Account = 8, Track = reference with { Account = 8 } }]), Token);
            await store.ClearAccountAsync(7, Token);
            Assert.Equal(0, (await store.StatisticsAsync(7, 0, long.MaxValue, TimeZoneInfo.Utc, Token)).Plays);
            Assert.Equal(1, (await store.StatisticsAsync(8, 0, long.MaxValue, TimeZoneInfo.Utc, Token)).Plays);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Corrupt_latest_snapshot_falls_back_without_loading_media()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-corrupt-player-{Guid.NewGuid()}.db");
        try
        {
            await using var store = new SqliteLibrary(path); await store.InitializeAsync(Token);
            var track = Track(); var reference = ListeningTrack.From(track); var entry = new QueueEntry(Guid.NewGuid(), track.Id);
            var snapshot = PlaybackEnvelope.Create(new(1, 7, [entry], entry.Id, [entry.Id], [entry.Id], 0, 12,
                RepeatMode.All, false, .5, false, true, new("offline", "Offline"), reference, null));
            var state = new ListeningState(1, 0, new(), snapshot with { Checksum = "broken" }, snapshot);
            await store.SaveStateAsync(7, "listening-v1", System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Token);
            var engine = new Engine(); await using var playback = new PlaybackCoordinator(engine, listeningStore: store);

            // Act
            await playback.BindListeningAsync(7, (_, _) => Task.FromResult<Track?>(track), (_, _) => Task.FromResult(new PreparedMedia("synthetic", false, 1)), Token);

            // Assert
            Assert.True(playback.RestoredPaused); Assert.NotNull(playback.RecoveryNotice); Assert.True(playback.OfflineOnly);
            Assert.Equal(12, playback.Queue.Position); Assert.Equal(0, engine.Loads); Assert.Equal(0, engine.Plays);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Initial_library_migrates_additively_with_existing_documents_preserved()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-upgrade-listening-{Guid.NewGuid()}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(Token); using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Tracks (Account INTEGER NOT NULL, Identity TEXT NOT NULL, Id TEXT NOT NULL, SearchText TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Account,Identity));
                    CREATE UNIQUE INDEX IX_Tracks_Account_Id ON Tracks(Account,Id);
                    CREATE TABLE Documents (Account INTEGER NOT NULL, Key TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(Account,Key));
                    CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);
                    INSERT INTO __EFMigrationsHistory VALUES('202610090001_InitialLibrary','10.0.12');
                    INSERT INTO Documents VALUES(7,'state:playlist','existing playlist');
                    INSERT INTO Documents VALUES(0,'state:pending-signout-v1','existing cleanup');
                    """;
                await command.ExecuteNonQueryAsync(Token);
            }

            // Act
            await using var store = new SqliteLibrary(path); await store.InitializeAsync(Token);

            // Assert
            Assert.Equal("existing playlist", await store.ReadStateAsync(7, "playlist", Token));
            Assert.Equal("existing cleanup", await store.ReadStateAsync(0, "pending-signout-v1", Token));
            Assert.Empty((await store.HistoryAsync(7, new(), Token)).Items);
            Assert.Equal(0, (await store.ReadListeningStateAsync(7, Token)).Revision);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Large_history_pages_are_bounded_and_statistics_keep_exact_distinct_counts()
    {
        // Arrange: synthetic storage workload, not playback/device evidence.
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-history-scale-{Guid.NewGuid()}.db");
        try
        {
            await using var store = new SqliteLibrary(path); await store.InitializeAsync(Token);
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(Token); await using var transaction = await connection.BeginTransactionAsync(Token);
                using var eventCommand = connection.CreateCommand(); eventCommand.Transaction = (SqliteTransaction)transaction;
                eventCommand.CommandText = "INSERT INTO Listens VALUES(7,@id,@start,@end,@track,-1001,1,@search,@json)";
                foreach (var key in new[] { "@id", "@start", "@end", "@track", "@search", "@json" }) eventCommand.Parameters.Add(new SqliteParameter(key, ""));
                using var bucketCommand = connection.CreateCommand(); bucketCommand.Transaction = (SqliteTransaction)transaction;
                bucketCommand.CommandText = "INSERT INTO ListenBuckets VALUES(7,@id,@listen,@hour,@json)";
                foreach (var key in new[] { "@id", "@listen", "@hour", "@json" }) bucketCommand.Parameters.Add(new SqliteParameter(key, ""));
                var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                var begin = DateTimeOffset.Parse("2026-10-01T00:00:00Z").ToUnixTimeMilliseconds();
                var references = Enumerable.Range(0, 100).Select(i => ListeningTrack.From(Track(1)) with
                { Id = Guid.NewGuid(), Message = i, Title = $"Synthetic track {i}", Artist = $"Credit {i % 10}", ArtistKey = $"credit {i % 10}" }).ToArray();
                for (var i = 0; i < 100_000; i++)
                {
                    var start = begin + i * 1000L;
                    var listen = new ListeningEvent(Guid.NewGuid(), 7, null, references[i % 100], start, start + 1000, 1000, 500,
                        start + 500, 0, 1, "completed", [new(start, start + 1000, 1000)]);
                    var id = listen.Id.ToString("N");
                    eventCommand.Parameters["@id"].Value = id; eventCommand.Parameters["@start"].Value = start;
                    eventCommand.Parameters["@end"].Value = start + 1000; eventCommand.Parameters["@track"].Value = listen.Track.Id.ToString("N");
                    eventCommand.Parameters["@search"].Value = listen.Track.SearchText; eventCommand.Parameters["@json"].Value = System.Text.Json.JsonSerializer.Serialize(listen, json);
                    await eventCommand.ExecuteNonQueryAsync(Token);
                    foreach (var bucket in ListeningBucket.From(listen))
                    {
                        bucketCommand.Parameters["@id"].Value = bucket.Id; bucketCommand.Parameters["@listen"].Value = id;
                        bucketCommand.Parameters["@hour"].Value = bucket.Hour; bucketCommand.Parameters["@json"].Value = System.Text.Json.JsonSerializer.Serialize(bucket, json);
                        await bucketCommand.ExecuteNonQueryAsync(Token);
                    }
                }
                await transaction.CommitAsync(Token);
            }

            // Act
            var first = await store.HistoryAsync(7, new(), Token);
            var second = await store.HistoryAsync(7, new(Before: first.Next), Token);
            var recent = await store.RecentTracksAsync(7, 6, Token);
            var stats = await store.StatisticsAsync(7, 0, long.MaxValue, TimeZoneInfo.Utc, Token);

            // Assert
            Assert.Equal(50, first.Items.Length); Assert.Equal(50, second.Items.Length);
            Assert.Equal(6, recent.Length); Assert.Equal(6, recent.Select(x => x.TrackId).Distinct().Count());
            Assert.Equal(first.Items[0].Track.Id, recent[0].TrackId);
            Assert.True(recent.Zip(recent.Skip(1)).All(pair => pair.First.LastActive >= pair.Second.LastActive));
            Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
            Assert.Equal(100_000, stats.Plays); Assert.Equal(100, stats.Tracks); Assert.Equal(10, stats.Artists);
            Assert.Equal(100_000_000, stats.Milliseconds); Assert.Equal(100_000, stats.Days.Sum(x => x.Plays));
            Assert.All(stats.TopTracks, x => Assert.Equal(1000, x.Plays));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Source_statistics_scan_past_nonmatching_batches_and_keep_partial_source_choices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"channelwave-source-statistics-{Guid.NewGuid()}.db");
        try
        {
            await using var store = new SqliteLibrary(path);
            await store.InitializeAsync(Token);
            var start = DateTimeOffset.Parse("2026-11-01T03:59:40Z").ToUnixTimeMilliseconds();
            var reference = ListeningTrack.From(Track());
            ListeningEvent Listen(ListeningTrack track, long at, double duration, bool qualified) => new(Guid.NewGuid(), track.Account, null,
                track, at, at + (long)duration, duration, 30_000, qualified ? at + 30_000 : null, 0, duration / 1000,
                "completed", [new(at, at + (long)duration, duration)]);
            var earlier = Enumerable.Range(0, 500).Select(_ => Listen(reference, start - 7_200_000, 40_000, true)).ToArray();
            var selected = Listen(reference with { Chat = -1002, Source = "Selected source", Id = Guid.NewGuid(), ArtistKey = "selected", Artist = "Selected artist" }, start, 40_000, true);
            var partial = Listen(reference with { Chat = -1003, Source = "Partial-only source", Id = Guid.NewGuid() }, start + 100_000, 10_000, false);
            var other = Listen(selected.Track with { Account = 8 }, start, 90_000, true);
            await store.CommitListeningAsync(new(7, 0, 0, new(), null, [.. earlier, selected, partial]), Token);
            await store.CommitListeningAsync(new(8, 0, 0, new(), null, [other]), Token);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

            var result = await store.StatisticsAsync(7, 0, long.MaxValue, zone, -1002, Token);
            var clipped = await store.StatisticsAsync(7, start + 10_000, start + 35_000, zone, -1002, Token);
            var partialOnly = await store.StatisticsAsync(7, 0, long.MaxValue, zone, -1003, Token);
            var absent = await store.StatisticsAsync(7, 0, long.MaxValue, zone, -9999, Token);

            Assert.Equal(40_000, result.Milliseconds);
            Assert.Equal(1, result.Plays); Assert.Equal(1, result.Tracks); Assert.Equal(1, result.Artists);
            Assert.Equal(selected.Track.Identity, Assert.Single(result.TopTracks).Key);
            Assert.Equal("selected", Assert.Single(result.TopArtists).Key);
            Assert.Equal("-1002", Assert.Single(result.TopSources).Key);
            Assert.Equal(new[] { 20_000d, 20_000d }, result.Days.Select(x => x.Milliseconds));
            Assert.Equal(new[] { 0, 1 }, result.Days.Select(x => x.Plays));
            Assert.Equal(3, result.Sources.Count);
            Assert.Equal("Partial-only source", result.Sources[-1003]);
            Assert.Equal(25_000, clipped.Milliseconds);
            Assert.Equal(new[] { 10_000d, 15_000d }, clipped.Days.Select(x => x.Milliseconds));
            Assert.Single(clipped.Sources);
            Assert.Equal(10_000, partialOnly.Milliseconds); Assert.Equal(0, partialOnly.Plays);
            Assert.Empty(partialOnly.TopTracks); Assert.Equal(0, absent.Milliseconds); Assert.Empty(absent.Days);
            Assert.Equal(3, absent.Sources.Count);
            Assert.Equal(90_000, (await store.StatisticsAsync(8, 0, long.MaxValue, zone, -1002, Token)).Milliseconds);

            await store.DeleteListeningAsync(7, selected.Id, ct: Token);
            var deleted = await store.StatisticsAsync(7, 0, long.MaxValue, zone, -1002, Token);
            Assert.Equal(0, deleted.Milliseconds); Assert.Equal(0, deleted.Plays);
            Assert.False(deleted.Sources.ContainsKey(-1002)); Assert.True(deleted.Sources.ContainsKey(-1003));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StatisticsAsync(7, 0, long.MaxValue, zone, -1002, canceled.Token));
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path)
    { SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
}
