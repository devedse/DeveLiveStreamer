using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.Core.Tests;

public sealed class SourceReorderTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void DropUpdatesDrawingOrderPersistsAndIsOneUndoStep(int from, int to)
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var scene = editor.Project.SelectedScene;
        var initial = scene.Items.Select(i => i.Id).Reverse().ToList();
        var itemId = initial[from];
        var otherScenes = editor.Project.Scenes.Where(s => s.Id != scene.Id)
            .Select(s => (s.Id, Items: s.Items.Select(i => i.Id).ToArray())).ToArray();
        var geometry = scene.Items.Select(i => (i.Id, i.SourceId, i.X, i.Y, i.Width, i.Height, i.Visible, i.Locked)).ToArray();
        var expected = initial.ToList(); expected.RemoveAt(from); expected.Insert(to, itemId);

        editor.ReorderItem(scene.Id, itemId, to);

        Assert.Equal(expected, scene.Items.AsEnumerable().Reverse().Select(i => i.Id));
        Assert.Equal(itemId, editor.SelectedItemId);
        foreach (var original in geometry)
        {
            var item = scene.Items.Single(i => i.Id == original.Id);
            Assert.Equal(original, (item.Id, item.SourceId, item.X, item.Y, item.Width, item.Height, item.Visible, item.Locked));
        }
        var saved = ProjectDocument.Parse(editor.Project.Serialize());
        Assert.Equal(expected, saved.SelectedScene.Items.AsEnumerable().Reverse().Select(i => i.Id));
        foreach (var other in otherScenes)
            Assert.Equal(other.Items, saved.Scenes.Single(s => s.Id == other.Id).Items.Select(i => i.Id));
        editor.Undo();
        Assert.Equal(initial, editor.Project.SelectedScene.Items.AsEnumerable().Reverse().Select(i => i.Id));
        Assert.False(editor.CanUndo);
        editor.Redo();
        Assert.Equal(expected, editor.Project.SelectedScene.Items.AsEnumerable().Reverse().Select(i => i.Id));
    }

    [Fact]
    public void DropInTheSamePositionDoesNotCreateHistory()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var scene = editor.Project.SelectedScene;
        editor.ReorderItem(scene.Id, scene.Items[^1].Id, 0);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void StaleDropAfterSceneSwitchOrSourceRemovalDoesNotEditTheCurrentScene()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var scene = editor.Project.SelectedScene;
        var itemId = scene.Items[0].Id;
        editor.SelectScene(editor.Project.Scenes.First(s => s.Id != scene.Id).Id);
        var before = editor.Project.Serialize();
        editor.ReorderItem(scene.Id, itemId, 0);
        editor.ReorderItem(editor.Project.SelectedSceneId, "removed-item", 0);
        Assert.Equal(before, editor.Project.Serialize());
        Assert.False(editor.CanUndo);
    }
}
