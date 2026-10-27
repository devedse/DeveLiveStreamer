namespace DeveLiveStreamer.App.Controls;

public sealed class SourceReorderHandle : SceneInteractionView
{
    public SourceReorderHandle(string sourceName)
    {
        WidthRequest = HeightRequest = 44;
        VerticalOptions = LayoutOptions.Center;
        CaptureDrag = true;
        Drawable = new Grip();
        SemanticProperties.SetDescription(this, $"Reorder {sourceName}");
        SemanticProperties.SetHint(this, "Drag up or down to change layer order. The top source is drawn on top.");
    }

    private sealed class Grip : IDrawable
    {
        public void Draw(ICanvas canvas, RectF bounds)
        {
            canvas.FillColor = Theme.Muted;
            for (var row = -1; row <= 1; row++)
                for (var column = -1; column <= 1; column += 2)
                    canvas.FillCircle(bounds.Center.X + column * 3.5f, bounds.Center.Y + row * 7, 1.6f);
        }
    }
}
