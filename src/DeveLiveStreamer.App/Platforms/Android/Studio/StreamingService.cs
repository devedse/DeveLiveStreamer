using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>Keeps capture and streaming alive while the app is in the background and shows the "live" notification.</summary>
[Service(Name = "nl.deve.livestreamer.StreamingService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeCamera | ForegroundService.TypeMicrophone | ForegroundService.TypeMediaProjection | ForegroundService.TypeMediaPlayback)]
public sealed class StreamingService : Service
{
    private const string Channel = "live";
    private static TaskCompletionSource? started;

    public static Task StartAsync(Context context, ForegroundService types)
    {
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(context, typeof(StreamingService)).PutExtra("types", (int)types);
        context.StartForegroundService(intent);
        return started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(StreamingService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "Live stream", NotificationImportance.Low)
            { Description = "Shown while DeveLiveStreamer is streaming." });
            var open = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!), PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(this, Channel)
                .SetContentTitle("You're live")
                .SetContentText("DeveLiveStreamer is streaming. Open the app to end the stream.")
                .SetSmallIcon(global::Android.Resource.Drawable.PresenceVideoOnline)
                .SetOngoing(true)
                .SetContentIntent(open)
                .Build();
            var types = (ForegroundService)(intent?.GetIntExtra("types", (int)ForegroundService.TypeMediaPlayback) ?? (int)ForegroundService.TypeMediaPlayback);
            StartForeground(1, notification, types);
            started?.TrySetResult();
        }
        catch (Exception ex) { started?.TrySetException(ex); StopSelf(); }
        return StartCommandResult.NotSticky;
    }
}

/// <summary>Asks the user for system screen capture (MediaProjection) consent.</summary>
internal static class ScreenCaptureConsent
{
    private const int RequestCode = 4711;
    private static TaskCompletionSource<Intent?>? pending;

    public static async Task<Intent?> RequestAsync(Context context)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("The app must be open to start screen capture.");
        var request = new TaskCompletionSource<Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref pending, request, null) is not null)
            throw new InvalidOperationException("A screen capture permission request is already open.");
        try
        {
            var manager = (MediaProjectionManager)context.GetSystemService(Context.MediaProjectionService)!;
            global::Android.Util.Log.Info("DeveLiveStreamer", "Requesting screen/device-audio capture consent");
            await MainThread.InvokeOnMainThreadAsync(() => activity.StartActivityForResult(manager.CreateScreenCaptureIntent(), RequestCode));
            return await request.Task;
        }
        finally { Interlocked.CompareExchange(ref pending, null, request); }
    }

    /// <summary>Called from MainActivity.OnActivityResult; returns whether the result belonged to this request.</summary>
    public static bool Complete(int requestCode, Result result, Intent? data)
    {
        if (requestCode != RequestCode) return false;
        pending?.TrySetResult(result == Result.Ok ? data : null);
        return true;
    }
}
