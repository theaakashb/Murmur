using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The meeting-detection offer (app/CallOfferUI.swift) in its own topmost, non-activating panel at the dictation indicator's spot,
/// so clicking it leaves the user's app (and its cursor) in front. It hides itself after <see cref="CallDetector.OfferLifetime"/> seconds.</summary>
internal sealed partial class CallOfferPanel
{
    readonly Window window = new();
    readonly nint hwnd;
    readonly Border surface = new() { Style = MurmurStyle.Style("MurmurBubble"), Width = 340 };
    readonly Controller controller;
    readonly AppServices s;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer hideTimer;

    public CallOfferPanel(AppServices services, Controller controller, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        s = services;
        this.controller = controller;
        hwnd = WindowNative.GetWindowHandle(window);
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        window.AppWindow.IsShownInSwitchers = false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)((long)style | Native.WsExNoActivate | Native.WsExToolWindow | Native.WsExTopmost | Native.WsExLayered));
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);
        window.Content = surface;
        surface.CornerRadius = new CornerRadius(18);
        hideTimer = ui.CreateTimer();
        hideTimer.Interval = TimeSpan.FromSeconds(CallDetector.OfferLifetime);
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => Hide();
        controller.CallOffered += Show;
    }

    static Button Quiet(string text, Action click, string? name = null)
    {
        var b = MurmurStyle.Named(new Button { Content = text, FontSize = 12, MinHeight = 28, Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent) }, name ?? text);
        b.Click += (_, _) => click();
        return b;
    }

    void Show(CallOffer found)
    {
        if (controller.MeetingLive) return;
        var title = MurmurStyle.Text(found.Text, "MurmurSettingLabel");
        title.FontSize = 12; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var wave = new FontIcon { Glyph = "", FontSize = 12, Style = MurmurStyle.Style("MurmurMutedIcon"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var close = Quiet("✕", Hide, "Not now");
        close.Padding = new Thickness(6, 0, 6, 0); close.MinHeight = 24; close.MinWidth = 0;
        var head = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { wave, title, close } };
        Grid.SetColumn(title, 1); Grid.SetColumn(close, 2);
        var never = Quiet(found.NeverLabel, () => { CallDetectionSettings.AddNever(s.Settings, found.App.ProcessName); Hide(); });
        never.FontSize = 11;
        var take = MurmurStyle.Named(new Button { Content = "Take Notes", FontSize = 12, MinHeight = 28, Style = MurmurStyle.Style("AccentButtonStyle") }, "Take Notes");
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 111, 240));
        foreach (var key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" }) take.Resources[key] = accent;
        take.Click += (_, _) => { Hide(); controller.StartMeeting(found.Source); };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Quiet("Not now", Hide), take } };
        row.Children.Add(never); row.Children.Add(right); Grid.SetColumn(right, 1);
        var question = MurmurStyle.Text(found.Question, "MurmurSettingLabel"); question.FontSize = 12;
        surface.Child = new StackPanel { Spacing = 8, Padding = new Thickness(10), Children = { head, question, row } };

        var work = DisplayArea.Primary.WorkArea;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        surface.Measure(new Windows.Foundation.Size(surface.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(surface.DesiredSize.Width * scale), height = (int)Math.Ceiling(surface.DesiredSize.Height * scale);
        int x = work.X + (work.Width - width) / 2;
        int y = s.Settings.Get(Prefs.DictationIndicator) == DictationIndicator.Top ? work.Y + (int)(24 * scale) : work.Y + work.Height - height - (int)(48 * scale);
        Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(36 * scale), (int)(36 * scale)), true);
        Native.SetWindowPos(hwnd, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
        hideTimer.Stop(); hideTimer.Start();
    }

    void Hide()
    {
        hideTimer.Stop();
        window.AppWindow.Hide();
    }
}
