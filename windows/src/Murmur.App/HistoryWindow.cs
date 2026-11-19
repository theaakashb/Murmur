using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>History: newest first, with the text, the app and how it ended. Copy puts the text on the clipboard; Delete removes the entry.</summary>
internal sealed class HistoryWindow : Window
{
    readonly AppServices s;
    readonly StackPanel list = new() { Spacing = 8, Padding = new Thickness(20) };

    public HistoryWindow(AppServices services)
    {
        s = services;
        Title = "Murmur History";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 680));
        Content = new ScrollViewer { Content = list };
    }

    public void Reload()
    {
        list.Children.Clear();
        List<HistoryEntry> entries;
        try { entries = s.History.Entries(); }
        catch (Exception) { list.Children.Add(new TextBlock { Text = "History could not be read." }); return; }
        list.Children.Add(new TextBlock { Text = HistoryText.Summary(s.Settings.Get(Prefs.HistoryMode), entries.Count, s.History.SessionOnlyIDs.Count), Opacity = 0.7 });
        if (entries.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing dictated yet." });
        foreach (var entry in entries) list.Children.Add(Row(entry));
    }

    Border Row(HistoryEntry entry)
    {
        var shown = entry.FinalText.Length > 0 ? entry.FinalText : entry.RawText;
        var header = string.Join("  |  ", new[] { entry.RecordedAt.ToLocalTime().ToString("g"), entry.App, entry.StatusLabel ?? entry.InsertionStatusRaw }.Where(x => !string.IsNullOrEmpty(x)));
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = header, Opacity = 0.7, FontSize = 12 });
        text.Children.Add(new TextBlock { Text = shown, TextWrapping = TextWrapping.Wrap, MaxLines = 8, IsTextSelectionEnabled = true });
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) => Native.CopyText(shown);
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => { try { s.History.Delete(entry); } catch (Exception) { } Reload(); };
        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(copy); buttons.Children.Add(delete);
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 1); grid.Children.Add(buttons);
        return new Border { Child = grid, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
    }
}
