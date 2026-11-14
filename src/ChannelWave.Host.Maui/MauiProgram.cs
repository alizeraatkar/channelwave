using Bit.BlazorUI;
using ChannelWave.Core;
using ChannelWave.Telegram;
using ChannelWave.Telegram.Native;
using ChannelWave.Storage.Sqlite;
using ChannelWave.UI;
using Microsoft.Extensions.DependencyInjection;
namespace ChannelWave.Host.Maui;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        var dataRoot = FileSystem.AppDataDirectory;
#if WINDOWS
        dataRoot = Environment.GetCommandLineArgs().Contains("--smoke-test")
            ? Environment.GetEnvironmentVariable("CHANNELWAVE_SMOKE_ROOT") ?? throw new InvalidOperationException("Smoke profile is required.")
            : FileSystem.AppDataDirectory;
#endif
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddBitBlazorUIServices();
#if CHANNELWAVE_LIBRARY_PERFORMANCE
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Services.AddSingleton<ITdTransport, LibraryPerformanceFixture.FixtureTransport>();
#else
        builder.Services.AddSingleton<ITdTransport>(_ => new NativeTdTransport());
#endif
        builder.Services.AddSingleton<TelegramSession>();
        builder.Services.AddSingleton<HomeSessionState>();
        builder.Services.AddScoped<HomeCollectionReader>();
        builder.Services.AddSingleton<ILibraryRepository>(_ => new SqliteLibrary(Path.Combine(dataRoot, "ChannelWave", "library.db")));
        builder.Services.AddSingleton<SourceIndexer>();
        builder.Services.AddSingleton<ChannelSync>();
        builder.Services.AddSingleton<ChannelCatalog>();
        builder.Services.AddSingleton<IRuntimeHost, MauiRuntimeHost>();
        builder.Services.AddSingleton(sp => new NativeOfflineStore(Path.Combine(dataRoot, "ChannelWave"), sp.GetRequiredService<ILibraryRepository>()));
        builder.Services.AddSingleton<MediaStorageService>();
        builder.Services.AddSingleton<ITransferWorker, TelegramTransferWorker>();
        builder.Services.AddSingleton<TransferManager>();
        builder.Services.AddSingleton<PlaybackMediaResolver>();
#if ANDROID
        builder.Services.AddSingleton<AndroidAudioEngine>();
        builder.Services.AddSingleton<IAudioEngine>(services => new MobileCastAudio(services.GetRequiredService<AndroidAudioEngine>(), new AndroidCast()));
#elif CHANNELWAVE_IOS_CAST
        builder.Services.AddSingleton<IAudioEngine>(_ =>
        {
            var cast = new IosCast();
            var audio = new MobileCastAudio(new NativeAudio(), cast);
            cast.ObserveBackground(audio.SignalLoss);
            return audio;
        });
#else
        builder.Services.AddSingleton<IAudioEngine, NativeAudio>();
#endif
        builder.Services.AddSingleton(sp => new PlaybackCoordinator(sp.GetRequiredService<IAudioEngine>(), sp.GetRequiredService<TelegramSession>().MediaWork, sp.GetRequiredService<ILibraryRepository>() as IListeningStore));
        builder.Services.AddScoped<ListeningPlaybackActions>();
        builder.Services.AddScoped<ArtworkCoordinator>();
#if CHANNELWAVE_LIBRARY_PERFORMANCE
        builder.Services.AddScoped<IMetadataSource, LibraryPerformanceFixture.FixtureMetadataSource>();
#else
        builder.Services.AddScoped<IMetadataSource, MetadataMediaSource>();
#endif
        builder.Services.AddScoped<MetadataScanner>();
        return builder.Build();
    }
}
