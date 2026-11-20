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
internal sealed class Bubble
{
    readonly Window window = new();
    readonly nint hwnd;
    readonly Border surface = new() { Style = MurmurStyle.Style("MurmurBubble") };
    static readonly double[] BarWeights = [0.6, 0.85, 1, 0.85, 0.6];
    readonly Border[] bars = new Border[BarWeights.Length];
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
        Native.SetLayeredWindowAttributes(hwnd, 0, 255, 2);

        for (int i = 0; i < bars.Length; i++)
        {
            var bar = new Border { Width = 9, Height = 13, Margin = new Thickness(2.5, 0, 2.5, 0), CornerRadius = new CornerRadius(4.5), VerticalAlignment = VerticalAlignment.Center, Style = MurmurStyle.Style("MurmurBubbleDot") };
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

    void ResizeAndPlace()
    {
        var work = (display ?? DisplayArea.Primary).WorkArea;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        surface.Measure(new Windows.Foundation.Size(surface.Width, double.PositiveInfinity));
        int width = (int)Math.Ceiling(surface.DesiredSize.Width * scale);
        int height = (int)Math.Ceiling(surface.DesiredSize.Height * scale);
        int x = work.X + (work.Width - width) / 2;
        int y = TopPlacement?.Invoke() == true ? work.Y + (int)(24 * scale) : work.Y + work.Height - height - (int)(48 * scale);
        Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(2 * surface.CornerRadius.TopLeft * scale), (int)(2 * surface.CornerRadius.TopLeft * scale)), true);
        Native.SetWindowPos(hwnd, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
    }

    /// <summary>True when the dictation indicator sits at the top of the screen.</summary>
    public Func<bool>? TopPlacement { get; set; }
}
