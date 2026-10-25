namespace DeveLiveStreamer.App.Platforms.Android.Studio;

internal static class Studio
{
    /// <summary>Logs the full exception to logcat and returns a one-line message for the status display.</summary>
    public static string Describe(string context, Exception exception)
    {
        global::Android.Util.Log.Error("DeveLiveStreamer", $"{context}: {exception}");
        var message = exception is Java.Lang.Throwable java ? java.LocalizedMessage ?? java.Class?.SimpleName : exception.Message;
        return $"{context}: {exception.GetType().Name}{(string.IsNullOrWhiteSpace(message) ? "" : " – " + message)}";
    }
}
