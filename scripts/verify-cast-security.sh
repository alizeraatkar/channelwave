#!/usr/bin/env bash
set -euo pipefail
workspace=${1:-/workspace}
scratch=$(mktemp -d /tmp/channelwave-cast.XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT
cp "$workspace/src/ChannelWave.Host.Maui/CastMediaLease.cs" "$scratch/"
cat > "$scratch/probe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
PROJECT
cat > "$scratch/Program.cs" <<'CSHARP'
using System.Net;
using System.Net.Sockets;
using ChannelWave.Host.Maui;

var address = (await Dns.GetHostAddressesAsync(Dns.GetHostName())).First(ip => ip.AddressFamily == AddressFamily.InterNetwork
    && ip.GetAddressBytes() is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _]);
var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
await File.WriteAllTextAsync(file, "synthetic cast recording");
try
{
    await using var lease = new CastMediaLease(address, file, "audio/mpeg");
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    if (await client.GetStringAsync(lease.Url) != "synthetic cast recording") throw new Exception("Authorized receiver could not read recording");
    using var badToken = await client.GetAsync(lease.Url + "wrong");
    if (badToken.StatusCode != HttpStatusCode.NotFound) throw new Exception("Invalid capability accepted");
    using var range = new HttpRequestMessage(HttpMethod.Get, lease.Url);
    range.Headers.TryAddWithoutValidation("Range", "bytes=999999-");
    using var badRange = await client.SendAsync(range);
    if (badRange.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable) throw new Exception("Invalid range accepted");
    using var restricted = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectCallback = async (context, ct) =>
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); await socket.ConnectAsync(context.DnsEndPoint, ct); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    } }) { Timeout = TimeSpan.FromSeconds(5) };
    using var wrongReceiver = await restricted.GetAsync(lease.Url);
    if (wrongReceiver.StatusCode != HttpStatusCode.NotFound) throw new Exception("Unselected source address accepted");
    lease.Revoke();
    try { await client.GetAsync(lease.Url); throw new Exception("Revoked lease is still reachable"); }
    catch (HttpRequestException) { }
    Console.WriteLine("PASS: Cast receiver/address and token restrictions, invalid ranges and lease revocation.");
}
finally { File.Delete(file); }
CSHARP
dotnet run --project "$scratch/probe.csproj" --verbosity quiet
