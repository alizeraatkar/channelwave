namespace ChannelWave.Core;

public static class NativeFileCopy
{
    public static async Task ExportAsync(string source, string destination, CancellationToken ct)
    {
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
