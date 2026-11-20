using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The Meetings screen (MeetingsPage in app/MeetingsUI.swift): header with Start/Stop, search, the meeting list and the detail with Notes, My notes and Transcript.
/// Redraws when the recorder's state changes shape; the elapsed clock has its own one-second timer.</summary>
internal sealed partial class MeetingsView
{
    readonly AppServices s;
    readonly Controller controller;
    readonly Func<XamlRoot?> root;
    readonly Func<nint> hwnd;
    readonly Grid view = new();
    readonly TextBlock clock = MurmurStyle.Text("", "MurmurSection");
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    readonly HashSet<string> chosen = [];
    static readonly string[] DateChoices = ["All dates", "Today", "Last 7 days", "Last 30 days", "Date range…"];
    int dateFilter;
    DateTime fromDate = DateTime.Today.AddDays(-29), throughDate = DateTime.Today;
    string search = "", tabName = "Notes", signature = "";
    string? selectedId;
    bool selecting, showEcho;
    int redrawQueued;

    public FrameworkElement View => view;
    MeetingRecorder Recorder => controller.Meetings;

    internal MeetingsView(AppServices services, Controller controller, Func<XamlRoot?> root, Func<nint> hwnd)
    {
        s = services;
        this.controller = controller;
        this.root = root;
        this.hwnd = hwnd;
        timer = s.UI.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => Tick();
        Recorder.Changed += OnChanged;
        view.Unloaded += (_, _) => { timer.Stop(); Recorder.Changed -= OnChanged; };
        Render();
        timer.Start();
    }

    string Signature()
    {
        var r = Recorder;
        var picked = r.Meetings.FirstOrDefault(m => m.Id == selectedId);
        return string.Join('|', r.Meetings.Count, r.Live?.Id, r.Finishing.Count, r.NotesWriting.Count, r.NotesProblem.Count, r.Unsaved.Count, r.Leftovers.Count,
            picked?.Notes?.WrittenAt.Ticks, picked?.LectureNotes?.WrittenAt.Ticks, picked?.LectureNotes?.Outdated, picked?.Notes?.Outdated, picked?.Lines.Count, picked?.Finished, r.Message);
    }

    void OnChanged()
    {
        if (Signature() == signature || Interlocked.Exchange(ref redrawQueued, 1) == 1) return;
        s.UI.TryEnqueue(() => { Interlocked.Exchange(ref redrawQueued, 0); Render(); });
    }

    void Tick() => clock.Text = Recorder.Live is { } live ? $"Recording {MeetingExport.Clock(Recorder.Elapsed)} · {live.Source.Name}" : "";

    // ---- small builders ----

    /// <summary>The Mac's Start menu (MeetingStartMenu): running call apps first, then All apps, then the microphone alone. Built each time it opens.</summary>
    Button StartMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            IReadOnlyList<Murmur.Core.ProcessInfo> running;
            try { running = Murmur.Platform.AudioSessions.Processes(); } catch (Exception) { running = new List<Murmur.Core.ProcessInfo>(); }
            var apps = CallApp.Catalog.Where(app => running.Any(p => CallApp.Normalize(p.Name) == app.ProcessName)).GroupBy(app => app.Name).Select(g => g.First()).ToList();
            if (apps.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No call app or browser is open", IsEnabled = false });
            foreach (var app in apps)
            {
                var item = new MenuFlyoutItem { Text = app.Name };
                var source = new MeetingSource(app.Name, app.ProcessName);
                item.Click += (_, _) => controller.StartMeeting(source);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var all = new MenuFlyoutItem { Text = "All apps (includes music and notifications)" };
            all.Click += (_, _) => controller.StartMeeting(MeetingSource.Everything);
            var mic = new MenuFlyoutItem { Text = "Only my microphone" };
            mic.Click += (_, _) => controller.StartMeeting(MeetingSource.MicrophoneOnly);
            menu.Items.Add(all); menu.Items.Add(mic);
        };
        var button = Btn("Start Meeting Notes", () => { }, filled: true);
        button.Flyout = menu;
        return button;
    }

    static Button Btn(string text, Action click, bool filled = false, bool plain = false, string? name = null) =>
        MurmurStyle.Pill(text, click, filled ? MurmurStyle.PillKind.Filled : plain ? MurmurStyle.PillKind.Plain : MurmurStyle.PillKind.Outline, name, height: 34);

    static TextBlock Mono(string text, double size = 11, bool accent = false)
    {
        var t = MurmurStyle.Text(text, accent ? "MurmurSectionInk" : "MurmurSection");
        t.FontSize = size;
        return t;
    }

    /// <summary>The Mac's DisclosureGroup: a chevron and "Audio and privacy" that reveal one paragraph under it.</summary>
    UIElement PrivacyDisclosure()
    {
        bool speakers = s.Settings.Get(SpeakerFacts.SettingKey) as bool? ?? false, room = s.Settings.Get(SpeakerFacts.RoomKey) as bool? ?? false;
        string retry = RetryWindow.Text;
        var body = Lede(speakers
            ? room ? $"For calls, the call’s audio is kept until speakers are separated. For microphone-only recordings, microphone audio is kept instead. Both are deleted after separation, at most {retry}."
                   : $"Your microphone as Me and the call’s audio as Them, transcribed on this PC. Your audio is deleted as soon as it is transcribed; the call’s is kept until its speakers are separated, at most {retry}."
            : "Your microphone as Me and the call’s audio as Them, transcribed on this PC. Audio is deleted as soon as it is transcribed.");
        body.Visibility = Visibility.Collapsed; body.Margin = new Thickness(18, 4, 0, 0); body.MaxWidth = 560;
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, Style = MurmurStyle.Style("MurmurMutedIcon") };
        var label = MurmurStyle.Text("Audio and privacy", "MurmurSettingLabel"); label.FontSize = 13;
        var header = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chevron, label } }, Padding = new Thickness(0, 2, 0, 2), MinHeight = 0, BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        MurmurStyle.Named(header, "Audio and privacy");
        header.Click += (_, _) =>
        {
            bool open = body.Visibility == Visibility.Collapsed;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = open ? "\uE70D" : "\uE76C";
        };
        return new StackPanel { Children = { header, body } };
    }

    static TextBlock Lede(string text) => MurmurStyle.Text(text, "MurmurDescription");

    // ---- render ----

    void Render()
    {
        signature = Signature();
        view.Children.Clear();
        view.RowDefinitions.Clear();
        var r = Recorder;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) view.RowDefinitions.Add(new RowDefinition { Height = h });

        var title = new TextBlock { Text = "Meetings", Style = MurmurStyle.Style("MurmurHeading"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Fonts/InstrumentSerif-Regular.ttf#Instrument Serif"), CharacterSpacing = -20 };
        title.FontSize = 48; title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var privacy = PrivacyDisclosure();
        var left = new StackPanel { Spacing = 4, Children = { title, Lede("Find transcripts and notes from your calls."), privacy } };
        FrameworkElement right;
        if (r.Live is { } live)
        {
            Tick();
            right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { clock, Btn("Stop", controller.StopMeeting, filled: true) } };
        }
        else right = StartMenu();
        var head = new Grid { Padding = new Thickness(40, 24, 40, 8), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { left, right } };
        Grid.SetColumn(right, 1);
        right.VerticalAlignment = VerticalAlignment.Center;

        var banners = new StackPanel();
        if (r.Unsaved.Count > 0) banners.Children.Add(Banner(MeetingRecorder.UnsavedWarning));
        if (s.Settings.Get(Prefs.HistoryMode) == HistoryMode.Off)
            banners.Children.Add(new Border { Padding = new Thickness(40, 0, 40, 8), Child = Lede("History is off, so meetings are kept only until Murmur quits. Export one to keep it.") });
        var top = new StackPanel { Children = { head, banners } };
        view.Children.Add(top);

        var rule = new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") };
        Grid.SetRow(rule, 1);
        view.Children.Add(rule);

        FrameworkElement body;
        if (r.Meetings.Count == 0)
            body = new StackPanel { Padding = new Thickness(40, 24, 40, 24), Spacing = 10, Children = { Lede("No meetings yet. Start meeting notes when a call begins; Murmur asks you to let participants know the first time.") } };
        else body = Body();
        Grid.SetRow(body, 2);
        view.Children.Add(body);
    }

    static Border Banner(string text)
    {
        var t = MurmurStyle.Text(text, "MurmurDescription");
        t.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        var border = new Border { Padding = new Thickness(40, 10, 40, 10), Style = MurmurStyle.Style("MurmurBubbleDot"), Child = t };
        return border;
    }

    List<Meeting> Filtered()
    {
        var today = DateTime.Today;
        (DateTime? from, DateTime? through) = dateFilter switch
        {
            1 => (today, today), 2 => (today.AddDays(-6), today), 3 => (today.AddDays(-29), today), 4 => (fromDate, throughDate), _ => ((DateTime?)null, (DateTime?)null)
        };
        return Recorder.Meetings.OrderByDescending(m => m.RecordedAt).Where(m => m.Matches(search.Trim(), from, through)).ToList();
    }

    bool Filtering => search.Trim().Length > 0 || dateFilter != 0;

    UIElement DateFilterBar()
    {
        var combo = MurmurStyle.Named(new ComboBox { ItemsSource = DateChoices, SelectedIndex = dateFilter, MinHeight = 36 }, "Filter meetings by date");
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0 && combo.SelectedIndex != dateFilter) { dateFilter = combo.SelectedIndex; Render(); } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { combo } };
        if (dateFilter == 4)
        {
            var from = MurmurStyle.Named(new CalendarDatePicker { Date = new DateTimeOffset(fromDate), Header = "From" }, "From date");
            var through = MurmurStyle.Named(new CalendarDatePicker { Date = new DateTimeOffset(throughDate), Header = "Through" }, "Through date");
            from.DateChanged += (_, e) => { if (e.NewDate is { } d && d.Date != fromDate) { fromDate = d.Date; if (fromDate > throughDate) throughDate = fromDate; Render(); } };
            through.DateChanged += (_, e) => { if (e.NewDate is { } d && d.Date != throughDate) { throughDate = d.Date; if (throughDate < fromDate) fromDate = throughDate; Render(); } };
            row.Children.Add(from); row.Children.Add(through);
        }
        return row;
    }

    FrameworkElement Body()
    {
        var filtered = Filtered();
        var box = MurmurStyle.Named(new TextBox { PlaceholderText = "Search meetings", Text = search, MinHeight = 36 }, "Search meetings");
        box.TextChanged += (_, _) => { if (box.Text == search) return; search = box.Text; Render(); view.DispatcherQueue.TryEnqueue(() => { if (FindBox(view) is { } b) { b.Focus(FocusState.Programmatic); b.SelectionStart = b.Text.Length; } }); };
        var count = Mono($"{filtered.Count} of {Recorder.Meetings.Count} meetings", 12);
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { count } };
        if (Filtering) counts.Children.Add(Btn("Clear filters", () => { search = ""; dateFilter = 0; Render(); }, plain: true));
        var select = selecting
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Mono($"{chosen.Count} selected", 12), Btn("Select All", () => { foreach (var m in filtered.Where(m => m.Id != Recorder.Live?.Id)) chosen.Add(m.Id); Render(); }, plain: true), Btn("Cancel", () => { selecting = false; chosen.Clear(); Render(); }, plain: true), Btn("Delete Selected…", () => _ = ConfirmDelete(Recorder.Meetings.Where(m => chosen.Contains(m.Id)).ToList())) } }
            : new StackPanel { Orientation = Orientation.Horizontal, Children = { Btn("Select Meetings", () => { selecting = true; Render(); }, plain: true) } };
        var bar = new Grid { Padding = new Thickness(40, 12, 40, 8), ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 6, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { box, DateFilterBar() } }, counts } }, select } };
        Grid.SetColumn(select, 1);
        select.VerticalAlignment = VerticalAlignment.Top;

        var list = new StackPanel();
        foreach (var m in filtered) list.Children.Add(Row(m));
        var scroller = MurmurStyle.Named(new ScrollViewer { Content = list }, "Meeting list");
        var selected = filtered.FirstOrDefault(m => m.Id == selectedId) ?? (selecting ? null : filtered.FirstOrDefault());
        if (selected is not null) selectedId = selected.Id;

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(280) }, new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        grid.Children.Add(scroller);
        var divider = new Border { Width = 1, Style = MurmurStyle.Style("MurmurSidebarPane"), BorderThickness = new Thickness(0, 0, 1, 0) };
        Grid.SetColumn(divider, 1); grid.Children.Add(divider);
        FrameworkElement detail = selected is null ? new StackPanel { Padding = new Thickness(24), Children = { MurmurStyle.Text(filtered.Count == 0 ? "No matching meetings" : "Select a meeting", "MurmurHistoryEmpty") } } : Detail(selected);
        Grid.SetColumn(detail, 2); grid.Children.Add(detail);

        var page = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Children = { bar, grid } };
        Grid.SetRow(grid, 1);
        return page;
    }

    static TextBox? FindBox(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Header: null } b && AutomationProperties.GetName(b) == "Search meetings") return b;
            if (FindBox(child) is { } found) return found;
        }
        return null;
    }

    FrameworkElement Row(Meeting m)
    {
        var details = new StackPanel { Spacing = 4 };
        var name = MurmurStyle.Text(m.Title, "MurmurSettingLabel"); name.FontSize = 14; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.MaxLines = 2; name.TextTrimming = TextTrimming.CharacterEllipsis;
        details.Children.Add(name);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Mono(MeetingExport.Clock(m.Seconds)), MurmurStyle.Text(m.RecordedAt.ToLocalTime().ToString("MMM d"), "MurmurCaption") } };
        if (Recorder.Finishing.Contains(m.Id)) meta.Children.Add(Mono("Finishing", 11, true));
        else if (Recorder.Leftovers.TryGetValue(m.Id, out var left)) meta.Children.Add(Mono($"{left} parts left", 11, true));
        details.Children.Add(meta);
        FrameworkElement content = details;
        if (selecting)
        {
            var check = new CheckBox { IsChecked = chosen.Contains(m.Id), IsEnabled = m.Id != Recorder.Live?.Id, Content = details, MinWidth = 0 };
            AutomationProperties.SetName(check, m.Title);
            check.Checked += (_, _) => chosen.Add(m.Id);
            check.Unchecked += (_, _) => chosen.Remove(m.Id);
            content = check;
        }
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0) };
        button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(button, m.Title);
        if (!selecting) button.Click += (_, _) => { selectedId = m.Id; tabName = "Notes"; Render(); };
        var wrap = new Border { Child = button, Style = MurmurStyle.Style(m.Id == selectedId && !selecting ? "MurmurNavOn" : "MurmurNavOff") };
        return new StackPanel { Children = { wrap, new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") } } };
    }

    // ---- detail ----

    FrameworkElement Detail(Meeting m)
    {
        var r = Recorder;
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        var title = MurmurStyle.Text(m.Title, "MurmurSettingLabel");
        title.FontSize = 22; title.FontWeight = Microsoft.UI.Text.FontWeights.Bold; title.IsTextSelectionEnabled = true;
        panel.Children.Add(title);
        panel.Children.Add(Mono($"{MeetingExport.Clock(m.Seconds)} · {(m.Source.MicOnly ? "My microphone only" : "Me and " + m.Source.Name)} · {(m.TranscribedBy is { } by ? "Transcribed by " + by : "Transcribed on this PC")}"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (m.Notes is { IsEmpty: false }) actions.Children.Add(Btn("Copy Notes", () => Native.CopyText(MeetingExport.Notes(m))));
        actions.Children.Add(Btn("Copy Transcript", () => Native.CopyText(MeetingExport.Transcript(m))));
        actions.Children.Add(Btn("Save Markdown…", () => _ = SaveMarkdown(m)));
        var del = Btn("Delete…", () => _ = ConfirmDelete([m]), plain: true);
        del.IsEnabled = m.Id != r.Live?.Id;
        actions.Children.Add(del);
        panel.Children.Add(new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        if (r.Finishing.Contains(m.Id)) panel.Children.Add(Lede("Finishing the transcript…"));
        else if (r.Leftovers.TryGetValue(m.Id, out var left))
            panel.Children.Add(new StackPanel { Spacing = 8, Children = { Lede($"{left} {(left == 1 ? "part" : "parts")} of this meeting {(left == 1 ? "was" : "were")} not transcribed. The audio is kept for up to 24 hours."), Btn("Finish Transcript", () => r.FinishLeftovers(m.Id), filled: true) } });

        string[] tabs = ["Notes", "Lecture notes", "My notes", "Transcript"];
        panel.Children.Add(new Segmented("Meeting detail", tabs, Math.Max(0, Array.IndexOf(tabs, tabName)), i => { tabName = tabs[i]; Render(); }, stretch: false, fontSize: 13));
        switch (tabName)
        {
            case "Notes": NotesTab(panel, m); break;
            case "Lecture notes": LectureTab(panel, m); break;
            case "My notes":
                var mine = MurmurStyle.Named(new TextBox { Text = m.MyNotes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160 }, "My notes");
                mine.TextChanged += (_, _) => { if (mine.Text != m.MyNotes) r.SetNotes(mine.Text, m.Id); };
                panel.Children.Add(mine);
                panel.Children.Add(MurmurStyle.Text("Your own notes go into the generated notes as yours. Editing them marks the notes out of date.", "MurmurCaption"));
                break;
            default: TranscriptTab(panel, m); break;
        }
        return MurmurStyle.Named(new ScrollViewer { Content = panel }, "Meeting detail");
    }

    bool NotesRouteOff => MeetingNotesRoutes.Load(s.Settings) is null or MeetingNotesRoute.None;

    void NotesTab(StackPanel panel, Meeting m)
    {
        var r = Recorder;
        bool writing = r.NotesWriting.Contains(m.Id), busy = writing || r.Finishing.Contains(m.Id);
        if (writing) panel.Children.Add(Lede("Writing notes…"));
        else if (r.NotesProblem.TryGetValue(m.Id, out var problem))
            panel.Children.Add(new StackPanel { Spacing = 8, Children = { Lede(problem), Btn("Try Again", () => r.WriteNotes(m.Id)) } });
        if (m.Notes is { } notes)
        {
            if (notes.Outdated == true) panel.Children.Add(Lede("The transcript or your notes changed after these notes were written."));
            if (notes.IsEmpty) panel.Children.Add(Lede("Nothing in the transcript could be written up as a point, decision or action item."));
            if (notes.Summary is { } summary) { var t = MurmurStyle.Text(summary, "MurmurSettingLabel"); t.FontSize = 15; t.IsTextSelectionEnabled = true; panel.Children.Add(t); }
            var titles = NoteSections.Titles(false);
            Section(panel, titles.Points, notes.KeyPoints, m, false);
            Section(panel, titles.Decisions, notes.Decisions, m, false);
            Section(panel, titles.Actions, notes.Actions, m, true);
            var written = $"Written by {notes.Route}. Items cite the lines they rest on{(notes.Dropped > 0 ? $"; {notes.Dropped} the transcript did not support {(notes.Dropped == 1 ? "was" : "were")} left out" : "")}.";
            panel.Children.Add(MurmurStyle.Text(written, "MurmurCaption"));
            if (!NotesRouteOff) { var update = Btn("Update Notes", () => r.WriteNotes(m.Id)); update.IsEnabled = !busy; update.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(update); }
        }
        else if (!busy)
        {
            if (NotesRouteOff)
                panel.Children.Add(new StackPanel { Spacing = 8, Children = { Lede(MeetingNotesRoutes.Load(s.Settings) is null ? "Choose how notes are written: a title, summary, key points, decisions and action items." : "Notes are off. This meeting keeps its transcript."), Btn("Choose Notes Route…", () => r.NotesChoicePending = m.Id) } });
            else if (!r.NotesProblem.ContainsKey(m.Id))
            {
                var write = Btn("Write Notes", () => r.WriteNotes(m.Id), filled: true);
                write.IsEnabled = m.Lines.Any(l => !l.IsHidden);
                panel.Children.Add(new StackPanel { Spacing = 8, Children = { Lede("No notes yet."), write } });
            }
        }
    }

    /// <summary>The Mac's lectureTab: concepts, examples and perspectives, written through the same notes route with lecture rules.</summary>
    void LectureTab(StackPanel panel, Meeting m)
    {
        var r = Recorder;
        var route = MeetingNotesRoutes.Load(s.Settings) ?? MeetingNotesRoute.None;
        panel.Children.Add(Lede("Lecture notes use concepts, examples and perspectives from the transcript, with citations."));
        panel.Children.Add(Lede($"Provider: {route.Label()}."));
        if (m.LectureNotes is { } notes)
        {
            if (notes.Outdated == true) panel.Children.Add(Lede("The source changed after these lecture notes were written."));
            if (notes.Summary is { } summary) { var t = MurmurStyle.Text(summary, "MurmurSettingLabel"); t.FontSize = 15; t.IsTextSelectionEnabled = true; panel.Children.Add(t); }
            var titles = NoteSections.Titles(true);
            Section(panel, titles.Points, notes.KeyPoints, m, false);
            Section(panel, titles.Decisions, notes.Decisions, m, false);
            Section(panel, titles.Actions, notes.Actions, m, false);
            panel.Children.Add(MurmurStyle.Text($"Written by {notes.Route}. Unsupported items dropped: {notes.Dropped}.", "MurmurCaption"));
            var copy = Btn("Copy Lecture Notes", () => Native.CopyText(MeetingExport.Notes(m, lecture: true))); copy.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(copy);
        }
        if (r.NotesProblem.TryGetValue(m.Id, out var problem)) panel.Children.Add(Lede(problem));
        bool busy = r.NotesWriting.Contains(m.Id) || r.Finishing.Contains(m.Id);
        if (r.NotesWriting.Contains(m.Id)) panel.Children.Add(Lede("Writing notes…"));
        var write = Btn(m.LectureNotes is null ? "Generate Lecture Notes" : "Update Lecture Notes", () => r.WriteNotes(m.Id, lecture: true), filled: true);
        write.IsEnabled = !NotesRouteOff && m.Finished && !busy && m.Lines.Any(l => !l.IsHidden);
        write.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(write);
        if (NotesRouteOff) panel.Children.Add(Lede("Choose a notes provider in Settings > Meetings first."));
    }

    void Section(StackPanel panel, string title, IReadOnlyList<NoteItem> items, Meeting m, bool actions)
    {
        if (items.Count == 0) return;
        var box = new StackPanel { Spacing = 8, Children = { Mono(title.ToUpperInvariant(), 11, true) } };
        box.Children[0].SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var item = items[i];
            var text = MurmurStyle.Text(item.Text, "MurmurSettingLabel"); text.FontSize = 14; text.IsTextSelectionEnabled = true;
            if (item.Done == true) text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (item.Owner is { } owner) meta.Children.Add(Mono(owner));
            if (item.Due is { } due) meta.Children.Add(Mono("Due " + due));
            foreach (var id in item.Lines)
                if (m.Lines.FirstOrDefault(l => l.Id == id) is { } line)
                {
                    var cite = MurmurStyle.Named(new HyperlinkButton { Content = MeetingExport.Clock(line.Start), Padding = new Thickness(0), FontSize = 11 }, "Cited line at " + MeetingExport.Clock(line.Start));
                    cite.Click += (_, _) => { tabName = "Transcript"; Render(); };
                    meta.Children.Add(cite);
                }
            var stack = new StackPanel { Spacing = 3, Children = { text, meta } };
            FrameworkElement row = stack;
            if (actions)
            {
                var check = MurmurStyle.Named(new CheckBox { IsChecked = item.Done == true, MinWidth = 0, Content = stack }, item.Done == true ? "Done" : "Not done");
                check.Click += (_, _) => Recorder.ToggleAction(index, m.Id);
                row = check;
            }
            else row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { MurmurStyle.Text("•", "MurmurSettingLabel"), stack } };
            box.Children.Add(row);
        }
        panel.Children.Add(box);
    }

    /// <summary>Separation progress with Skip, then each speaker with Rename and Merge into (app/MeetingsUI.swift speakers()).</summary>
    void SpeakersSection(StackPanel panel, Meeting m)
    {
        if (SpeakerFacts.Status(m.Speakers, Recorder.Separating.Contains(m.Id), m.Source.MicOnly) is { } status)
        {
            var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(Lede(status));
            if (m.Speakers?.State == SpeakerState.Pending) { var skip = Btn("Skip", () => Recorder.SkipSeparation(m.Id)); Grid.SetColumn(skip, 1); row.Children.Add(skip); }
            panel.Children.Add(row);
        }
        var numbers = MeetingSpeakers.Numbers(m);
        if (m.Speakers?.State != SpeakerState.Done || numbers.Length == 0) return;
        panel.Children.Add(Mono("SPEAKERS", 11, accent: true));
        var flow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (int number in numbers)
        {
            int n = number;
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += (_, _) => _ = RenameSpeaker(m, n);
            menu.Items.Add(rename);
            if (numbers.Length > 1)
            {
                var merge = new MenuFlyoutSubItem { Text = "Merge into" };
                foreach (int other in numbers.Where(o => o != n))
                {
                    int o = other;
                    var item = new MenuFlyoutItem { Text = MeetingSpeakers.Label(o, m) };
                    item.Click += (_, _) => Recorder.MergeSpeaker(n, o, m.Id);
                    merge.Items.Add(item);
                }
                menu.Items.Add(merge);
            }
            var b = Btn(MeetingSpeakers.Label(n, m), () => { }, name: "Speaker " + MeetingSpeakers.Label(n, m));
            b.Flyout = menu;
            flow.Children.Add(b);
        }
        panel.Children.Add(new ScrollViewer { Content = flow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(MurmurStyle.Text("Names stay with this meeting. To move one line, use its speaker label.", "MurmurCaption"));
    }

    async Task RenameSpeaker(Meeting m, int number)
    {
        var box = MurmurStyle.Named(new TextBox { Text = m.Speakers?.Names.GetValueOrDefault(number) ?? "", PlaceholderText = $"Speaker {number}", MaxLength = 60 }, "Speaker name");
        var dialog = new ContentDialog
        {
            XamlRoot = root(), Title = "Rename speaker", PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 10, Children = { MurmurStyle.Text($"The name is kept with this meeting only. Leave it empty to go back to “Speaker {number}”."), box } }
        };
        if (await MurmurStyle.Named(dialog, "Rename speaker").ShowAsync() == ContentDialogResult.Primary) Recorder.RenameSpeaker(number, box.Text, m.Id);
    }

    void TranscriptTab(StackPanel panel, Meeting m)
    {
        SpeakersSection(panel, m);
        if (m.Lines.Any(l => l.Shown != null))
        {
            var echo = new CheckBox { Content = "Show hidden echo", IsChecked = showEcho, FontSize = 13 };
            echo.Click += (_, _) => { showEcho = echo.IsChecked == true; Render(); };
            panel.Children.Add(echo);
        }
        if (m.Lines.Count == 0) panel.Children.Add(Lede("No words were transcribed."));
        foreach (var line in m.Lines.Where(l => showEcho || !l.IsHidden))
        {
            var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(56) }, new ColumnDefinition { Width = new GridLength(96) }, new ColumnDefinition() } };
            row.Children.Add(Mono(MeetingExport.Clock(line.Start)));
            var who = MurmurStyle.Text(MeetingSpeakers.Label(line, m), "MurmurSettingLabel"); who.FontSize = 13; who.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            FrameworkElement whoCell = who;
            var numbers = MeetingSpeakers.Numbers(m);
            if (m.Speakers?.State == SpeakerState.Done && line.Stream == m.Source.SpeakerStream && line.Speaker is not null)
            {
                // The Mac's per-line menu: move this one line to another speaker or a new one.
                var menu = new MenuFlyout();
                foreach (int other in numbers.Where(o => o != line.Speaker))
                {
                    int o = other; int id = line.Id;
                    var item = new MenuFlyoutItem { Text = "Move to " + MeetingSpeakers.Label(o, m) };
                    item.Click += (_, _) => Recorder.ReassignLine(id, o, m.Id);
                    menu.Items.Add(item);
                }
                var fresh = new MenuFlyoutItem { Text = "Move to a new speaker" };
                int lineId = line.Id, next = (numbers.Length == 0 ? 0 : numbers.Max()) + 1;
                fresh.Click += (_, _) => Recorder.ReassignLine(lineId, next, m.Id);
                menu.Items.Add(fresh);
                who.TextTrimming = TextTrimming.CharacterEllipsis;
                whoCell = new Button { Content = who, Flyout = menu, Padding = new Thickness(0), MinHeight = 0, BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left };
                MurmurStyle.Named(whoCell, MeetingSpeakers.Label(line, m) + ", change speaker");
            }
            var words = MurmurStyle.Text(line.VisibleText, "MurmurSettingLabel"); words.FontSize = 14; words.IsTextSelectionEnabled = true;
            Grid.SetColumn(whoCell, 1); Grid.SetColumn(words, 2);
            row.Children.Add(whoCell); row.Children.Add(words);
            panel.Children.Add(row);
        }
    }

    // ---- sheets and files ----

    async Task ConfirmDelete(List<Meeting> meetings)
    {
        if (meetings.Count == 0) return;
        var one = meetings.Count == 1;
        var dialog = new ContentDialog
        {
            XamlRoot = root(), Title = one ? "Delete this meeting?" : $"Delete {meetings.Count} meetings?",
            Content = new TextBlock { Text = one ? $"“{meetings[0].Title}”, its transcript and its notes are deleted from this PC. This can’t be undone." : "Their transcripts and notes are deleted from this PC. This can’t be undone.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await MurmurStyle.Named(dialog, "Delete meetings").ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var m in meetings) Recorder.Delete(m.Id);
        chosen.Clear(); selecting = false;
        if (meetings.Any(m => m.Id == selectedId)) selectedId = null;
        Render();
    }

    async Task SaveMarkdown(Meeting m)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(MeetingExport.FileName(m)) };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        InitializeWithWindow.Initialize(picker, hwnd());
        if (await picker.PickSaveFileAsync() is not { } file) return;
        try { await Windows.Storage.FileIO.WriteTextAsync(file, MeetingExport.Markdown(m)); }
        catch (Exception) { Recorder.Message = "The meeting could not be saved."; }
    }
}
