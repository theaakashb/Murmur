using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The floating tip card (TipCard in app/TipsUI.swift): milestones and the voice-edit tip, in a topmost, non-activating panel at the
/// dictation indicator's spot, like <see cref="CallOfferPanel"/>. <see cref="TipPresenter"/> decides when it shows and hides.</summary>
internal sealed class TipPanel
{
    readonly Window window = new();
    readonly nint hwnd;
    readonly Border surface = new() { Style = MurmurStyle.Style("MurmurBubble"), Width = 340 };
    readonly AppServices s;
    readonly TipPresenter presenter;
    readonly Action<string, string> openSettings;
    readonly Action openHome;

    public TipPanel(AppServices services, TipPresenter presenter, Action<string, string> openSettings, Action openHome)
    {
        s = services; this.presenter = presenter; this.openSettings = openSettings; this.openHome = openHome;
        hwnd = WindowNative.GetWindowHandle(window);
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        window.AppWindow.IsShownInSwitchers = false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)((long)style | Native.WsExNoActivate | Native.WsExToolWindow | Native.WsExTopmost | Native.WsExLayered));
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);
        window.Content = surface;
        surface.CornerRadius = new CornerRadius(18);
        presenter.Shown += Show;
        presenter.Hidden += () => window.AppWindow.Hide();
    }

    static Button Quiet(string text, Action click, string? name = null, double size = 12)
    {
        var b = MurmurStyle.Named(new Button { Content = text, FontSize = size, MinHeight = 28, Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent) }, name ?? text);
        b.Click += (_, _) => click();
        return b;
    }

    Button Prominent(string text, Action click)
    {
        var b = MurmurStyle.Named(new Button { Content = text, FontSize = 12, MinHeight = 28, BorderThickness = new Thickness(0), Style = MurmurStyle.Style("AccentButtonStyle") }, text);
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 111, 240));
        foreach (var key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" }) b.Resources[key] = accent;
        b.Click += (_, _) => click();
        return b;
    }

    void Show(TipPresenter.Card card)
    {
        bool milestone = card.Kind == TipPresenter.Kind.Milestone;
        var title = MurmurStyle.Text(milestone ? "Milestone" : "Tip: fix it by voice", "MurmurSettingLabel");
        title.FontSize = 12; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var sparkles = new FontIcon { Glyph = "", FontSize = 12, Style = MurmurStyle.Style("MurmurMutedIcon"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var close = Quiet("✕", presenter.Dismiss, "Not now");
        close.Padding = new Thickness(6, 0, 6, 0); close.MinHeight = 24; close.MinWidth = 0;
        var head = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { sparkles, title, close } };
        Grid.SetColumn(title, 1); Grid.SetColumn(close, 2);
        var body = new StackPanel { Spacing = 8, Padding = new Thickness(10), Children = { head } };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        void Act() => presenter.Act(openSettings, openHome);
        if (milestone)
        {
            var m = card.Milestone!;
            var words = MurmurStyle.Text($"{m.Words:N0} words", "MurmurAccentText");
            words.FontSize = 22; words.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            var saved = MurmurStyle.Text(m.SecondsSaved >= 60 ? $"About {Tips.Duration(m.SecondsSaved)} of typing saved." : "Dictated with Murmur.", "MurmurSettingLabel");
            saved.FontSize = 12;
            body.Children.Add(words); body.Children.Add(saved);
            if (Tips.Next(m.Words) is { } next)
            {
                var hint = MurmurStyle.Text($"Next: {next:N0}", "MurmurCaption");
                hint.FontSize = 11; hint.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(hint);
            }
            var see = Prominent("See Home", Act);
            row.Children.Add(see); Grid.SetColumn(see, 1);
        }
        else
        {
            var how = MurmurStyle.Text("You rewrote a dictation by hand. " + presenter.VoiceEditHow(), "MurmurSettingLabel");
            how.FontSize = 12;
            body.Children.Add(how);
            var never = Quiet("Don’t suggest this", presenter.Never, size: 11);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Quiet("Not now", presenter.Dismiss), Prominent("Show Me", Act) } };
            row.Children.Add(never); row.Children.Add(right); Grid.SetColumn(right, 1);
        }
        body.Children.Add(row);
        surface.Child = body;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(surface, presenter.Spoken(card));

        var work = DisplayArea.Primary.WorkArea;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        surface.Measure(new Windows.Foundation.Size(surface.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(surface.DesiredSize.Width * scale), height = (int)Math.Ceiling(surface.DesiredSize.Height * scale);
        int x = work.X + (work.Width - width) / 2;
        int y = s.Settings.Get(Prefs.DictationIndicator) == DictationIndicator.Top ? work.Y + (int)(24 * scale) : work.Y + work.Height - height - (int)(48 * scale);
        Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(36 * scale), (int)(36 * scale)), true);
        Native.SetWindowPos(hwnd, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
    }
}
