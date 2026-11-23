using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Murmur.App;

internal static class MurmurStyle
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static T Named<T>(T control, string name) where T : FrameworkElement
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    public static TextBlock Text(string text, string style = "MurmurDescription") =>
        new() { Text = text, Style = Style(style), TextWrapping = TextWrapping.Wrap };

    /// <summary>A brush from the element's current theme dictionary; resources set in code do not follow theme changes by themselves.</summary>
    public static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key, FrameworkElement context)
    {
        var name = context.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        return (Microsoft.UI.Xaml.Media.Brush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[name])[key];
    }

    public enum PillKind { Filled, Outline, Plain }

    /// <summary>The Mac's InkButtonStyle: an ink capsule (filled), an ink outline, or plain ink text. The Button is only a hit target around the styled Border.</summary>
    public static Button Pill(string text, Action click, PillKind kind = PillKind.Outline, string? name = null, double height = 36)
    {
        var label = new TextBlock { Text = text, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Style = Style(kind == PillKind.Filled ? "MurmurPillText" : "MurmurSettingLabel") };
        label.FontSize = 14; label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var face = new Border { Child = label, Height = height, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(height / 2), Style = Style(kind switch { PillKind.Filled => "MurmurPillFilled", PillKind.Outline => "MurmurPillOutline", _ => "MurmurNavOff" }) };
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var b = new Button { Content = face, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), Background = clear, CornerRadius = new CornerRadius(height / 2) };
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" }) b.Resources[key] = clear;
        Named(b, name ?? text);
        b.Click += (_, _) => click();
        b.IsEnabledChanged += (_, _) => face.Opacity = b.IsEnabled ? 1 : 0.4;
        return b;
    }

    /// <summary>The Mac's SettingInfoButton: a small (i) that opens a flyout with the text.</summary>
    public static Button Info(string text, string about)
    {
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Style = Style("MurmurDescription") };
        var b = new Button { Content = new FontIcon { Glyph = "\uE946", FontSize = 13, Style = Style("MurmurMutedIcon") }, Padding = new Thickness(4), MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), Flyout = new Flyout { Content = body } };
        return Named(b, about);
    }

    public static Border HRule(bool soft = false) => new() { Height = 1, Style = Style("MurmurRuleLine"), Opacity = soft ? 0.5 : 1 };

    /// <summary>A page like the Mac's: the 64 pt serif title and a lede, with the content below.</summary>
    public static StackPanel Page(string title, string lede)
    {
        var heading = new TextBlock { Text = title, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Fonts/InstrumentSerif-Regular.ttf#Instrument Serif"), CharacterSpacing = -20, Style = Style("MurmurHeading") };
        heading.FontSize = 64; heading.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        var lead = Text(lede, "MurmurDescription");
        lead.FontSize = 16; lead.Margin = new Thickness(0, 6, 0, 0);
        heading.TextWrapping = TextWrapping.NoWrap;
        // At a narrow window the 64 px title would clip: it scales down to fit the width, and never up (the Mac's wider minimum window never needs this).
        var fit = new Viewbox { Child = heading, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
        return new StackPanel { Padding = new Thickness(40, 24, 40, 32), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left, Children = { fit, lead } };
    }

    public static Border Card(UIElement child) => new() { Child = child, Style = Style("MurmurCard"), Margin = new Thickness(0, 0, 0, 8) };

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
