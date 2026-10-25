namespace DeveLiveStreamer.Core;

public sealed class ProjectStorage(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string ProjectPath => Path.Combine(directory, "scene-collection.json");
    public string? RecoveryMessage { get; private set; }

    public async Task<ProjectDocument> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(ProjectPath)) return ProjectDocument.CreateDefault();
            try { return ProjectDocument.Parse(await File.ReadAllTextAsync(ProjectPath)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                if (File.Exists(ProjectPath + ".bak"))
                {
                    var recovered = ProjectDocument.Parse(await File.ReadAllTextAsync(ProjectPath + ".bak"));
                    RecoveryMessage = "The last saved collection was damaged. Your previous backup was restored.";
                    return recovered;
                }
                throw new InvalidDataException("Your saved scene collection could not be opened. Export or preserve the app data before resetting it.", ex);
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(string snapshot)
    {
        ProjectDocument.Parse(snapshot);
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var pending = ProjectPath + ".tmp";
            await File.WriteAllTextAsync(pending, snapshot);
            if (File.Exists(ProjectPath))
            {
                // Do not replace a good backup with the corrupted file we recovered from.
                try { ProjectDocument.Parse(await File.ReadAllTextAsync(ProjectPath)); File.Copy(ProjectPath, ProjectPath + ".bak", true); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
            }
            File.Move(pending, ProjectPath, true);
        }
        finally { gate.Release(); }
    }
}
