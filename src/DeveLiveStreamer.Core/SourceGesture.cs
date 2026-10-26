namespace DeveLiveStreamer.Core;

public enum SourceInteractionMode { Select, Move, Resize }

/// <summary>Explicit transform gestures keep their target even when the finger crosses overlapping sources.</summary>
public sealed class SourceGesture(SceneEditor editor)
{
    private string? targetId, sceneId;
    private double originX, originY, startX, startY, startWidth, startHeight;
    private SourceInteractionMode mode;

    public SourceInteractionMode Mode
    {
        get => mode;
        set { Finish(cancel: true); mode = value; }
    }

    public void Select(string? itemId)
    {
        Mode = SourceInteractionMode.Select;
        editor.SelectedItemId = itemId;
    }

    public void Begin(double x, double y)
    {
        Finish(cancel: false);
        if (Mode == SourceInteractionMode.Select)
        {
            editor.SelectedItemId = editor.Project.SelectedScene.Items.LastOrDefault(i =>
                i.Visible && i.Opacity > 0 && x >= i.X && x <= i.X + i.Width && y >= i.Y && y <= i.Y + i.Height)?.Id;
            return;
        }
        if (editor.SelectedItem is not { Visible: true, Locked: false } item || item.Opacity <= 0) return;
        targetId = item.Id; sceneId = editor.Project.SelectedSceneId;
        originX = x; originY = y;
        startX = item.X; startY = item.Y; startWidth = item.Width; startHeight = item.Height;
        editor.BeginTransform();
    }

    public void Drag(double x, double y)
    {
        if (targetId is null || editor.Project.Scenes.FirstOrDefault(s => s.Id == sceneId)?.Items.FirstOrDefault(i => i.Id == targetId) is not { } item) return;
        var dx = x - originX; var dy = y - originY;
        if (Mode == SourceInteractionMode.Resize)
        {
            item.Width = Math.Max(1d / editor.Project.Output.CanvasWidth, startWidth + dx);
            item.Height = Math.Max(1d / editor.Project.Output.CanvasHeight, startHeight + dy);
        }
        else { item.X = Snap(startX + dx, item.Width); item.Y = Snap(startY + dy, item.Height); }
    }

    public void Finish(bool cancel)
    {
        if (targetId is null) return;
        targetId = sceneId = null;
        editor.EndTransform(cancel);
    }

    private static double Snap(double position, double size)
    {
        foreach (var target in new[] { 0d, .5 - size / 2, 1 - size })
            if (Math.Abs(position - target) < .012) return target;
        return position;
    }
}
