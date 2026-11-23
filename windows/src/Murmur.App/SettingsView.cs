using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Murmur.Platform;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Murmur.App;

internal sealed partial class SettingsView
{
    public static readonly string[] Panes = ["General", "Dictation", "Writing", "Meetings", "Privacy & Storage", "Providers", "Updates"];
    static readonly Dictionary<string, string[]> Tabs = new() { ["Dictation"] = ["Recording", "Transcription", "Insertion"], ["Writing"] = ["Cleanup", "Modes", "Dictionary", "Cues", "Learning"] };
    static readonly Dictionary<string, string> Descriptions = new()
    {
        ["General"] = "Make Murmur feel at home on your PC.",
        ["Dictation"] = "Set up your voice-to-text workflow.",
        ["Writing"] = "Shape your text and keep your own voice.",
        ["Meetings"] = "Notes and transcripts for your calls.",
        ["Privacy & Storage"] = "Choose what Murmur saves on this PC, and for how long.",
        ["Updates"] = "Keep Murmur current, and choose whether it starts with Windows.",
        ["Providers"] = "Needed only for cloud routes. Stored in Windows Credential Manager.",
    };

    readonly AppServices s;
    readonly Controller controller;
    readonly Func<XamlRoot?> root;
    readonly Func<nint> hwnd;
    readonly Dictionary<string, StackPanel> panes = [];
    readonly List<Action> refreshNotes = [];
    readonly InfoBar status = MurmurStyle.Named(new InfoBar { IsClosable = true }, "Data operation status");
    readonly TextBlock title = new() { Style = MurmurStyle.Style("MurmurHeading") };
    readonly TextBlock description = MurmurStyle.Text("", "MurmurDescription");
    readonly Border tabHost = new() { Margin = new Thickness(0, 24, 0, 0) };
    readonly ContentControl content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    readonly Dictionary<string, int> tabMemory = [];
    StackPanel page = null!;
    string current = "";

    /// <summary>The pane area; the shell hosts it next to the Settings sidebar.</summary>
    public ScrollViewer View { get; }

    /// <summary>One built pane (the welcome guide reuses Transcription and Cleanup).</summary>
    public StackPanel Body(string pane) => panes[pane];

    internal SettingsView(AppServices services, Controller controller, Func<XamlRoot?> root, Func<nint> hwnd)
    {
        s = services;
        this.controller = controller;
        this.root = root;
        this.hwnd = hwnd;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        description.Margin = new Thickness(0, 6, 0, 0);
        View = MurmurStyle.Named(new ScrollViewer
        {
            Content = new StackPanel { Padding = new Thickness(36, 28, 36, 32), Width = 772, HorizontalAlignment = HorizontalAlignment.Left, Children = { title, description, tabHost, content } }
        }, "Settings pane");
        BuildPane("General", BuildGeneral);
        BuildPane("Recording", BuildRecording);
        BuildPane("Transcription", BuildTranscription);
        BuildPane("Insertion", BuildInsertion);
        BuildPane("Cleanup", BuildCleanup);
        BuildPane("Modes", BuildModes);
        BuildPane("Dictionary", BuildDictionary);
        BuildPane("Cues", BuildCues);
        BuildPane("Learning", BuildLearning);
        BuildPane("Meetings", BuildMeetings);
        BuildPane("Privacy & Storage", BuildData);
        BuildPane("Providers", BuildKeys);
        BuildPane("Updates", BuildUpdates);
        UpdateModelRoutes();
        Show("General");
    }

    void BuildPane(string name, Action build)
    {
        page = new StackPanel();
        build();
        panes.Add(name, page);
    }

    /// <summary>Settings search: the first pane (and tab) whose labels contain the text, or null.</summary>
    public (string Pane, int Tab)? Find(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        foreach (var pane in Panes)
        {
            var names = Tabs.TryGetValue(pane, out var t) ? t : [pane];
            for (int i = 0; i < names.Length; i++)
                if (pane.Contains(query, StringComparison.OrdinalIgnoreCase) || names[i].Contains(query, StringComparison.OrdinalIgnoreCase) || Texts(panes[names[i]]).Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    return (pane, i);
        }
        return null;
    }

    /// <summary>Every setting row whose labels or description contain the text, for the search overlay (the Mac's SettingsSearch.matches): its label, and the pane and tab it is in. Rows that start with a sentence (description-only paragraphs) are left out.</summary>
    public List<(string Pane, int Tab, string Title, string Where)> Matches(string query)
    {
        var found = new List<(string, int, string, string)>();
        query = query.Trim();
        if (query.Length == 0) return found;
        foreach (var pane in Panes)
        {
            var names = Tabs.TryGetValue(pane, out var t) ? t : [pane];
            for (int i = 0; i < names.Length; i++)
                if (panes[names[i]] is Panel body)
                    foreach (var row in body.Children)
                        if (Texts(row).ToList() is { Count: > 0 } texts && texts[0].Length <= 60 && !texts[0].EndsWith('.') && texts.Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)))
                            found.Add((pane, i, texts[0], names.Length > 1 ? $"{pane} > {names[i]}" : pane));
        }
        return found;
    }

    // Walks the built (possibly never shown) tree by its logical children.
    static IEnumerable<string> Texts(object node)
    {
        switch (node)
        {
            case TextBlock { Text.Length: > 0 } text: yield return text.Text; break;
            case Panel panel: foreach (var child in panel.Children) foreach (var x in Texts(child)) yield return x; break;
            case Border { Child: { } c }: foreach (var x in Texts(c)) yield return x; break;
            case ContentControl { Content: { } c }: foreach (var x in Texts(c)) yield return x; break;
        }
    }

    /// <summary>After a search jump: scrolls to the first row whose label matches and tints it for a moment (the Mac scrolls to the setting; the tint says which one).</summary>
    public void Reveal(string query)
    {
        query = query.Trim();
        if (query.Length == 0 || content.Content is not Panel body) return;
        foreach (var row in body.Children.OfType<FrameworkElement>())
        {
            if (!Texts(row).Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            var timer = s.UI.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(60);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.2 });
                var wash = MurmurStyle.ThemeBrush("MurmurAccentWash", row);
                if (row is Grid g) g.Background = wash;
                else if (row is Panel p) p.Background = wash;
                else if (row is Border b) b.Background = wash;
                var fade = s.UI.CreateTimer();
                fade.Interval = TimeSpan.FromSeconds(2.5);
                fade.IsRepeating = false;
                fade.Tick += (_, _) => { if (row is Panel q) q.Background = null; else if (row is Border b2) b2.Background = null; };
                fade.Start();
            };
            timer.Start();
            return;
        }
    }

    public void Show(string pane, int tab = -1)
    {
        current = pane;
        title.Text = pane;
        description.Text = Descriptions[pane];
        if (Tabs.TryGetValue(pane, out var names))
        {
            int index = tab >= 0 ? tab : tabMemory.GetValueOrDefault(pane);
            tabHost.Child = new Segmented(pane + " tabs", names, index, i => { tabMemory[pane] = i; ShowPane(names[i]); }, stretch: false);
            tabMemory[pane] = index;
            ShowPane(names[index]);
        }
        else { tabHost.Child = null; ShowPane(pane); }
    }

    void ShowPane(string pane)
    {
        if (!panes.TryGetValue(pane, out var body)) return;
        if (pane == "Modes") Render(body, RenderModes);
        content.Content = body;
        View.ChangeView(null, 0, null, true);
    }

    void BuildGeneral()
    {
        SegmentedRow("Dictation indicator", "Where the recording indicator and its notices appear.",
            ["Top of the screen", "Bottom of the screen"], s.Settings.Get(Prefs.DictationIndicator) == DictationIndicator.Top ? 0 : 1,
            i => s.Settings.Set(Prefs.DictationIndicator, i == 0 ? DictationIndicator.Top : DictationIndicator.Bottom));
        Toggle("Show in taskbar", "Adds Murmur's window to the taskbar and Alt+Tab. The tray icon always stays, so you can reopen Murmur from it.", Prefs.ShowInDock, s.NotifyShowInTaskbarChanged);

        Heading("Tips");
        var tips = new Tips(s.Settings);
        var enabled = MurmurStyle.Named(new ToggleSwitch { IsOn = tips.Enabled, OnContent = null, OffContent = null, MinWidth = 0 }, "Show tips");
        enabled.Toggled += (_, _) => tips.SetEnabled(enabled.IsOn);
        Row("Show tips", "Milestones and occasional tips about features you have not tried.", enabled);
        var reset = MurmurStyle.Named(new Button { Content = "Show Tips Again" }, "Show Tips Again");
        reset.Click += (_, _) => { tips.Reset(); enabled.IsOn = tips.Enabled; };
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(reset);
    }

    readonly Dictionary<object, Action<bool>> modelRoutes = [];
    bool speechFilesPresent, cleanupFilesPresent;
    int modelCheck;
    readonly List<Action> refreshRules = [];
    Segmented? styleTabs;
    void StyleChanged() { styleTabs?.Select((int)CleanupStyleNow); foreach (var refresh in refreshNotes) refresh(); }

    RouteSettings Routes => RouteSettings.Load(s.Settings);
    void SaveRoutes(Func<RouteSettings, RouteSettings> change) { change(Routes).Save(s.Settings); foreach (var refresh in refreshNotes) refresh(); }

    // ---- building blocks ----

    void Heading(string title)
    {
        var heading = MurmurStyle.Text(title, "MurmurSettingLabel");
        heading.FontSize = 14; heading.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        heading.Margin = new Thickness(0, page.Children.Count > 0 ? 28 : 8, 0, 8);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        page.Children.Add(heading);
    }

    void Note(string text)
    {
        var note = MurmurStyle.Text(text);
        note.Margin = new Thickness(0, 8, 0, 8);
        page.Children.Add(note);
    }

    static Button Help(string text)
    {
        var tip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        // The Mac's questionmark.circle: a 16 px ring with a small ?.
        var mark = new TextBlock { Text = "?", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Style = MurmurStyle.Style("MurmurTabOff") };
        var ring = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2), Style = MurmurStyle.Style("MurmurRuleTopStrong"), Child = mark };
        var help = new Button
        {
            Content = ring, Padding = new Thickness(4), MinWidth = 0, MinHeight = 0,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Flyout = new Flyout { Content = tip }, VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(help, text);
        return MurmurStyle.Named(help, "About: " + text);
    }

    /// <summary>The Mac's row: label, (?) help, and the control at the right. A <paramref name="visible"/> description (privacy disclosures) also stays on screen under the label.</summary>
    void Row(string title, string? description, FrameworkElement control, bool visible = false)
    {
        var label = MurmurStyle.Text(title, "MurmurSettingLabel");
        label.VerticalAlignment = VerticalAlignment.Center; label.FontSize = 13;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { label } };
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { line } };
        if (description is not null)
        {
            if (visible) labels.Children.Add(MurmurStyle.Text(description, "MurmurCaption"));
            else line.Children.Add(Help(description));
        }
        var grid = new Grid
        {
            ColumnSpacing = 16, MinHeight = 64, Padding = new Thickness(0, 8, 0, 8),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { labels, control }
        };
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        page.Children.Add(grid);
    }

    ToggleSwitch Toggle(string title, string description, Setting<bool> setting, Action? changed = null, bool visible = false)
    {
        var toggle = MurmurStyle.Named(new ToggleSwitch { IsOn = s.Settings.Get(setting), OnContent = null, OffContent = null, MinWidth = 0 }, title);
        toggle.Toggled += (_, _) => { s.Settings.Set(setting, toggle.IsOn); changed?.Invoke(); };
        refreshRules.Add(() => toggle.IsOn = s.Settings.Get(setting));
        Row(title, description, toggle, visible);
        return toggle;
    }

    void Field(StackPanel fields, string header, string value, string placeholder, Action<string> save)
    {
        var box = MurmurStyle.Named(new TextBox { Header = header, Text = value, PlaceholderText = placeholder }, header);
        box.TextChanged += (_, _) => save(box.Text.Trim());
        fields.Children.Add(box);
    }

    static ComboBox Picker(string title, string[] items, int selected, Action<int> changed)
    {
        var box = MurmurStyle.Named(new ComboBox { Width = 208 }, title);
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        return box;
    }

    void Choice(string title, string? description, string[] items, int selected, Action<int> changed, bool visible = false) =>
        Row(title, description, Picker(title, items, selected, changed), visible);

    /// <summary>A label, then an underline-tab choice with its help text under it (the Mac's Cleanup style, indicator and route pickers).</summary>
    Segmented SegmentedRow(string title, string? help, string[] items, int selected, Action<int> changed, Func<int, string>? note = null, int[]? cloud = null)
    {
        Heading(title);
        var tabs = new Segmented(title, items, selected, i => { changed(i); foreach (var refresh in refreshNotes) refresh(); }, cloud: cloud);
        var row = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { tabs } };
        if (help is not null && note is not null) { var h = Help(help); Grid.SetColumn(h, 1); row.Children.Add(h); }
        page.Children.Add(row);
        if (note is not null)
        {
            var caption = MurmurStyle.Text(note(selected));
            caption.Margin = new Thickness(0, 8, 0, 0);
            refreshNotes.Add(() => caption.Text = note(tabs.SelectedIndex));
            page.Children.Add(caption);
        }
        else if (help is not null) { var caption = MurmurStyle.Text(help); caption.Margin = new Thickness(0, 8, 0, 0); page.Children.Add(caption); }
        return tabs;
    }

    // ---- routes ----

    static readonly (TranscriptionRoute Route, string Label)[] TranscriptionChoices = [(TranscriptionRoute.Local, "Local (this PC)"), (TranscriptionRoute.Cloud, "Custom Provider"), (TranscriptionRoute.MurmurCloud, "Murmur Cloud")];
    static readonly (CleanupChoice Choice, string Label)[] CleanupChoices = [(CleanupChoice.LocalRules, "Local rules"), (CleanupChoice.LocalModel, "Local model"), (CleanupChoice.CustomAPI, "Custom Provider"), (CleanupChoice.MurmurCloud, "Murmur Cloud")];

    string TranscriptionNote(TranscriptionRoute route)
    {
        if (route == TranscriptionRoute.Local) return speechFilesPresent ? "Audio stays on this PC. Downloading the model uses the network once. Dictation does not." : "Audio stays on this PC. Download the speech model on this page to use this.";
        var plan = RoutePlan.Make(Routes with { Transcription = route }, true, s.Key);
        return DisclosureText.Summary(plan, ProcessingStage.Transcription)
            ?? (route == TranscriptionRoute.Cloud ? "Sends each recording to the address under Custom Provider. Nothing is sent until you enter an address and a model." : "Sends each recording to Murmur Cloud.");
    }

    string CleanupNote(CleanupChoice choice)
    {
        if (choice == CleanupChoice.LocalRules) return "Text stays on this PC.";
        if (choice == CleanupChoice.LocalModel) return cleanupFilesPresent ? "Text stays on this PC. One fixed prompt: screen context and rewrite presets are not used." : "Text stays on this PC. Download the cleanup model on this page to use this.";
        var plan = RoutePlan.Make(Routes with { Cleanup = choice }, true, s.Key);
        return DisclosureText.Summary(plan, ProcessingStage.Cleanup)
            ?? (choice == CleanupChoice.CustomAPI ? "Sends the transcript text to the address under Custom Provider. Nothing is sent until you enter an address and a model." : "Sends the transcript text to Murmur Cloud.");
    }

    void BuildTranscription()
    {
        Heading("Speech recognition");
        // The model row follows the chosen languages: English uses Parakeet, others the model that covers them (PR #102).
        var speechHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ModelRow? speechRow = null;
        void ShowSpeechRow() { speechRow?.Detach(); speechRow = new ModelRow(s, controller.RequiredSpeechSlot, controller, root, go => { s.Local.Release(); go(); }, UpdateModelRoutes, required: true); speechHost.Content = MurmurStyle.Card(speechRow.Panel); }
        var picker = new DictationLanguagePicker(() => s.Settings.Get(Prefs.DictationLanguages), v => { s.Settings.Set(Prefs.DictationLanguages, v); ShowSpeechRow(); UpdateModelRoutes(); foreach (var refresh in refreshNotes) refresh(); }, () => Routes.Transcription == TranscriptionRoute.Local);
        refreshNotes.Add(picker.Refresh);
        refreshNotes.Add(() => picker.IsEnabled = !controller.IsBusy && !controller.SpeechModelDownloading);
        page.Children.Add(picker.Panel);
        ShowSpeechRow();
        page.Children.Add(speechHost);
        Toggle("Use GPU when available", "Accelerates live words and local cleanup.", Prefs.UseGpuWhenAvailable,
            LocalCleanupRunner.Shared.MemoryPressure, visible: true);
        var tabs = SegmentedRow("Where transcription runs", null, [.. TranscriptionChoices.Select(c => c.Label)], Array.FindIndex(TranscriptionChoices, c => c.Route == Routes.Transcription),
            i => SaveRoutes(r => r with { Transcription = TranscriptionChoices[i].Route }), i => TranscriptionNote(TranscriptionChoices[i].Route));
        tabs.SetEnabled(0, false);
        modelRoutes[TranscriptionRoute.Local] = on => tabs.SetEnabled(0, on && !controller.SpeechModelDownloading);
        refreshNotes.Add(() => { for (int i = 1; i < TranscriptionChoices.Length; i++) tabs.SetEnabled(i, !controller.SpeechModelDownloading); });
        var body = page;
        var providerSection = new StackPanel();
        body.Children.Add(providerSection);
        page = providerSection;
        BuildProvider(transcription: true);
        page = body;
        void RefreshProvider() => providerSection.Visibility = ProviderFieldGating.ShowTranscriptionFields(Routes.Transcription) ? Visibility.Visible : Visibility.Collapsed;
        refreshNotes.Add(RefreshProvider);
        RefreshProvider();
    }

    CleanupOptions SavedCleanupOptions => ProcessingSettings.Load(s.Settings).Options;
    CleanupStyle CleanupStyleNow => CleanupStyles.Current(Routes.Cleanup, SavedCleanupOptions);

    void SetCleanupStyle(CleanupStyle style)
    {
        var (route, options) = CleanupStyles.Select(style, Routes.Cleanup, SavedCleanupOptions);
        s.Settings.Set(Prefs.CleanupFillers, options.Fillers);
        s.Settings.Set(Prefs.CleanupCorrections, options.Corrections);
        s.Settings.Set(Prefs.CleanupLists, options.Lists);
        s.Settings.Set(Prefs.CleanupPunctuation, options.Punctuation);
        s.Settings.Set(Prefs.CleanupSpoken, options.Spoken);
        s.Settings.Set(Prefs.CleanupPolish, options.Polish);
        s.Settings.Set(Prefs.CleanupProfessional, options.Professional);
        if (route != Routes.Cleanup) SaveRoutes(r => r with { Cleanup = route });
    }

    string StyleHelp(CleanupStyle style) => style switch
    {
        CleanupStyle.Faithful => "Keeps the words as dictated, apart from dictionary spellings: “Um, let’s meet Tuesday, no, I mean, Thursday, at the cafe.”",
        CleanupStyle.Natural => "Removes fillers and applies clear spoken corrections, otherwise keeping your wording: “Let’s meet Thursday at the cafe.”",
        CleanupStyle.Polish => "Also resolves backtracks and false starts, and fixes small grammar slips, keeping your voice: “Let’s meet Thursday at the cafe.”",
        _ => "Rewrites into clear, professional writing of what you meant: “Let’s meet on Thursday at the café.”"
    };

    void BuildCleanup()
    {
        const string cloudHelp = "Polish and Professional run on Murmur Cloud or a Custom Provider.";
        var offer = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var offerText = MurmurStyle.Text("");
        var getCloud = new Button { Content = MurmurCloud.PurchaseAvailable ? "Start a trial" : "Coming soon", IsEnabled = MurmurCloud.PurchaseAvailable };
        getCloud.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(MurmurCloud.BaseURL.Appending("/billing/checkout") + "?plan=basic"));
        offer.Children.Add(offerText);
        offer.Children.Add(getCloud);
        styleTabs = SegmentedRow("Cleanup style", cloudHelp, ["Faithful", "Natural", "Polish", "Professional"], (int)CleanupStyleNow,
            i =>
            {
                var style = (CleanupStyle)i;
                if (style.NeedsModel() && !Routes.Cleanup.CloudOnlyOK())
                {
                    offerText.Text = $"{style} runs on Murmur Cloud or a Custom Provider.";
                    offer.Visibility = Visibility.Visible;
                    styleTabs!.Select((int)CleanupStyleNow);
                    return;
                }
                offer.Visibility = Visibility.Collapsed;
                SetCleanupStyle(style);
                foreach (var refresh in refreshRules) refresh();
                StyleChanged();
            }, i => StyleHelp((CleanupStyle)i), cloud: [2, 3]);
        page.Children.Add(offer);

        var body = page;
        var routeSection = new StackPanel();
        body.Children.Add(routeSection);
        page = routeSection;
        SegmentedRow("Cleanup runs on", null, CleanupChoices.Select(c => c.Label).ToArray(), Array.FindIndex(CleanupChoices, c => c.Choice == Routes.Cleanup),
            i =>
            {
                offer.Visibility = Visibility.Collapsed;
                SaveRoutes(r => r with { Cleanup = CleanupChoices[i].Choice });
                StyleChanged();
            }, i => CleanupNote(CleanupChoices[i].Choice));
        var naturalHelp = MurmurStyle.Text("");
        routeSection.Children.Add(naturalHelp);
        var cleanupRow = new ModelRow(s, s.CleanupSlot, controller, root, go => { LocalCleanupRunner.Shared.Shutdown(); go(); }, UpdateModelRoutes);
        var modelCard = MurmurStyle.Card(cleanupRow.Panel);
        routeSection.Children.Add(modelCard);
        page = body;
        page.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 16, 0, 8), Style = MurmurStyle.Style("MurmurRuleLine") });
        Toggle("Remove filler words", "Takes out “um”, “uh” and similar.", Prefs.CleanupFillers, StyleChanged);
        Toggle("Apply spoken corrections", "Uses your final wording for clear corrections. Ambiguous corrections stay.", Prefs.CleanupCorrections, StyleChanged);
        Toggle("Format spoken lists", "Turns “one, two, three” into a numbered list.", Prefs.CleanupLists, StyleChanged);
        Toggle("Fix punctuation and capitalization", "Adds commas, periods and capitals.", Prefs.CleanupPunctuation, StyleChanged);
        Toggle("Spoken commands and numbers", "Interprets “comma” and “new paragraph”, and writes spoken numbers as digits.", Prefs.CleanupSpoken, StyleChanged);
        var screenContext = Toggle("Use screen context", "Available for Polish and Professional on Murmur Cloud or a Custom Provider. Modes can turn it on or off.", Prefs.ScreenContext);
        Note("Website modes use the focused page in Edge, Chrome, Brave, Vivaldi, Opera, Arc, or Firefox when Windows can read it. App matching still works in other browsers and when the page is unavailable.");
        var providerSection = new StackPanel();
        body.Children.Add(providerSection);
        page = providerSection;
        BuildProvider(transcription: false);
        page = body;
        void RefreshGating()
        {
            var route = Routes.Cleanup;
            var style = CleanupStyleNow;
            routeSection.Visibility = ProviderFieldGating.ShowCleanupRoute(style) ? Visibility.Visible : Visibility.Collapsed;
            providerSection.Visibility = ProviderFieldGating.ShowCleanupFields(route, style) ? Visibility.Visible : Visibility.Collapsed;
            modelCard.Visibility = route == CleanupChoice.LocalModel ? Visibility.Visible : Visibility.Collapsed;
            screenContext.IsEnabled = style.NeedsModel();
            styleTabs.SetCloud(2, !route.CloudOnlyOK());
            styleTabs.SetCloud(3, !route.CloudOnlyOK());
            naturalHelp.Visibility = style == CleanupStyle.Natural ? Visibility.Visible : Visibility.Collapsed;
            naturalHelp.Text = route switch
            {
                CleanupChoice.LocalRules => "Natural uses built-in rules on this PC.",
                CleanupChoice.LocalModel => "Natural runs the Local model’s one cleanup under Murmur’s guard.",
                _ => "On this route Natural uses built-in rules on this PC. Choose Polish or Professional to use the AI model.",
            };
        }
        refreshNotes.Add(RefreshGating);
        RefreshGating();
    }

    void BuildLearning()
    {
        Toggle("Learn from my corrections", "After a dictation, Murmur watches only that pasted text for about a minute. If you fix a word, such as a name, it can learn the spelling for next time. It never reads password fields, and nothing leaves this PC.", Prefs.LearningEnabled, controller.LearningSettingChanged, visible: true);
        Toggle("Learn only from readable fields", "Murmur first reads the text box through Windows accessibility. If an app does not allow that, it can instead follow the keys you type into it, in memory only, for that one dictation: keys are never saved, logged or sent. Turn this on to use accessibility only.", Prefs.ReadableLearning, visible: true);
    }

    /// <summary>Enables local transcription only while its files are installed and pass the pinned checksums. Hashing runs off the UI thread (once per launch, then cached).</summary>
    void UpdateModelRoutes()
    {
        int check = ++modelCheck;
        _ = Task.Run(() =>
        {
            var speechInstalled = s.Local.ModelFilesPresent;
            var cleanupInstalled = LocalCleanupRunner.InstalledStatus() is null;
            var speech = s.Local.HelperPresent && speechInstalled && s.Local.Verify();
            s.UI.TryEnqueue(() =>
            {
                if (check != modelCheck) return;
                speechFilesPresent = speechInstalled;
                cleanupFilesPresent = cleanupInstalled;
                modelRoutes[TranscriptionRoute.Local](speech);
                foreach (var refresh in refreshNotes) refresh();
            });
        });
    }

    void BuildProvider(bool transcription)
    {
        var fields = new StackPanel { Spacing = 12 };
        var routes = Routes;
        if (transcription)
        {
            Field(fields, "Transcription API base URL", routes.CustomTranscriptionAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomTranscriptionAPIBaseURL = v }));
            Field(fields, "Transcription model name", routes.CustomTranscriptionModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomTranscriptionModel = v }));
        }
        else
        {
            Field(fields, "Cleanup API base URL", routes.CustomAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomAPIBaseURL = v }));
            Field(fields, "Cleanup model name", routes.CustomAPIModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomAPIModel = v }));
            string[] efforts = ["", "minimal", "low", "medium", "high"];
            var effort = Picker("Reasoning effort", efforts.Select(e => e.Length == 0 ? "None" : e).ToArray(), Math.Max(0, Array.IndexOf(efforts, routes.CustomAPIReasoningEffort)), i => SaveRoutes(x => x with { CustomAPIReasoningEffort = efforts[i] }));
            effort.Header = "Reasoning effort";
            fields.Children.Add(effort);
        }

        fields.Children.Add(MurmurStyle.Text("Any OpenAI-compatible server. A server on this PC (127.0.0.1) needs no key and sends nothing off the PC. Manage keys under Providers.", "MurmurCaption"));
        page.Children.Add(MurmurStyle.Named(new Expander
        {
            Header = "Custom Provider", Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true
        }, transcription ? "Custom transcription provider" : "Custom cleanup provider"));
    }

    void BuildKeys()
    {
        Heading("API Keys");
        Note("API keys stay on this PC, outside settings and History.");
        KeyRow("Custom transcription key", ProviderID.CustomTranscription, () => Routes.CustomTranscriptionAPIBaseURL);
        KeyRow("Custom cleanup key", ProviderID.CustomCleanup, () => Routes.CustomAPIBaseURL);
        KeyRow("Murmur Cloud key", ProviderID.MurmurCloud, () => "");
    }

    void KeyRow(string header, ProviderID provider, Func<string> address)
    {
        var box = MurmurStyle.Named(new PasswordBox { PlaceholderText = "Enter API key" }, header);
        var result = MurmurStyle.Text("", "MurmurCaption");
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        void Refresh() { try { result.Text = s.Credentials.Exists(provider) ? "A key is saved." : KeyText.None; } catch (CredentialException) { result.Text = KeyText.Unreadable; } }
        Refresh();
        var save = MurmurStyle.Named(new Button { Content = "Save" }, "Save " + header);
        save.Click += (_, _) =>
        {
            if (APIKey.Create(box.Password) is not { } key) { result.Text = KeyText.Invalid; return; }
            try
            {
                if (provider == ProviderID.MurmurCloud) s.Credentials.Save(key, provider);
                else CustomAPIKeyBinding.Save(key, address(), s.Settings, s.Credentials, provider);
                box.Password = ""; result.Text = KeyText.Saved;
            }
            catch (ProviderException e) { result.Text = e.Error.UserMessage; }
            catch (CredentialException) { result.Text = KeyText.StoreFailed; }
        };
        var validate = MurmurStyle.Named(new Button { Content = "Validate" }, "Validate " + header);
        validate.Click += async (_, _) =>
        {
            APIKey? key = APIKey.Create(box.Password);
            try { key ??= provider == ProviderID.MurmurCloud ? s.Credentials.Read(provider) : CustomAPIKeyBinding.Read(address(), s.Settings, s.Credentials, provider); }
            catch (Exception e) when (e is CredentialException or ProviderException) { result.Text = KeyText.Unreadable; return; }
            result.Text = KeyText.Checking;
            result.Text = await KeyCheck.RunAsync(provider, key, address());
        };
        var remove = MurmurStyle.Named(new Button { Content = "Remove" }, "Remove " + header);
        remove.Click += (_, _) =>
        {
            try { s.Credentials.Delete(provider); if (provider != ProviderID.MurmurCloud) CustomAPIKeyBinding.Clear(s.Settings, provider); result.Text = KeyText.Removed; }
            catch (CredentialException) { result.Text = KeyText.RemoveFailed; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, validate, remove } };
        page.Children.Add(MurmurStyle.Card(new StackPanel
        {
            Spacing = 8, Children = { MurmurStyle.Text(header, "BodyStrongTextBlockStyle"), box, result, buttons }
        }));
    }

    void BuildRecording()
    {
        Heading("Shortcut");
        var spec = s.Settings.Get(Prefs.Shortcut);
        string[] labels = ["Right Ctrl", "Right Alt", "Right Shift", "Custom key"];
        int preset = Array.IndexOf(ShortcutSpec.Presets, spec);
        var presets = Picker("Dictation shortcut", labels, preset >= 0 ? preset : spec.StartsWith("custom:") ? 3 : 0, i =>
        {
            if (i >= ShortcutSpec.Presets.Length) return;
            s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Presets[i]);
            controller.ReloadShortcuts();
        });
        Row("Dictation shortcut", "Hold the shortcut to record and release to stop. Escape cancels.", presets);
        var record = MurmurStyle.Named(new Button { Content = "Record key" }, "Record custom dictation key");
        var recorded = MurmurStyle.Text(spec.StartsWith("custom:") ? ShortcutSpec.Label(spec) : "", "MurmurCaption");
        record.Click += async (_, _) =>
        {
            record.IsEnabled = false;
            recorded.Text = "Press a key (Esc cancels)…";
            var vk = await controller.RecordNextKey();
            string? conflict = vk == 0 ? null : VoiceEditSettings.VoiceEditConflict(s.Settings.Get(Prefs.VoiceEditShortcut), new EditChord(vk, 0), spec => VoiceEditBinding.Parse(spec, 0), 0);
            if (vk != 0 && conflict is null)
            {
                s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Custom(vk));
                controller.ReloadShortcuts();
                presets.SelectedIndex = 3;
            }
            var current = s.Settings.Get(Prefs.Shortcut);
            recorded.Text = conflict ?? (current.StartsWith("custom:") ? ShortcutSpec.Label(current) : "");
            record.IsEnabled = true;
        };
        presets.SelectionChanged += (_, _) => { if (presets.SelectedIndex is >= 0 and < 3) recorded.Text = ""; };
        Row("Custom key", null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recorded, record } });
        BuildVoiceEdit();
        Toggle("Press once to start, again to stop", "Holding still works: release to stop. Off means hold to record.", Prefs.PressToToggle);

        Heading("Audio");
        List<AudioDevice> devices = [];
        try { devices = [.. AudioCapture.ListDevices()]; } catch (Exception) { /* no audio service: only the default is offered */ }
        var device = s.Settings.Get(Prefs.InputDeviceUID);
        var picker = Picker("Microphone", ["System default", .. devices.Select(d => d.Name)],
            Math.Max(0, devices.FindIndex(d => d.Id == device) + 1), i => s.Settings.Set(Prefs.InputDeviceUID, i == 0 ? "" : devices[i - 1].Id));
        picker.IsEnabled = devices.Count > 0;
        Row("Microphone", "Choose the audio input Murmur uses for dictation.", picker);
        Toggle("Start and stop sounds", "Play a short sound when recording starts or stops.", Prefs.RecordingSounds);

        Heading("Live words");
        var liveWordsRow = new ModelRow(s, s.LiveWordsSlot, controller, root, go => go(), UpdateModelRoutes);
        page.Children.Add(MurmurStyle.Card(liveWordsRow.Panel));
        Toggle("Show live words while speaking", "Shows the words in the indicator as you speak. Needs the live words download.", Prefs.LiveWords);
        Toggle("Show live words on cloud routes", "Also shows live words while transcription runs in the cloud. Off by default.", Prefs.LiveWordsOnCloud);
    }

    void BuildInsertion()
    {
        Toggle("Insert at the cursor", "When off, the dictation is saved and shown with a Copy button. Ctrl+Win+Shift+V pastes the last dictation.", Prefs.AutomaticInsertion);
        Toggle("Add a leading space when needed", "Separates dictation from existing text at the cursor.", Prefs.IntelligentLeadingSpace);
    }

    void BuildData()
    {
        Heading("Transcript history");
        Choice("Keep history", HistoryModes.OffDisclosure, ["Save on this PC", "Off"],
            s.Settings.Get(Prefs.HistoryMode) == HistoryMode.On ? 0 : 1,
            i => { s.Settings.Set(Prefs.HistoryMode, i == 0 ? HistoryMode.On : HistoryMode.Off); s.ApplyHistoryMode(); }, visible: true);
        var retentions = Enum.GetValues<HistoryRetention>();
        Choice("Keep transcripts for", null, retentions.Select(r => r.Label()).ToArray(), Array.IndexOf(retentions, s.Settings.Get(Prefs.Retention)), i => s.Settings.Set(Prefs.Retention, retentions[i]));

        Heading("Your data");
        var backup = MurmurStyle.Named(new Button { Content = "Back Up…" }, "Back up Murmur data");
        backup.Click += async (_, _) => await Backup();
        var restore = MurmurStyle.Named(new Button { Content = "Restore…" }, "Restore Murmur backup");
        restore.Click += async (_, _) => await Restore();
        var delete = MurmurStyle.Named(new Button { Content = "Delete All Data…" }, "Delete all Murmur data");
        delete.Click += async (_, _) => await DeleteAll();
        Row("Backup and restore", "Save History, the dictionary and settings to a Murmur Backup folder. API keys and audio are excluded. Restore keeps your current provider routes.", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { restore, backup }
        }, visible: true);
        Row("Delete all data", "Deletes History, settings and credentials from this PC. Downloaded models stay.", delete, visible: true);
        page.Children.Add(status);
    }

    void ShowStatus(string message, bool succeeded)
    {
        status.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        status.Message = message;
        status.IsOpen = true;
    }

    async Task<string?> PickFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, hwnd());
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    async Task<bool> Confirm(string title, string body, string action)
    {
        var dialog = new ContentDialog { XamlRoot = root(), Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await MurmurStyle.Named(dialog, title).ShowAsync() == ContentDialogResult.Primary;
    }

    async Task Backup()
    {
        if (await PickFolder() is not { } parent) return;
        try { DataBackup.Export(s.Locations, s.Settings.Snapshot(), Path.Combine(parent, "Murmur Backup")); ShowStatus("Backup saved in the Murmur Backup folder.", true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RetentionError or StoreError) { ShowStatus("Backup failed: " + e.Message, false); }
    }

    async Task Restore()
    {
        if (await PickFolder() is not { } folder) return;
        if (!File.Exists(Path.Combine(folder, DataBackup.SettingsFile)) && Directory.Exists(Path.Combine(folder, "Murmur Backup"))) folder = Path.Combine(folder, "Murmur Backup");
        try
        {
            var backup = DataBackup.ReadSettings(folder);
            if (!await Confirm("Restore this backup?", "This replaces your History and dictionary with the backup's. Where dictations are sent, your keys and this PC's settings stay as they are.", "Restore")) return;
            var current = s.Settings.Snapshot();
            var merged = DataBackup.Merged(backup, current);
            DataBackup.Restore(folder, s.Locations);
            foreach (var key in current.Keys.Where(k => !merged.ContainsKey(k) && !DataBackup.IsMachineSetting(k))) s.Settings.Remove(key);
            foreach (var (key, value) in merged) s.Settings.Set(key, value);
            s.History.ClearSessionOnly(); s.ReopenData(); controller.ResetMeetings(); controller.ReloadShortcuts();
            ShowStatus("Backup restored. Reopen Settings to see the restored values.", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RetentionError or StoreError) { ShowStatus("Restore failed: " + e.Message, false); }
    }

    async Task DeleteAll()
    {
        if (!await Confirm(DeleteAllDataText.Title, DeleteAllDataText.Body, "Delete all")) return;
        var report = DataWipe.DeleteAllData(s.Locations, () =>
        {
            var removed = new List<string>();
            foreach (var provider in new[] { ProviderID.CustomTranscription, ProviderID.CustomCleanup, ProviderID.CustomAPI, ProviderID.MurmurCloud })
            {
                if (s.Credentials.Exists(provider)) removed.Add(provider.DisplayName());
                s.Credentials.Delete(provider);
                if (provider != ProviderID.MurmurCloud) CustomAPIKeyBinding.Clear(s.Settings, provider);
            }
            s.Credentials.DeleteAll();
            return removed;
        }, s.Settings);
        s.History.ClearSessionOnly();
        s.ReopenData();
        controller.ResetMeetings();
        ShowStatus(report.Succeeded ? "All Murmur data was deleted." : "Some data could not be deleted: " + string.Join(" ", report.Failures), report.Succeeded);
    }
}
