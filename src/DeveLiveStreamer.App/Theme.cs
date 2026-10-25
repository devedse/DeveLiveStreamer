using Microsoft.Maui.Controls.Shapes;

namespace DeveLiveStreamer.App;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb("#0D1218");
    public static readonly Color Panel = Color.FromArgb("#151D26");
    public static readonly Color Raised = Color.FromArgb("#202C38");
    public static readonly Color Line = Color.FromArgb("#2B3946");
    public static readonly Color Text = Color.FromArgb("#F1F5F9");
    public static readonly Color Muted = Color.FromArgb("#93A4B5");
    public static readonly Color Accent = Color.FromArgb("#5AE0C2");
    public static readonly Color AccentDark = Color.FromArgb("#143A35");
    public static readonly Color Danger = Color.FromArgb("#FF7C88");

    public static Label Label(string text, double size = 14, Color? color = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextColor = color ?? Text,
        FontFamily = bold ? "OpenSansSemibold" : "OpenSansRegular",
        HeightRequest = Math.Ceiling(size * 1.6),
        VerticalTextAlignment = TextAlignment.Center,
        LineBreakMode = LineBreakMode.TailTruncation
    };
    public static Label Caption(string text) => Label(text, 10, Muted, true);
    public static Border Card(View content, Thickness? padding = null) => new()
    {
        BackgroundColor = Panel, Stroke = new SolidColorBrush(Line), StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 14 },
        Padding = padding ?? new Thickness(14), Content = content
    };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, FontSize = 13, FontFamily = "OpenSansSemibold",
            HeightRequest = 44, MinimumWidthRequest = 44, Padding = new Thickness(14, 0), CornerRadius = 10,
            BackgroundColor = primary ? Accent : Raised, TextColor = primary ? Background : Text,
            BorderWidth = 0 };
        button.Clicked += (_, _) => action();
        return button;
    }
    public static ImageButton Icon(string name, string description, Action action)
    {
        var button = new ImageButton { Source = $"ic_{name}.png", BackgroundColor = Colors.Transparent,
            HeightRequest = 44, WidthRequest = 44, Padding = 12, CornerRadius = 9 };
        SemanticProperties.SetDescription(button, description);
        button.Clicked += (_, _) => action();
        return button;
    }
    public static Entry Entry(string value, string? placeholder = null) => new()
    {
        Text = value, Placeholder = placeholder, TextColor = Text, PlaceholderColor = Muted,
        BackgroundColor = Raised, FontSize = 14, HeightRequest = 46, Margin = new Thickness(0, 3, 0, 0),
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };
    public static VerticalStackLayout Field(string label, View control, string? help = null)
    {
        var stack = new VerticalStackLayout { Spacing = 4, Margin = new Thickness(0, 5, 0, 10) };
        stack.Children.Add(Label(label, 12, Muted)); stack.Children.Add(control);
        if (help is not null) stack.Children.Add(new Label { Text = help, FontSize = 11, TextColor = Muted });
        return stack;
    }
}
