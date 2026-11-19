using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Murmur.App;

internal static class Fluent
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static T Named<T>(T control, string name) where T : FrameworkElement
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    public static TextBlock Text(string text, string style = "MurmurDescription") =>
        new() { Text = text, Style = Style(style), TextWrapping = TextWrapping.Wrap };

    public static Border Card(UIElement child) => new() { Child = child, Style = Style("MurmurCard") };

    public static void SetTitleBar(Window window, TitleBar caption)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(caption);
        window.AppWindow.TitleBar.PreferredTheme = App.CaptionTheme;
    }

    // Initial dimensions are in device-independent pixels, like the XAML layout.
    public static void SetInitialSize(Window window, int width, int height)
    {
        window.Activated += SizeOnFirstActivation;
        void SizeOnFirstActivation(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            window.Activated -= SizeOnFirstActivation;
            var appWindow = window.AppWindow;
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            double scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
            int w = Math.Min((int)(width * scale), work.Width);
            int h = Math.Min((int)(height * scale), work.Height);
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        }
    }
}
