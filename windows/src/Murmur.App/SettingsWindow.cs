using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Murmur.Platform;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Murmur.App;

public sealed partial class SettingsWindow : Window
{
    readonly AppServices s;
    readonly Controller controller;
    readonly Dictionary<string, StackPanel> panes = [];
    readonly List<Action> refreshNotes = [];
    readonly InfoBar status = Fluent.Named(new InfoBar { IsClosable = true }, "Data operation status");
    StackPanel page = null!;

    internal SettingsWindow(AppServices services, Controller controller)
    {
        s = services;
        this.controller = controller;
        InitializeComponent();
        Title = "Murmur Settings";
        Fluent.SetTitleBar(this, Caption);
        Fluent.SetInitialSize(this, 920, 780);
        BuildPane("General", BuildGeneral);
        BuildPane("Recording", BuildRecording);
        BuildPane("Transcription", BuildTranscription);
        BuildPane("Insertion", BuildInsertion);
        BuildPane("Writing", BuildCleanup);
        BuildPane("Privacy & Storage", BuildData);
        BuildPane("Providers", BuildKeys);
        DictationTabs.SelectedItem = DictationTabs.Items[0];
        Navigation.SelectedItem = Navigation.MenuItems[1];
        UpdateModelRoutes();
    }

    void BuildPane(string name, Action build)
    {
        page = new StackPanel { Spacing = 8 };
        build();
        panes.Add(name, page);
    }

    void SelectPane(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Content: string pane }) return;
        PaneTitle.Text = pane;
        PaneDescription.Text = pane switch
        {
            "General" => "Make Murmur feel at home on your PC.",
            "Dictation" => "Set up your voice-to-text workflow.",
            "Writing" => "Shape your text and keep your own voice.",
            "Privacy & Storage" => "Choose what Murmur saves on this PC, and for how long.",
            _ => "Needed only for cloud routes. Stored in Windows Credential Manager."
        };
        DictationTabs.Visibility = pane == "Dictation" ? Visibility.Visible : Visibility.Collapsed;
        ShowPane(pane == "Dictation" ? DictationTabs.SelectedItem?.Text ?? "Recording" : pane);
    }

    void SelectDictationTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Navigation.SelectedItem is NavigationViewItem { Content: "Dictation" } && sender.SelectedItem?.Text is { } pane)
            ShowPane(pane);
    }

    void ShowPane(string pane)
    {
        if (!panes.TryGetValue(pane, out var content)) return;
        PaneContent.Content = content;
        PaneScroll.ChangeView(null, 0, null, true);
    }

    void BuildGeneral() => Choice("Dictation indicator",
        "Where the recording indicator and its notices appear.",
        ["Top of the screen", "Bottom of the screen"], s.Settings.Get(Prefs.DictationIndicator) == DictationIndicator.Top ? 0 : 1,
        i => s.Settings.Set(Prefs.DictationIndicator, i == 0 ? DictationIndicator.Top : DictationIndicator.Bottom));

    readonly Dictionary<object, RadioButton> modelRoutes = [];
    bool speechFilesPresent, cleanupFilesPresent;
    int modelCheck;

    RouteSettings Routes => RouteSettings.Load(s.Settings);
    void SaveRoutes(Func<RouteSettings, RouteSettings> change) { change(Routes).Save(s.Settings); foreach (var refresh in refreshNotes) refresh(); }

    // ---- building blocks ----

    void Heading(string title)
    {
        var heading = Fluent.Text(title, "SubtitleTextBlockStyle");
        heading.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        page.Children.Add(heading);
    }

    void Note(string text) => page.Children.Add(Fluent.Text(text));

    void Row(string title, string? description, FrameworkElement control)
    {
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Fluent.Text(title, "BodyStrongTextBlockStyle"));
        if (description is not null) labels.Children.Add(Fluent.Text(description, "MurmurCaption"));
        var grid = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { labels, control }
        };
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        page.Children.Add(Fluent.Card(grid));
    }

    void Toggle(string title, string description, Setting<bool> setting, Action? changed = null)
    {
        var toggle = Fluent.Named(new ToggleSwitch { IsOn = s.Settings.Get(setting) }, title);
        toggle.Toggled += (_, _) => { s.Settings.Set(setting, toggle.IsOn); changed?.Invoke(); };
        Row(title, description, toggle);
    }

    void Field(StackPanel fields, string header, string value, string placeholder, Action<string> save)
    {
        var box = Fluent.Named(new TextBox { Header = header, Text = value, PlaceholderText = placeholder }, header);
        box.TextChanged += (_, _) => save(box.Text.Trim());
        fields.Children.Add(box);
    }

    static ComboBox Picker(string title, string[] items, int selected, Action<int> changed)
    {
        var box = Fluent.Named(new ComboBox { Width = 208 }, title);
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        return box;
    }

    void Choice(string title, string? description, string[] items, int selected, Action<int> changed) =>
        Row(title, description, Picker(title, items, selected, changed));

    void RouteRow(RadioButton radio, Func<string> disclosure)
    {
        var note = Fluent.Text(disclosure(), "MurmurCaption");
        note.Margin = new Thickness(28, 0, 0, 0);
        refreshNotes.Add(() => note.Text = disclosure());
        page.Children.Add(Fluent.Card(new StackPanel { Children = { radio, note } }));
    }

    // ---- routes ----

    static readonly (TranscriptionRoute Route, string Label)[] TranscriptionChoices = [(TranscriptionRoute.Local, "Local (this PC)"), (TranscriptionRoute.Cloud, "Custom Provider"), (TranscriptionRoute.MurmurCloud, "Murmur Cloud")];
    static readonly (CleanupChoice Choice, string Label)[] CleanupChoices = [(CleanupChoice.LocalRules, "Local rules"), (CleanupChoice.LocalModel, "Local model (this PC)"), (CleanupChoice.CustomAPI, "Custom Provider"), (CleanupChoice.MurmurCloud, "Murmur Cloud")];

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
        var speechRow = new ModelRow(s, s.SpeechSlot, controller, () => Content.XamlRoot, go => { s.Local.Release(); go(); }, UpdateModelRoutes);
        page.Children.Add(Fluent.Card(speechRow.Panel));
        Heading("Where transcription runs");
        foreach (var (route, label) in TranscriptionChoices)
        {
            var radio = new RadioButton { Content = label, GroupName = "transcription", IsChecked = Routes.Transcription == route, IsEnabled = route != TranscriptionRoute.Local };
            radio.Checked += (_, _) => SaveRoutes(r => r with { Transcription = route });
            if (route == TranscriptionRoute.Local) modelRoutes[route] = radio;
            RouteRow(Fluent.Named(radio, label), () => TranscriptionNote(route));
        }
        BuildProvider(transcription: true);
    }

    void BuildCleanup()
    {
        Heading("Cleanup runs on");
        foreach (var (choice, label) in CleanupChoices)
        {
            var radio = new RadioButton { Content = label, GroupName = "cleanup", IsChecked = Routes.Cleanup == choice, IsEnabled = choice != CleanupChoice.LocalModel };
            if (choice == CleanupChoice.LocalModel) modelRoutes[choice] = radio;
            radio.Checked += (_, _) => SaveRoutes(r => r with { Cleanup = choice });
            RouteRow(Fluent.Named(radio, label), () => CleanupNote(choice));
        }
        Heading("Local cleanup model");
        var cleanupRow = new ModelRow(s, s.CleanupSlot, controller, () => Content.XamlRoot, go => { LocalCleanupRunner.Shared.Shutdown(); go(); }, UpdateModelRoutes);
        page.Children.Add(Fluent.Card(cleanupRow.Panel));
        Heading("Cleanup style");
        Toggle("Polish", "Use an AI model to smooth the wording. Available on model routes.", Prefs.CleanupPolish);
        Toggle("Professional", "Use an AI model to make the wording more formal. Available on model routes.", Prefs.CleanupProfessional);
        Heading("Cleanup rules");
        Toggle("Remove filler words", "Takes out “um”, “uh” and similar.", Prefs.CleanupFillers);
        Toggle("Apply spoken corrections", "Uses your final wording for clear corrections. Ambiguous corrections stay.", Prefs.CleanupCorrections);
        Toggle("Format spoken lists", "Turns “one, two, three” into a numbered list.", Prefs.CleanupLists);
        Toggle("Fix punctuation and capitalization", "Adds commas, periods and capitals.", Prefs.CleanupPunctuation);
        Toggle("Spoken commands and numbers", "Interprets “comma” and “new paragraph”, and writes spoken numbers as digits.", Prefs.CleanupSpoken);
        Toggle("Use screen context", "With Polish or Professional on a Custom Provider or Murmur Cloud, words from the screen can appear in the pasted text.", Prefs.ScreenContext);
        Note("Website modes use the focused page in Edge, Chrome, Brave, Vivaldi, Opera, Arc, or Firefox when Windows can read it. App matching still works in other browsers and when the page is unavailable.");
        BuildProvider(transcription: false);
    }

    /// <summary>Enables "Local (this PC)" and "Local model" only while their files are installed and pass the pinned checksums. Hashing runs off the UI thread (once per launch, then cached).</summary>
    void UpdateModelRoutes()
    {
        int check = ++modelCheck;
        _ = Task.Run(() =>
        {
            var speechInstalled = s.Local.ModelFilesPresent;
            var cleanupInstalled = LocalCleanupRunner.InstalledStatus() is null;
            var speech = s.Local.HelperPresent && speechInstalled && s.Local.Verify();
            var cleanup = cleanupInstalled && (LocalCleanupRunner.Verifier?.Invoke() ?? false);
            s.UI.TryEnqueue(() =>
            {
                if (check != modelCheck) return;
                speechFilesPresent = speechInstalled;
                cleanupFilesPresent = cleanupInstalled;
                modelRoutes[TranscriptionRoute.Local].IsEnabled = speech;
                modelRoutes[CleanupChoice.LocalModel].IsEnabled = cleanup;
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

        fields.Children.Add(Fluent.Text("Any OpenAI-compatible server. A server on this PC (127.0.0.1) needs no key and sends nothing off the PC. Manage keys under Providers.", "MurmurCaption"));
        page.Children.Add(Fluent.Named(new Expander
        {
            Header = "Custom Provider", Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = transcription ? routes.Transcription == TranscriptionRoute.Cloud : routes.Cleanup == CleanupChoice.CustomAPI
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
        var box = Fluent.Named(new PasswordBox { PlaceholderText = "Enter API key" }, header);
        var result = Fluent.Text("", "MurmurCaption");
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        void Refresh() { try { result.Text = s.Credentials.Exists(provider) ? "A key is saved." : KeyText.None; } catch (CredentialException) { result.Text = KeyText.Unreadable; } }
        Refresh();
        var save = Fluent.Named(new Button { Content = "Save" }, "Save " + header);
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
        var validate = Fluent.Named(new Button { Content = "Validate" }, "Validate " + header);
        validate.Click += async (_, _) =>
        {
            APIKey? key = APIKey.Create(box.Password);
            try { key ??= provider == ProviderID.MurmurCloud ? s.Credentials.Read(provider) : CustomAPIKeyBinding.Read(address(), s.Settings, s.Credentials, provider); }
            catch (Exception e) when (e is CredentialException or ProviderException) { result.Text = KeyText.Unreadable; return; }
            result.Text = KeyText.Checking;
            result.Text = await KeyCheck.RunAsync(provider, key, address());
        };
        var remove = Fluent.Named(new Button { Content = "Remove" }, "Remove " + header);
        remove.Click += (_, _) =>
        {
            try { s.Credentials.Delete(provider); if (provider != ProviderID.MurmurCloud) CustomAPIKeyBinding.Clear(s.Settings, provider); result.Text = KeyText.Removed; }
            catch (CredentialException) { result.Text = KeyText.RemoveFailed; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, validate, remove } };
        page.Children.Add(Fluent.Card(new StackPanel
        {
            Spacing = 8, Children = { Fluent.Text(header, "BodyStrongTextBlockStyle"), box, result, buttons }
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
        var record = Fluent.Named(new Button { Content = "Record key" }, "Record custom dictation key");
        var recorded = Fluent.Text(spec.StartsWith("custom:") ? ShortcutSpec.Label(spec) : "", "MurmurCaption");
        record.Click += async (_, _) =>
        {
            record.IsEnabled = false;
            recorded.Text = "Press a key (Esc cancels)…";
            var vk = await controller.RecordNextKey();
            if (vk != 0)
            {
                s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Custom(vk));
                controller.ReloadShortcuts();
                presets.SelectedIndex = 3;
            }
            var current = s.Settings.Get(Prefs.Shortcut);
            recorded.Text = current.StartsWith("custom:") ? ShortcutSpec.Label(current) : "";
            record.IsEnabled = true;
        };
        presets.SelectionChanged += (_, _) => { if (presets.SelectedIndex is >= 0 and < 3) recorded.Text = ""; };
        Row("Custom key", null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recorded, record } });
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
        var liveWordsRow = new ModelRow(s, s.LiveWordsSlot, controller, () => Content.XamlRoot, go => go(), UpdateModelRoutes);
        page.Children.Add(Fluent.Card(liveWordsRow.Panel));
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
            i => { s.Settings.Set(Prefs.HistoryMode, i == 0 ? HistoryMode.On : HistoryMode.Off); s.ApplyHistoryMode(); });
        var retentions = Enum.GetValues<HistoryRetention>();
        Choice("Keep transcripts for", null, retentions.Select(r => r.Label()).ToArray(), Array.IndexOf(retentions, s.Settings.Get(Prefs.Retention)), i => s.Settings.Set(Prefs.Retention, retentions[i]));

        Heading("Your data");
        var backup = Fluent.Named(new Button { Content = "Back Up…" }, "Back up Murmur data");
        backup.Click += async (_, _) => await Backup();
        var restore = Fluent.Named(new Button { Content = "Restore…" }, "Restore Murmur backup");
        restore.Click += async (_, _) => await Restore();
        var delete = Fluent.Named(new Button { Content = "Delete All Data…" }, "Delete all Murmur data");
        delete.Click += async (_, _) => await DeleteAll();
        Row("Backup and restore", "Save History, the dictionary and settings to a Murmur Backup folder. API keys and audio are excluded. Restore keeps your current provider routes.", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { restore, backup }
        });
        Row("Delete all data", "Deletes History, settings and credentials from this PC. Downloaded models stay.", delete);
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
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    async Task<bool> Confirm(string title, string body, string action)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await Fluent.Named(dialog, title).ShowAsync() == ContentDialogResult.Primary;
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
            s.History.ClearSessionOnly(); s.ReopenData(); controller.ReloadShortcuts();
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
        ShowStatus(report.Succeeded ? "All Murmur data was deleted." : "Some data could not be deleted: " + string.Join(" ", report.Failures), report.Succeeded);
    }
}
