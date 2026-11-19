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
        Fluent.SetTitleBar(this, Caption);
        Fluent.SetInitialSize(this, 760, 680);
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
                Padding = new Thickness(16, 48, 16, 48),
                Children =
                {
                    new SymbolIcon(Symbol.Clock) { HorizontalAlignment = HorizontalAlignment.Center },
                    Fluent.Text("Your dictation appears here", "SubtitleTextBlockStyle"),
                    Fluent.Text(mode == HistoryMode.On
                        ? "Completed transcripts are saved here before they are inserted."
                        : HistoryModes.OffDisclosure)
                }
            };
            foreach (var block in empty.Children.OfType<TextBlock>()) block.TextAlignment = TextAlignment.Center;
            Entries.Children.Add(Fluent.Card(empty));
            return;
        }

        foreach (var day in entries.GroupBy(entry => entry.RecordedAt.ToLocalTime().Date))
        {
            var heading = Fluent.Text(day.Key.ToString("D"), "BodyStrongTextBlockStyle");
            heading.Margin = new Thickness(0, 12, 0, 0);
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            Entries.Children.Add(heading);
            foreach (var entry in day) Entries.Children.Add(Row(entry));
        }
    }

    Border Row(HistoryEntry entry)
    {
        var shown = entry.FinalText.Length > 0 ? entry.FinalText : entry.RawText;
        var time = entry.RecordedAt.ToLocalTime().ToString("t");
        var header = string.Join("  ·  ", new[] { time, entry.App, entry.StatusLabel ?? entry.InsertionStatusRaw }
            .Where(x => !string.IsNullOrEmpty(x)));
        var transcript = Fluent.Text(shown, "BodyTextBlockStyle");
        transcript.MaxLines = 8;
        transcript.TextTrimming = TextTrimming.CharacterEllipsis;
        transcript.IsTextSelectionEnabled = true;
        var copy = Fluent.Named(new Button { Content = "Copy Text" }, $"Copy transcript from {time}");
        copy.Click += (_, _) =>
        {
            if (Native.CopyText(shown)) copy.Content = "Copied";
            else ShowError("The transcript could not be copied.");
        };
        var delete = Fluent.Named(new Button { Content = "Delete" }, $"Delete transcript from {time}");
        delete.Click += (_, _) =>
        {
            try { s.History.Delete(entry); }
            catch (Exception) { ShowError("The transcript could not be deleted."); return; }
            Reload();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, delete } };
        return Fluent.Card(new StackPanel
        {
            Spacing = 8,
            Children = { Fluent.Text(header, "MurmurCaption"), transcript, actions }
        });
    }

    void ShowError(string message)
    {
        Status.Message = message;
        Status.IsOpen = true;
    }
}
