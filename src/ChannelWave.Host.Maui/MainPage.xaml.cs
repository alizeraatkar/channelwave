namespace ChannelWave.Host.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if ANDROID
        blazorWebView.BlazorWebViewInitialized += (_, args) =>
        {
            if (Platform.CurrentActivity is MainActivity activity) activity.AttachDialogBackHandler(args.WebView);
        };
#endif
    }
}
