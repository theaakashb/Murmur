using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

public sealed partial class HistoryWindow : Window
{
    readonly AppServices s;

    internal HistoryWindow(AppServices services)
    {
        s = services;
        InitializeComponent();
        Title = "Murmur History";
        MurmurStyle.SetTitleBar(this, Caption);
        MurmurStyle.SetInitialSize(this, 1000, 720);
    }

    public void Reload()
    {
        Entries.Children.Clear();
        Status.IsOpen = false;
        List<HistoryEntry> entries;
        try { entries = s.History.Entries(); }
        catch (Exception) { Summary.Text = ""; ShowError("History could not be read."); return; }

        var mode = s.Settings.Get(Prefs.HistoryMode);
        Summary.Text = HistoryText.Summary(mode, entries.Count, s.History.SessionOnlyIDs.Count);
        if (entries.Count == 0)
        {
            var empty = new StackPanel
            {
                Spacing = 8,
                Padding = new Thickness(16, 80, 16, 80),
                Children =
                {
                    MurmurStyle.Text("Your dictation appears here", "MurmurHistoryEmpty"),
                    MurmurStyle.Text(mode == HistoryMode.On
                        ? "Completed transcripts are saved here before they are inserted."
                        : HistoryModes.OffDisclosure)
                }
            };
            foreach (var block in empty.Children.OfType<TextBlock>()) block.TextAlignment = TextAlignment.Center;
            Entries.Children.Add(empty);
            return;
        }

        var today = DateTime.Today;
        foreach (var day in entries.GroupBy(entry => entry.RecordedAt.ToLocalTime().Date))
        {
            var date = day.Key == today ? "Today" : day.Key == today.AddDays(-1) ? "Yesterday"
                : day.Key.ToString(day.Key.Year == today.Year ? "MMM d" : "MMM d, yyyy");
            var heading = MurmurStyle.Text(date.ToUpperInvariant(), "MurmurSection");
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            var count = day.Count();
            var labels = new StackPanel { Spacing = 4, Children = { heading, MurmurStyle.Text(count == 1 ? "1 ENTRY" : $"{count} ENTRIES", "MurmurSection") } };
            var rows = new StackPanel { Spacing = 10 };
            foreach (var entry in day)
            {
                if (rows.Children.Count > 0)
                    rows.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
                rows.Children.Add(Row(entry));
            }
            var group = new Grid
            {
                Padding = new Thickness(0, 24, 0, 24),
                ColumnSpacing = 16,
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(80) }, new ColumnDefinition() },
                Children = { labels, rows }
            };
            Grid.SetColumn(rows, 1);
            Entries.Children.Add(group);
            Entries.Children.Add(new Border { Height = 1, Style = MurmurStyle.Style("MurmurRuleLine") });
        }
    }

    StackPanel Row(HistoryEntry entry)
    {
        var shown = entry.FinalText.Length > 0 ? entry.FinalText : entry.RawText;
        var time = entry.RecordedAt.ToLocalTime().ToString("t");
        var header = string.Join("  ·  ", new[] { time, entry.App, entry.StatusLabel ?? entry.InsertionStatusRaw }
            .Where(x => !string.IsNullOrEmpty(x)));
        var transcript = MurmurStyle.Text(shown, "MurmurTranscript");
        transcript.MaxLines = 8;
        transcript.TextTrimming = TextTrimming.CharacterEllipsis;
        transcript.IsTextSelectionEnabled = true;
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, delete } };
        return new StackPanel
        {
            Spacing = 6,
            Children = { MurmurStyle.Text(header, "MurmurCaption"), transcript, actions }
        };
    }

    void ShowError(string message)
    {
        Status.Message = message;
        Status.IsOpen = true;
    }
}
