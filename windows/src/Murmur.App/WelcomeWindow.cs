using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Murmur.Core;
using Murmur.Platform;
using WinRT.Interop;
using Step = Murmur.Core.WelcomeGuide.Step;

namespace Murmur.App;

/// <summary>The welcome guide (app/WelcomeGuideView.swift) for Windows: microphone privacy, an optional replacement for a missing Right Ctrl, the two routes, the speech model, and a test dictation.</summary>
internal sealed partial class WelcomeWindow : Window
{
    readonly AppServices s;
    readonly Controller controller;
    readonly WelcomeGuide guide;
    readonly Grid left = new() { Padding = new Thickness(24) };
    readonly StackPanel leftBody = new();
    readonly Grid right = new() { Padding = new Thickness(24) };
    readonly Dictionary<Step, SettingsView> settingsViews = [];
    Step step;
    bool missingRightCtrl, micChecked, micOk;
    ModelRow? modelRow;

    internal WelcomeWindow(AppServices services, Controller controller)
    {
        s = services;
        this.controller = controller;
        guide = new WelcomeGuide(s.Settings);
        // No Right Ctrl on this keyboard (Copilot-key laptops): the user turns the extra step on from the Microphone step, or already chose another key.
        missingRightCtrl = s.Settings.Get(Prefs.Shortcut).StartsWith("custom:", StringComparison.Ordinal);
        step = guide.ResumeStep(missingRightCtrl);
        Title = "Welcome to Murmur";
        var caption = new TitleBar { Title = "Welcome to Murmur" };
        AutomationProperties.SetName(caption, "Welcome title bar");
        var body = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) }, new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) } } };
        var leftHost = new Border { Child = left, Style = MurmurStyle.Style("MurmurSidebarPane") };
        left.Children.Add(leftBody);
        body.Children.Add(leftHost);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Style = MurmurStyle.Style("MurmurWindowRoot"), Children = { caption, body } };
        Grid.SetRow(body, 1);
        Content = root;
        MurmurStyle.SetTitleBar(this, caption);
        MurmurStyle.SetInitialSize(this, 1000, 680);
        controller.SetupShowing = step == Step.TryIt;
        Closed += (_, _) => controller.SetupShowing = false;
        Render();
    }

    /// <summary>The chosen languages use the English Parakeet model (the Mac's chosenSpeechModel == .parakeetEnglish).</summary>
    bool EnglishModel => (DictationLanguages.Create(s.Settings.Get(Prefs.DictationLanguages)) ?? DictationLanguages.English).LocalModel == LocalSpeechModel.ParakeetEnglish;
    bool CloudOnly => RouteSettings.Load(s.Settings).Transcription != TranscriptionRoute.Local && MeetingRoute.Load(s.Settings) == MeetingTranscription.Cloud && !controller.RequiredSpeechSlot.Row.IsReady;

    string Shortcut =>ShortcutSpec.Label(s.Settings.Get(Prefs.Shortcut));
    XamlRoot? Root() => Content.XamlRoot;

    IReadOnlyList<Step> Steps => WelcomeGuide.Steps(missingRightCtrl);

    static string PhaseTitle(WelcomeGuide.Phase phase) => phase switch { WelcomeGuide.Phase.Welcome => "Welcome", WelcomeGuide.Phase.GetReady => "Get ready", _ => "First words" };

    (string Title, string Detail) Info(Step st) => st switch
    {
        Step.Microphone => ("Microphone", "A little access, a clear reason. So Murmur can hear you while the shortcut is held. Audio stays on this PC unless you choose a cloud route."),
        Step.RecordShortcut => ("Your key", "This keyboard may not have a Right Ctrl key; some laptops use that spot for Copilot. Pick another key to hold while you speak."),
        Step.Transcription => ("Transcription", "Choose your languages and where Murmur transcribes your audio."),
        Step.Cleanup => ("Cleanup", EnglishModel
            ? "Choose where cleanup runs and how much it changes your words. Faithful keeps cleanup off."
            : "With these languages, Murmur keeps your words as spoken and applies only the spellings you’ve added to Dictionary."),
        Step.Model => ("Speech model", CloudOnly ? "Dictation and meeting transcription run in the cloud."
            : controller.RequiredSpeechSlot is { Downloader: { } d } slot ? $"{slot.Facts.DisplayName} · {ModelFacts.FormatBytes(d.DownloadSize)} download" : controller.RequiredSpeechSlot.Facts.DisplayName),
        Step.TryIt => ("Find your words.", $"Hold {Shortcut} and say a sentence in any app. Or click the field below, then hold {Shortcut} and speak."),
        _ => ("A little room for thought.", "Speak freely. Your voice becomes text, right where you’re writing."),
    };

    bool Done(Step st) => st switch
    {
        Step.Microphone => MicrophonePrivacy.Read() != MicrophonePrivacy.State.Blocked && micOk,
        Step.RecordShortcut => s.Settings.Get(Prefs.Shortcut).StartsWith("custom:", StringComparison.Ordinal),
        Step.Transcription or Step.Cleanup => guide.IsConfirmed(st),
        Step.Model => s.Local.ModelFilesPresent,
        _ => false,
    };

    static TextBlock T(string text, double size, bool medium = false, string style = "MurmurSettingLabel", double top = 0)
    {
        var t = MurmurStyle.Text(text, style);
        t.FontSize = size; t.Margin = new Thickness(0, top, 0, 0);
        if (medium) t.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        return t;
    }

    static Button Filled(string text, Action click)
    {
        var accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 111, 240));
        var b = MurmurStyle.Named(new Button { Content = text, BorderThickness = new Thickness(0), Style = MurmurStyle.Style("AccentButtonStyle"), MinHeight = 36, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(18) }, text);
        foreach (var key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" }) b.Resources[key] = accent;
        b.Click += (_, _) => click();
        return b;
    }

    static Button Outline(string text, Action click, bool plain = false)
    {
        var b = MurmurStyle.Named(new Button { Content = text, MinHeight = 36, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(18) }, text);
        if (plain) { b.BorderThickness = new Thickness(0); b.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent); }
        b.Click += (_, _) => click();
        return b;
    }

    void Go(Step next) { controller.SetupShowing = next == Step.TryIt; step = next; guide.Save(next); Render(); }

    void Render()
    {
        var steps = Steps;
        int index = steps.ToList().IndexOf(step);
        if (index < 0) { step = steps[0]; index = 0; }
        var phase = WelcomeGuide.PhaseFor(step);
        var ready = steps.Where(x => WelcomeGuide.PhaseFor(x) == WelcomeGuide.Phase.GetReady).ToList();

        leftBody.Children.Clear();
        leftBody.Children.Add(T(phase == WelcomeGuide.Phase.GetReady ? $"Get ready · {ready.IndexOf(step) + 1} of {ready.Count}" : PhaseTitle(phase), 12, true, "MurmurTabOff"));
        leftBody.Children.Add(T("murmur", 40, true, "MurmurSettingLabel", 16));
        var list = new StackPanel { Margin = new Thickness(0, 28, 0, 0) };
        int n = 1;
        foreach (var p in new[] { WelcomeGuide.Phase.Welcome, WelcomeGuide.Phase.GetReady, WelcomeGuide.Phase.FirstWords })
        {
            bool current = p == phase;
            var first = steps.First(x => WelcomeGuide.PhaseFor(x) == p);
            var row = new Grid { ColumnSpacing = 12, Height = 40, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            row.Children.Add(T($"{n++:00}", 13, false, "MurmurTabOff"));
            var name = T(PhaseTitle(p), 14, current); Grid.SetColumn(name, 1); row.Children.Add(name);
            foreach (var c in row.Children.OfType<TextBlock>()) c.VerticalAlignment = VerticalAlignment.Center;
            var bar = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Opacity = current ? 1 : 0, Style = MurmurStyle.Style("MurmurBubbleDot") };
            Grid.SetColumnSpan(bar, 2); row.Children.Add(bar);
            var button = MurmurStyle.Named(new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), CornerRadius = new CornerRadius(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) }, PhaseTitle(p));
            button.Click += (_, _) => Go(first);
            list.Children.Add(button);
            if (p == WelcomeGuide.Phase.GetReady && current)
                foreach (var sub in ready)
                {
                    var subRow = new Grid { Margin = new Thickness(28, 0, 0, 0), MinHeight = 30, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                    var label = T(Info(sub).Title, 13, sub == step); label.VerticalAlignment = VerticalAlignment.Center;
                    subRow.Children.Add(label);
                    if (Done(sub)) { var tick = new FontIcon { Glyph = "îœ¾", FontSize = 12, Style = MurmurStyle.Style("MurmurAccentIcon") }; Grid.SetColumn(tick, 1); AutomationProperties.SetName(tick, "Ready"); subRow.Children.Add(tick); }
                    var sb = MurmurStyle.Named(new Button { Content = subRow, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) }, Info(sub).Title);
                    var target = sub;
                    sb.Click += (_, _) => Go(target);
                    list.Children.Add(sb);
                }
        }
        leftBody.Children.Add(list);

        right.Children.Clear();
        right.RowDefinitions.Clear();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var (title, detail) = Info(step);
        var head = T(title, 40, true);
        AutomationProperties.SetHeadingLevel(head, AutomationHeadingLevel.Level1);
        var content = new StackPanel { Children = { head, T(detail, 13, false, "MurmurDescription", 10), Body() } };
        right.Children.Add(MurmurStyle.Named(new ScrollViewer { Content = content, Padding = new Thickness(0, 0, 12, 0) }, "Welcome step"));

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        if (index > 0) nav.Children.Add(Outline("â† Back", () => Go(steps[index - 1])));
        nav.Children.Add(Outline("Not Now", Close, plain: true));
        var spacer = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Margin = new Thickness(0, 24, 0, 0) };
        Button next;
        if (step == Step.TryIt) next = Filled(guide.NextUnconfirmed() == null ? "Finish" : "Review Choices →", Finish);
        else
        {
            var label = step == Step.Welcome ? "Get started →" : WelcomeGuide.IsConfiguration(step) ? "Use These Settings →" : Done(step) ? "Continue →" : "Skip for Now →";
            var go = () => { guide.Confirm(step); Go(steps[index + 1]); };
            next = step == Step.Welcome || Done(step) ? Filled(label, go) : Outline(label, go);
        }
        spacer.Children.Add(nav); Grid.SetColumn(next, 1); spacer.Children.Add(next);
        Grid.SetRow(spacer, 1);
        right.Children.Add(spacer);
    }

    void Finish()
    {
        if (guide.NextUnconfirmed() is { } pending) { Go(pending); return; }
        guide.Complete();
        Close();
    }

    UIElement Body()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        switch (step)
        {
            case Step.Welcome:
                panel.Children.Add(MurmurStyle.Text("Audio stays on this PC unless you choose a cloud route. You see what a cloud route sends before you confirm it.", "MurmurDescription"));
                break;
            case Step.Microphone:
                var privacy = MicrophonePrivacy.Read();
                var status = MurmurStyle.Text(privacy == MicrophonePrivacy.State.Blocked ? "Windows is blocking microphone access for desktop apps. Turn it on in Windows settings, then press Check."
                    : micChecked ? (micOk ? "Microphone found and allowed." : "No microphone was found. Check that one is connected.")
                    : privacy == MicrophonePrivacy.State.Allowed ? "Windows allows microphone access." : "", "MurmurCaption");
                AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Filled("Open Microphone Settings", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"))), Outline("Check Microphone", CheckMic) } });
                panel.Children.Add(status);
                panel.Children.Add(MurmurStyle.Text("Turn on “Let desktop apps access your microphone” in Windows settings, then come back here.", "MurmurCaption"));
                var noKey = new HyperlinkButton { Content = missingRightCtrl ? "I do have a Right Ctrl key" : "My keyboard has no Right Ctrl key", Padding = new Thickness(0) };
                noKey.Click += (_, _) => { missingRightCtrl = !missingRightCtrl; Render(); };
                panel.Children.Add(noKey);
                break;
            case Step.RecordShortcut:
                var recorded = MurmurStyle.Text(ShortcutSpec.Label(s.Settings.Get(Prefs.Shortcut)), "MurmurSettingLabel");
                recorded.FontSize = 28; recorded.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                var record = Filled("Record key", () => { });
                record.Click += async (_, _) =>
                {
                    record.IsEnabled = false;
                    recorded.Text = "Press a key (Esc cancels)…";
                    var vk = await controller.RecordNextKey();
                    if (vk != 0) { s.Settings.Set(Prefs.Shortcut, ShortcutSpec.Custom(vk)); controller.ReloadShortcuts(); }
                    recorded.Text = ShortcutSpec.Label(s.Settings.Get(Prefs.Shortcut));
                    record.IsEnabled = true;
                };
                panel.Children.Add(recorded); panel.Children.Add(record);
                break;
            case Step.Transcription:
            case Step.Cleanup:
                if (!settingsViews.TryGetValue(step, out var view)) settingsViews[step] = view = new SettingsView(s, controller, Root, () => WindowNative.GetWindowHandle(this));
                // The language picker is part of the Transcription pane (SettingsView.BuildTranscription), as on the Mac; Cleanup shows nothing to choose for other languages.
                if (step == Step.Cleanup && !EnglishModel) break;
                var pane = view.Body(step == Step.Cleanup ? "Cleanup" : "Transcription");
                if (pane.Parent is Panel old) old.Children.Remove(pane);
                panel.Children.Add(pane);
                break;
            case Step.Model:
                // Rebuilt each time: the required model follows the chosen languages.
                modelRow?.Detach();
                modelRow = new ModelRow(s, controller.RequiredSpeechSlot, controller, Root, go => { s.Local.Release(); go(); }, () => Render(), required: true);
                if (modelRow.Panel.Parent is Panel op) op.Children.Remove(modelRow.Panel);
                panel.Children.Add(MurmurStyle.Card(modelRow.Panel));
                break;
            case Step.TryIt:
                var test = MurmurStyle.Named(new TextBox { PlaceholderText = "Waiting for your voice…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, FontSize = 16 }, "Test dictation");
                panel.Children.Add(MurmurStyle.Text($"Your shortcut: {Shortcut}. Change it in Settings → Dictation.", "MurmurCaption"));
                panel.Children.Add(test);
                var tip = s.Settings.Get(Prefs.LearningEnabled)
                    ? "Correct a misheard word within a minute of dictating to teach Murmur its spelling. You can also add names and terms in Dictionary."
                    : "Add names and terms in Dictionary. You can also turn on learning from your spelling corrections in Learning.";
                panel.Children.Add(MurmurStyle.Text(EnglishModel ? tip : "Add names and their misheard forms in Dictionary.", "MurmurCaption"));
                break;
        }
        return panel;
    }

    void CheckMic()
    {
        try { micOk = AudioCapture.ListDevices().Any(); } catch (Exception) { micOk = false; }
        micChecked = true;
        Render();
    }
}
