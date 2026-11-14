using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ChannelWave.Host.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private DialogBackCallback? dialogBack;

    internal void AttachDialogBackHandler(Android.Webkit.WebView webView)
    {
        dialogBack?.Remove();
        dialogBack = new DialogBackCallback(this, webView);
        OnBackPressedDispatcher.AddCallback(this, dialogBack);
    }

    private sealed class DialogBackCallback(MainActivity activity, Android.Webkit.WebView webView) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        private bool pending;

        public override void HandleOnBackPressed()
        {
            if (pending) return;
            pending = true;
            try
            {
                webView.EvaluateJavascript("Boolean(window.channelwaveHandleBack?.())", new ScriptResult(result =>
                {
                    pending = false;
                    if (result != "true") ForwardBack();
                }));
            }
            catch (Exception)
            {
                pending = false;
                ForwardBack();
            }
        }

        private void ForwardBack()
        {
            Enabled = false;
            try { activity.OnBackPressedDispatcher.OnBackPressed(); }
            finally { Enabled = true; }
        }
    }

    private sealed class ScriptResult(Action<string?> complete) : Java.Lang.Object, Android.Webkit.IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => complete(value?.ToString());
    }
}
