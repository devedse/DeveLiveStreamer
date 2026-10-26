using System.Globalization;
using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App;

public sealed class SourcePropertiesPage : ContentPage
{
    public SourcePropertiesPage(EditorSession session)
    {
        Title = "Source properties"; BackgroundColor = Theme.Background; SafeAreaEdges = SafeAreaEdges.All; HideSoftInputOnTapped = true;
        var editor = session.Editor; var item = editor.SelectedItem!; var source = editor.Project.SourceFor(item);
        var name = Theme.Entry(source.Name);
        var text = new Editor { Text = source.Content, HeightRequest = 100, BackgroundColor = Theme.Raised, TextColor = Theme.Text, FontSize = 15 };
        var color = Theme.Entry(source.Color, "#RRGGBB");
        var font = Theme.Entry(source.FontSize.ToString(CultureInfo.InvariantCulture)); font.Keyboard = Keyboard.Numeric;
        var x = Number(item.X * editor.Project.Output.CanvasWidth); var y = Number(item.Y * editor.Project.Output.CanvasHeight);
        var width = Number(item.Width * editor.Project.Output.CanvasWidth); var height = Number(item.Height * editor.Project.Output.CanvasHeight);
        var opacity = Number(item.Opacity * 100);
        var cameraChoices = new List<CameraChoice>
        {
            new("", "Automatic rear camera", false),
            new("", "Automatic front camera", true)
        };
        cameraChoices.AddRange(CameraCatalog.Available());
        var selectedCamera = cameraChoices.FirstOrDefault(c => c.Id == source.CameraId && c.FrontCamera == source.FrontCamera);
        if (selectedCamera is null)
        {
            selectedCamera = new(source.CameraId, $"Camera ID {source.CameraId} (currently unavailable)", source.FrontCamera);
            cameraChoices.Add(selectedCamera);
        }
        var cameraPicker = new Picker { ItemsSource = cameraChoices, SelectedItem = selectedCamera,
            TextColor = Theme.Text, BackgroundColor = Theme.Raised, HeightRequest = 46, Title = "Select a camera" };
        var mirror = new Switch { IsToggled = source.Mirror, OnColor = Theme.Accent };
        var networkAudio = new Switch { IsToggled = source.AudioEnabled, OnColor = Theme.Accent };
        var body = new VerticalStackLayout { Spacing = 8, Padding = 20 };
        body.Children.Add(Theme.Caption(source.Kind.ToString().ToUpperInvariant() + " SOURCE"));
        body.Children.Add(Theme.Label(source.Name, 25, bold: true));
        body.Children.Add(Theme.Field("Name", name));
        if (source.Kind == SourceKind.Text) body.Children.Add(Theme.Field("Text", text));
        if (source.Kind == SourceKind.Network)
        {
            text.HeightRequest = 70; text.Keyboard = Keyboard.Url;
            var addresses = StudioHost.Engine.LocalAddresses();
            var example = addresses.Count > 0 ? addresses[0] : "<this phone's IP>";
            body.Children.Add(Theme.Field("Incoming stream URL", text,
                "Receive a push on this phone: rtmp://0.0.0.0:1935/live/stream or srt://0.0.0.0:9000 — then send to " +
                $"rtmp://{example}:1935/live/stream or srt://{example}:9000. Pull from a server: rtmp://server/app/key or srt://server:port?streamid=…"));
            body.Children.Add(Theme.Field("Include this stream's audio", networkAudio));
        }
        if (source.Kind is SourceKind.Text or SourceKind.Color) body.Children.Add(Theme.Field("Color", color, "Hex color, for example #5AE0C2"));
        if (source.Kind == SourceKind.Text) body.Children.Add(Theme.Field("Font size (canvas pixels)", font));
        if (source.Kind == SourceKind.Camera) body.Children.Add(Theme.Field("Camera device", cameraPicker,
            "Choose the camera for this source. Sources using the same camera share one capture. Running different cameras at once depends on your phone's hardware."));
        if (source.Kind is SourceKind.Camera or SourceKind.Image or SourceKind.Screen or SourceKind.Network or SourceKind.Text)
            body.Children.Add(Theme.Field("Mirror horizontally", mirror, source.Kind == SourceKind.Camera ? "Front cameras arrive mirrored like a selfie; turn this on to show viewers the unmirrored view." : null));
        if (source.Kind == SourceKind.Screen) body.Children.Add(new Label { Text = "Captures the display or app you choose in Android's dialog. Permission is requested when this screen source is active while you're live.", FontSize = 12, TextColor = Theme.Muted });
        body.Children.Add(Theme.Caption("TRANSFORM  /  CANVAS PIXELS"));
        body.Children.Add(Pair(Theme.Field("Position X", x), Theme.Field("Position Y", y)));
        body.Children.Add(Pair(Theme.Field("Width", width), Theme.Field("Height", height)));
        body.Children.Add(Theme.Field("Opacity (%)", opacity));
        body.Children.Add(new Label { Text = "Select Move or Resize in the editor, then drag anywhere in the preview to adjust the selected source. Source content is shared by scenes that reference it.", FontSize = 11, TextColor = Theme.Muted });
        body.Children.Add(Pair(Theme.Button("Move up", () => editor.MoveSelected(1)), Theme.Button("Move down", () => editor.MoveSelected(-1))));
        var delete = Theme.Button("Delete source from scene", async () =>
        {
            if (await DisplayAlertAsync("Remove source?", "Other scenes using this source will keep it.", "Remove", "Cancel"))
            { editor.DeleteSelected(); await Navigation.PopModalAsync(); }
        }); delete.TextColor = Theme.Danger; body.Children.Add(delete);
        var actions = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10, Padding = 16, BackgroundColor = Theme.Panel };
        actions.Add(Theme.Button("Cancel", async () => await Navigation.PopModalAsync()), 0);
        actions.Add(Theme.Button("Apply changes", async () =>
        {
            try
            {
                if (source.Kind is SourceKind.Text or SourceKind.Color) _ = Color.FromArgb(color.Text);
                if (source.Kind == SourceKind.Network && !string.IsNullOrWhiteSpace(text.Text))
                    await Streaming.Ingest.IncomingStream.Create(text.Text.Trim()).DisposeAsync(); // validates the URL
                var px = Parse(x); var py = Parse(y); var pw = Parse(width); var ph = Parse(height); var alpha = Parse(opacity) / 100;
                var fontSize = Parse(font);
                editor.Edit(p =>
                {
                    var current = p.SelectedScene.Items.First(i => i.Id == item.Id); var definition = p.SourceFor(current);
                    definition.Name = string.IsNullOrWhiteSpace(name.Text) ? source.Kind.ToString() : name.Text.Trim();
                    if (source.Kind is SourceKind.Text or SourceKind.Network) definition.Content = source.Kind == SourceKind.Network ? (text.Text ?? "").Trim() : text.Text ?? "";
                    definition.Mirror = mirror.IsToggled; definition.AudioEnabled = networkAudio.IsToggled;
                    if (source.Kind is SourceKind.Text or SourceKind.Color) definition.Color = color.Text;
                    definition.FontSize = fontSize;
                    if (source.Kind == SourceKind.Camera && cameraPicker.SelectedItem is CameraChoice choice)
                    { definition.CameraId = choice.Id; definition.FrontCamera = choice.FrontCamera; }
                    current.X = px / p.Output.CanvasWidth; current.Y = py / p.Output.CanvasHeight;
                    current.Width = pw / p.Output.CanvasWidth; current.Height = ph / p.Output.CanvasHeight; current.Opacity = alpha;
                });
                await Navigation.PopModalAsync();
            }
            catch (Exception ex) { await DisplayAlertAsync("Check source settings", ex.Message, "OK"); }
        }, true), 1);
        var root = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        root.Add(new ScrollView { Content = body }, 0, 0); root.Add(actions, 0, 1); Content = root;
    }
    private static Entry Number(double value) { var entry = Theme.Entry(value.ToString("0.##", CultureInfo.InvariantCulture)); entry.Keyboard = Keyboard.Numeric; return entry; }
    private static double Parse(Entry entry) => double.TryParse(entry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new InvalidDataException("Enter a valid number for each transform and size.");
    internal static Grid Pair(View left, View right)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 12 };
        grid.Add(left, 0); grid.Add(right, 1); return grid;
    }
}
