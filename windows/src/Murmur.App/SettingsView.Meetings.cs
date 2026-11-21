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
        var transcription = new[] { MeetingTranscription.Local, MeetingTranscription.Cloud };
        SegmentedRow("Meeting transcription", "A change applies to meetings started afterwards; a meeting in progress keeps its route. Dictation’s route is set separately in Transcription.",
            ["On this PC (Parakeet)", "Cloud (Custom Provider)"], MeetingRoute.Load(s.Settings) == MeetingTranscription.Cloud ? 1 : 0,
            i => s.Settings.Set("meetingTranscription", i == 1 ? "cloud" : "local"),
            i =>
            {
                if (i == 0) return "Meeting audio is transcribed on this PC and never leaves it. This is the default.";
                var plan = RoutePlan.Make(RouteSettings.Load(s.Settings) with { Transcription = TranscriptionRoute.Cloud }, false, s.Key);
                return plan.Transcriber is null ? (plan.Refusal ?? "No cloud transcriber is set up.") + " Choose one and save its key in Settings → Transcription."
                    : DisclosureText.Summary(plan, ProcessingStage.Transcription) ?? "Meeting audio is sent to the cloud transcriber.";
            }, cloud: [1]);

        var routes = new[] { MeetingNotesRoute.None, MeetingNotesRoute.CustomProvider, MeetingNotesRoute.LocalModel };
        int current = Math.Max(0, Array.IndexOf(routes, MeetingNotesRoutes.Load(s.Settings) ?? MeetingNotesRoute.None));
        SegmentedRow("Meeting notes", null, [.. routes.Select(r => r.Label())], current,
            i => { s.Settings.Set("meetingNotesRoute", routes[i].SettingValue()); },
            i =>
            {
                var r = RouteSettings.Load(s.Settings);
                var host = Uri.TryCreate(r.CustomAPIBaseURL, UriKind.Absolute, out var uri) ? uri.Host : null;
                return routes[i].Disclosure(host, uri?.IsLoopback == true, null);
            });

        // The on-device notes model: the same responsive row as the other models (progress, cancel, remove, verify), with the no-GPU warning where the model is chosen.
        var notesModel = new ModelRow(s, s.LocalNotesSlot, controller, root, go => go(), () => { });
        var gpuNote = MurmurStyle.Text("", "MurmurCaption");
        gpuNote.TextWrapping = TextWrapping.Wrap;
        void ShowGpuNote()
        {
            gpuNote.Text = NotesGpu.Warning(NotesGpu.Known) ?? "";
            gpuNote.Visibility = gpuNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowGpuNote();
        _ = NotesGpu.Available().ContinueWith(_ => s.UI.TryEnqueue(ShowGpuNote), TaskScheduler.Default);
        notesModel.Panel.Children.Insert(Math.Min(3, notesModel.Panel.Children.Count), gpuNote);
        page.Children.Add(MurmurStyle.Card(notesModel.Panel));

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
