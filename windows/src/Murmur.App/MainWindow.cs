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
    string? historyEntry;

    internal MainWindow(AppServices services, Controller controller)
    {
        s = services;
        this.controller = controller;
        Title = "Murmur";
        // "Show in taskbar" (the Mac's "Show in Dock", app/main.swift activation policy): off keeps the window out of the taskbar and Alt+Tab; the tray icon always stays.
        void ApplyTaskbar() => AppWindow.IsShownInSwitchers = s.Settings.Get(Prefs.ShowInDock);
        ApplyTaskbar();
        s.ShowInTaskbarChanged += ApplyTaskbar;
        Closed += (_, _) => s.ShowInTaskbarChanged -= ApplyTaskbar;
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
        AddSearchOverlay(root);
        MurmurStyle.SetTitleBar(this, caption);
        // The Mac window's minimum is 880 x 680 (app/main.swift:93, contentMinSize); the pages are laid out for at least that.
        MurmurStyle.SetInitialSize(this, 1100, 760, minWidth: 880, minHeight: 680);
        controller.TipCards.Changed += RefreshSidebar;
        Closed += (_, _) => { controller.TipCards.Changed -= RefreshSidebar; settings?.Detach(); };
        Navigate(Page.Home);
    }

    /// <summary>The "new" dots changed: redraw the sidebar in place.</summary>
    void RefreshSidebar()
    {
        sidebar.Child = page == Page.Settings ? SettingsSidebar() : MainSidebar();
        if (page == Page.Settings) Highlight(settingsPane);
    }

    /// <summary>Opens a Settings pane on a named tab (the tip cards' "Show Me").</summary>
    public void ShowSettingsPane(string pane, string tab)
    {
        settingsPane = pane; Navigate(Page.Settings); settings!.Show(pane, TabIndex(pane, tab)); Highlight(pane); Activate();
    }

    public void ShowHome() { Navigate(Page.Home); Activate(); }
    public void ShowInsights() { Navigate(Page.Insights); Activate(); }
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
            case Page.Insights: content.Content = new InsightsView(s, () => { settingsPane = "Privacy & Storage"; Navigate(Page.Settings); }).Build(); break;
            case Page.Meetings:
                meetings = new MeetingsView(s, controller, Root, () => WindowNative.GetWindowHandle(this));
                content.Content = meetings.View;
                break;
            case Page.History:
                history ??= new HistoryView(s, Root);
                if (historyEntry is { } id) history.Select(id);
                historyEntry = null;
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

    UIElement HomePage()
    {
        HomeView? view = null;
        view = new HomeView(s, controller, new HomeView.Actions(StartDictationFromHome, () => Navigate(Page.Meetings), () => Navigate(Page.History),
            (pane, tab) => { settingsPane = pane; Navigate(Page.Settings); settings!.Show(pane, TabIndex(pane, tab)); Highlight(pane); },
            () => ((App)Application.Current).ShowWelcome(), id => { historyEntry = id; Navigate(Page.History); })) { Refresh = () => { if (page == Page.Home) content.Content = HomePage(); } };
        return view.Build();
    }

    static int TabIndex(string pane, string tab) => pane == "Dictation" ? Array.IndexOf(["Recording", "Transcription", "Insertion"], tab) : 0;

    /// <summary>The Mac hides Murmur, then starts a toggled dictation so the words go to the app that was in front.</summary>
    void StartDictationFromHome()
    {
        (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize();
        var timer = s.UI.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => controller.StartDictationFromHome();
        timer.Start();
    }

    // ---- sidebars ----

    static FontIcon Icon(string glyph) => new() { Glyph = glyph, FontSize = 18, Style = MurmurStyle.Style("MurmurMutedIcon") };

    Border NavItem(string key, string glyph, string label, Action click, bool selected, bool mono, bool dot = false)
    {
        var text = MurmurStyle.Text(label, mono ? "MurmurSectionInk" : "MurmurSettingLabel");
        if (mono) { text.CharacterSpacing = 140; text.FontSize = 14; }
        else { text.FontSize = 16; text.Style = MurmurStyle.Style(selected ? "MurmurSettingLabel" : "MurmurTabOff"); text.FontSize = 16; }
        text.VerticalAlignment = VerticalAlignment.Center;
        var icon = Icon(glyph);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { icon, text } };
        // The Mac's ExploreDot: something new in here the user has not looked at.
        if (dot) row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MurmurAccent"], VerticalAlignment = VerticalAlignment.Center });
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
                NavItem("settings", "", "SETTINGS", () => Navigate(Page.Settings), false, true, controller.TipCards.AnyDot),
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
        var open = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE721", FontSize = 13, Style = MurmurStyle.Style("MurmurMutedIcon") }, MurmurStyle.Text("Search settings", "MurmurDescription"), new TextBlock { Text = "Ctrl K", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center } } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 16, 16), CornerRadius = new CornerRadius(8), MinHeight = 34
        };
        MurmurStyle.Named(open, "Search settings");
        open.Click += (_, _) => OpenSearch();
        var search = open;
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 0) };
        (string Pane, string Glyph)[] panes = [("General", ""), ("Dictation", ""), ("Writing", ""), ("Meetings", "\uE716"), ("Privacy & Storage", ""), ("Providers", ""), ("Updates", "\uE777")];
        foreach (var (pane, glyph) in panes)
        {
            if (pane == "Privacy & Storage") list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 14, 0, 14), Style = MurmurStyle.Style("MurmurRuleLine") });
            var p = pane;
            list.Children.Add(NavItem(p, glyph, p, () => { settingsPane = p; settings!.Show(p); Highlight(p); }, p == settingsPane, false, p == "Writing" && controller.TipCards.AnyDot));
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
}
