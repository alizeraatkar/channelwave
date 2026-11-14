using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ChannelWave.Host.Maui;

internal interface ICastMediaLease : IAsyncDisposable
{
    string Url { get; }
    void Revoke();
}

/// <summary>Serves a single prepared recording to the selected receiver for the lifetime of the current Cast load.</summary>
internal sealed class CastMediaLease : ICastMediaLease
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly IPAddress receiver;
    private readonly string path;
    private readonly string contentType;
    private readonly string resource = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly Task loop;
    private int revoked;

    public CastMediaLease(IPAddress receiver, string path, string contentType)
    {
        if (receiver.AddressFamily != AddressFamily.InterNetwork && !receiver.IsIPv4MappedToIPv6)
            throw new IOException("This Cast connection requires an IPv4 address on the local network.");
        this.receiver = receiver.MapToIPv4();
        this.path = path;
        this.contentType = contentType;
        if (!IsPrivate(this.receiver) || !File.Exists(path))
            throw new IOException("Casting requires a complete song and a receiver on your local network.");

        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(this.receiver, 8009);
        var address = ((IPEndPoint)route.LocalEndPoint!).Address;
        if (!IsPrivate(address))
            throw new IOException("Connect to the same local network as the Cast device.");

        // Reserve an ephemeral port on the selected interface. HttpListener cannot bind to port zero.
        var reservation = new TcpListener(address, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var prefix = $"http://{address}:{port}/";
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch { listener.Close(); lifetime.Dispose(); throw; }
        Url = prefix.TrimEnd('/') + resource;
        loop = ServeAsync(lifetime.Token);
    }

    public string Url { get; }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _];
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        var requests = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync();
                requests.RemoveAll(task => task.IsCompleted);
                if (requests.Count >= 8)
                {
                    request.Response.StatusCode = 503;
                    request.Response.Close();
                    continue;
                }
                requests.Add(SendAsync(request, ct));
            }
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(requests); }
    }

    private async Task SendAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        using var response = context.Response;
        try
        {
            if (request.RawUrl != resource || !receiver.Equals(request.RemoteEndPoint?.Address.MapToIPv4()))
            {
                response.StatusCode = 404;
                return;
            }
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Expose-Headers"] = "Content-Range, Accept-Ranges";
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "Range";
                return;
            }
            if (request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "GET, HEAD, OPTIONS";
                return;
            }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            response.ContentType = contentType;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Accept-Ranges"] = "bytes";
            var length = input.Length;
            long start = 0, end = length - 1;
            if (request.Headers["Range"] is { } range)
            {
                if (!TryRange(range, length, out start, out end))
                {
                    response.StatusCode = 416;
                    response.Headers["Content-Range"] = $"bytes */{length}";
                    return;
                }
                response.StatusCode = 206;
                response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }
            response.ContentLength64 = end - start + 1;
            if (request.HttpMethod == "HEAD") return;
            input.Position = start;
            var buffer = new byte[64 * 1024];
            for (var remaining = response.ContentLength64; remaining > 0;)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) break;
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            response.Abort();
        }
    }

    internal static bool TryRange(string value, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (length <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = value[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length) return false;
        if (parts[1].Length == 0) return true;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
        end = Math.Min(end, length - 1);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Revoke();
        try { await loop; }
        finally { lifetime.Dispose(); }
    }

    public void Revoke()
    {
        if (Interlocked.Exchange(ref revoked, 1) != 0) return;
        lifetime.Cancel();
        listener.Abort();
    }
}
