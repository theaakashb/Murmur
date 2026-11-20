using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The Mac's settings search overlay (settingsSearchOverlay in app/SettingsView.swift): a dimmed window with a card holding the field and the list of matching settings; Enter or a click opens one.</summary>
internal sealed partial class MainWindow
{
    readonly Grid searchOverlay = new() { Visibility = Visibility.Collapsed, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(77, 0, 0, 0)) };
    readonly TextBox searchBox = MurmurStyle.Named(new TextBox { PlaceholderText = "Search settings", MinHeight = 34 }, "Search settings field");
    readonly StackPanel searchResults = new() { Spacing = 2 };
    List<(string Pane, int Tab, string Title, string Where)> found = [];

    void AddSearchOverlay(Grid root)
    {
        Grid.SetRowSpan(searchOverlay, 2);
        searchOverlay.Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, searchOverlay)) CloseSearch(); };
        var close = MurmurStyle.Named(new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) }, "Close settings search");
        close.Click += (_, _) => CloseSearch();
        var head = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { searchBox, close } };
        Grid.SetColumn(close, 1);
        var card = new Border
        {
            Width = 440, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 120, 0, 0), Padding = new Thickness(20), CornerRadius = new CornerRadius(12), Style = MurmurStyle.Style("MurmurCard"),
            Child = new StackPanel { Spacing = 12, Children = { head, new ScrollViewer { MaxHeight = 300, Content = searchResults } } }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, "Settings search");
        searchOverlay.Children.Add(card);
        searchBox.TextChanged += (_, _) => Refill();
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) CloseSearch();
            else if (e.Key == Windows.System.VirtualKey.Enter && found.Count > 0) OpenResult(found[0]);
        };
        var shortcut = new KeyboardAccelerator { Key = Windows.System.VirtualKey.K, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, e) => { OpenSearch(); e.Handled = true; };
        root.KeyboardAccelerators.Add(shortcut);
        root.Children.Add(searchOverlay);
    }

    void OpenSearch()
    {
        if (page != Page.Settings) return;
        settings ??= new SettingsView(s, controller, Root, () => WindowNative.GetWindowHandle(this));
        searchBox.Text = "";
        Refill();
        searchOverlay.Visibility = Visibility.Visible;
        searchBox.Focus(FocusState.Programmatic);
    }

    void CloseSearch() { searchOverlay.Visibility = Visibility.Collapsed; searchBox.Text = ""; }

    void Refill()
    {
        found = settings?.Matches(searchBox.Text) ?? [];
        searchResults.Children.Clear();
        if (found.Count == 0)
        {
            searchResults.Children.Add(new TextBlock { Text = searchBox.Text.Trim().Length == 0 ? "Type to find a setting." : "No matching settings.", Margin = new Thickness(8), Opacity = 0.7 });
            return;
        }
        foreach (var hit in found)
        {
            var title = MurmurStyle.Text(hit.Title, "MurmurSettingLabel"); title.FontSize = 14; title.FontWeight = Microsoft.UI.Text.FontWeights.Medium; title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis;
            var where = MurmurStyle.Text(hit.Where, "MurmurCaption");
            var row = MurmurStyle.Named(new Button { Content = new StackPanel { Spacing = 2, Children = { title, where } }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) }, $"{hit.Title}, {hit.Where}");
            var picked = hit;
            row.Click += (_, _) => OpenResult(picked);
            searchResults.Children.Add(row);
        }
    }

    void OpenResult((string Pane, int Tab, string Title, string Where) hit)
    {
        CloseSearch();
        settingsPane = hit.Pane;
        settings!.Show(hit.Pane, hit.Tab);
        Highlight(hit.Pane);
        settings.Reveal(hit.Title);
    }
}
