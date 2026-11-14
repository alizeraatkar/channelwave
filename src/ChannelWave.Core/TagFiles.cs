namespace ChannelWave.Core;

/// <summary>A read-only snapshot of one account's file. Export always creates a copy.</summary>
public interface ITagFile : IReadOnlyMediaFile
{
    Task<string> ExportCopyAsync(Id3Rewrite rewrite, string fileName, CancellationToken ct);
}

public sealed record Id3Rewrite(long OriginalPrefixLength, byte[] Prefix, bool ReplaceLegacyFooter, byte[]? LegacyFooter);

/// <summary>Shared native I/O; host-specific save/share behavior stays in the host.</summary>
public sealed class NativeTagFile(long accountId, string path,
    Func<NativeTagFile, Id3Rewrite, string, CancellationToken, Task<string>> export) : ITagFile
{
    private readonly FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public long AccountId => accountId;
    public long Length => input.Length;

    public async Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (offset < 0 || count < 0 || count > Id3Codec.MaxTagBytes || offset > input.Length - count)
                throw new InvalidDataException("Invalid metadata range.");
            var bytes = new byte[count]; input.Position = offset;
            await input.ReadExactlyAsync(bytes, ct); return bytes;
        }
        finally { gate.Release(); }
    }

    public Task<string> ExportCopyAsync(Id3Rewrite rewrite, string fileName, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return export(this, rewrite, fileName, ct);
    }

    public async Task WriteCopyAsync(string destination, Id3Rewrite rewrite, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Path.GetFullPath(destination).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Export must use a separate file.");
            var end = input.Length - (rewrite.ReplaceLegacyFooter ? 128 : 0);
            if (rewrite.OriginalPrefixLength < 0 || rewrite.OriginalPrefixLength > end || rewrite.Prefix.Length > Id3Codec.MaxTagBytes
                || rewrite.ReplaceLegacyFooter && rewrite.LegacyFooter?.Length != 128)
                throw new InvalidDataException("Invalid tag boundaries.");
            var created = false;
            try
            {
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                await output.WriteAsync(rewrite.Prefix, ct);
                input.Position = rewrite.OriginalPrefixLength;
                var buffer = new byte[81920];
                for (var remaining = end - input.Position; remaining > 0;)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read == 0) throw new EndOfStreamException("The source file became incomplete.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read;
                }
                if (rewrite.ReplaceLegacyFooter && rewrite.LegacyFooter is not null) await output.WriteAsync(rewrite.LegacyFooter, ct);
                await output.FlushAsync(ct); ct.ThrowIfCancellationRequested();
            }
            catch { if (created) File.Delete(destination); throw; }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; await input.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
