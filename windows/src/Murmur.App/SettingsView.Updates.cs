using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>Settings → Updates (app/SettingsPaneViews.swift `updates`): the version, Check for Updates, the automatic-check switch, and Open at login.</summary>
internal sealed partial class SettingsView
{
    void BuildUpdates()
    {
        var updater = App.Updater;
        var status = MurmurStyle.Text("", "MurmurDescription");
        void Refresh()
        {
            status.Text = updater is null ? ""
                : updater.ReadyVersion is { } ready ? $"Version {ready} is ready. Restart to install it."
                : !updater.IsInstalled ? "Updates install only in the installed build of Murmur."
                : "You have the latest version.";
        }
        Refresh();
        var title = MurmurStyle.Text("Murmur " + AppInfo.Version, "MurmurSettingLabel");
        title.FontSize = 18; title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var check = Btn("Check for Updates", async () =>
        {
            if (updater is null) return;
            status.Text = "Checking…";
            await updater.CheckAsync();
            Refresh();
        });
        var restart = Btn("Restart to Update", () => { if (updater?.RestartToUpdate() is { } message) status.Text = message; });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { check, restart } };
        restart.Visibility = updater?.ReadyVersion is null ? Visibility.Collapsed : Visibility.Visible;
        check.IsEnabled = updater?.IsInstalled == true && updater.ReadyVersion is null;
        if (updater is not null) updater.UpdateReady += _ => s.UI.TryEnqueue(() => { Refresh(); restart.Visibility = Visibility.Visible; check.IsEnabled = false; });
        var head = new Grid { Padding = new Thickness(0, 12, 0, 12), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { new StackPanel { Spacing = 3, Children = { title, status } }, buttons } };
        Grid.SetColumn(buttons, 1);
        page.Children.Add(head);

        Toggle("Check automatically", Updater.Explanation, Prefs.UpdateChecks, visible: true);

        var login = MurmurStyle.Named(new ToggleSwitch { IsOn = LoginItem.IsOn, OnContent = null, OffContent = null, MinWidth = 0, IsEnabled = updater?.IsInstalled == true }, "Open at login");
        login.Toggled += (_, _) => { if (!LoginItem.Set(login.IsOn)) login.IsOn = LoginItem.IsOn; };
        Row("Open at login", updater?.IsInstalled == true ? "Start Murmur in the tray when you sign in to Windows." : "Only the installed build can start at login. This build is not installed.", login, visible: true);
    }
}
