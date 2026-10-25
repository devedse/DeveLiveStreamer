namespace DeveLiveStreamer.App;

public sealed record CameraChoice(string Id, string Name, bool FrontCamera)
{
    public override string ToString() => Name;
}

public static class CameraCatalog
{
    public static IReadOnlyList<CameraChoice> Available()
    {
#if ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = (global::Android.Hardware.Camera2.CameraManager)context.GetSystemService(global::Android.Content.Context.CameraService)!;
            return manager.GetCameraIdList().Select(id =>
            {
                using var characteristics = manager.GetCameraCharacteristics(id);
                var facing = (characteristics.Get(global::Android.Hardware.Camera2.CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue();
                var front = facing == (int)global::Android.Hardware.Camera2.LensFacing.Front;
                var label = front ? "Front camera" : facing == (int)global::Android.Hardware.Camera2.LensFacing.Back ? "Rear camera" : "External camera";
                return new CameraChoice(id, $"{label} (ID {id})", front);
            }).ToArray();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
#endif
        return [];
    }
}
