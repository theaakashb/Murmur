using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The Mac's live meeting panel (MeetingLiveView in app/MeetingsUI.swift): a small topmost window at the top right of the screen for the whole recording.
/// It is shown without taking focus from the user's app, but unlike the call offer it can be activated by a click, so the My notes box takes typing.
/// It cannot be closed, only minimized to a timer pill. Windows has no notch, so the Mac's menu-bar placement and the dragged position are not ported.</summary>
internal sealed class LiveMeetingPanel
{
    const double Width = 340, Height = 520, PillWidth = 96, PillHeight = 30;
    const string Notice = "Heads up: I'm taking notes on this call with Murmur, which transcribes the audio on my PC.";
    readonly Window window = new();
    readonly nint hwnd;
    readonly Border surface = new() { Style = MurmurStyle.Style("MurmurBubble") };
    readonly Controller controller;
    readonly AppServices s;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    readonly TextBlock clock = MurmurStyle.Text("", "MurmurSection"), source = MurmurStyle.Text("", "MurmurCaption"), pillClock = MurmurStyle.Text("", "MurmurSection");
    readonly StackPanel status = new() { Spacing = 6 }, lines = new() { Spacing = 6 };
    readonly Microsoft.UI.Xaml.Controls.ProgressBar me = new() { Maximum = 1, Height = 6 }, them = new() { Maximum = 1, Height = 6 };
    readonly TextBlock appNote = MurmurStyle.Text("", "MurmurCaption");
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox notes = MurmurStyle.Named(new TextBox { PlaceholderText = "Type a note", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 96 }, "My notes");
    FrameworkElement? full, pill;
    string? shownId, statusSignature;
    int shownLines = -1;
    bool collapsed, placed;

    public LiveMeetingPanel(AppServices services, Controller controller, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        s = services;
        this.controller = controller;
        hwnd = WindowNative.GetWindowHandle(window);
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        window.AppWindow.IsShownInSwitchers = false;
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)((long)style | Native.WsExToolWindow | Native.WsExTopmost));
        window.Content = surface;
        surface.CornerRadius = new CornerRadius(18);
        notes.TextChanged += (_, _) => { if (controller.Meetings.Live is { } live && notes.Text != live.MyNotes) controller.Meetings.SetNotes(notes.Text, live.Id); };
        timer = ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    static Button Quiet(string text, Action click)
    {
        var b = MurmurStyle.Named(new Button { Content = text, FontSize = 12, MinHeight = 28, Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) }, text);
        b.Click += (_, _) => click();
        return b;
    }

    static FrameworkElement Dot() => new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red), VerticalAlignment = VerticalAlignment.Center };

    void Tick()
    {
        var r = controller.Meetings;
        if (r.Live is not { } live)
        {
            if (shownId is not null) { shownId = null; shownLines = -1; statusSignature = null; placed = false; window.AppWindow.Hide(); }
            return;
        }
        string time = MeetingExport.Clock(r.Elapsed);
        if (shownId != live.Id)
        {
            shownId = live.Id; shownLines = -1; statusSignature = null;
            collapsed = s.Settings.Get(Prefs.MeetingPanelCollapsed);
            notes.Text = live.MyNotes;
            Build();
        }
        clock.Text = "Recording " + time;
        pillClock.Text = time;
        source.Text = (live.Source.MicOnly ? "Mic only" : live.Source.Name) + (live.TranscribedBy is { } by ? " · " + by : "");
        me.Value = Math.Min(1, r.MeLevel); them.Value = Math.Min(1, r.ThemLevel);
        appNote.Text = live.Source.AllApps ? "All apps: music and notification sounds are recorded too." : "";
        UpdateStatus(r);
        UpdateLines(live);
        Place();
    }

    void Build()
    {
        var minimize = Quiet("", () => SetCollapsed(true));
        minimize.Content = new FontIcon { Glyph = "", FontSize = 12, Style = MurmurStyle.Style("MurmurMutedIcon") };
        MurmurStyle.Named(minimize, "Minimize to the recording timer");
        minimize.Padding = new Thickness(6, 0, 6, 0); minimize.MinHeight = 24; minimize.MinWidth = 0;
        var head = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var dot = Dot();
        source.TextTrimming = TextTrimming.CharacterEllipsis; source.TextWrapping = TextWrapping.NoWrap; source.HorizontalAlignment = HorizontalAlignment.Right; source.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(dot); head.Children.Add(clock); head.Children.Add(source); head.Children.Add(minimize);
        Grid.SetColumn(clock, 1); Grid.SetColumn(source, 2); Grid.SetColumn(minimize, 3);
        clock.VerticalAlignment = VerticalAlignment.Center;

        var reminder = MurmurStyle.Text("Let others know you’re taking notes.", "MurmurSettingLabel"); reminder.FontSize = 13; reminder.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var copyNotice = new HyperlinkButton { Content = "Copy Notice", FontSize = 13, Padding = new Thickness(0) };
        copyNotice.Click += (_, _) => Native.CopyText(Notice);
        var remind = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { reminder, copyNotice } };
        Grid.SetColumn(copyNotice, 1);

        scroll.Content = lines;
        var mine = MurmurStyle.Text("MY NOTES", "MurmurSectionInk"); mine.FontSize = 11;
        var cancel = Quiet("Cancel…", () => _ = ConfirmCancel());
        var stop = MurmurStyle.Pill("Stop", controller.StopMeeting, MurmurStyle.PillKind.Filled, height: 32);
        var buttons = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { cancel, stop } };
        Grid.SetColumn(stop, 1);

        var column = new Grid { Padding = new Thickness(14), RowSpacing = 10 };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            column.RowDefinitions.Add(new RowDefinition { Height = h });
        UIElement[] rows = [head, remind, status, Meter("Me", me), Meter("Them", them), scroll, mine, notes, buttons];
        for (int i = 0; i < rows.Length; i++) { Grid.SetRow((FrameworkElement)rows[i], i); column.Children.Add(rows[i]); }
        full = column;

        var expand = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Dot(), pillClock } }, Padding = new Thickness(12, 0, 12, 0), MinHeight = 0, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(PillHeight / 2) };
        expand.Click += (_, _) => SetCollapsed(false);
        MurmurStyle.Named(expand, "Recording meeting notes. Show the panel");
        pill = expand;
        surface.Child = collapsed ? pill : full;
    }

    FrameworkElement Meter(string label, Microsoft.UI.Xaml.Controls.ProgressBar bar)
    {
        var text = MurmurStyle.Text(label, "MurmurSection"); text.FontSize = 11; text.Width = 40;
        bar.Foreground = MurmurStyle.ThemeBrush("MurmurAccent", surface);
        var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() }, Children = { text, bar } };
        Grid.SetColumn(bar, 1);
        bar.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, label + " level");
        return row;
    }

    void SetCollapsed(bool value)
    {
        collapsed = value;
        s.Settings.Set(Prefs.MeetingPanelCollapsed, value);
        surface.Child = collapsed ? pill : full;
        placed = false;
        Place();
    }

    /// <summary>The cautions the Mac shows under the reminder: history off, no call audio, a warning, a late transcript, and a call that seems to have ended. Rebuilt only when they change.</summary>
    void UpdateStatus(MeetingRecorder r)
    {
        bool persists = s.Settings.Get(Prefs.HistoryMode).PersistsTranscripts();
        var signature = string.Join('|', persists, r.SystemAudioProblem, r.Warning, r.Behind, r.AskEnded, appNote.Text);
        if (signature == statusSignature) return;
        statusSignature = signature;
        status.Children.Clear();
        if (!persists) status.Children.Add(Small("History is off: this meeting is kept only until Murmur quits.", "MurmurSectionInk"));
        if (r.SystemAudioProblem is { } problem)
            status.Children.Add(new StackPanel { Spacing = 6, Children = { Small(problem, "MurmurSettingLabel"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Quiet("Record Only My Microphone", r.ContinueWithMicrophoneOnly), Quiet("Stop", controller.StopMeeting) } } } });
        if (r.Warning is { } warning) { var w = Small(warning, "MurmurSectionInk"); w.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; status.Children.Add(w); }
        if (r.Behind) status.Children.Add(Small("The transcript is behind; nothing is lost.", "MurmurCaption"));
        if (r.AskEnded)
            status.Children.Add(new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { Small("Call ended?", "MurmurSettingLabel"), Col(Quiet("Stop", controller.StopMeeting), 1) } });
        if (appNote.Text.Length > 0) status.Children.Add(Small(appNote.Text, "MurmurCaption"));
    }

    static T Col<T>(T element, int column) where T : FrameworkElement { Grid.SetColumn(element, column); return element; }

    static TextBlock Small(string text, string style) { var t = MurmurStyle.Text(text, style); t.FontSize = 12; return t; }

    void UpdateLines(Meeting live)
    {
        var shown = live.Lines.Where(l => !l.IsHidden).TakeLast(40).ToList();
        if (shown.Count == shownLines) return;
        shownLines = shown.Count;
        lines.Children.Clear();
        if (shown.Count == 0) lines.Children.Add(Small("Lines appear about 15 seconds after they are spoken.", "MurmurCaption"));
        foreach (var line in shown)
        {
            var who = MurmurStyle.Text(MeetingSpeakers.Label(line, live), "MurmurSection"); who.FontSize = 11;
            var words = MurmurStyle.Text(line.VisibleText, "MurmurSettingLabel"); words.FontSize = 13; words.IsTextSelectionEnabled = true;
            lines.Children.Add(new StackPanel { Children = { who, words } });
        }
        scroll.UpdateLayout();
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
    }

    async Task ConfirmCancel()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = surface.XamlRoot, Title = "Discard this meeting?", Content = new TextBlock { Text = "The recording and its transcript are deleted.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Discard", CloseButtonText = "Keep Recording", DefaultButton = ContentDialogButton.Close
        };
        if (await MurmurStyle.Named(dialog, "Discard meeting").ShowAsync() == ContentDialogResult.Primary) controller.Meetings.Cancel();
    }

    /// <summary>Top right of the primary work area, shown without activating. Re-run when the size changes (minimize or expand).</summary>
    void Place()
    {
        if (placed) return;
        placed = true;
        var work = DisplayArea.Primary.WorkArea;
        var scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        double w = collapsed ? PillWidth : Width, h = collapsed ? PillHeight : Height;
        int width = (int)Math.Ceiling(w * scale), height = (int)Math.Ceiling(h * scale), margin = (int)(16 * scale);
        surface.Width = w; surface.Height = h;
        Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, (int)(36 * scale), (int)(36 * scale)), true);
        Native.SetWindowPos(hwnd, Native.HwndTopmost, work.X + work.Width - width - margin, work.Y + margin, width, height, Native.SwpNoActivate | Native.SwpShowWindow);
    }
}
