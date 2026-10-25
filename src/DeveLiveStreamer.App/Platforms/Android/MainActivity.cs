using Android.App;
using Android.Content;
using Android.Content.PM;
using DeveLiveStreamer.App.Platforms.Android.Studio;

namespace DeveLiveStreamer.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!ScreenCaptureConsent.Complete(requestCode, resultCode, data)) base.OnActivityResult(requestCode, resultCode, data);
    }
}
