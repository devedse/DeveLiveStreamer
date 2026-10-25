using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App;

public sealed class EditorSession
{
    private readonly ProjectStorage storage = new(FileSystem.AppDataDirectory);
    public SceneEditor Editor { get; private set; } = new(ProjectDocument.CreateDefault());
    public bool Loaded { get; private set; }
    public string SaveStatus { get; private set; } = "Saved on this device";
    public string? RecoveryMessage => storage.RecoveryMessage;
    public event EventHandler? Changed;
    public event EventHandler? SaveStateChanged;

    public async Task InitializeAsync()
    {
        if (Loaded) return;
        Editor = new SceneEditor(await storage.LoadAsync());
        Editor.Changed += OnEditorChanged;
        Loaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private async void OnEditorChanged(object? sender, EventArgs args)
    {
        var snapshot = Editor.Project.Serialize();
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await storage.SaveAsync(snapshot);
            SaveStatus = "All changes saved";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            SaveStatus = "Save failed · export your collection";
        }
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task ExportAsync()
    {
        var path = Path.Combine(FileSystem.CacheDirectory, "DeveLiveStreamer-scenes.json");
        await File.WriteAllTextAsync(path, Editor.Project.Serialize());
        await Share.Default.RequestAsync(new ShareFileRequest("Scene collection", new ShareFile(path)));
    }
    public async Task ImportAsync()
    {
        var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Import a scene collection JSON" });
        if (picked is null) return;
        using var reader = new StreamReader(await picked.OpenReadAsync());
        var imported = ProjectDocument.Parse(await reader.ReadToEndAsync());
        Editor.Edit(p => { p.Name = imported.Name; p.Sources = imported.Sources; p.Scenes = imported.Scenes;
            p.SelectedSceneId = imported.SelectedSceneId; p.Output = imported.Output; });
    }
}
