using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace DeveLiveStreamer.App.Platforms.Android;

internal static class AndroidLayoutCompatibility
{
    public static void Configure()
    {
        // MAUI's batched initializer casts unset (NaN) minimums directly to integers.
        // This runtime produced int.MaxValue, making every native view enormous.
        // The normal minimum-size mappers resolve NaN to zero and preserve explicit minimums.
        ViewHandler.ViewMapper.AppendToMapping("_InitializeBatchedProperties", (handler, view) =>
        {
            if (handler.PlatformView is global::Android.Views.View native)
            {
                native.UpdateMinimumWidth(view);
                native.UpdateMinimumHeight(view);
            }
        });
    }
}
