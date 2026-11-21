using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>A topmost, non-activating dictation indicator. Only the copy card accepts clicks.</summary>
internal sealed partial class Bubble
{
    readonly Window window = new();
    readonly nint hwnd;
    readonly Border surface = new() { Style = MurmurStyle.Style("MurmurBubble") };
    static readonly double[] BarWeights = [0.6, 0.85, 1, 0.85, 0.6];
    readonly Rectangle[] bars = new Rectangle[BarWeights.Length];
    // The Mac bars are 9 pt wide capsules (Capsule() clip, radius = width / 2).
    const double BarWidth = 9, BarRadius = BarWidth / 2;
    readonly StackPanel waveform = new() { Width = 80, Height = 40, Orientation = Orientation.Horizontal, Padding = new Thickness(5, 0, 5, 0) };
    readonly TextBlock text = MurmurStyle.Text("", "MurmurSettingLabel");
    readonly TextBlock partial = MurmurStyle.Text("", "MurmurSettingLabel");
    readonly Border dot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock cardPreview = MurmurStyle.Text("", "MurmurDescription");
    readonly Grid indicator;
    readonly StackPanel card;
    readonly TextBlock cardTitle = MurmurStyle.Text("", "BodyStrongTextBlockStyle");
    readonly DispatcherQueueTimer hideTimer;
    string? cardText;
    DisplayArea? display;
    bool visible, isListening, isAnimating;
    volatile float voiceLevel;
    readonly Windows.UI.ViewManagement.UISettings animations = new();

    public Bubble(DispatcherQueue ui)
    {
        hwnd = WindowNative.GetWindowHandle(window);
        var appWindow = window.AppWindow;
        appWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        appWindow.IsShownInSwitchers = false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)((long)style | Native.WsExNoActivate | Native.WsExToolWindow | Native.WsExTransparent | Native.WsExTopmost | Native.WsExLayered));
        // WS_EX_TRANSPARENT only lets clicks through on a layered window.
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);
        // The pill has no background of its own (the Mac's minimal pill is five dots and nothing else): a clear backdrop gives per-pixel alpha with smooth edges.
        window.SystemBackdrop = new ClearBackdrop();
        surface.Background = null;
        // The 1 px light outline is the presenter's WS_BORDER (a non-client frame around the whole window); drop it and the other frame styles, then the DWM border colour and shadow, and keep the corners square, because XAML rounds the card.
        var presenter = (OverlappedPresenter)appWindow.Presenter;
        FrameBefore = $"{Native.DescribeFrame(hwnd)}; presenter border {presenter.HasBorder} titlebar {presenter.HasTitleBar} resizable {presenter.IsResizable}";
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        Native.RemoveWindowChrome(hwnd);

        for (int i = 0; i < bars.Length; i++)
        {
            var bar = new Rectangle { Width = BarWidth, Height = 13, RadiusX = BarRadius, RadiusY = BarRadius, Margin = new Thickness(2.5, 0, 2.5, 0), VerticalAlignment = VerticalAlignment.Center, Style = MurmurStyle.Style("MurmurBubbleBar") };
            bars[i] = bar;
            waveform.Children.Add(bar);
        }
        indicator = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { waveform, dot, text, partial }
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(partial, 1);
        partial.Margin = new Thickness(6, 0, 7, 0);
        partial.VerticalAlignment = VerticalAlignment.Center;

        var copy = MurmurStyle.Named(new Button { Content = "Copy", Style = MurmurStyle.Style("AccentButtonStyle") }, "Copy dictation");
        var dismiss = MurmurStyle.Named(new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0) }, "Dismiss dictation notice");
        copy.Click += (_, _) => { if (cardText is not null) Native.CopyText(cardText); Hide(); };
        dismiss.Click += (_, _) => Hide();

        var header = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { new SymbolIcon(Symbol.Paste) { VerticalAlignment = VerticalAlignment.Center }, cardTitle, dismiss }
        };
        cardTitle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(cardTitle, 1);
        Grid.SetColumn(dismiss, 2);
        var hint = MurmurStyle.Text("Ctrl+Win+Shift+V pastes it too", "MurmurCaption");
        hint.VerticalAlignment = VerticalAlignment.Center;
        var footer = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { hint, copy }
        };
        Grid.SetColumn(copy, 1);
        partial.MaxLines = 2;
        cardPreview.MaxLines = 3;
        partial.TextTrimming = cardPreview.TextTrimming = TextTrimming.CharacterEllipsis;
        card = new StackPanel { Spacing = 8, Children = { header, cardPreview, footer } };
        surface.Child = new Grid { Children = { indicator, card } };
        window.Content = surface;
        hideTimer = ui.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => Hide();
    }

    public void ShowListening() => Present("Listening", dismissAfter: null, listening: true);

    public void ShowProcessing(string? message = null) => Present(message ?? "Processing", null, processing: true, showLabel: message is not null);
    public void ShowError(string message, double dismissAfter) => Present(message, dismissAfter, error: true);
    public void ShowNotice(string message, double dismissAfter) => Present(message, dismissAfter);

    /// <summary>The paste-failure (or saved, not pasted) card: text with Copy, 30 s.</summary>
    public void ShowCard(string title, string copyText, double dismissAfter = 30)
    {
        cardText = copyText;
        cardPreview.Text = copyText;
        cardTitle.Text = title.StartsWith("Could not paste", StringComparison.Ordinal) ? "Couldn’t paste here" : title;
        Present(title, dismissAfter, error: title != "Saved, not pasted", card: true);
    }

    /// <summary>The latest normalized RMS level, written by the capture thread.</summary>
    public void SetLevel(float level) => voiceLevel = level;

    void DrawWaveform(object? sender, object args)
    {
        double time = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        bool motion = animations.AnimationsEnabled;
        double voice = Math.Sqrt(voiceLevel);
        for (int i = 0; i < bars.Length; i++)
        {
            double wobble = motion ? 0.65 + 0.35 * Math.Sin(time * 9 + i * 1.7) : 1;
            double sweep = motion ? Math.Exp(-Math.Pow(time * 0.8 % 1 * 7 - i * 1.2 - 0.6, 2)) : 1;
            bars[i].Height = isListening ? 13 + 27 * voice * wobble * BarWeights[i] : 13;
            bars[i].Opacity = isListening ? 1 : 0.35 + 0.65 * sweep;
        }
    }

    void Animate(bool animate)
    {
        if (animate == isAnimating) return;
        isAnimating = animate;
        if (animate) CompositionTarget.Rendering += DrawWaveform;
        else CompositionTarget.Rendering -= DrawWaveform;
    }

    /// <summary>Shows live words on the UI thread while the indicator is listening.</summary>
    public void SetPartial(string? text)
    {
        if (!isListening) return;
        var words = string.IsNullOrWhiteSpace(text) ? "" : text;
        if (partial.Text == words) return;
        partial.Text = words;
        partial.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        surface.Width = words.Length > 0 ? 420 : 80;
        ApplyChrome(listening: true, filled: words.Length > 0);
        ResizeAndPlace();
    }

    public void Hide()
    {
        hideTimer.Stop();
        isListening = false;
        Animate(false);
        partial.Text = cardPreview.Text = "";
        cardText = null;
        if (!visible) return;
        visible = false;
        Native.ShowWindow(hwnd, 0);
    }

    void Present(string message, double? dismissAfter, bool listening = false, bool processing = false, bool error = false, bool card = false, bool showLabel = false)
    {
        hideTimer.Stop();
        isListening = listening;
        text.Text = message;
        text.VerticalAlignment = VerticalAlignment.Center;
        waveform.Visibility = listening || processing ? Visibility.Visible : Visibility.Collapsed;
        text.Visibility = listening || processing && !showLabel ? Visibility.Collapsed : Visibility.Visible;
        text.Margin = listening || processing ? new Thickness(6, 0, 7, 0) : new Thickness(8, 0, 0, 0);
        dot.Visibility = listening || processing ? Visibility.Collapsed : Visibility.Visible;
        dot.Style = MurmurStyle.Style(error ? "MurmurBubbleError" : "MurmurBubbleDot");
        MurmurStyle.Named(surface, message);
        // The minimal pill (listening or processing, no label) is just the dots; every other state is a card.
        ApplyChrome(listening || processing && !showLabel, filled: !(listening || processing && !showLabel));
        surface.Padding = listening || processing ? new Thickness(0) : new Thickness(10);
        surface.CornerRadius = new CornerRadius(listening || processing ? 16 : 18);
        partial.Visibility = Visibility.Collapsed;
        partial.Text = "";
        this.card.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        indicator.Visibility = card ? Visibility.Collapsed : Visibility.Visible;
        if (!card) { cardText = null; cardPreview.Text = ""; }
        Animate(listening || processing);
        if (isAnimating) DrawWaveform(null, EventArgs.Empty);

        // The card accepts clicks while WS_EX_NOACTIVATE keeps focus in the target app.
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        style = card ? style & ~Native.WsExTransparent : style | Native.WsExTransparent;
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)style);

        var foreground = Native.GetForegroundWindow();
        display = foreground != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;
        surface.Width = card ? 320 : error ? 240 : listening || processing && !showLabel ? 80 : processing ? 300 : 220;
        ResizeAndPlace();
        visible = true;
        if (dismissAfter is { } seconds) { hideTimer.Interval = TimeSpan.FromSeconds(seconds); hideTimer.Start(); }
    }

    /// <summary>Minimal: transparent, no border. Card: the paper fill with a hairline so it reads without a shadow.</summary>
    void ApplyChrome(bool listening, bool filled)
    {
        if (filled)
        {
            surface.Background = MurmurStyle.ThemeBrush("MurmurPaper", surface);
            surface.BorderBrush = MurmurStyle.ThemeBrush("MurmurRule", surface);
            surface.BorderThickness = new Thickness(1);
            if (listening) surface.CornerRadius = new CornerRadius(22);
        }
        else
        {
            surface.Background = null;
            surface.BorderBrush = null;
            surface.BorderThickness = new Thickness(0);
        }
    }

    /// <summary>Dip size to physical pixels at a display scale (80 x 40 pt is 80 x 40 px at 100% and 120 x 60 px at 150%).</summary>
    internal static (int Width, int Height) PixelSize(double widthDip, double heightDip, double scale) =>
        ((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale));

    void ResizeAndPlace()
    {
        var work = (display ?? DisplayArea.Primary).WorkArea;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        surface.Measure(new Windows.Foundation.Size(surface.Width, double.PositiveInfinity));
        var (width, height) = PixelSize(surface.DesiredSize.Width, surface.DesiredSize.Height, scale);
        int x = work.X + (work.Width - width) / 2;
        // The Mac sits 24 pt above the bottom of the visible area (or below the top).
        int y = TopPlacement?.Invoke() == true ? work.Y + (int)(24 * scale) : work.Y + work.Height - height - (int)(24 * scale);
        if (Offscreen) { x = y = -32000; }
        Native.SetWindowPos(hwnd, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
        // The window frame adds a few pixels of its own; size the client area itself so the pill is exactly its design size.
        window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));
    }

    /// <summary>Render mode only: the frame as the presenter left it, before the chrome is removed.</summary>
    internal string FrameBefore = "";

    /// <summary>Render mode only: parks the window far off every screen.</summary>
    internal bool Offscreen { get; set; }

    /// <summary>True when the dictation indicator sits at the top of the screen.</summary>
    public Func<bool>? TopPlacement { get; set; }
}
