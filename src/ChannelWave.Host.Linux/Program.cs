using Bit.BlazorUI;
using ChannelWave.Core;
using ChannelWave.Telegram;
using ChannelWave.Telegram.Native;
using ChannelWave.Storage.Sqlite;
using ChannelWave.UI;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
namespace ChannelWave.Host.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var smoke = args.Contains("--smoke-test");
        var root = smoke ? Environment.GetEnvironmentVariable("CHANNELWAVE_SMOKE_ROOT") ?? throw new InvalidOperationException("Smoke profile is required.")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChannelWave");
        LinuxProfileSecurity.Prepare(root);
        using var profileLock = new FileStream(Path.Combine(root, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddBitBlazorUIServices();
        builder.Services.AddSingleton<ITdTransport>(_ => new NativeTdTransport());
        builder.Services.AddSingleton<TelegramSession>();
        builder.Services.AddSingleton<HomeSessionState>();
        builder.Services.AddScoped<HomeCollectionReader>();
        builder.Services.AddSingleton<ILibraryRepository>(_ => new SqliteLibrary(Path.Combine(root, "library.db")));
        builder.Services.AddSingleton<SourceIndexer>();
        builder.Services.AddSingleton<ChannelSync>();
        builder.Services.AddSingleton<ChannelCatalog>();
        builder.Services.AddSingleton(sp => new NativeOfflineStore(root, sp.GetRequiredService<ILibraryRepository>()));
        builder.Services.AddSingleton<IRuntimeHost>(sp => new LinuxRuntimeHost(root, sp.GetRequiredService<NativeOfflineStore>()));
        builder.Services.AddSingleton<MediaStorageService>();
        builder.Services.AddSingleton<ITransferWorker, TelegramTransferWorker>();
        builder.Services.AddSingleton<TransferManager>();
        builder.Services.AddSingleton<PlaybackMediaResolver>();
        builder.Services.AddSingleton<IAudioEngine>(_ => new LibVlcAudio(discardAudio: smoke));
        builder.Services.AddSingleton(sp => new PlaybackCoordinator(sp.GetRequiredService<IAudioEngine>(), sp.GetRequiredService<TelegramSession>().MediaWork, sp.GetRequiredService<ILibraryRepository>() as IListeningStore));
        builder.Services.AddScoped<ListeningPlaybackActions>();
        builder.Services.AddScoped<ArtworkCoordinator>();
        builder.Services.AddScoped<IMetadataSource, MetadataMediaSource>();
        builder.Services.AddScoped<MetadataScanner>();
        builder.RootComponents.Add<Diagnostic>("#app");
        var app = builder.Build();
        if (smoke)
        {
            Environment.ExitCode = NativeReleaseSmoke.RunAsync(app.Services).GetAwaiter().GetResult();
            return;
        }
        app.MainWindow.SetTitle("ChannelWave");
        app.MainWindow.SetIconFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon-192.png"));
        app.Run();
    }
}
