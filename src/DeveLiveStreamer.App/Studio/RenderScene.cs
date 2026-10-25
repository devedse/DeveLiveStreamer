using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App.Studio;

/// <summary>An immutable copy of one visible scene layer, safe to read on the render thread.</summary>
public sealed record RenderLayer(string ItemId, string SourceId, SourceKind Kind, string Name, string Content, string Color, double FontSize,
    string CameraId, bool FrontCamera, bool Mirror, bool AudioEnabled, double X, double Y, double Width, double Height, double Opacity);

/// <summary>The program scene as the native compositor sees it: visible layers bottom-to-top in canvas-normalized coordinates.</summary>
public sealed record RenderScene(int CanvasWidth, int CanvasHeight, IReadOnlyList<RenderLayer> Layers)
{
    public static readonly RenderScene Empty = new(1920, 1080, []);

    public static RenderScene From(ProjectDocument project)
    {
        var layers = new List<RenderLayer>();
        foreach (var item in project.SelectedScene.Items.Where(i => i.Visible && i.Opacity > 0))
        {
            var source = project.SourceFor(item);
            layers.Add(new RenderLayer(item.Id, source.Id, source.Kind, source.Name, source.Content, source.Color, source.FontSize,
                source.CameraId, source.FrontCamera, source.Mirror, source.AudioEnabled, item.X, item.Y, item.Width, item.Height, item.Opacity));
        }
        return new(project.Output.CanvasWidth, project.Output.CanvasHeight, layers);
    }
}
