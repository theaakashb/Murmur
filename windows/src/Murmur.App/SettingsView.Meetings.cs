using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>Settings → Meetings: the notes route, call detection and the apps never offered.</summary>
internal sealed partial class SettingsView
{
    StackPanel neverPanel = null!;

    void BuildMeetings()
    {
        var routes = new[] { MeetingNotesRoute.None, MeetingNotesRoute.CustomProvider };
        int current = MeetingNotesRoutes.Load(s.Settings) == MeetingNotesRoute.CustomProvider ? 1 : 0;
        SegmentedRow("Meeting notes", null, [.. routes.Select(r => r.Label())], current,
            i => { s.Settings.Set("meetingNotesRoute", routes[i] == MeetingNotesRoute.CustomProvider ? "custom-api" : "none"); },
            i =>
            {
                var r = RouteSettings.Load(s.Settings);
                var host = Uri.TryCreate(r.CustomAPIBaseURL, UriKind.Absolute, out var uri) ? uri.Host : null;
                return routes[i].Disclosure(host, uri?.IsLoopback == true, null);
            });

        Heading("Calls");
        var detect = MurmurStyle.Named(new ToggleSwitch { IsOn = CallDetectionSettings.Enabled(s.Settings), OnContent = null, OffContent = null, MinWidth = 0 }, "Offer meeting notes when a call starts");
        detect.Toggled += (_, _) => CallDetectionSettings.SetEnabled(s.Settings, detect.IsOn);
        Row("Offer meeting notes when a call starts", "When a call app starts using the microphone, Murmur offers to take notes. It never records until you accept.", detect, visible: true);

        Heading("Speakers");
        var details = new StackPanel { Spacing = 8 };
        var enabled = MurmurStyle.Named(new ToggleSwitch { IsOn = s.Settings.Get(SpeakerFacts.SettingKey) as bool? ?? false, OnContent = null, OffContent = null, MinWidth = 0 }, SpeakerFacts.DisplayName);
        var room = MurmurStyle.Named(new ToggleSwitch { IsOn = s.Settings.Get(SpeakerFacts.RoomKey) as bool? ?? false, OnContent = null, OffContent = null, MinWidth = 0 }, "Separate speakers in microphone-only recordings");
        void SpeakersChanged() { controller.RefreshSpeakerModels(); }
        enabled.Toggled += (_, _) => { s.Settings.Set(SpeakerFacts.SettingKey, enabled.IsOn); details.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed; SpeakersChanged(); };
        room.Toggled += (_, _) => { s.Settings.Set(SpeakerFacts.RoomKey, room.IsOn); SpeakersChanged(); };
        Row(SpeakerFacts.DisplayName, SpeakerFacts.Explanation(room.IsOn), enabled);
        var roomRow = new Grid { MinHeight = 56, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { MurmurStyle.Text("Separate speakers in microphone-only recordings", "MurmurSettingLabel"), room } };
        Grid.SetColumn(room, 1);
        details.Children.Add(roomRow);
        details.Children.Add(MurmurStyle.Text($"For microphone-only recordings, microphone audio is kept on this PC until speaker separation finishes, up to {RetryWindow.Text}, then deleted.", "MurmurCaption"));
        foreach (var slot in new[] { s.SpeakerSegmentationSlot, s.SpeakerEmbeddingSlot })
            details.Children.Add(MurmurStyle.Card(new ModelRow(s, slot, controller, root, go => go(), SpeakersChanged).Panel));
        details.Children.Add(MurmurStyle.Text(SpeakerFacts.Credits, "MurmurCaption"));
        details.Visibility = enabled.IsOn ? Visibility.Visible : Visibility.Collapsed;
        page.Children.Add(details);

        Heading("Never offered for");
        neverPanel = new StackPanel();
        page.Children.Add(neverPanel);
        RenderNever();
    }

    void RenderNever()
    {
        neverPanel.Children.Clear();
        var never = CallDetectionSettings.Never(s.Settings);
        if (never.Count == 0) neverPanel.Children.Add(MurmurStyle.Text("No apps. Choose “Never for …” on a meeting offer to add one.", "MurmurCaption"));
        foreach (var app in never)
        {
            var name = app;
            var row = new Grid { MinHeight = 48, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            var label = MurmurStyle.Text(name, "MurmurSettingLabel"); label.VerticalAlignment = VerticalAlignment.Center;
            var remove = Btn("Remove", () => { CallDetectionSettings.RemoveNever(s.Settings, name); RenderNever(); }, "Remove " + name, plain: true);
            Grid.SetColumn(remove, 1);
            row.Children.Add(label); row.Children.Add(remove);
            neverPanel.Children.Add(row);
        }
    }
}
