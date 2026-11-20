using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;
using WinRT.Interop;

namespace Murmur.App;

/// <summary>The one shared window, like the Mac's: a sidebar (Home, Insights, History, Settings, status), and Settings opens inside it with its own sidebar.
/// </summary>
internal sealed partial class MainWindow : Window
{
    enum Page { Home, Insights, Meetings, History, Settings }

    readonly AppServices s;
    readonly Controller controller;
    readonly Grid body = new();
    readonly ContentControl content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };
    readonly Border sidebar = new() { Width = 216, Style = MurmurStyle.Style("MurmurSidebarPane") };
    readonly Dictionary<string, Border> navItems = [];
    SettingsView? settings;
    HistoryView? history;
    MeetingsView? meetings;
    Page page = Page.Home;
    string settingsPane = "General";

    internal MainWindow(AppServices services, Controller controller)
    {
        s = services;
        this.controller = controller;
        Title = "Murmur";
        var caption = new TitleBar { Title = "Murmur" };
        AutomationProperties.SetName(caption, "Murmur title bar");
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        body.Children.Add(content);
        var root = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Style = MurmurStyle.Style("MurmurWindowRoot"), Children = { caption, body } };
        Grid.SetRow(body, 1);
        Content = root;
        MurmurStyle.SetTitleBar(this, caption);
        MurmurStyle.SetInitialSize(this, 1100, 760);
        Navigate(Page.Home);
    }

    public void ShowHome() { Navigate(Page.Home); Activate(); }
    public void ShowMeetings() { Navigate(Page.Meetings); Activate(); }
    public void ShowHistory() { Navigate(Page.History); Activate(); }
    public void ShowSettings() { Navigate(Page.Settings); Activate(); }

    XamlRoot? Root() => Content.XamlRoot;

    void Navigate(Page next)
    {
        page = next;
        sidebar.Child = next == Page.Settings ? SettingsSidebar() : MainSidebar();
        switch (next)
        {
            case Page.Home: content.Content = HomePage(); break;
            case Page.Insights: content.Content = InsightsPage(); break;
            case Page.Meetings:
                meetings = new MeetingsView(s, controller, Root, () => WindowNative.GetWindowHandle(this));
                content.Content = meetings.View;
                break;
            case Page.History:
                history ??= new HistoryView(s, Root);
                history.Reload();
                content.Content = history.View;
                break;
            default:
                settings ??= new SettingsView(s, controller, Root, () => WindowNative.GetWindowHandle(this));
                settings.Show(settingsPane);
                content.Content = settings.View;
                break;
        }
    }

    // ---- sidebars ----

    static FontIcon Icon(string glyph) => new() { Glyph = glyph, FontSize = 18, Style = MurmurStyle.Style("MurmurMutedIcon") };

    Border NavItem(string key, string glyph, string label, Action click, bool selected, bool mono)
    {
        var text = MurmurStyle.Text(label, mono ? "MurmurSectionInk" : "MurmurSettingLabel");
        if (mono) { text.CharacterSpacing = 140; text.FontSize = 14; }
        else { text.FontSize = 16; text.Style = MurmurStyle.Style(selected ? "MurmurSettingLabel" : "MurmurTabOff"); text.FontSize = 16; }
        text.VerticalAlignment = VerticalAlignment.Center;
        var icon = Icon(glyph);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { icon, text } };
        var button = new Button
        {
            Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0), Height = 36, CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
        };
        MurmurStyle.Named(button, label);
        button.Tag = text;
        button.Click += (_, _) => click();
        var item = new Border { Child = button, CornerRadius = new CornerRadius(8), Style = MurmurStyle.Style(selected ? "MurmurNavOn" : "MurmurNavOff") };
        navItems[key] = item;
        return item;
    }

    UIElement MainSidebar()
    {
        navItems.Clear();
        var items = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(8, 28, 8, 0),
            Children =
            {
                NavItem("home", "", "HOME", () => Navigate(Page.Home), page == Page.Home, true),
                NavItem("insights", "", "INSIGHTS", () => Navigate(Page.Insights), page == Page.Insights, true),
                NavItem("meetings", "\uE716", "MEETINGS", () => Navigate(Page.Meetings), page == Page.Meetings, true),
                NavItem("history", "", "HISTORY", () => Navigate(Page.History), page == Page.History, true),
                NavItem("settings", "", "SETTINGS", () => Navigate(Page.Settings), false, true),
            }
        };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Style = MurmurStyle.Style("MurmurBubbleDot"), VerticalAlignment = VerticalAlignment.Center };
        var ready = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(20, 0, 0, 22), VerticalAlignment = VerticalAlignment.Bottom, Children = { dot, MurmurStyle.Text("READY", "MurmurSection") } };
        AutomationProperties.SetName(ready, "Status: ready");
        return new Grid { Children = { items, ready } };
    }

    UIElement SettingsSidebar()
    {
        navItems.Clear();
        var back = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Icon(""), MurmurStyle.Text("Back to Murmur", "MurmurTabOff") } },
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(8, 0, 0, 0)
        };
        ((TextBlock)((StackPanel)back.Content).Children[1]).FontSize = 15;
        ((FontIcon)((StackPanel)back.Content).Children[0]).FontSize = 14;
        MurmurStyle.Named(back, "Back to Murmur");
        back.Click += (_, _) => Navigate(Page.Home);
        var heading = MurmurStyle.Text("Settings", "MurmurSettingLabel");
        heading.FontSize = 20; heading.FontWeight = Microsoft.UI.Text.FontWeights.Medium; heading.Margin = new Thickness(16, 22, 0, 12);
        var search = MurmurStyle.Named(new TextBox { PlaceholderText = "Search settings", Margin = new Thickness(16, 0, 16, 16), CornerRadius = new CornerRadius(8), MinHeight = 34 }, "Search settings");
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || settings?.Find(search.Text) is not { } hit) return;
            settingsPane = hit.Pane;
            settings.Show(hit.Pane, hit.Tab);
            Highlight(hit.Pane);
        };
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 0) };
        (string Pane, string Glyph)[] panes = [("General", ""), ("Dictation", ""), ("Writing", ""), ("Meetings", "\uE716"), ("Privacy & Storage", ""), ("Providers", ""), ("Updates", "\uE777")];
        foreach (var (pane, glyph) in panes)
        {
            if (pane == "Privacy & Storage") list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 14, 0, 14), Style = MurmurStyle.Style("MurmurRuleLine") });
            var p = pane;
            list.Children.Add(NavItem(p, glyph, p, () => { settingsPane = p; settings!.Show(p); Highlight(p); }, p == settingsPane, false));
        }
        return new StackPanel { Margin = new Thickness(0, 16, 0, 0), Children = { back, heading, search, list } };
    }

    void Highlight(string pane)
    {
        foreach (var (key, item) in navItems)
        {
            item.Style = MurmurStyle.Style(key == pane ? "MurmurNavOn" : "MurmurNavOff");
            if (item.Child is Button { Tag: TextBlock label } && !label.Text.All(char.IsUpper)) { label.Style = MurmurStyle.Style(key == pane ? "MurmurSettingLabel" : "MurmurTabOff"); label.FontSize = 16; }
        }
    }

    // ---- Home and Insights (minimal; Insights counts come from Murmur.Core) ----

    static StackPanel Page_(string title, string lede)
    {
        var heading = new TextBlock { Text = title, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Fonts/InstrumentSerif-Regular.ttf#Instrument Serif"), FontSize = 64, CharacterSpacing = -20, Style = MurmurStyle.Style("MurmurHeading") };
        heading.FontSize = 64; heading.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        var lead = MurmurStyle.Text(lede, "MurmurDescription");
        lead.FontSize = 16; lead.Margin = new Thickness(0, 6, 0, 0);
        return new StackPanel { Padding = new Thickness(40, 24, 40, 32), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left, Children = { heading, lead } };
    }

    UIElement HomePage()
    {
        var panel = Page_("Home", "Dictate anywhere the cursor is.");
        var label = MurmurStyle.Text("SHORTCUT", "MurmurSection"); label.Margin = new Thickness(0, 28, 0, 0);
        var shortcut = MurmurStyle.Text(ShortcutSpec.Label(s.Settings.Get(Prefs.Shortcut)), "MurmurSettingLabel");
        shortcut.FontSize = 28; shortcut.FontWeight = Microsoft.UI.Text.FontWeights.Bold; shortcut.Margin = new Thickness(0, 8, 0, 0);
        var how = MurmurStyle.Text(s.Settings.Get(Prefs.PressToToggle) ? "Press to start, press again to stop. Escape cancels." : "Hold to record and release to stop. Escape cancels.");
        how.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(label); panel.Children.Add(shortcut); panel.Children.Add(how);
        return MurmurStyle.Named(new ScrollViewer { Content = panel }, "Home");
    }

    UIElement InsightsPage()
    {
        var panel = Page_("Insights", "Counted on this PC from the dictations History keeps. Nothing is sent anywhere.");
        Insights result;
        try { result = Insights.Compute(s.History.Entries().Select(InsightsItem.FromHistory), s.Settings.Get(Prefs.HistoryMode) == HistoryMode.On, DateTimeOffset.Now, TimeZoneInfo.Local); }
        catch (Exception) { result = new Insights.Empty(); }
        switch (result)
        {
            case Insights.HistoryOff:
                panel.Children.Add(Notice("History is off", "Insights counts only what History keeps on this PC, so there is nothing to count. Turn History on in Privacy & Storage to see your words, pace and streaks."));
                break;
            case Insights.Stats stats:
                var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 200, ItemHeight = 104, MaximumRowsOrColumns = 4, Margin = new Thickness(0, 28, 0, 0) };
                foreach (var (name, value) in new[]
                {
                    ("WORDS DICTATED", stats.Words.ToString("N0")), ("DICTATIONS", stats.Dictations.ToString("N0")),
                    ("RECORDING TIME", stats.RecordingSeconds is { } r ? Insights.Duration(r) : "—"),
                    ("SPEAKING PACE", stats.WordsPerMinute is { } w ? $"{Math.Round(w)} WPM" : "—"),
                    ("EST. TIME SAVED", stats.SecondsSaved is { } t ? Insights.Duration(t) : "—"),
                    ("WORDS / DICTATION", stats.AverageWords.ToString("0.0")), ("WORDS CHANGED", stats.WordsChanged.ToString("N0")),
                    ("CURRENT STREAK", $"{stats.CurrentStreak} {(stats.CurrentStreak == 1 ? "day" : "days")}"), ("LONGEST STREAK", $"{stats.LongestStreak} {(stats.LongestStreak == 1 ? "day" : "days")}"),
                })
                {
                    var value_ = MurmurStyle.Text(value, "MurmurSettingLabel"); value_.FontSize = 24; value_.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    wrap.Children.Add(new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 12, 0), Children = { MurmurStyle.Text(name, "MurmurSection"), value_ } });
                }
                panel.Children.Add(wrap);
                break;
            default:
                panel.Children.Add(Notice("Nothing to count", "History has no dictations right now: either you have not dictated yet, or older dictations have expired."));
                break;
        }
        return MurmurStyle.Named(new ScrollViewer { Content = panel }, "Insights");
    }

    static UIElement Notice(string title, string detail)
    {
        var heading = MurmurStyle.Text(title, "MurmurSettingLabel");
        heading.FontSize = 22; heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return new StackPanel { Spacing = 12, Margin = new Thickness(0, 28, 0, 0), Children = { heading, MurmurStyle.Text(detail) } };
    }
}
