using Microsoft.UI.Xaml;

namespace Murmur.App;

public partial class App : Application
{
    public App() { InitializeComponent(); }

    // shortcut: placeholder window until the tray shell lands (M2), then the app starts in the tray.
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new Window { Title = "Murmur" };
        window.Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Murmur" };
        window.Activate();
    }
}
