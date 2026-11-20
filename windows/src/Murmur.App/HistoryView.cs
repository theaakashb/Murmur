using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>History as on the Mac: a search field, one-line collapsed rows that expand to the full text and actions, and a footer with Clear History.</summary>
internal sealed class HistoryView
{
    readonly AppServices s;
    readonly Func<XamlRoot?> root;
    readonly TextBlock summary = MurmurStyle.Text("", "MurmurDescription");
    readonly TextBox search = MurmurStyle.Named(new TextBox { PlaceholderText = "Search transcripts", FontSize = 16, MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center }, "Search transcripts");
    readonly InfoBar status = MurmurStyle.Named(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true }, "History status");
    readonly StackPanel entries = new() { Padding = new Thickness(0, 0, 0, 24) };
    readonly TextBlock footerText = MurmurStyle.Text("", "MurmurDescription");
    readonly Button clear = MurmurStyle.Named(new Button { Content = "Clear History…", Padding = new Thickness(20, 10, 20, 10), CornerRadius = new CornerRadius(22) }, "Clear History");

    public Grid View { get; }

    internal HistoryView(AppServices services, Func<XamlRoot?> root)
    {
        s = services;
        this.root = root;
        var title = new TextBlock { Text = "History", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Fonts/InstrumentSerif-Regular.ttf#Instrument Serif"), FontSize = 64, CharacterSpacing = -20, Style = MurmurStyle.Style("MurmurHeading") };
        title.FontSize = 64; title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        search.TextChanged += (_, _) => Reload();
        clear.Click += async (_, _) => await ClearAll();
        var top = new StackPanel { Spacing = 12, Padding = new Thickness(40, 24, 40, 16), Children = { title, summary, search, status } };
        var list = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Style = MurmurStyle.Style("MurmurRuleTop"), Child = MurmurStyle.Named(new ScrollViewer { Content = new Border { Padding = new Thickness(40, 0, 40, 0), Child = entries } }, "Dictation history") };
        var footer = new Border
        {
            Style = MurmurStyle.Style("MurmurRuleTop"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(40, 14, 40, 14),
            Child = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { footerText, clear } }
        };
        Grid.SetColumn(clear, 1);
        footerText.VerticalAlignment = VerticalAlignment.Center;
        View = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } }, Children = { top, list, footer } };
        Grid.SetRow(list, 1); Grid.SetRow(footer, 2);
    }

    string FooterText()
    {
        var mode = s.Settings.Get(Prefs.HistoryMode);
        if (mode != HistoryMode.On) return HistoryModes.OffDisclosure;
        return "Saved on this PC only. " + s.Settings.Get(Prefs.Retention) switch
        {
            HistoryRetention.ThirtyDays => "Kept for 30 days.",
            HistoryRetention.SixMonths => "Kept for six months.",
            _ => "Kept until you delete it."
        };
    }

    string? openId;

    /// <summary>Opens the screen with this entry expanded and scrolled into view (a Home recent-dictation row). The search is cleared so the entry is listed.</summary>
    public void Select(string id) { openId = id; search.Text = ""; }

    public void Reload()
    {
        entries.Children.Clear();
        status.IsOpen = false;
        footerText.Text = FooterText();
        List<HistoryEntry> all;
        try { all = s.History.Entries(); }
        catch (Exception) { summary.Text = ""; ShowError("History could not be read."); return; }
        clear.IsEnabled = all.Count > 0;

        var mode = s.Settings.Get(Prefs.HistoryMode);
        summary.Text = HistoryText.Summary(mode, all.Count, s.History.SessionOnlyIDs.Count);
        var query = search.Text.Trim();
        var shown = query.Length == 0 ? all : all.Where(e => Text(e).Contains(query, StringComparison.CurrentCultureIgnoreCase) || (e.App ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (shown.Count == 0)
        {
            var empty = new StackPanel
            {
                Spacing = 8, Padding = new Thickness(16, 80, 16, 80),
                Children =
                {
                    MurmurStyle.Text(all.Count > 0 ? "No matching transcripts" : "Your dictation appears here", "MurmurHistoryEmpty"),
                    MurmurStyle.Text(all.Count > 0 ? "Try a different word." : mode == HistoryMode.On ? "Completed transcripts are saved here before they are inserted." : HistoryModes.OffDisclosure)
                }
            };
            foreach (var block in empty.Children.OfType<TextBlock>()) block.TextAlignment = TextAlignment.Center;
            entries.Children.Add(empty);
            return;
        }

        var today = DateTime.Today;
        foreach (var day in shown.GroupBy(entry => entry.RecordedAt.ToLocalTime().Date))
        {
            var date = day.Key == today ? "Today" : day.Key == today.AddDays(-1) ? "Yesterday"
                : day.Key.ToString(day.Key.Year == today.Year ? "MMM d" : "MMM d, yyyy");
            var heading = MurmurStyle.Text(date.ToUpperInvariant(), "MurmurSection");
            heading.Style = MurmurStyle.Style("MurmurSectionInk");
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            var count = day.Count();
            var labels = new StackPanel { Spacing = 8, Margin = new Thickness(0, 22, 0, 0), Children = { heading, MurmurStyle.Text(count == 1 ? "1 ENTRY" : $"{count} ENTRIES", "MurmurSection") } };
            var rows = new StackPanel();
            foreach (var entry in day)
            {
                rows.Children.Add(Row(entry));
                rows.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
            }
            var group = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(176) }, new ColumnDefinition() },
                Children = { labels, rows }
            };
            Grid.SetColumn(rows, 1);
            entries.Children.Add(new Border { Height = 24 });
            entries.Children.Add(group);
        }
        entries.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 24, 0, 0), Style = MurmurStyle.Style("MurmurRuleLine") });
    }

    static string Text(HistoryEntry entry) => entry.FinalText.Length > 0 ? entry.FinalText : entry.RawText;

    FrameworkElement Row(HistoryEntry entry)
    {
        var shown = Text(entry);
        var time = entry.RecordedAt.ToLocalTime().ToString("t");
        var chevron = new FontIcon { Glyph = "", FontSize = 12, Style = MurmurStyle.Style("MurmurMutedIcon") };
        var clock = MurmurStyle.Text(time, "MurmurSection");
        clock.FontSize = 14; clock.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
        var transcript = MurmurStyle.Text(shown, "MurmurTranscript");
        transcript.MaxLines = 2;
        transcript.TextTrimming = TextTrimming.CharacterEllipsis;
        var header = new Grid
        {
            ColumnSpacing = 14, Padding = new Thickness(8, 18, 4, 18),
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(14) }, new ColumnDefinition { Width = new GridLength(88) }, new ColumnDefinition() },
            Children = { chevron, clock, transcript }
        };
        Grid.SetColumn(clock, 1); Grid.SetColumn(transcript, 2);
        chevron.VerticalAlignment = clock.VerticalAlignment = VerticalAlignment.Top;
        clock.Margin = new Thickness(0, 3, 0, 0);

        var details = new StackPanel { Spacing = 10, Padding = new Thickness(116, 0, 4, 18), Visibility = Visibility.Collapsed };
        var meta = string.Join("  ·  ", new[] { entry.App is { Length: > 0 } a ? AppNames.Display(a) : null, entry.StatusLabel ?? entry.InsertionStatusRaw }.Where(x => !string.IsNullOrEmpty(x)));
        if (meta.Length > 0) details.Children.Add(MurmurStyle.Text(meta, "MurmurCaption"));
        var full = MurmurStyle.Text(shown, "MurmurTranscript");
        full.IsTextSelectionEnabled = true;
        details.Children.Add(full);
        var copy = MurmurStyle.Named(new Button { Content = "Copy Text" }, $"Copy transcript from {time}");
        copy.Click += (_, _) =>
        {
            if (Native.CopyText(shown)) copy.Content = "Copied";
            else ShowError("The transcript could not be copied.");
        };
        var delete = MurmurStyle.Named(new Button { Content = "Delete" }, $"Delete transcript from {time}");
        delete.Click += (_, _) =>
        {
            try { s.History.Delete(entry); }
            catch (Exception) { ShowError("The transcript could not be deleted."); return; }
            Reload();
        };
        details.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, delete } });

        var toggle = new Button
        {
            Content = header, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), CornerRadius = new CornerRadius(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
        };
        MurmurStyle.Named(toggle, $"Transcript from {time}");
        void Set(bool open)
        {
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = open ? "" : "";
            transcript.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (open) { header.Children.Remove(transcript); } else if (!header.Children.Contains(transcript)) { header.Children.Add(transcript); Grid.SetColumn(transcript, 2); }
        }
        toggle.Click += (_, _) => Set(details.Visibility == Visibility.Collapsed);
        var result = new StackPanel { Children = { toggle, details } };
        if (entry.Id == openId)
        {
            openId = null;
            Set(true);
            result.Loaded += (_, _) => result.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.2 });
        }
        return result;
    }

    async Task ClearAll()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root(), Title = "Clear History?", Content = new TextBlock { Text = "This deletes every saved dictation transcript from this PC. It can't be undone.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Clear History", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await MurmurStyle.Named(dialog, "Clear History").ShowAsync() != ContentDialogResult.Primary) return;
        try { foreach (var entry in s.History.Entries()) s.History.Delete(entry); }
        catch (Exception) { ShowError("History could not be fully cleared."); }
        Reload();
    }

    void ShowError(string message)
    {
        status.Message = message;
        status.IsOpen = true;
    }
}
