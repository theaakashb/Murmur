using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>
/// The small status bubble (spec/pipeline.md section 14): a topmost, no-activate, click-through window near the bottom (or top) centre of the active
/// monitor. It never takes focus. The paste-failure card removes click-through so its Copy button works, and still never activates.
/// </summary>
internal sealed class Bubble
{
    static readonly Color Surface = Color.FromArgb(255, 24, 24, 24), Accent = Color.FromArgb(255, 77, 111, 240), Danger = Color.FromArgb(255, 224, 96, 96);
    readonly Window window = new();
    readonly nint hwnd;
    readonly TextBlock text = new() { Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly Border dot = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    readonly Border levelTrack = new() { Height = 4, Width = 72, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly Border levelFill = new() { Height = 4, Width = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Accent), HorizontalAlignment = HorizontalAlignment.Left };
    readonly ProgressRing ring = new() { Width = 16, Height = 16, IsActive = true, Foreground = new SolidColorBrush(Accent), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel cardButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock cardPreview = new() { Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)), FontSize = 12, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button copy = new() { Content = "Copy" }, dismiss = new() { Content = "Dismiss" };
    readonly DispatcherQueueTimer hideTimer;
    string? cardText;
    bool visible;

    public Bubble(DispatcherQueue ui)
    {
        hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = window.AppWindow;
        appWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        appWindow.IsShownInSwitchers = false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)((long)style | Native.WsExNoActivate | Native.WsExToolWindow | Native.WsExTransparent | Native.WsExTopmost | Native.WsExLayered));
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);

        levelTrack.Child = levelFill;
        row.Children.Add(dot); row.Children.Add(ring); row.Children.Add(text); row.Children.Add(levelTrack);
        copy.Click += (_, _) => { if (cardText is not null) Native.CopyText(cardText); Hide(); };
        dismiss.Click += (_, _) => Hide();
        cardButtons.Children.Add(copy); cardButtons.Children.Add(dismiss);
        var stack = new StackPanel { Padding = new Thickness(18, 12, 18, 12), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(row); stack.Children.Add(cardPreview); stack.Children.Add(cardButtons);
        window.Content = new Grid { Background = new SolidColorBrush(Surface), RequestedTheme = ElementTheme.Dark, Children = { stack } };
        hideTimer = ui.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => Hide();
    }

    public void ShowListening() => Present("Listening", dismissAfter: null, listening: true);
    public void ShowProcessing(string message) => Present(message, null, processing: true);
    public void ShowError(string message, double dismissAfter) => Present(message, dismissAfter, error: true);
    public void ShowNotice(string message, double dismissAfter) => Present(message, dismissAfter);

    /// <summary>The paste-failure (or saved, not pasted) card: text with Copy, 30 s.</summary>
    public void ShowCard(string title, string copyText, double dismissAfter = 30)
    {
        cardText = copyText;
        cardPreview.Text = copyText;
        Present(title, dismissAfter, error: title != "Saved, not pasted", card: true);
    }

    /// <summary>Peak 0..1 of the latest audio block.</summary>
    public void SetLevel(float peak) => levelFill.Width = Math.Max(4, Math.Min(1, Math.Sqrt(peak) * 1.4) * levelTrack.Width);

    public void Hide()
    {
        hideTimer.Stop();
        if (!visible) return;
        visible = false;
        Native.ShowWindow(hwnd, 0);
    }

    void Present(string message, double? dismissAfter, bool listening = false, bool processing = false, bool error = false, bool card = false)
    {
        hideTimer.Stop();
        text.Text = message;
        text.Margin = new Thickness(0);
        ring.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
        dot.Visibility = processing ? Visibility.Collapsed : Visibility.Visible;
        dot.Background = new SolidColorBrush(error ? Danger : Accent);
        levelTrack.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        cardPreview.Visibility = cardButtons.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        if (listening) SetLevel(0);

        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        int width = (int)((card ? 440 : error ? 400 : 260) * scale);
        int height = (int)((card ? 150 : error ? (message.Length > 70 ? 96 : 60) : 52) * scale);
        text.MaxWidth = (card || error ? 360 : 200);
        // The card must be clickable; every other state lets clicks through to the app underneath.
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        style = card ? style & ~Native.WsExTransparent : style | Native.WsExTransparent;
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)style);

        var (x, y) = Place(width, height, scale);
        Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(24 * scale), (int)(24 * scale)), true);
        Native.SetWindowPos(hwnd, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
        visible = true;
        if (dismissAfter is { } seconds) { hideTimer.Interval = TimeSpan.FromSeconds(seconds); hideTimer.Start(); }
    }

    (int X, int Y) Place(int width, int height, double scale)
    {
        var foreground = Native.GetForegroundWindow();
        var area = foreground != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;
        var work = area.WorkArea;
        int x = work.X + (work.Width - width) / 2;
        bool top = TopPlacement?.Invoke() == true;
        int y = top ? work.Y + (int)(24 * scale) : work.Y + work.Height - height - (int)(48 * scale);
        return (x, y);
    }

    /// <summary>True when the dictation indicator sits at the top of the screen.</summary>
    public Func<bool>? TopPlacement { get; set; }
}
