using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App.Controls;

public sealed class SceneCanvas : ContentView
{
    private readonly AbsoluteLayout layers = new() { IsClippedToBounds = true };
    private readonly SceneInteractionView overlay;
    private readonly Dictionary<string, (SourceKind Kind, View View)> views = [];
    private EditorSession? session;
    private PointF start;
    private double startX, startY, startWidth, startHeight;
    private bool resizing, transforming;
    public event EventHandler? SelectionChanged;

    public SceneCanvas()
    {
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
    public void SetSession(EditorSession value) { session = value; UpdateLayers(); }
    public void Refresh() => UpdateLayers();
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
        if (session is null || args.Touches.Length == 0) return;
        start = args.Touches[0];
        var editor = session.Editor;
        var selected = editor.SelectedItem;
        resizing = selected is not null && selected.Visible && !selected.Locked &&
            Math.Abs(start.X - ItemBounds(selected).Right) < 22 && Math.Abs(start.Y - ItemBounds(selected).Bottom) < 22;
        if (!resizing)
        {
            selected = editor.Project.SelectedScene.Items.LastOrDefault(i => i.Visible && ItemBounds(i).Contains(start.X, start.Y));
            editor.SelectedItemId = selected?.Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (selected is { Locked: false })
        {
            startX = selected.X; startY = selected.Y; startWidth = selected.Width; startHeight = selected.Height;
            editor.BeginTransform(); transforming = true;
        }
        overlay.Invalidate();
    }
    private void Drag(object? sender, TouchEventArgs args)
    {
        if (!transforming || session?.Editor.SelectedItem is not { } item || args.Touches.Length == 0 || Width <= 0 || Height <= 0) return;
        var point = args.Touches[0];
        var dx = (point.X - start.X) / Width; var dy = (point.Y - start.Y) / Height;
        if (resizing)
        {
            item.Width = Math.Max(1d / session.Editor.Project.Output.CanvasWidth, startWidth + dx);
            item.Height = Math.Max(1d / session.Editor.Project.Output.CanvasHeight, startHeight + dy);
        }
        else { item.X = Snap(startX + dx, item.Width); item.Y = Snap(startY + dy, item.Height); }
        UpdateLayers();
    }
    private static double Snap(double position, double size)
    {
        foreach (var target in new[] { 0d, .5 - size / 2, 1 - size })
            if (Math.Abs(position - target) < .012) return target;
        return position;
    }
    private void Finish(bool cancel)
    {
        if (!transforming || session is null) return;
        transforming = false; session.Editor.EndTransform(cancel); UpdateLayers();
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
            if (item.Locked) return;
            graphics.FillColor = Theme.Accent;
            foreach (var point in new[] { new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top),
                new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom) })
                graphics.FillRectangle((float)point.X - 4, (float)point.Y - 4, 8, 8);
        }
    }
}
