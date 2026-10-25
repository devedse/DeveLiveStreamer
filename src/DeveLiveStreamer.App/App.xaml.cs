using Microsoft.Extensions.DependencyInjection;

namespace DeveLiveStreamer.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new NavigationPage(new EditorPage(new EditorSession()))
        {
            BarBackgroundColor = Theme.Background,
            BarTextColor = Theme.Text
        });
	}
}
