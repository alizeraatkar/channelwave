using ChannelWave.Core;
using System.Text.Json;

namespace ChannelWave.Telegram;

/// <summary>A read-only range view; cancellation is drained while background ownership is held.</summary>
public sealed class TelegramMetadataFile(TelegramSession session, Track track, JsonElement file,
    Func<JsonElement, long, int, CancellationToken, Task<byte[]>> read) : IReadOnlyMediaFile
{
    private JsonElement current = file;
    private int requests;
    private long requestedBytes;
    public long AccountId => track.Origin.AccountId;
    public long Length { get; } = file.Id("size");
    public static async Task<TelegramMetadataFile> OpenAsync(TelegramSession session, Track track,
        Func<JsonElement, long, int, CancellationToken, Task<byte[]>> read, CancellationToken ct)
    {
        await session.WaitForRequestsAsync(ct);
        var file = await new TelegramDownloads(session).ResolveAsync(track, ct);
        ValidateMedia(track, file);
        if (file.Id("size") <= 0) throw new IOException("Telegram has not reported the audio size yet.");
        return new(session, track, file, read);
    }

    public static void ValidateMedia(Track track, JsonElement file)
    {
        var remote = file.GetProperty("remote");
        var actual = track with { Media = track.Media with { UniqueId = remote.Text("unique_id"), RemoteId = remote.Text("id"), Size = file.Id("size") } };
        if (!Metadata.IsSameMedia(track, actual)) throw new InvalidDataException("The source audio changed. Reindex the source before reading metadata.");
    }

    public async Task<byte[]> ReadRangeAsync(long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count < 0 || count > EmbeddedMetadataReader.MaxBytes || offset > Length - count) throw new InvalidDataException("Invalid metadata range.");
        using var output = new MemoryStream(count);
        while (count > 0)
        {
            var chunk = Math.Min(count, 1024 * 1024);
            var bytes = await session.MediaWork.BackgroundAsync(async token =>
            {
                await session.WaitForRequestsAsync(token);
                if (++requests > EmbeddedMetadataReader.MaxOperations || (requestedBytes += chunk) > EmbeddedMetadataReader.MaxBytes)
                    throw new InvalidDataException("Metadata scan exceeds its range read budget.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var requested = false; var id = current.GetProperty("id").GetInt32();
                try
                {
                    if (session.AccountId != AccountId) throw new OperationCanceledException(token);
                    var prefix = await session.Transport.SendAsync(Td.Query("getFileDownloadedPrefixSize", ("file_id", id), ("offset", offset)), timeout.Token);
                    if (prefix.Id("size") < chunk)
                    {
                        requested = true;
                        current = await session.Transport.SendAsync(Td.Query("downloadFile", ("file_id", id), ("priority", 1),
                            ("offset", offset), ("limit", chunk), ("synchronous", true)), timeout.Token);
                        var available = await session.Transport.SendAsync(Td.Query("getFileDownloadedPrefixSize", ("file_id", id), ("offset", offset)), timeout.Token);
                        if (available.Id("size") < chunk) throw new IOException("Telegram returned an incomplete metadata range.");
                    }
                    timeout.Token.ThrowIfCancellationRequested();
                    if (session.AccountId != AccountId) throw new OperationCanceledException(token);
                    var data = await read(current, offset, chunk, timeout.Token);
                    if (data.Length != chunk) throw new IOException("Telegram returned an incomplete metadata range.");
                    return data;
                }
                finally
                {
                    // No foreground request starts until this callback releases background ownership.
                    if (requested && timeout.IsCancellationRequested)
                    {
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await session.Transport.SendAsync(Td.Query("cancelDownloadFile", ("file_id", id), ("only_if_pending", false)), cancel.Token); }
                        catch (Exception) { }
                    }
                }
            }, ct);
            await output.WriteAsync(bytes, ct); offset += chunk; count -= chunk;
        }
        return output.ToArray();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
