using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.Core.Tests;

public sealed class SceneEditorTests
{
    [Fact]
    public void ProjectRoundTripPreservesSharedSourceReferences()
    {
        var project = ProjectDocument.CreateDefault();
        var parsed = ProjectDocument.Parse(project.Serialize());
        var shared = parsed.Sources.Single(s => s.Name == "Channel tag");
        Assert.Equal(2, parsed.Scenes.Count(s => s.Items.Any(i => i.SourceId == shared.Id)));
        Assert.Equal(project.Serialize(), parsed.Serialize());
    }
    [Fact]
    public void DuplicateSceneKeepsSharedContentButIndependentPlacement()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var original = editor.Project.SelectedScene;
        editor.DuplicateScene();
        var copy = editor.Project.SelectedScene;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Items[0].SourceId, copy.Items[0].SourceId);
        Assert.NotEqual(original.Items[0].Id, copy.Items[0].Id);
        copy.Items[0].X = .3;
        Assert.Equal(0, original.Items[0].X);
    }
    [Fact]
    public void RemovingSourceDoesNotBreakOtherScenes()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        editor.DuplicateScene();
        var item = editor.Project.SelectedScene.Items[0];
        editor.SelectedItemId = item.Id;
        editor.DeleteSelected();
        Assert.Contains(editor.Project.Sources, s => s.Id == item.SourceId);
        editor.Project.Validate();
    }
    [Fact]
    public void DeletingLastSceneRollsBackWithoutHistoryCorruption()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        while (editor.Project.Scenes.Count > 1) editor.DeleteScene();
        var before = editor.Project.Serialize();
        Assert.Throws<InvalidOperationException>(() => editor.DeleteScene());
        Assert.Equal(before, editor.Project.Serialize());
        editor.Undo();
        Assert.Equal(2, editor.Project.Scenes.Count);
    }
    [Fact]
    public void LayerOrderUndoAndRedoPreserveTheActualDrawingOrder()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var item = editor.Project.SelectedScene.Items[0];
        editor.SelectedItemId = item.Id;
        editor.MoveSelected(1);
        Assert.Equal(item.Id, editor.Project.SelectedScene.Items[1].Id);
        editor.Undo(); Assert.Equal(item.Id, editor.Project.SelectedScene.Items[0].Id);
        editor.Redo(); Assert.Equal(item.Id, editor.Project.SelectedScene.Items[1].Id);
    }
    [Fact]
    public void TransformGestureBecomesOneUndoStep()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var item = editor.Project.SelectedScene.Items[0];
        editor.BeginTransform(); item.X = .1; item.X = .2; item.Width = .4; editor.EndTransform();
        editor.Undo(); Assert.Equal(0, editor.Project.SelectedScene.Items[0].X);
        Assert.False(editor.CanUndo);
        editor.Redo(); Assert.Equal(.2, editor.Project.SelectedScene.Items[0].X);
    }
    [Fact]
    public void InvalidEditRollsBackAndLeavesProjectValid()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        Assert.Throws<InvalidDataException>(() => editor.Edit(p => p.SelectedScene.Items[0].Width = double.NaN));
        editor.Project.Validate(); Assert.False(editor.CanUndo);
    }
    [Theory]
    [InlineData(3840, 2160, 120, 1)]
    [InlineData(7680, 4320, 240, 1)]
    [InlineData(1920, 1080, 60000, 1001)]
    public void CustomOutputModesHaveNoArbitraryResolutionOrFpsCeiling(int width, int height, int n, int d)
    {
        var settings = new OutputSettings { OutputWidth = width, OutputHeight = height, FpsNumerator = n, FpsDenominator = d, SrtLatencyMs = 0 };
        settings.Validate(); Assert.Equal((double)n / d, settings.Fps);
    }
    [Fact]
    public void UnknownSchemaAndMissingReferencesAreRejected()
    {
        var project = ProjectDocument.CreateDefault(); project.SchemaVersion = 2;
        Assert.Throws<InvalidDataException>(() => ProjectDocument.Parse(project.Serialize()));
        project.SchemaVersion = 1; project.Sources.Clear();
        Assert.Throws<InvalidDataException>(() => project.Validate());
    }
    [Fact]
    public void IncompleteImportedCollectionsProduceAnActionableValidationError()
    {
        Assert.Throws<InvalidDataException>(() => ProjectDocument.Parse("{\"Scenes\":null,\"Sources\":null}"));
    }
    [Fact]
    public async Task AtomicStorageRecoversFromDamagedLatestSave()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deve-editor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ProjectStorage(directory); var original = ProjectDocument.CreateDefault();
            await storage.SaveAsync(original.Serialize());
            var newer = ProjectDocument.Parse(original.Serialize()); newer.Name = "Later edit";
            await storage.SaveAsync(newer.Serialize());
            await File.WriteAllTextAsync(storage.ProjectPath, "broken JSON");
            var recovered = await storage.LoadAsync(); Assert.Equal(original.Name, recovered.Name);
            Assert.NotNull(storage.RecoveryMessage);
            await storage.SaveAsync(recovered.Serialize());
            Assert.Equal(original.Name, ProjectDocument.Parse(await File.ReadAllTextAsync(storage.ProjectPath + ".bak")).Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
