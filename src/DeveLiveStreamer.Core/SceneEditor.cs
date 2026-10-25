namespace DeveLiveStreamer.Core;

public sealed class SceneEditor
{
    private readonly List<string> undo = [];
    private readonly List<string> redo = [];
    private string? transaction;
    public ProjectDocument Project { get; private set; }
    public string? SelectedItemId { get; set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public SceneItem? SelectedItem => Project.SelectedScene.Items.FirstOrDefault(i => i.Id == SelectedItemId);
    public event EventHandler? Changed;

    public SceneEditor(ProjectDocument project) { project.Validate(); Project = project; }

    public void Edit(Action<ProjectDocument> change)
    {
        var before = Project.Serialize();
        try { change(Project); Project.Validate(); }
        catch { Project = ProjectDocument.Parse(before); throw; }
        if (before == Project.Serialize()) return;
        PushUndo(before);
        redo.Clear();
        Notify();
    }

    public void SelectScene(string id)
    {
        if (!Project.Scenes.Any(s => s.Id == id)) return;
        Project.SelectedSceneId = id;
        SelectedItemId = null;
        Notify();
    }

    public void BeginTransform() => transaction = Project.Serialize();
    public void EndTransform(bool cancel = false)
    {
        if (transaction is null) return;
        var before = transaction;
        transaction = null;
        if (cancel) Project = ProjectDocument.Parse(before);
        else if (before != Project.Serialize()) { Project.Validate(); PushUndo(before); redo.Clear(); }
        Notify();
    }

    public void AddScene(string name) => Edit(p =>
    {
        var scene = new Scene { Name = UniqueName(name.Trim(), p.Scenes.Select(s => s.Name)) };
        p.Scenes.Add(scene);
        p.SelectedSceneId = scene.Id;
        SelectedItemId = null;
    });

    public void DuplicateScene() => Edit(p =>
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(p.SelectedScene, ProjectJsonContext.Default.Scene), ProjectJsonContext.Default.Scene)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(p.SelectedScene.Name + " copy", p.Scenes.Select(s => s.Name));
        foreach (var item in copy.Items) item.Id = Guid.NewGuid().ToString("N");
        p.Scenes.Add(copy);
        p.SelectedSceneId = copy.Id;
        SelectedItemId = null;
    });

    public void DeleteScene() => Edit(p =>
    {
        if (p.Scenes.Count == 1) throw new InvalidOperationException("Keep at least one scene.");
        p.Scenes.Remove(p.SelectedScene);
        p.SelectedSceneId = p.Scenes[0].Id;
        SelectedItemId = null;
        PruneSources(p);
    });

    public void AddSource(SourceDefinition source) => Edit(p =>
    {
        source.Name = UniqueName(source.Name, p.Sources.Select(s => s.Name));
        p.Sources.Add(source);
        var item = new SceneItem { SourceId = source.Id };
        if (source.Kind == SourceKind.Text) { item.X = .08; item.Y = .45; item.Width = .84; item.Height = .15; }
        if (source.Kind == SourceKind.Image) { item.X = .2; item.Y = .2; item.Width = .6; item.Height = .6; }
        p.SelectedScene.Items.Add(item);
        SelectedItemId = item.Id;
    });

    public void DeleteSelected() => Edit(p =>
    {
        p.SelectedScene.Items.RemoveAll(i => i.Id == SelectedItemId);
        SelectedItemId = null;
        PruneSources(p);
    });

    public void MoveSelected(int delta) => Edit(p =>
    {
        var list = p.SelectedScene.Items;
        var index = list.FindIndex(i => i.Id == SelectedItemId);
        if (index < 0) return;
        var target = Math.Clamp(index + delta, 0, list.Count - 1);
        var item = list[index]; list.RemoveAt(index); list.Insert(target, item);
    });

    public void FitSelected() => Edit(_ =>
    {
        if (SelectedItem is not { Locked: false } item) return;
        item.X = item.Y = 0; item.Width = item.Height = 1;
    });

    public void Undo()
    {
        if (!CanUndo) return;
        redo.Add(Project.Serialize());
        Project = ProjectDocument.Parse(undo[^1]); undo.RemoveAt(undo.Count - 1);
        SelectedItemId = null; Notify();
    }
    public void Redo()
    {
        if (!CanRedo) return;
        PushUndo(Project.Serialize());
        Project = ProjectDocument.Parse(redo[^1]); redo.RemoveAt(redo.Count - 1);
        SelectedItemId = null; Notify();
    }
    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private void PushUndo(string snapshot) { undo.Add(snapshot); if (undo.Count > 100) undo.RemoveAt(0); }
    private static void PruneSources(ProjectDocument p) => p.Sources.RemoveAll(s => !p.Scenes.Any(scene => scene.Items.Any(i => i.SourceId == s.Id)));
    private static string UniqueName(string requested, IEnumerable<string> names)
    {
        var basis = string.IsNullOrWhiteSpace(requested) ? "Untitled" : requested;
        var existing = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = basis; var n = 2;
        while (existing.Contains(name)) name = $"{basis} {n++}";
        return name;
    }
}
