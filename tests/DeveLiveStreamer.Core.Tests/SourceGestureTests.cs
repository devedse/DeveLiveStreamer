using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.Core.Tests;

public sealed class SourceGestureTests
{
    private static (SceneEditor Editor, SceneItem Small, SceneItem Background) Create()
    {
        var editor = new SceneEditor(ProjectDocument.CreateDefault());
        var scene = editor.Project.SelectedScene;
        scene.Items.RemoveRange(1, scene.Items.Count - 1);
        var background = scene.Items[0];
        var small = new SceneItem { SourceId = background.SourceId, X = .2, Y = .2, Width = .1, Height = .1 };
        scene.Items.Add(small);
        editor.SelectedItemId = small.Id;
        return (editor, small, background);
    }

    [Fact]
    public void ResizeFromAnywhereKeepsTheSmallSelectedSourceInsteadOfTheBackground()
    {
        var (editor, small, background) = Create();
        var gesture = new SourceGesture(editor) { Mode = SourceInteractionMode.Resize };
        gesture.Begin(.8, .8); // Well outside the small source; the background is underneath the finger.
        gesture.Drag(.95, .9); gesture.Finish(false);
        Assert.Equal(small.Id, editor.SelectedItemId);
        Assert.Equal(.25, small.Width, 9); Assert.Equal(.2, small.Height, 9);
        Assert.Equal(1, background.Width); Assert.Equal(1, background.Height);
        editor.Undo(); Assert.Equal(.1, editor.Project.SelectedScene.Items[1].Width);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void MoveChangesPositionWithoutResizingOrSelectingTheSourceUnderTheFinger()
    {
        var (editor, small, _) = Create();
        var gesture = new SourceGesture(editor) { Mode = SourceInteractionMode.Move };
        gesture.Begin(.8, .8); gesture.Drag(.9, .9); gesture.Finish(false);
        Assert.Equal(.3, small.X, 9); Assert.Equal(.3, small.Y, 9);
        Assert.Equal(.1, small.Width); Assert.Equal(small.Id, editor.SelectedItemId);
    }

    [Fact]
    public void SelectionModeDoesNotMoveTheSourceWhenTheFingerDrags()
    {
        var (editor, small, _) = Create();
        var gesture = new SourceGesture(editor);
        gesture.Begin(.25, .25); gesture.Drag(.8, .8); gesture.Finish(false);
        Assert.Equal(small.Id, editor.SelectedItemId);
        Assert.Equal(.2, small.X); Assert.Equal(.1, small.Width);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void CancelRestoresGeometryAndLockedSourcesCannotBeTransformed()
    {
        var (editor, small, _) = Create();
        var gesture = new SourceGesture(editor) { Mode = SourceInteractionMode.Resize };
        gesture.Begin(.5, .5); gesture.Drag(.9, .9); gesture.Finish(true);
        var restored = editor.SelectedItem!;
        Assert.Equal(.1, restored.Width); Assert.False(editor.CanUndo);
        restored.Locked = true;
        gesture.Begin(.5, .5); gesture.Drag(.8, .8); gesture.Finish(false);
        Assert.Equal(.1, restored.Width); Assert.False(editor.CanUndo);
    }

    [Fact]
    public void FitPreservesLayerAspectWhileStretchFillsTheCanvasAndBothCanBeUndone()
    {
        var (editor, small, _) = Create();
        small.Height = .2;
        editor.FitSelected();
        Assert.Equal(.5, small.Width); Assert.Equal(1, small.Height);
        Assert.Equal(.25, small.X); Assert.Equal(0, small.Y);
        editor.StretchSelected(); Assert.Equal(1, small.Width); Assert.Equal(1, small.Height);
        editor.Undo(); Assert.Equal(.5, editor.Project.SelectedScene.Items[1].Width);
    }

    [Fact]
    public void CenterAndMirrorAreUndoableAndRespectLocks()
    {
        var (editor, small, _) = Create();
        editor.CenterSelected(); Assert.Equal(.45, small.X); Assert.Equal(.45, small.Y);
        editor.MirrorSelected(); Assert.True(editor.Project.SourceFor(small).Mirror);
        editor.Undo(); Assert.False(editor.Project.SourceFor(editor.SelectedItem ?? editor.Project.SelectedScene.Items[1]).Mirror);
        var current = editor.Project.SelectedScene.Items[1];
        editor.SelectedItemId = current.Id; current.Locked = true;
        editor.StretchSelected(); Assert.Equal(.1, current.Width);
    }
}
