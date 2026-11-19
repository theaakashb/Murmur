using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Murmur.Platform;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The one Settings window: plain Fluent controls in a single scrolling page. Every control writes through to the settings store at once.</summary>
internal sealed class SettingsWindow : Window
{
    readonly AppServices s;
    readonly Controller controller;
    readonly StackPanel page = new() { Spacing = 10, Padding = new Thickness(28), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
    readonly List<Action> refreshNotes = [];
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(AppServices services, Controller controller)
    {
        s = services; this.controller = controller;
        Title = "Murmur Settings";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 800));
        Content = new ScrollViewer { Content = page };
        BuildTranscription(); BuildCleanup(); BuildModels(); BuildProvider(); BuildKeys(); BuildRecording(); BuildInsertion(); BuildData();
        page.Children.Add(status);
        UpdateModelRoutes();
    }

    readonly Dictionary<object, RadioButton> modelRoutes = [];
    ModelRow? speechRow, cleanupRow;

    RouteSettings Routes => RouteSettings.Load(s.Settings);
    void SaveRoutes(Func<RouteSettings, RouteSettings> change) { change(Routes).Save(s.Settings); foreach (var refresh in refreshNotes) refresh(); }

    // ---- building blocks ----

    TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 14, 0, 0) };
        block.Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"];
        page.Children.Add(block);
        return block;
    }

    TextBlock Note(string text = "", Func<string>? live = null, Thickness? margin = null)
    {
        var note = new TextBlock { Text = live?.Invoke() ?? text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = margin ?? new Thickness(28, 0, 0, 6) };
        if (live is not null) refreshNotes.Add(() => note.Text = live());
        page.Children.Add(note);
        return note;
    }

    void Toggle(string header, Setting<bool> setting, Action? changed = null)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = s.Settings.Get(setting) };
        toggle.Toggled += (_, _) => { s.Settings.Set(setting, toggle.IsOn); changed?.Invoke(); };
        page.Children.Add(toggle);
    }

    TextBox Field(string header, string value, string placeholder, Action<string> save)
    {
        var box = new TextBox { Header = header, Text = value, PlaceholderText = placeholder };
        box.TextChanged += (_, _) => save(box.Text.Trim());
        page.Children.Add(box);
        return box;
    }

    ComboBox Choice(string header, IEnumerable<string> items, int selected, Action<int> changed)
    {
        var box = new ComboBox { Header = header, MinWidth = 260 };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(box.SelectedIndex); };
        page.Children.Add(box);
        return box;
    }

    // ---- routes ----

    static readonly (TranscriptionRoute Route, string Label)[] TranscriptionChoices = [(TranscriptionRoute.Local, "Local (this PC)"), (TranscriptionRoute.Cloud, "Custom Provider"), (TranscriptionRoute.MurmurCloud, "Murmur Cloud")];
    static readonly (CleanupChoice Choice, string Label)[] CleanupChoices = [(CleanupChoice.LocalRules, "Local rules"), (CleanupChoice.LocalModel, "Local model (this PC)"), (CleanupChoice.CustomAPI, "Custom Provider"), (CleanupChoice.MurmurCloud, "Murmur Cloud")];

    string TranscriptionNote(TranscriptionRoute route)
    {
        if (route == TranscriptionRoute.Local) return s.Local.ModelFilesPresent ? "Audio stays on this PC. Parakeet Unified runs on this PC without the network." : "Audio stays on this PC. Download the speech model under Models below to use this.";
        var plan = RoutePlan.Make(Routes with { Transcription = route }, true, s.Key);
        return DisclosureText.Summary(plan, ProcessingStage.Transcription)
            ?? (route == TranscriptionRoute.Cloud ? "Sends each recording to the address you enter below. Nothing is sent until you enter an address and a model." : "Sends each recording to Murmur Cloud.");
    }

    string CleanupNote(CleanupChoice choice)
    {
        if (choice == CleanupChoice.LocalRules) return "Text stays on this PC.";
        if (choice == CleanupChoice.LocalModel) return LocalCleanupRunner.InstalledStatus() is null ? "Text stays on this PC. One fixed prompt: screen context and rewrite presets are not used." : "Text stays on this PC. Needs the cleanup model under Models below.";
        var plan = RoutePlan.Make(Routes with { Cleanup = choice }, true, s.Key);
        return DisclosureText.Summary(plan, ProcessingStage.Cleanup)
            ?? (choice == CleanupChoice.CustomAPI ? "Sends the transcript text to the address you enter below. Nothing is sent until you enter an address and a model." : "Sends the transcript text to Murmur Cloud.");
    }

    void BuildTranscription()
    {
        Heading("Transcription");
        foreach (var (route, label) in TranscriptionChoices)
        {
            var radio = new RadioButton { Content = label, GroupName = "transcription", IsChecked = Routes.Transcription == route };
            radio.Checked += (_, _) => SaveRoutes(r => r with { Transcription = route });
            if (route == TranscriptionRoute.Local) modelRoutes[route] = radio;
            page.Children.Add(radio);
            Note(live: () => TranscriptionNote(route));
        }
    }

    void BuildCleanup()
    {
        Heading("Cleanup");
        foreach (var (choice, label) in CleanupChoices)
        {
            var radio = new RadioButton { Content = label, GroupName = "cleanup", IsChecked = Routes.Cleanup == choice };
            if (choice == CleanupChoice.LocalModel) modelRoutes[choice] = radio;
            radio.Checked += (_, _) => SaveRoutes(r => r with { Cleanup = choice });
            page.Children.Add(radio);
            Note(live: () => CleanupNote(choice));
        }
        Toggle("Remove filler words", Prefs.CleanupFillers);
        Toggle("Apply spoken corrections", Prefs.CleanupCorrections);
        Toggle("Format spoken lists", Prefs.CleanupLists);
        Toggle("Punctuation and capitalization", Prefs.CleanupPunctuation);
        Toggle("Spoken commands and numbers", Prefs.CleanupSpoken);
        Toggle("Polish (model routes only)", Prefs.CleanupPolish);
        Toggle("Professional (model routes only)", Prefs.CleanupProfessional);
        Toggle("Use screen context (model routes only)", Prefs.ScreenContext);
        Note("With Polish or Professional on a Custom Provider or Murmur Cloud, words from the screen can appear in the pasted text.", margin: new Thickness(0, 0, 0, 6));
    }

    // ---- models ----

    void BuildModels()
    {
        Heading("Models");
        Note("Downloaded once, checked against pinned checksums, and kept on this PC. Delete all data keeps them.", margin: new Thickness(0, 0, 0, 6));
        speechRow = new ModelRow(s, s.SpeechSlot, controller, () => Content.XamlRoot, go => { s.Local.Release(); go(); }, UpdateModelRoutes);
        cleanupRow = new ModelRow(s, s.CleanupSlot, controller, () => Content.XamlRoot, go => { LocalCleanupRunner.Shared.Shutdown(); go(); }, UpdateModelRoutes);
        page.Children.Add(new TextBlock { Text = "Speech recognition", Opacity = 0.7, FontSize = 12 });
        page.Children.Add(speechRow.Panel);
        page.Children.Add(new TextBlock { Text = "Live words", Opacity = 0.7, FontSize = 12 });
        var liveWordsRow = new ModelRow(s, s.LiveWordsSlot, controller, () => Content.XamlRoot, go => go(), UpdateModelRoutes);
        page.Children.Add(liveWordsRow.Panel);
        Toggle("Show live words while speaking", Prefs.LiveWords);
        Toggle("Show live words on cloud routes", Prefs.LiveWordsOnCloud);
        page.Children.Add(new TextBlock { Text = "Cleanup", Opacity = 0.7, FontSize = 12 });
        page.Children.Add(cleanupRow.Panel);
    }

    /// <summary>Enables "Local (this PC)" and "Local model" only while their files are installed and pass the pinned checksums. Hashing runs off the UI thread (once per launch, then cached).</summary>
    void UpdateModelRoutes()
    {
        _ = Task.Run(() =>
        {
            var speech = s.Local.HelperPresent && s.Local.ModelFilesPresent && s.Local.Verify();
            var cleanup = LocalCleanupRunner.InstalledStatus() is null && (LocalCleanupRunner.Verifier?.Invoke() ?? false);
            s.UI.TryEnqueue(() =>
            {
                modelRoutes[TranscriptionRoute.Local].IsEnabled = speech;
                modelRoutes[CleanupChoice.LocalModel].IsEnabled = cleanup;
                foreach (var refresh in refreshNotes) refresh();
            });
        });
    }

    void BuildProvider()
    {
        Heading("Custom Provider");
        Note("Any OpenAI-compatible server. A server on this PC (127.0.0.1) needs no key and sends nothing off the PC.", margin: new Thickness(0, 0, 0, 6));
        var r = Routes;
        Field("Transcription address", r.CustomTranscriptionAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomTranscriptionAPIBaseURL = v }));
        Field("Transcription model", r.CustomTranscriptionModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomTranscriptionModel = v }));
        Field("Cleanup address", r.CustomAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomAPIBaseURL = v }));
        Field("Cleanup model", r.CustomAPIModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomAPIModel = v }));
        string[] efforts = ["", "minimal", "low", "medium", "high"];
        Choice("Reasoning effort", efforts.Select(e => e.Length == 0 ? "None" : e), Math.Max(0, Array.IndexOf(efforts, r.CustomAPIReasoningEffort)), i => SaveRoutes(x => x with { CustomAPIReasoningEffort = efforts[i] }));
    }

    void BuildKeys()
    {
        Heading("API keys");
        Note("Keys are stored in Windows Credential Manager on this PC, never in settings or History.", margin: new Thickness(0, 0, 0, 6));
        KeyRow("Custom transcription key", ProviderID.CustomTranscription, () => Routes.CustomTranscriptionAPIBaseURL);
        KeyRow("Custom cleanup key", ProviderID.CustomCleanup, () => Routes.CustomAPIBaseURL);
        KeyRow("Murmur Cloud key", ProviderID.MurmurCloud, () => "");
    }

    void KeyRow(string header, ProviderID provider, Func<string> address)
    {
        var box = new PasswordBox { Header = header, MinWidth = 320 };
        var result = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Refresh() { try { result.Text = s.Credentials.Exists(provider) ? "A key is saved." : KeyText.None; } catch (CredentialException) { result.Text = KeyText.Unreadable; } }
        Refresh();
        var save = new Button { Content = "Save" };
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
        var validate = new Button { Content = "Validate" };
        validate.Click += async (_, _) =>
        {
            APIKey? key = APIKey.Create(box.Password);
            try { key ??= provider == ProviderID.MurmurCloud ? s.Credentials.Read(provider) : CustomAPIKeyBinding.Read(address(), s.Settings, s.Credentials, provider); }
            catch (Exception e) when (e is CredentialException or ProviderException) { result.Text = KeyText.Unreadable; return; }
            result.Text = KeyText.Checking;
            result.Text = await KeyCheck.RunAsync(provider, key, address());
        };
        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            try { s.Credentials.Delete(provider); if (provider != ProviderID.MurmurCloud) CustomAPIKeyBinding.Clear(s.Settings, provider); result.Text = KeyText.Removed; }
            catch (CredentialException) { result.Text = KeyText.RemoveFailed; }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        row.Children.Add(box);
        foreach (var button in new[] { save, validate, remove }) { button.VerticalAlignment = VerticalAlignment.Bottom; row.Children.Add(button); }
        page.Children.Add(row);
        page.Children.Add(result);
    }

    // ---- recording and insertion ----

    void BuildRecording()
    {
        Heading("Recording");
        List<AudioDevice> devices = [];
        try { devices = [.. AudioCapture.ListDevices()]; } catch (Exception) { /* no audio service: only the default is offered */ }
        var current = s.Settings.Get(Prefs.InputDeviceUID);
        var picker = Choice("Microphone", (string[])["System default", .. devices.Select(d => d.Name)], Math.Max(0, devices.FindIndex(d => d.Id == current) + 1), i => s.Settings.Set(Prefs.InputDeviceUID, i == 0 ? "" : devices[i - 1].Id));
        picker.IsEnabled = devices.Count > 0;

        var spec = s.Settings.Get(Prefs.Shortcut);
        var presets = new ComboBox { Header = "Dictation key", MinWidth = 260 };
        string[] labels = ["Right Ctrl", "Right Alt", "Right Shift", "Custom key"];
        foreach (var label in labels) presets.Items.Add(label);
        presets.SelectedIndex = Math.Max(0, Array.IndexOf(ShortcutSpec.Presets, spec) is var p and >= 0 ? p : spec.StartsWith("custom:") ? 3 : 0);
        var record = new Button { Content = "Record key", VerticalAlignment = VerticalAlignment.Bottom };
        var recorded = new TextBlock { Text = spec.StartsWith("custom:") ? ShortcutSpec.Label(spec) : "", VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.7 };
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex is >= 0 and < 3) { s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Presets[presets.SelectedIndex]); controller.ReloadShortcuts(); }
        };
        record.Click += async (_, _) =>
        {
            record.IsEnabled = false; recorded.Text = "Press a key (Esc cancels)...";
            var vk = await controller.RecordNextKey();
            if (vk != 0) { s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Custom(vk)); controller.ReloadShortcuts(); presets.SelectedIndex = 3; recorded.Text = Native.KeyName(vk); }
            else recorded.Text = "";
            record.IsEnabled = true;
        };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyRow.Children.Add(presets); keyRow.Children.Add(record); keyRow.Children.Add(recorded);
        page.Children.Add(keyRow);
        Toggle("Press to toggle (a short tap starts, the next press stops)", Prefs.PressToToggle);
        Toggle("Recording sounds", Prefs.RecordingSounds);
    }

    void BuildInsertion()
    {
        Heading("Insertion");
        Toggle("Paste automatically", Prefs.AutomaticInsertion);
        Note("When off, the dictation is saved and shown with a Copy button; Ctrl+Win+Shift+V pastes the last one.", margin: new Thickness(0, 0, 0, 6));
        Toggle("Add a leading space when needed", Prefs.IntelligentLeadingSpace);
    }

    // ---- data ----

    void BuildData()
    {
        Heading("History and data");
        Choice("Keep history", (string[])["On", "Off"], s.Settings.Get(Prefs.HistoryMode) == HistoryMode.On ? 0 : 1, i => { s.Settings.Set(Prefs.HistoryMode, i == 0 ? HistoryMode.On : HistoryMode.Off); s.ApplyHistoryMode(); });
        Note(HistoryModes.OffDisclosure, margin: new Thickness(0, 0, 0, 6));
        var retentions = Enum.GetValues<HistoryRetention>();
        Choice("Delete history after", retentions.Select(r => r.Label()), Array.IndexOf(retentions, s.Settings.Get(Prefs.Retention)), i => s.Settings.Set(Prefs.Retention, retentions[i]));

        var backup = new Button { Content = "Back up..." };
        backup.Click += async (_, _) => await Backup();
        var restore = new Button { Content = "Restore..." };
        restore.Click += async (_, _) => await Restore();
        var delete = new Button { Content = "Delete all data..." };
        delete.Click += async (_, _) => await DeleteAll();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(backup); row.Children.Add(restore); row.Children.Add(delete);
        page.Children.Add(row);
        Note("Back up writes History, the dictionary and settings (never keys or audio) to a Murmur Backup folder inside the folder you pick. Restore never changes where dictations are sent.", margin: new Thickness(0, 0, 0, 6));
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    async Task Backup()
    {
        if (await PickFolder() is not { } parent) return;
        try { DataBackup.Export(s.Locations, s.Settings.Snapshot(), Path.Combine(parent, "Murmur Backup")); status.Text = "Backup saved in the Murmur Backup folder."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RetentionError or StoreError) { status.Text = "Backup failed: " + e.Message; }
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
            status.Text = "Backup restored. Reopen Settings to see the restored values.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RetentionError or StoreError) { status.Text = "Restore failed: " + e.Message; }
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
        status.Text = report.Succeeded ? "All Murmur data was deleted." : "Some data could not be deleted: " + string.Join(" ", report.Failures);
    }
}
