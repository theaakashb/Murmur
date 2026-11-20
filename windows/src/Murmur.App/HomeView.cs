using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>Home as on the Mac (home in app/HomeView.swift): route line, milestone, shortcut with Start Dictation, meeting notes, readiness rows and recent dictations.</summary>
internal sealed class HomeView(AppServices s, Controller controller, HomeView.Actions go)
{
    internal sealed record Actions(Action StartDictation, Action OpenMeetings, Action OpenHistory, Action<string, string> OpenSettings, Action ReplayWelcome, Action<string> OpenEntry);

    static TextBlock Eyebrow(string text) => MurmurStyle.Text(text.ToUpperInvariant(), "MurmurEyebrow");

    static Grid Header(string eyebrow, UIElement? action, double top)
    {
        var grid = new Grid { Margin = new Thickness(0, top, 0, 8), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var e = Eyebrow(eyebrow); e.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(e);
        if (action is not null) { Grid.SetColumn((FrameworkElement)action, 1); grid.Children.Add(action); }
        return grid;
    }

    bool SpeechSatisfied => RouteSettings.Load(s.Settings).Transcription != TranscriptionRoute.Local || (s.Local.HelperPresent && s.Local.ModelFilesPresent);

    public UIElement Build()
    {
        var panel = MurmurStyle.Page("Home", "Dictate anywhere the cursor is.");
        bool micAllowed = MicrophonePrivacy.Read() != MicrophonePrivacy.State.Blocked, setup = micAllowed && SpeechSatisfied;
        var routes = RouteSettings.Load(s.Settings);

        panel.Children.Add(RouteLine(routes));
        if (Milestone() is { } card) panel.Children.Add(card);

        // Shortcut
        bool toggle = s.Settings.Get(Prefs.PressToToggle);
        var shortcutHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 28, 0, 0), Children = { Eyebrow("Shortcut"), MurmurStyle.Info(toggle ? "Press to start, press again to stop. Escape cancels." : "Hold to record, or double-press to keep recording until you press again. Escape cancels.", "How to use the shortcut") } };
        var name = MurmurStyle.Text(ShortcutSpec.Label(s.Settings.Get(Prefs.Shortcut)), "MurmurSettingLabel");
        name.FontSize = 28; name.FontWeight = Microsoft.UI.Text.FontWeights.Bold; name.CharacterSpacing = -30; name.VerticalAlignment = VerticalAlignment.Center;
        var start = MurmurStyle.Pill("Start Dictation", go.StartDictation, MurmurStyle.PillKind.Filled);
        start.IsEnabled = setup && !controller.IsBusy;
        ToolTipService.SetToolTip(start, "Murmur steps aside so your words go to the app you were using. Press the shortcut to finish.");
        panel.Children.Add(shortcutHead);
        panel.Children.Add(Row(name, start, 26, 12));

        // Meeting notes
        bool live = controller.Meetings.Live is not null;
        var mnTitle = MurmurStyle.Text("Meeting notes", "MurmurSettingLabel");
        mnTitle.FontSize = 17; mnTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; mnTitle.VerticalAlignment = VerticalAlignment.Center;
        var mnInfo = MurmurStyle.Info(live ? "Recording now. Open Meetings for the transcript." : "Transcribe a call on this PC: your microphone as Me, the call’s audio as Them.", "About meeting notes");
        var mnButton = live ? MurmurStyle.Pill("Open Meetings", go.OpenMeetings) : MurmurStyle.Pill("Start Meeting Notes", () => controller.StartMeeting(), MurmurStyle.PillKind.Filled);
        panel.Children.Add(Row(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mnTitle, mnInfo } }, mnButton, 16, 18));

        // Readiness
        panel.Children.Add(Header("Ready to dictate", setup ? null : MurmurStyle.Pill("Open Welcome Guide", go.ReplayWelcome, MurmurStyle.PillKind.Plain), 28));
        var ready = new StackPanel { Spacing = 0 };
        ready.Children.Add(MurmurStyle.HRule());
        ready.Children.Add(Permission("Microphone", micAllowed, "Open Microphone Settings", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"))));
        ready.Children.Add(Permission("Speech model", SpeechSatisfied, "Open Transcription", () => go.OpenSettings("Dictation", "Transcription")));
        panel.Children.Add(ready);

        // Recent
        var history = SafeEntries();
        panel.Children.Add(Header("Recent dictations", history.Count > 0 ? MurmurStyle.Pill("Show All in History →", go.OpenHistory, MurmurStyle.PillKind.Plain) : null, 28));
        var recent = new StackPanel();
        recent.Children.Add(MurmurStyle.HRule());
        if (history.Count == 0)
            recent.Children.Add(new Border { Padding = new Thickness(0, 16, 0, 16), Child = MurmurStyle.Text(s.Settings.Get(Prefs.HistoryMode) == HistoryMode.On ? "Nothing yet. Your dictations appear here and in History." : "History is off, so dictations are kept only until Murmur quits.") });
        foreach (var e in history.Take(5)) recent.Children.Add(RecentRow(e));
        panel.Children.Add(recent);

        var replay = MurmurStyle.Pill("Replay Welcome Guide", go.ReplayWelcome);
        replay.Margin = new Thickness(0, 28, 0, 0); replay.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(replay);
        return MurmurStyle.Named(new ScrollViewer { Content = panel }, "Home");
    }

    List<HistoryEntry> SafeEntries()
    {
        try { return s.History.Entries(); } catch (Exception) { return []; }
    }

    static Grid Row(UIElement left, UIElement right, double top, double vertical)
    {
        var g = new Grid { Margin = new Thickness(0, top, 0, 0), Padding = new Thickness(0, vertical, 0, vertical), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        ((FrameworkElement)left).VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left); Grid.SetColumn((FrameworkElement)right, 1); g.Children.Add(right);
        ((FrameworkElement)right).VerticalAlignment = VerticalAlignment.Center;
        return g;
    }

    UIElement RouteLine(RouteSettings routes)
    {
        string transcription = routes.Transcription switch { TranscriptionRoute.Local => "On this PC", TranscriptionRoute.MurmurCloud => "Murmur Cloud", _ => "Custom Provider" };
        string cleanup = routes.Cleanup switch { CleanupChoice.LocalRules => "Local rules", CleanupChoice.LocalModel => "Local model", CleanupChoice.CustomAPI => "Custom Provider", _ => "Murmur Cloud" };
        var plan = RoutePlan.Make(routes, true, s.Key);
        var details = new List<string>();
        foreach (var stage in new[] { ProcessingStage.Transcription, ProcessingStage.Cleanup })
            if (DisclosureText.Summary(plan, stage) is { Length: > 0 } text) details.Add(text);
        details.Add("On-device routes process data on this PC. Custom providers use the server configured in Settings.");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 12, 0, 0),
            Children = { MurmurStyle.Text($"Transcription: {transcription} · Cleanup: {cleanup}", "MurmurDescription"), MurmurStyle.Info(string.Join("\n\n", details), "Where dictation data goes") }
        };
    }

    UIElement? Milestone()
    {
        var tips = new Tips(s.Settings);
        if (tips.HomeMilestone is not { } reached) return null;
        var title = MurmurStyle.Text("MILESTONE", "MurmurAccentText"); title.FontSize = 12; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var words = MurmurStyle.Text($"{reached:N0} words", "MurmurAccentText");
        words.FontSize = 30; words.FontWeight = Microsoft.UI.Text.FontWeights.Bold; words.CharacterSpacing = -30;
        var saved = MurmurStyle.Text($"About {Tips.Duration(tips.LifetimeSecondsSaved)} of typing saved so far.", "MurmurDescription");
        saved.FontSize = 14;
        var rungs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        int limit = Tips.Next(reached) ?? reached;
        foreach (int rung in Tips.Rungs.Where(r => r <= limit)) rungs.Children.Add(Rung(rung, reached));
        var dismiss = MurmurStyle.Pill("Dismiss", () => { tips.DismissHome(); Refresh?.Invoke(); }, MurmurStyle.PillKind.Plain);
        var bottom = new Grid { Margin = new Thickness(0, 4, 0, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { rungs, dismiss } };
        Grid.SetColumn(dismiss, 1);
        return new Border { Margin = new Thickness(0, 20, 0, 0), Style = MurmurStyle.Style("MurmurAccentCard"), Child = new StackPanel { Spacing = 6, Children = { title, words, saved, bottom } } };
    }

    /// <summary>Set by MainWindow so a dismissed card disappears without leaving the page.</summary>
    public Action? Refresh { get; set; }

    static UIElement Rung(int rung, int reached)
    {
        bool done = rung <= reached;
        string label = rung >= 1000 ? $"{rung / 1000}k" : rung.ToString();
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Style = MurmurStyle.Style(done ? "MurmurPillText" : "MurmurAccentText") };
        text.FontSize = 12; text.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        var chip = new Border { Child = text, Padding = new Thickness(9, 3, 9, 3), CornerRadius = new CornerRadius(12), Style = MurmurStyle.Style(done ? "MurmurBar" : "MurmurNavOff") };
        if (!done) { chip.BorderThickness = new Thickness(1); chip.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MurmurAccent"]; }
        return MurmurStyle.Named(chip, done ? $"{rung:N0} words reached" : $"Next: {rung:N0} words");
    }

    static UIElement Permission(string title, bool allowed, string actionTitle, Action action)
    {
        var name = MurmurStyle.Text(title, "MurmurSettingLabel"); name.FontSize = 15; name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; name.VerticalAlignment = VerticalAlignment.Center;
        var state = MurmurStyle.Text(allowed ? "ALLOWED ✓" : "NEEDED", allowed ? "MurmurEyebrow" : "MurmurSectionInk"); state.VerticalAlignment = VerticalAlignment.Center;
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { state } };
        if (!allowed) right.Children.Add(MurmurStyle.Pill(actionTitle, action, MurmurStyle.PillKind.Outline, height: 32));
        var row = new Grid { MinHeight = 44, Padding = new Thickness(0, 18, 0, 18), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { name, right } };
        Grid.SetColumn(right, 1);
        right.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Children = { row, MurmurStyle.HRule(true) } };
    }

    UIElement RecentRow(HistoryEntry e)
    {
        var when = MurmurStyle.Text(Relative(e.RecordedAt), "MurmurSection"); when.FontSize = 13; when.Width = 120; when.TextTrimming = TextTrimming.CharacterEllipsis; when.TextWrapping = TextWrapping.NoWrap;
        string text = (e.FinalText.Length > 0 ? e.FinalText : e.RawText).Replace('\n', ' ').Replace('\r', ' ');
        var body = new TextBlock { Text = text, Style = MurmurStyle.Style("MurmurTranscript"), FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, TextWrapping = TextWrapping.NoWrap };
        body.FontSize = 15;
        var g = new Grid { Padding = new Thickness(0, 12, 0, 12), ColumnSpacing = 16, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() }, Children = { when, body } };
        Grid.SetColumn(body, 1);
        var button = new Button { Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        MurmurStyle.Named(button, "Open in History: " + Relative(e.RecordedAt));
        button.Click += (_, _) => go.OpenEntry(e.Id);
        return new StackPanel { Children = { button, MurmurStyle.HRule(true) } };
    }

    /// <summary>"2 minutes ago", "yesterday": the Mac's relative named date style.</summary>
    internal static string Relative(DateTime recorded, DateTime? now = null)
    {
        var span = (now ?? DateTime.Now) - recorded.ToLocalTime();
        if (span.TotalSeconds < 45) return "now";
        if (span.TotalMinutes < 60) { int m = Math.Max(1, (int)Math.Round(span.TotalMinutes)); return m == 1 ? "1 minute ago" : $"{m} minutes ago"; }
        if (span.TotalHours < 24) { int h = (int)Math.Round(span.TotalHours); return h == 1 ? "1 hour ago" : $"{h} hours ago"; }
        int d = (int)Math.Floor(span.TotalDays);
        return d == 1 ? "yesterday" : d < 7 ? $"{d} days ago" : recorded.ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }
}
