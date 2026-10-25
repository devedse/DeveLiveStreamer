using Android.Content;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace DeveLiveStreamer.App.Platforms.Android;

public sealed class SceneInteractionHandler : GraphicsViewHandler
{
    protected override PlatformTouchGraphicsView CreatePlatformView() => new SceneTouchView(Context);

    private sealed class SceneTouchView(Context context) : PlatformTouchGraphicsView(context)
    {
        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e?.ActionMasked == MotionEventActions.Down)
                Parent?.RequestDisallowInterceptTouchEvent(true);
            try { return base.OnTouchEvent(e); }
            finally
            {
                if (e?.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                    Parent?.RequestDisallowInterceptTouchEvent(false);
            }
        }
        protected override void OnDetachedFromWindow()
        {
            Parent?.RequestDisallowInterceptTouchEvent(false);
            TouchesCanceled();
            base.OnDetachedFromWindow();
        }
    }
}
