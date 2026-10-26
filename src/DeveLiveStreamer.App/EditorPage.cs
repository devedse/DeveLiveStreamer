using DeveLiveStreamer.App.Controls;
using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App;

public sealed class EditorPage : ContentPage
{
    private readonly EditorSession session;
    private readonly SceneCanvas canvas = new();
    private readonly VerticalStackLayout sources = new() { Spacing = 3 };
    private readonly Button sceneTitle;
    private readonly Label outputInfo = Theme.Label("1920 × 1080 · 30 fps", 11, Theme.Muted);
    private readonly Label saved = Theme.Label("Saved on this device", 10, Theme.Muted);
    private readonly Label sourceCount = Theme.Caption("4 SOURCES");
    private readonly Label selectedName = Theme.Label("Tap a source to select it", 12, Theme.Muted);
    private readonly Button properties;
    private readonly Button moveSource, resizeSource, sourceActions;
    private readonly ScrollView editorScroll;
    private readonly Border previewBorder, sourceActionBar;
    private readonly Label interactionHint = Theme.Label("Select a source in the preview or list", 10, Theme.Muted);
    private readonly Button allowCamera;
    private readonly Button goLive;
    private readonly Label liveChip = Theme.Label("●  OFFLINE", 10, Theme.Muted, true);
    private readonly Border liveChipCard;
    private readonly Label destination = new() { FontSize = 11, TextColor = Theme.Muted, LineHeight = 1.2, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(12, 0) };
    private readonly Label footerTitle = Theme.Label("●  Scene collection", 12, Theme.Accent, true);
    private readonly ImageButton undo, redo;
    private readonly IStudioEngine engine = StudioHost.Engine;
    private bool initialized;

    public EditorPage(EditorSession session)
    {
        this.session = session;
        Title = "Studio"; BackgroundColor = Theme.Background; SafeAreaEdges = SafeAreaEdges.All;
        NavigationPage.SetHasNavigationBar(this, false);

        var header = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection {
            new(40), new(GridLength.Star), new(44), new(44) }, Padding = new Thickness(16, 6), HeightRequest = 62 };
        header.Add(new Image { Source = "brand_logo.png", HeightRequest = 32, WidthRequest = 32 }, 0);
        var brand = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(7, 0, 0, 0) };
        brand.Children.Add(Theme.Label("DeveLiveStreamer", 15, bold: true));
        brand.Children.Add(Theme.Label("YOUR MOBILE STUDIO", 8, Theme.Muted)); header.Add(brand, 1);
        header.Add(Theme.Icon("folder", "Scene collection menu", () => Run(CollectionMenu)), 2);
        header.Add(Theme.Icon("settings", "Open settings", () => Run(OpenSettings)), 3);

        var pageContent = new VerticalStackLayout { Spacing = 15, Padding = new Thickness(16, 8, 16, 18) };
        var previewHeading = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        sceneTitle = Theme.Button("Camera ▾", () => Run(ChooseScene));
        sceneTitle.BackgroundColor = Colors.Transparent; sceneTitle.FontSize = 20;
        sceneTitle.Padding = new Thickness(0); sceneTitle.HeightRequest = 36; sceneTitle.HorizontalOptions = LayoutOptions.Start;
        SemanticProperties.SetDescription(sceneTitle, "Choose scene");
        var titleStack = new VerticalStackLayout { Spacing = 3 }; titleStack.Children.Add(Theme.Caption("SCENE PREVIEW")); titleStack.Children.Add(sceneTitle);
        previewHeading.Add(titleStack, 0);
        liveChipCard = Theme.Card(liveChip, new Thickness(10, 6));
        liveChipCard.VerticalOptions = LayoutOptions.Center;
        var chipTap = new TapGestureRecognizer(); chipTap.Tapped += (_, _) => Run(ShowStreamDetails); liveChipCard.GestureRecognizers.Add(chipTap);
        SemanticProperties.SetDescription(liveChipCard, "Stream status and details");
        previewHeading.Add(liveChipCard, 1); pageContent.Children.Add(previewHeading);

        canvas.HeightRequest = 200;
        canvas.SizeChanged += (_, _) => UpdateCanvasAspect();
        previewBorder = Theme.Card(canvas, new Thickness(0));
        previewBorder.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 };
        pageContent.Children.Add(previewBorder);

        var previewToolbar = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(44), new(44) } };
        previewToolbar.Add(outputInfo, 0);
        undo = Theme.Icon("undo", "Undo edit", () => session.Editor.Undo());
        redo = Theme.Icon("redo", "Redo edit", () => session.Editor.Redo());
        previewToolbar.Add(undo, 1); previewToolbar.Add(redo, 2);
        pageContent.Children.Add(previewToolbar);

        var selectionControls = new VerticalStackLayout { Spacing = 5 };
        selectedName.FontSize = 12;
        selectionControls.Children.Add(selectedName);
        var actionButtons = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection {
            new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 5 };
        moveSource = Theme.Button("Move", () => ToggleInteraction(SourceInteractionMode.Move));
        resizeSource = Theme.Button("Resize", () => ToggleInteraction(SourceInteractionMode.Resize));
        properties = Theme.Button("Properties", () => Run(OpenProperties));
        sourceActions = Theme.Button("Actions ▾", () => Run(SourceActions));
        var controls = new[] { moveSource, resizeSource, properties, sourceActions };
        for (var i = 0; i < controls.Length; i++)
        {
            controls[i].FontSize = 11; controls[i].Padding = new Thickness(4, 0);
            controls[i].IsEnabled = false; controls[i].Opacity = .35;
            actionButtons.Add(controls[i], i);
        }
        SemanticProperties.SetDescription(moveSource, "Move selected source");
        SemanticProperties.SetDescription(resizeSource, "Resize selected source");
        SemanticProperties.SetDescription(sourceActions, "Selected source actions");
        selectionControls.Children.Add(actionButtons); selectionControls.Children.Add(interactionHint);
        sourceActionBar = Theme.Card(selectionControls, new Thickness(10));
        pageContent.Children.Add(sourceActionBar);

        var sourcePanel = new VerticalStackLayout { Spacing = 8 };
        var sourceHeading = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        sourceHeading.Add(sourceCount, 0); sourceHeading.Add(Theme.Button("+ Add source", () => Run(AddSource)), 1);
        sourcePanel.Children.Add(sourceHeading); sourcePanel.Children.Add(sources);
        pageContent.Children.Add(Theme.Card(sourcePanel));

        allowCamera = Theme.Button("Allow camera access", () => Run(AllowCamera));
        pageContent.Children.Add(allowCamera);
        var destinationTap = new TapGestureRecognizer(); destinationTap.Tapped += (_, _) => Run(OpenSettings); destination.GestureRecognizers.Add(destinationTap);
        pageContent.Children.Add(destination);

        var footer = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) }, Padding = new Thickness(18, 9), BackgroundColor = Theme.Panel, HeightRequest = 60 };
        var footerText = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        footerText.Children.Add(footerTitle); footerText.Children.Add(saved); footer.Add(footerText, 0);
        goLive = Theme.Button("Go live", () => Run(ToggleLive), true);
        footer.Add(goLive, 1);
        var root = new Grid { RowDefinitions = new RowDefinitionCollection { new(62), new(GridLength.Star), new(60) } };
        editorScroll = new ScrollView { Content = pageContent };
        root.Add(header, 0, 0); root.Add(editorScroll, 0, 1); root.Add(footer, 0, 2); Content = root;
        canvas.SelectionChanged += (_, _) => RenderSources();
        session.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(Render);
        session.SaveStateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateLiveStatus);
        engine.StatusChanged += (_, _) => UpdateLiveStatus();
        engine.SourcesChanged += (_, _) => RenderSources();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (!initialized) { await session.InitializeAsync(); initialized = true; canvas.SetSession(session); }
            Render();
            if (session.RecoveryMessage is { } message) await DisplayAlertAsync("Collection recovered", message, "OK");
        }
        catch (Exception ex) { await DisplayAlertAsync("Could not open collection", ex.Message, "OK"); }
    }
    private void UpdateCanvasAspect()
    {
        if (canvas.Width <= 0) return;
        var output = session.Editor.Project.Output;
        var height = canvas.Width * output.CanvasHeight / output.CanvasWidth;
        if (Math.Abs(canvas.HeightRequest - height) > 1) canvas.HeightRequest = height;
    }
    private void Render()
    {
        var editor = session.Editor; var p = editor.Project;
        sceneTitle.Text = p.SelectedScene.Name + " ▾";
        outputInfo.Text = $"{p.Output.OutputWidth} × {p.Output.OutputHeight} · {p.Output.Fps:0.##} fps";
        undo.IsEnabled = editor.CanUndo; redo.IsEnabled = editor.CanRedo;
        undo.Opacity = editor.CanUndo ? 1 : .3; redo.Opacity = editor.CanRedo ? 1 : .3;
        allowCamera.IsVisible = engine.IsSupported && !engine.CameraPermitted && p.SelectedScene.Items.Any(i => p.SourceFor(i).Kind == SourceKind.Camera);
        UpdateCanvasAspect(); RenderSources(); canvas.Refresh(); UpdateLiveStatus();
    }

    private void UpdateLiveStatus()
    {
        var status = engine.Status;
        var output = session.Editor.Project.Output;
        var (chip, color) = status.State switch
        {
            LiveState.Live => ($"●  LIVE  {Duration(status.Duration)}", Theme.Danger),
            LiveState.Connecting or LiveState.Starting => ("●  CONNECTING", Theme.Accent),
            LiveState.Reconnecting => ("●  RECONNECTING", Theme.Accent),
            LiveState.Stopping => ("●  ENDING", Theme.Muted),
            _ => ("●  OFFLINE", Theme.Muted)
        };
        liveChip.Text = chip; liveChip.TextColor = color;
        liveChipCard.Stroke = new SolidColorBrush(status.State == LiveState.Live ? Theme.Danger : Theme.Line);
        goLive.Text = status.IsActive ? "End stream" : "Go live";
        goLive.BackgroundColor = status.IsActive ? Theme.Danger : Theme.Accent;
        goLive.IsEnabled = status.State is not (LiveState.Starting or LiveState.Stopping);
        if (status.IsActive)
        {
            footerTitle.Text = status.State == LiveState.Live ? "●  Live" : "●  " + status.State;
            footerTitle.TextColor = status.State == LiveState.Live ? Theme.Danger : Theme.Accent;
            saved.Text = status.State == LiveState.Live
                ? $"{status.BitrateKbps / 1000:0.0} Mbps · {status.EncodeFps:0} fps" + (status.RttMs is { } rtt ? $" · RTT {rtt:0} ms" : "") + (status.DroppedFrames > 0 ? $" · {status.DroppedFrames} dropped" : "")
                : status.Message;
            destination.Text = (status.Message.Length > 0 && status.State == LiveState.Live ? status.Message + "\n" : "") + $"Streaming to {status.Destination}  ·  tap the status for details";
        }
        else
        {
            footerTitle.Text = "●  Scene collection"; footerTitle.TextColor = Theme.Accent;
            saved.Text = session.SaveStatus;
            destination.Text = !engine.IsSupported ? "Live streaming is available in the Android build."
                : string.IsNullOrWhiteSpace(output.ServerUrl) ? "Add your SRT or RTMP server in Settings › Stream to go live."
                : $"Ready to stream to {DescribeDestination(output)}  ·  {output.OutputWidth}×{output.OutputHeight} @ {output.Fps:0.##} fps"
                  + (status.Message.Length > 0 ? "\n" + status.Message : "");
        }
    }

    private static string DescribeDestination(OutputSettings output)
    {
        try
        {
            return Streaming.StreamEndpoint.Parse(output.ServerUrl, output.StreamKey) switch
            {
                Streaming.SrtEndpoint srt => $"SRT {srt.Host}:{srt.Port}" + (srt.StreamId is { Length: > 0 } id ? $" ({id})" : ""),
                Streaming.RtmpEndpoint rtmp => $"{(rtmp.Tls ? "RTMPS" : "RTMP")} {rtmp.Host}/{rtmp.App}",
                var other => other.Host
            };
        }
        catch (FormatException ex) { return "an invalid URL: " + ex.Message; }
    }

    private static string Duration(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

    private async Task ToggleLive()
    {
        if (engine.Status.IsActive)
        {
            if (await DisplayAlertAsync("End the stream?", "Viewers will see the stream end.", "End stream", "Keep streaming")) await engine.StopAsync();
            return;
        }
        if (string.IsNullOrWhiteSpace(session.Editor.Project.Output.ServerUrl))
        {
            await DisplayAlertAsync("Where should we stream?", "Add your SRT or RTMP server URL in Settings › Stream first.", "Open settings");
            await OpenSettings();
            return;
        }
        try { await engine.StartAsync(session.Editor.Project); }
        catch (Exception ex) { await DisplayAlertAsync("Could not go live", ex.Message, "OK"); }
    }

    private async Task ShowStreamDetails()
    {
        var status = engine.Status;
        var lines = new List<string>();
        if (status.IsActive)
        {
            lines.Add($"{status.State} · {Duration(status.Duration)} · {status.Destination}");
            lines.Add($"Output {status.BitrateKbps:0} kbps · rendered {status.RenderFps:0.#} fps · encoded {status.EncodeFps:0.#} fps · dropped {status.DroppedFrames}"
                + (status.RttMs is { } rtt ? $" · RTT {rtt:0.#} ms" : ""));
            if (status.Message.Length > 0) lines.Add(status.Message);
        }
        else lines.Add(status.Message.Length > 0 ? status.Message : "Offline. Tap Go live to start streaming the current scene.");
        lines.AddRange(status.Warnings);
        await DisplayAlertAsync("Stream details", string.Join("\n\n", lines), "Close");
    }
    private void RenderSources()
    {
        canvas.SyncSelection();
        var editor = session.Editor; sources.Children.Clear();
        sourceCount.Text = $"SOURCES  /  {editor.Project.SelectedScene.Items.Count:00}";
        foreach (var item in editor.Project.SelectedScene.Items.AsEnumerable().Reverse())
        {
            var source = editor.Project.SourceFor(item); var selected = item.Id == editor.SelectedItemId;
            var row = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(30), new(GridLength.Star), new(40), new(40) },
                Padding = new Thickness(8, 0, 0, 0), HeightRequest = 46, BackgroundColor = selected ? Theme.Raised : Colors.Transparent };
            row.Add(new Image { Source = $"ic_{KindIcon(source.Kind)}.png", HeightRequest = 17, WidthRequest = 17 }, 0);
            var name = Theme.Label(source.Name, 12, item.Visible ? Theme.Text : Theme.Muted);
            var detail = item.Visible ? engine.SourceStatus(source.Id) : null;
            if (detail is null) row.Add(name, 1);
            else
            {
                row.HeightRequest = 58;
                var stack = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
                stack.Children.Add(name);
                stack.Children.Add(new Label { Text = detail, FontSize = 10, TextColor = Theme.Accent, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 });
                row.Add(stack, 1);
            }
            row.Add(Theme.Icon(item.Visible ? "eye" : "hidden", "Toggle source visibility", () => editor.Edit(_ => item.Visible = !item.Visible)), 2);
            row.Add(Theme.Icon(item.Locked ? "lock" : "unlock", "Toggle source lock", () => editor.Edit(_ => item.Locked = !item.Locked)), 3);
            SemanticProperties.SetDescription(row, $"Source {source.Name}{(selected ? ", selected" : "")}");
            var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) =>
            {
                canvas.SelectItem(item.Id);
                Run(() => editorScroll.ScrollToAsync(sourceActionBar, ScrollToPosition.MakeVisible, true));
            };
            row.GestureRecognizers.Add(tap);
            sources.Children.Add(row);
        }
        if (editor.Project.SelectedScene.Items.Count == 0)
            sources.Children.Add(Theme.Label("An empty canvas. Add your first source.", 12, Theme.Muted));
        RenderSelectionActions();
    }

    private void ToggleInteraction(SourceInteractionMode mode)
    {
        canvas.Mode = canvas.Mode == mode ? SourceInteractionMode.Select : mode;
        RenderSelectionActions();
        if (canvas.Mode != SourceInteractionMode.Select)
            Run(() => editorScroll.ScrollToAsync(previewBorder, ScrollToPosition.MakeVisible, true));
    }

    private void RenderSelectionActions()
    {
        var editor = session.Editor;
        var item = editor.SelectedItem;
        selectedName.Text = item is null ? "No source selected" : editor.Project.SourceFor(item).Name + (item.Locked ? " · locked" : "");
        var canTransform = item is { Visible: true, Locked: false, Opacity: > 0 };
        if (!canTransform) canvas.Mode = SourceInteractionMode.Select;
        moveSource.IsEnabled = resizeSource.IsEnabled = canTransform;
        properties.IsEnabled = sourceActions.IsEnabled = item is not null;
        foreach (var button in new[] { moveSource, resizeSource, properties, sourceActions }) button.Opacity = button.IsEnabled ? 1 : .35;
        foreach (var (button, mode) in new[] { (moveSource, SourceInteractionMode.Move), (resizeSource, SourceInteractionMode.Resize) })
        {
            var active = canvas.Mode == mode;
            button.BackgroundColor = active ? Theme.Accent : Theme.Raised;
            button.TextColor = active ? Theme.Background : Theme.Text;
        }
        interactionHint.Text = item is null ? "Select a source in the preview or list" : canvas.Mode switch
        {
            SourceInteractionMode.Move => "Drag anywhere to move · tap Move to finish",
            SourceInteractionMode.Resize => "Drag anywhere to resize · tap Resize to finish",
            _ => item.Locked ? "Unlock in Actions to move or resize" : "Choose Move or Resize to adjust this source"
        };
    }

    private async Task SourceActions()
    {
        canvas.CancelGesture();
        var editor = session.Editor;
        if (editor.SelectedItem is not { } item) return;
        var options = new List<string>();
        if (!item.Locked) options.AddRange(["Center", "Center horizontally", "Center vertically", "Fit to canvas", "Stretch to canvas", "Mirror horizontally"]);
        options.AddRange(["Move up", "Move down", "Bring to front", "Send to back", item.Visible ? "Hide" : "Show", item.Locked ? "Unlock" : "Lock", "Remove from scene"]);
        var action = await DisplayActionSheetAsync(editor.Project.SourceFor(item).Name, "Cancel", null, options.ToArray());
        if (editor.SelectedItem?.Id != item.Id) return;
        switch (action)
        {
            case "Center": editor.CenterSelected(); break;
            case "Center horizontally": editor.CenterSelected(vertical: false); break;
            case "Center vertically": editor.CenterSelected(horizontal: false); break;
            case "Fit to canvas": editor.FitSelected(); break;
            case "Stretch to canvas": editor.StretchSelected(); break;
            case "Mirror horizontally": editor.MirrorSelected(); break;
            case "Move up": editor.MoveSelected(1); break;
            case "Move down": editor.MoveSelected(-1); break;
            case "Bring to front": editor.MoveSelected(editor.Project.SelectedScene.Items.Count); break;
            case "Send to back": editor.MoveSelected(-editor.Project.SelectedScene.Items.Count); break;
            case "Hide": case "Show": editor.Edit(_ => item.Visible = !item.Visible); break;
            case "Lock": case "Unlock": editor.Edit(_ => item.Locked = !item.Locked); break;
            case "Remove from scene":
                if (await DisplayAlertAsync("Remove source?", "Other scenes using this source will keep it.", "Remove", "Cancel")) editor.DeleteSelected();
                break;
        }
    }
    private static string KindIcon(SourceKind kind) => kind switch { SourceKind.Camera => "camera", SourceKind.Text => "text", SourceKind.Image => "image", SourceKind.Color => "square", SourceKind.Screen => "display", _ => "link" };
    private async void Run(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { await DisplayAlertAsync("Could not complete that", ex.Message, "OK"); }
    }
    private async Task AddScene()
    {
        var name = await DisplayPromptAsync("New scene", "Give this scene a name", "Create", "Cancel", placeholder: "Scene name");
        if (name is not null) session.Editor.AddScene(name);
    }
    private async Task ChooseScene()
    {
        var editor = session.Editor;
        var choices = editor.Project.Scenes.Select(s => s.Name).Concat(["＋ New scene", "Scene actions…"]).ToArray();
        var choice = await DisplayActionSheetAsync("Choose scene", "Cancel", null, choices);
        canvas.CancelGesture();
        if (choice == "＋ New scene") await AddScene();
        else if (choice == "Scene actions…") await SceneMenu();
        else if (editor.Project.Scenes.FirstOrDefault(s => s.Name == choice) is { } scene) editor.SelectScene(scene.Id);
    }
    private async Task SceneMenu()
    {
        var action = await DisplayActionSheetAsync("Scene actions", "Cancel", null, "Rename scene", "Duplicate scene", "Delete scene");
        if (action == "Rename scene")
        {
            var name = await DisplayPromptAsync("Rename scene", "Scene name", initialValue: session.Editor.Project.SelectedScene.Name);
            if (!string.IsNullOrWhiteSpace(name)) session.Editor.Edit(p => p.SelectedScene.Name = name.Trim());
        }
        if (action == "Duplicate scene") session.Editor.DuplicateScene();
        if (action == "Delete scene" && await DisplayAlertAsync("Delete scene?", "Sources used by other scenes will be kept.", "Delete", "Cancel")) session.Editor.DeleteScene();
    }
    private async Task AddSource()
    {
        var action = await DisplayActionSheetAsync("Add a source", "Cancel", null, "Camera", "Text", "Image", "Color", "Device screen", "Network stream (RTMP/SRT)");
        SourceDefinition? source = action switch
        {
            "Camera" => new() { Kind = SourceKind.Camera, Name = "Camera" },
            "Text" => new() { Kind = SourceKind.Text, Name = "Text", Content = "Your text here" },
            "Color" => new() { Kind = SourceKind.Color, Name = "Color", Color = "#234850" },
            "Device screen" => new() { Kind = SourceKind.Screen, Name = "Device screen" },
            "Network stream (RTMP/SRT)" => new() { Kind = SourceKind.Network, Name = "Network stream", Content = "rtmp://0.0.0.0:1935/live/stream" },
            _ => null
        };
        if (action == "Image")
        {
            var image = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose an image", FileTypes = FilePickerFileType.Images });
            if (image is null) return;
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }.Contains(extension)) throw new InvalidDataException("Choose a PNG, JPEG, WebP, GIF or BMP image.");
            var directory = Path.Combine(FileSystem.AppDataDirectory, "images"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
            await using (var input = await image.OpenReadAsync())
            await using (var output = File.Create(path)) await input.CopyToAsync(output);
            source = new() { Kind = SourceKind.Image, Name = Path.GetFileNameWithoutExtension(image.FileName), Content = path };
        }
        if (source is null) return;
        session.Editor.AddSource(source); await OpenProperties();
    }
    private async Task OpenProperties()
    {
        if (session.Editor.SelectedItem is not null) await Navigation.PushModalAsync(new NavigationPage(new SourcePropertiesPage(session)) { BarBackgroundColor = Theme.Background, BarTextColor = Theme.Text });
    }
    private async Task OpenSettings() => await Navigation.PushModalAsync(new NavigationPage(new SettingsPage(session)) { BarBackgroundColor = Theme.Background, BarTextColor = Theme.Text });
    private async Task AllowCamera()
    {
        if (!await engine.RequestCameraAsync()) { await DisplayAlertAsync("Camera access", "Allow camera access in Android Settings to use camera sources.", "OK"); return; }
        Render();
    }
    private async Task CollectionMenu()
    {
        var action = await DisplayActionSheetAsync("Scene collection", "Cancel", null, "Export collection", "Import collection", "About this build");
        if (action == "Export collection") await session.ExportAsync();
        if (action == "Import collection" && await DisplayAlertAsync("Import collection?", "This replaces the current collection. You can undo the import.", "Import", "Cancel")) await session.ImportAsync();
        if (action == "About this build") await DisplayAlertAsync($"DeveLiveStreamer · {AppInfo.Current.VersionString}", "OBS-style scenes composed on the GPU and streamed over SRT or RTMP(S). Cameras, the device screen, incoming RTMP/SRT streams, images, text and colors can be layered; microphone, device audio and network audio are mixed into AAC.\n\nThe preview shows the exact frame that is streamed. Scenes stay on your device. Export includes configuration, not imported image files.", "Got it");
    }
}
