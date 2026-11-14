using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace ChannelWave.Telegram.Native;

public sealed class NativeTdTransport(string? libraryPath = null) : ITdTransport
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Send(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Receive(IntPtr client, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Execute(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    private readonly Channel<JsonElement> updates = Channel.CreateUnbounded<JsonElement>(new() { SingleWriter = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sendLock = new();
    private IntPtr library, client;
    private Send? send;
    private Receive? receive;
    private Destroy? destroy;
    private Task? pump;
    private long sequence;
    private bool disposed;
    public ChannelReader<JsonElement> Updates => updates.Reader;

    public Task StartAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        lock (sendLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (client != IntPtr.Zero) return;
            var appleStatic = OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();
            var path = libraryPath ?? Environment.GetEnvironmentVariable("CHANNELWAVE_TDJSON") ??
                (OperatingSystem.IsWindows() ? "tdjson.dll" : OperatingSystem.IsMacOS() ? "libtdjson.dylib" : "libtdjson.so");
            if (libraryPath is null && Environment.GetEnvironmentVariable("CHANNELWAVE_TDJSON") is null && File.Exists(Path.Combine(AppContext.BaseDirectory, path)))
                path = Path.Combine(AppContext.BaseDirectory, path);
            if (!appleStatic) library = NativeLibrary.Load(path);
            T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            try
            {
                Execute execute = appleStatic ? AppleTd.Execute : Bind<Execute>("td_json_client_execute");
                execute(IntPtr.Zero, "{\"@type\":\"setLogStream\",\"log_stream\":{\"@type\":\"logStreamEmpty\"}}");
                execute(IntPtr.Zero, "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");
                send = appleStatic ? AppleTd.Send : Bind<Send>("td_json_client_send");
                receive = appleStatic ? AppleTd.Receive : Bind<Receive>("td_json_client_receive");
                destroy = appleStatic ? AppleTd.Destroy : Bind<Destroy>("td_json_client_destroy");
                Create create = appleStatic ? AppleTd.Create : Bind<Create>("td_json_client_create");
                client = create();
                if (client == IntPtr.Zero) throw new InvalidOperationException("TDLib did not create a client.");
                pump = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { if (library != IntPtr.Zero) NativeLibrary.Free(library); library = IntPtr.Zero; throw; }
        }
    }, ct);

    public async Task<JsonElement> SendAsync(JsonObject request, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
        lock (sendLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (client == IntPtr.Zero) throw new InvalidOperationException("Initialize TDLib first.");
            pending[id] = completion;
            var query = (JsonObject)request.DeepClone(); query["@extra"] = id;
            send!(client, query.ToJsonString());
        }
        try { var result = await completion.Task; Td.ThrowIfError(result); return result; }
        finally { pending.TryRemove(id, out _); }
    }

    private void ReceiveLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var ptr = receive!(client, 0.1);
                if (ptr == IntPtr.Zero) continue;
                using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr)!);
                var value = document.RootElement.Clone();
                if (value.TryGetProperty("@extra", out var extra))
                {
                    if (pending.TryGetValue(extra.GetString()!, out var completion)) completion.TrySetResult(value);
                }
                else updates.Writer.TryWrite(value);
            }
            updates.Writer.TryComplete();
        }
        catch (Exception e)
        {
            updates.Writer.TryComplete(e);
            foreach (var item in pending.Values) item.TrySetException(new IOException("TDLib receive loop stopped."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        if (client != IntPtr.Zero)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await SendAsync(Td.Query("close"), timeout.Token); } catch (Exception) { /* No session data is logged. */ }
        }
        lock (sendLock) { disposed = true; lifetime.Cancel(); }
        if (pump is not null) await pump;
        // Destruction can block while native resources are released; keep it off the UI thread.
        await Task.Run(() => { if (client != IntPtr.Zero) destroy!(client); if (library != IntPtr.Zero) NativeLibrary.Free(library); });
        updates.Writer.TryComplete(); lifetime.Dispose();
    }

    // Apple AOT hosts must statically link the pinned archive and its crypto dependencies.
    private static class AppleTd
    {
        [DllImport("__Internal", EntryPoint = "td_json_client_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Create();
        [DllImport("__Internal", EntryPoint = "td_json_client_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr client);
        [DllImport("__Internal", EntryPoint = "td_json_client_send", CallingConvention = CallingConvention.Cdecl)] internal static extern void Send(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
        [DllImport("__Internal", EntryPoint = "td_json_client_receive", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Receive(IntPtr client, double timeout);
        [DllImport("__Internal", EntryPoint = "td_json_client_execute", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Execute(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    }
}
