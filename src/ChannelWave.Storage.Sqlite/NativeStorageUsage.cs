using ChannelWave.Core;

namespace ChannelWave.Storage.Sqlite;

public static class NativeStorageUsage
{
    public static async Task<StorageUsage> ReadAsync(string root, NativeOfflineStore offline, long account, CancellationToken ct, string? exportRoot = null)
    {
        var copies = await offline.CopiesAsync(account, ct);
        long saved = 0, temporary = 0, other = 0, protectedBytes = 0;
        foreach (var copy in copies)
        {
            var file = new FileInfo(offline.AudioPath(account, copy.Track.Id));
            if (file.Exists) saved += file.Length;
        }
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("offline" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                try
                {
                    var size = new FileInfo(file).Length;
                    if (relative.StartsWith("files" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) temporary += size;
                    else other += size;
                }
                catch (FileNotFoundException) { }
            }
        if (exportRoot is not null && Directory.Exists(exportRoot))
            foreach (var file in Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try { var info = new FileInfo(file); temporary += info.Length; if (info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-1)) protectedBytes += info.Length; } catch (FileNotFoundException) { }
            }
        return new(saved, temporary, other, ProtectedBytes: protectedBytes);
    }
}
