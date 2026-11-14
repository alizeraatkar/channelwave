using ChannelWave.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChannelWave.Storage.Sqlite;

/// <summary>Verified, app-owned pins. Paths are derived only from account and GUID identities.</summary>
public sealed class NativeOfflineStore(string root, ILibraryRepository library)
{
    private const string ManifestKey = "offline-copies:v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string AccountRoot(long account) => Path.Combine(root, "offline", account.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public string AudioPath(long account, Guid id) => Path.Combine(AccountRoot(account), id.ToString("N") + ".audio");
    private async Task<OfflineCopy[]> ReadAsync(long account, CancellationToken ct)
    {
        var json = await library.ReadStateAsync(account, ManifestKey, ct);
        return (json is null ? [] : JsonSerializer.Deserialize<OfflineCopy[]>(json) ?? []).Where(c => c.Track.Origin.AccountId == account).ToArray();
    }
    private Task WriteAsync(long account, IEnumerable<OfflineCopy> copies, CancellationToken ct)
        => library.SaveStateAsync(account, ManifestKey, JsonSerializer.Serialize(copies), ct);
    public async Task<IReadOnlyList<OfflineCopy>> CopiesAsync(long? account, CancellationToken ct)
    {
        if (account is null)
        {
            var marker = Path.Combine(root, "offline", "last-account");
            if (!File.Exists(marker) || !long.TryParse(await File.ReadAllTextAsync(marker, ct), out var recent)) return [];
            account = recent;
        }
        await gate.WaitAsync(ct);
        try
        {
            var copies = await ReadAsync(account.Value, ct);
            var current = new Dictionary<Guid, Track>();
            foreach (var batch in copies.Select(c => c.Track.Id).Chunk(500))
                foreach (var track in await library.SearchAsync(account.Value, new LibrarySearchRequest(new(), Playlist: batch, Limit: batch.Length), ct))
                    current[track.Id] = track;
            var folder = AccountRoot(account.Value);
            if (Directory.Exists(folder))
            {
                var retained = copies.Select(c => Path.GetFullPath(AudioPath(account.Value, c.Track.Id))).ToHashSet(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (file.EndsWith(".partial", StringComparison.Ordinal) || file.EndsWith(".audio", StringComparison.Ordinal) && !retained.Contains(Path.GetFullPath(file)))
                        File.Delete(file);
            }
            return copies.Select(copy =>
            {
                var file = new FileInfo(AudioPath(account.Value, copy.Track.Id));
                var track = current.GetValueOrDefault(copy.Track.Id) ?? copy.Track;
                return copy with { Track = track, Health = !file.Exists ? OfflineHealth.Missing : file.Length != copy.Bytes || copy.MediaKey != Metadata.MediaKey(track) ? OfflineHealth.Damaged : copy.Health };
            }).ToArray();
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(Track track, string source, CancellationToken ct)
    {
        if (track.Origin.Protected) throw new InvalidOperationException("This source restricts saving.");
        await gate.WaitAsync(ct);
        var account = track.Origin.AccountId;
        var destination = AudioPath(account, track.Id);
        var partial = destination + ".partial";
        try
        {
            var current = await library.TrackAsync(account, track.Id, ct);
            if (current is null || current.Origin.Protected || Metadata.MediaKey(current) != Metadata.MediaKey(track))
                throw new InvalidOperationException("This song changed or its source was removed. Refresh your library and retry.");
            Directory.CreateDirectory(AccountRoot(account));
            string hash;
            long bytes;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; bytes = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    digest.AppendData(buffer, 0, count); bytes += count;
                }
                await output.FlushAsync(ct);
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            if (bytes <= 0 || track.Media.Size > 0 && bytes != track.Media.Size) throw new InvalidDataException("The downloaded song is incomplete. Retry saving it.");
            ct.ThrowIfCancellationRequested();
            current = await library.TrackAsync(account, track.Id, ct);
            if (current is null || current.Origin.Protected || Metadata.MediaKey(current) != Metadata.MediaKey(track))
                throw new InvalidOperationException("This song is no longer available to save.");
            File.Move(partial, destination, overwrite: true);
            var copies = await ReadAsync(account, ct);
            await WriteAsync(account, copies.Where(c => c.Track.Id != track.Id).Append(new(current, Metadata.MediaKey(current), bytes, hash, DateTimeOffset.UtcNow)), ct);
            var marker = Path.Combine(root, "offline", "last-account");
            await File.WriteAllTextAsync(marker + ".partial", account.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            File.Move(marker + ".partial", marker, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); gate.Release(); }
    }
    public async Task<PreparedMedia?> OpenAsync(Track track, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var copies = await ReadAsync(track.Origin.AccountId, ct);
            var copy = copies.FirstOrDefault(c => c.Track.Id == track.Id);
            if (copy is null) return null;
            var path = AudioPath(track.Origin.AccountId, track.Id);
            if (track.Origin.Protected || copy.MediaKey != Metadata.MediaKey(track)) throw new InvalidDataException("This saved copy is outdated. Save the song again.");
            try
            {
                await using var input = File.OpenRead(path);
                if (input.Length != copy.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(copy.Hash, StringComparison.Ordinal))
                    throw new InvalidDataException("This saved copy is damaged. Reconnect and save it again.");
                return new(path, false, copy.Bytes);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                await WriteAsync(track.Origin.AccountId, copies.Select(c => c.Track.Id == track.Id ? c with { Health = File.Exists(path) ? OfflineHealth.Damaged : OfflineHealth.Missing } : c), ct);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(Track track, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var copies = await ReadAsync(track.Origin.AccountId, ct);
            var path = AudioPath(track.Origin.AccountId, track.Id);
            if (File.Exists(path)) File.Delete(path);
            await WriteAsync(track.Origin.AccountId, copies.Where(c => c.Track.Id != track.Id), ct);
        }
        finally { gate.Release(); }
    }
    public async Task ClearAccountAsync(long account, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var folder = AccountRoot(account);
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            var marker = Path.Combine(root, "offline", "last-account");
            if (File.Exists(marker) && (await File.ReadAllTextAsync(marker, ct)).Trim() == account.ToString(System.Globalization.CultureInfo.InvariantCulture)) File.Delete(marker);
        }
        finally { gate.Release(); }
    }
}
