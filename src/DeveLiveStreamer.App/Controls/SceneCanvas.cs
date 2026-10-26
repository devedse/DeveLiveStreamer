using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App.Controls;

public sealed class SceneCanvas : ContentView
{
    private readonly AbsoluteLayout layers = new() { IsClippedToBounds = true };
    private readonly SceneInteractionView overlay;
    private readonly Dictionary<string, (SourceKind Kind, View View)> views = [];
    private EditorSession? session;
    private SourceGesture? gesture;
    private string? selectedItemId, selectedSceneId;
    public event EventHandler? SelectionChanged;

    public SceneCanvas()
    {
        SemanticProperties.SetDescription(this, "Scene preview");
        overlay = new SceneInteractionView { Drawable = new SelectionDrawable(this), BackgroundColor = Colors.Transparent };
        var host = new Grid { BackgroundColor = Color.FromArgb("#0A0F15"), IsClippedToBounds = true };
        // With a native engine the preview is the compositor's own output; otherwise MAUI views approximate it.
        host.Children.Add(StudioHost.Engine.IsSupported ? new ScenePreview() : layers);
        host.Children.Add(overlay); Content = host;
        SizeChanged += (_, _) => UpdateLayers();
        overlay.StartInteraction += Start;
        overlay.DragInteraction += Drag;
        overlay.EndInteraction += (_, _) => Finish(false);
        overlay.CancelInteraction += (_, _) => Finish(true);
        Unloaded += (_, _) => Finish(true);
    }
    public void SetSession(EditorSession value) { session = value; gesture = new SourceGesture(value.Editor); Refresh(); }
    public void Refresh() { SyncSelection(); UpdateLayers(); }
    public SourceInteractionMode Mode
    {
        get => gesture?.Mode ?? SourceInteractionMode.Select;
        set
        {
            if (gesture is not null) gesture.Mode = value;
            overlay.CaptureDrag = value != SourceInteractionMode.Select;
            overlay.Invalidate();
        }
    }
    public void SyncSelection()
    {
        if (session is null) return;
        if (selectedItemId != session.Editor.SelectedItemId || selectedSceneId != session.Editor.Project.SelectedSceneId)
        {
            Mode = SourceInteractionMode.Select;
            selectedItemId = session.Editor.SelectedItemId; selectedSceneId = session.Editor.Project.SelectedSceneId;
        }
    }
    public void SelectItem(string itemId)
    {
        gesture?.Select(itemId); Refresh(); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public void CancelGesture() => Finish(true);
    private Rect ItemBounds(SceneItem item) => new(item.X * Width, item.Y * Height, item.Width * Width, item.Height * Height);

    private void UpdateLayers()
    {
        if (session is null) return;
        if (StudioHost.Engine.IsSupported) { StudioHost.Engine.UpdateScene(session.Editor.Project); overlay.Invalidate(); return; }
        if (Width <= 0 || Height <= 0) return;
        var project = session.Editor.Project;
        var items = project.SelectedScene.Items;
        foreach (var key in views.Keys.Where(id => !items.Any(i => i.Id == id)).ToArray())
        {
            layers.Children.Remove(views[key].View); views.Remove(key);
        }
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]; var source = project.SourceFor(item);
            if (!views.TryGetValue(item.Id, out var cached) || cached.Kind != source.Kind)
            {
                if (cached.View is not null) layers.Children.Remove(cached.View);
                var view = CreateLayer(source);
                views[item.Id] = (source.Kind, view);
                layers.Children.Add(view);
            }
            var layer = views[item.Id].View;
            layer.ZIndex = index; layer.IsVisible = item.Visible; layer.Opacity = item.Opacity;
            AbsoluteLayout.SetLayoutBounds(layer, ItemBounds(item));
            if (layer is Label label && source.Kind == SourceKind.Text)
            {
                label.Text = source.Content; label.TextColor = ParseColor(source.Color);
                label.FontSize = source.FontSize * Width / project.Output.CanvasWidth;
            }
            if (layer is BoxView box) box.Color = ParseColor(source.Color);
            if (layer is Image image) image.Source = source.Content;
        }
        overlay.Invalidate();
    }
    private static View CreateLayer(SourceDefinition source)
    {
        switch (source.Kind)
        {
            case SourceKind.Text:
                return new Label { Text = source.Content, TextColor = ParseColor(source.Color),
                    FontFamily = "OpenSansSemibold", FontAutoScalingEnabled = false,
                    LineBreakMode = LineBreakMode.WordWrap, VerticalTextAlignment = TextAlignment.Center,
                    InputTransparent = true };
            case SourceKind.Color: return new BoxView { Color = ParseColor(source.Color), InputTransparent = true };
            case SourceKind.Image: return new Image { Source = source.Content, Aspect = Aspect.AspectFit, InputTransparent = true };
            case SourceKind.Camera: return Placeholder("CAMERA", "Camera capture is available in the Android build");
            case SourceKind.Screen: return Placeholder("DEVICE SCREEN", "Screen capture is available in the Android build");
            default: return Placeholder("NETWORK SOURCE", string.IsNullOrWhiteSpace(source.Content) ? "Configure an incoming RTMP or SRT URL" : source.Content);
        }
    }
    private static Grid Placeholder(string title, string subtitle)
    {
        var grid = new Grid { BackgroundColor = Color.FromArgb("#13232D"), Padding = 18 };
        var content = new VerticalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
        content.Children.Add(new Label { Text = title, TextColor = Theme.Muted, FontSize = 11, HorizontalTextAlignment = TextAlignment.Center });
        content.Children.Add(new Label { Text = subtitle, TextColor = Theme.Muted, FontSize = 10, HorizontalTextAlignment = TextAlignment.Center });
        grid.Children.Add(content); return grid;
    }
    private void Start(object? sender, TouchEventArgs args)
    {
        if (gesture is null || args.Touches.Length == 0 || Width <= 0 || Height <= 0) return;
        var point = args.Touches[0];
        gesture.Begin(point.X / Width, point.Y / Height);
        SyncSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        overlay.Invalidate();
    }
    private void Drag(object? sender, TouchEventArgs args)
    {
        if (gesture is null || args.Touches.Length == 0 || Width <= 0 || Height <= 0) return;
        var point = args.Touches[0];
        gesture.Drag(point.X / Width, point.Y / Height);
        UpdateLayers();
    }
    private void Finish(bool cancel)
    {
        gesture?.Finish(cancel); UpdateLayers();
    }
    internal static Color ParseColor(string value)
    {
        try { return Color.FromArgb(value); } catch { return Colors.White; }
    }
    private sealed class SelectionDrawable(SceneCanvas canvas) : IDrawable
    {
        public void Draw(ICanvas graphics, RectF dirtyRect)
        {
            var item = canvas.session?.Editor.SelectedItem;
            if (item is null || !item.Visible) return;
            var rect = canvas.ItemBounds(item);
            graphics.StrokeColor = item.Locked ? Theme.Muted : Theme.Accent; graphics.StrokeSize = 1.5f;
            graphics.DrawRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
        }
    }
}
