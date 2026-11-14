using ChannelWave.Core;

namespace ChannelWave.UI;

public sealed record HomeCollectionSnapshot(IReadOnlyList<Track> Items, HashSet<Guid> SavedIds,
    bool More = false, bool Unavailable = false, int RecentOffset = 0);

/// <summary>Process-local browsing state. Native hosts retain it across WebView recreation.</summary>
public sealed class HomeSessionState
{
    public sealed class Collection(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public HomeCollectionSnapshot? Snapshot { get; internal set; }
        public string? Error { get; internal set; }
        public bool FailedAppend { get; internal set; }
        public bool Loading => Pending is not null;
        internal bool Fresh;
        internal Task? Pending;
        internal CancellationTokenSource? Read;
    }

    private readonly object gate = new();
    private readonly Dictionary<(string Tab, bool Preview), Collection> collections = [];
    private readonly Dictionary<string, double> scroll = [];
    public long? Account { get; private set; }
    public string SelectedTab { get; set; } = "all";
    public long Revision { get; private set; }

    public void Bind(long? account)
    {
        lock (gate)
        {
            if (Account == account) return;
            foreach (var entry in collections.Values) entry.Read?.Cancel();
            collections.Clear(); scroll.Clear(); SelectedTab = "all";
            Account = account; Revision++;
        }
    }

    public void Invalidate(string? tab = null)
    {
        lock (gate)
        {
            Revision++;
            foreach (var pair in collections.Where(p => tab is null || p.Key.Tab == tab))
            {
                pair.Value.Fresh = false;
                pair.Value.Read?.Cancel();
                pair.Value.Pending = null;
            }
        }
    }

    public void RetainTabs(IReadOnlySet<string> ids)
    {
        lock (gate)
        {
            foreach (var key in collections.Keys.Where(key => !ids.Contains(key.Tab)).ToArray())
            {
                collections[key].Read?.Cancel();
                collections.Remove(key);
            }
            foreach (var key in scroll.Keys.Where(key => !ids.Any(id => key.StartsWith(id + ":", StringComparison.Ordinal))).ToArray())
                scroll.Remove(key);
            if (!ids.Contains(SelectedTab)) SelectedTab = "all";
        }
    }

    public void UpdateTracks(IEnumerable<Track> tracks, bool automatic = false)
    {
        lock (gate)
        {
            var updates = tracks.Where(t => t.Origin.AccountId == Account).ToDictionary(t => t.Id);
            if (updates.Count == 0) return;
            Revision++;
            foreach (var pair in collections)
            {
                var entry = pair.Value;
                // Metadata cannot change membership/order in these default collections.
                // Custom artist/album/filter queries must evaluate their membership again.
                if (pair.Key.Tab is not ("new" or "liked" or "recent" or "playlists") || entry.Loading)
                {
                    entry.Fresh = false; entry.Read?.Cancel(); entry.Pending = null;
                }
                if (entry.Snapshot is { } snapshot && snapshot.Items.Any(t => updates.ContainsKey(t.Id)))
                    entry.Snapshot = snapshot with { Items = snapshot.Items.Select(t =>
                    {
                        if (!updates.TryGetValue(t.Id, out var update) || !Metadata.IsSameMedia(t, update)) return t;
                        // A queued scan can arrive after a favorite, source or local-label edit.
                        var next = update with { Favorite = t.Favorite, Available = t.Available,
                            Origin = t.Origin with { Message = update.Origin.Message } };
                        return !automatic ? next : next with
                        {
                            TitleOverride = t.TitleOverride, PerformerOverride = t.PerformerOverride,
                            Title = t.TitleOverride ?? (string.IsNullOrWhiteSpace(update.EmbeddedMetadata?.Title) ? t.OriginalTitle : update.EmbeddedMetadata.Title),
                            Performer = t.PerformerOverride ?? (string.IsNullOrWhiteSpace(update.EmbeddedMetadata?.Artist) ? t.OriginalPerformer : update.EmbeddedMetadata.Artist)
                        };
                    }).ToArray() };
            }
        }
    }

    public Collection Get(long account, string tab, bool preview, string fingerprint)
    {
        lock (gate)
        {
            Bind(account);
            var key = (tab, preview);
            if (!collections.TryGetValue(key, out var entry) || entry.Fingerprint != fingerprint)
            {
                entry?.Read?.Cancel();
                collections[key] = entry = new(fingerprint);
            }
            return entry;
        }
    }

    public Task ReadAsync(Collection entry, bool append,
        Func<HomeCollectionSnapshot?, CancellationToken, Task<HomeCollectionSnapshot>> read)
    {
        lock (gate)
        {
            if (entry.Pending is { } pending) return pending;
            if (!append && entry.Fresh) return Task.CompletedTask;
            var cancellation = new CancellationTokenSource();
            entry.Read = cancellation; entry.Error = null;
            // Yield before calling the adapter so Pending is assigned even for synchronous adapters.
            return entry.Pending = ReadCoreAsync(entry, cancellation, append, read);
        }
    }

    private async Task ReadCoreAsync(Collection entry, CancellationTokenSource cancellation, bool append,
        Func<HomeCollectionSnapshot?, CancellationToken, Task<HomeCollectionSnapshot>> read)
    {
        await Task.Yield();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var result = await read(entry.Snapshot, cancellation.Token);
            lock (gate)
                if (!cancellation.IsCancellationRequested && entry.Read == cancellation)
                { entry.Snapshot = result; entry.Fresh = true; entry.FailedAppend = false; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (gate)
                if (!cancellation.IsCancellationRequested && entry.Read == cancellation)
                { entry.Error = e.Message; entry.FailedAppend = append; }
        }
        finally
        {
            lock (gate)
                if (entry.Read == cancellation) { entry.Pending = null; entry.Read = null; }
            cancellation.Dispose();
        }
    }

    public double Scroll(string view) { lock (gate) return scroll.GetValueOrDefault(view); }
    public void RememberScroll(long account, string view, double top)
    { lock (gate) if (Account == account) scroll[view] = Math.Max(0, top); }
}
