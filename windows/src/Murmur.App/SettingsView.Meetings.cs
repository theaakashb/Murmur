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
        var picker = new DictationLanguagePicker(() => MeetingLanguages.Effective(s.Settings), v => { s.Settings.Set(MeetingLanguages.Setting, v); RenderMeetingRoutes(); foreach (var refresh in refreshNotes) refresh(); },
            () => !MeetingLanguages.UsesCustomProvider(s.Settings), "What languages do you take meetings in?");
        refreshNotes.Add(picker.Refresh);
        refreshNotes.Add(() => picker.IsEnabled = !controller.IsBusy && !controller.MeetingSpeechSlot.InFlight);
        page.Children.Add(picker.Panel);
        meetingRoutes = new StackPanel();
        page.Children.Add(meetingRoutes);
        refreshNotes.Add(() => { if (sharedNote is not null) sharedNote.Visibility = controller.MeetingModelSharedWithDictation ? Visibility.Visible : Visibility.Collapsed; });

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
        notesModelCard = MurmurStyle.Card(notesModel.Panel);
        RenderMeetingRoutes();

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
        BuildMeetingAdvanced();
    }

    StackPanel meetingRoutes = null!, customBody = null!;
    FrameworkElement notesModelCard = null!;
    ModelRow? meetingRow;
    TextBlock? sharedNote;
    bool? meetingSame;
    Action? discloseCustom;

    static TextBlock Caption(string text)
    {
        var caption = MurmurStyle.Text(text, "MurmurCaption");
        caption.TextWrapping = TextWrapping.Wrap;
        return caption;
    }

    /// <summary>Meeting transcription and notes for the current languages and route. Redrawn when the languages or the Custom Provider switch change.</summary>
    void RenderMeetingRoutes()
    {
        var outer = page;
        page = meetingRoutes;
        meetingRoutes.Children.Clear();
        bool custom = MeetingLanguages.UsesCustomProvider(s.Settings);
        Heading("Meeting transcription");
        meetingRoutes.Children.Add(Caption("Meetings are transcribed after each chunk is recorded, not live, so a larger model only means the transcript finishes a little later. A change applies to meetings started afterwards. Dictation’s languages and model are set separately in Transcription."));
        sharedNote = null;
        meetingRow?.Detach(); meetingRow = null;
        if (custom) meetingRoutes.Children.Add(Caption("Meeting audio goes to your Custom Provider (Advanced, below)."));
        else
        {
            var model = controller.MeetingSpeechModel;
            meetingRow = new ModelRow(s, controller.SpeechModelSlot(model), controller, root, go => { s.Local.Release(); go(); }, () => { }, speechModel: () => model);
            meetingRoutes.Children.Add(MurmurStyle.Card(meetingRow.Panel));
            sharedNote = Caption("Also used by dictation: removing it here removes it for dictation too.");
            sharedNote.Visibility = controller.MeetingModelSharedWithDictation ? Visibility.Visible : Visibility.Collapsed;
            meetingRoutes.Children.Add(sharedNote);
            meetingRoutes.Children.Add(Caption("Audio stays on this PC."));
        }

        Heading("Meeting notes");
        if (custom) meetingRoutes.Children.Add(Caption("Notes are written by your Custom Provider (Advanced, below)."));
        else
        {
            var routes = new[] { MeetingNotesRoute.None, MeetingNotesRoute.LocalModel };
            var disclosure = MurmurStyle.Text("");
            disclosure.Margin = new Thickness(0, 8, 0, 0);
            void Disclose(MeetingNotesRoute route) => disclosure.Text = route.Disclosure(null, false, null);
            var current = MeetingNotesRoutes.Load(s.Settings) ?? MeetingNotesRoute.None;
            Disclose(current);
            meetingRoutes.Children.Add(new Segmented("Meeting notes", [.. routes.Select(r => r.Label())], Math.Max(0, Array.IndexOf(routes, current)),
                i => { s.Settings.Set("meetingNotesRoute", routes[i].SettingValue()); Disclose(routes[i]); }));
            meetingRoutes.Children.Add(disclosure);
            meetingRoutes.Children.Add(notesModelCard);
        }
        page = outer;
    }

    /// <summary>One switch sends both meeting transcription and notes to a Custom Provider, set up right here. The fields are the same Custom Provider settings as Transcription and Cleanup use;
    /// "same provider" writes one address and key to both.</summary>
    void BuildMeetingAdvanced()
    {
        var r = Routes;
        meetingSame = r.CustomAPIBaseURL == r.CustomTranscriptionAPIBaseURL;
        var body = page;
        var (advanced, advancedBody) = AdvancedSection("Meeting advanced settings", MeetingLanguages.UsesCustomProvider(s.Settings));
        body.Children.Add(advanced);
        customBody = new StackPanel { Spacing = 4 };
        page = advancedBody;
        Toggle("Use a Custom Provider for meetings", "Sends meeting audio for transcription, and the transcript for notes, to OpenAI-compatible endpoints you choose.", null, visible: true,
            get: () => MeetingLanguages.UsesCustomProvider(s.Settings),
            set: on =>
            {
                MeetingLanguages.SetUsesCustomProvider(s.Settings, on);
                customBody.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                RenderMeetingRoutes(); foreach (var refresh in refreshNotes) refresh();
            });
        advancedBody.Children.Add(customBody);
        customBody.Visibility = MeetingLanguages.UsesCustomProvider(s.Settings) ? Visibility.Visible : Visibility.Collapsed;
        page = body;
        RenderCustom();
        refreshNotes.Add(() => discloseCustom?.Invoke());
    }

    void RenderCustom()
    {
        var outer = page;
        page = customBody;
        customBody.Children.Clear();
        var r = Routes;
        bool same = meetingSame ?? true;
        Toggle("Same provider for transcription and notes", "One address and API key for both.", null, visible: true,
            get: () => meetingSame ?? true,
            set: on => { meetingSame = on; if (on) SaveRoutes(x => x with { CustomAPIBaseURL = x.CustomTranscriptionAPIBaseURL }); RenderCustom(); });
        var fields = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 8) };
        if (same) Field(fields, "API base URL", r.CustomTranscriptionAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomTranscriptionAPIBaseURL = v, CustomAPIBaseURL = v }));
        else
        {
            Field(fields, "Transcription API base URL", r.CustomTranscriptionAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomTranscriptionAPIBaseURL = v }));
            Field(fields, "Notes API base URL", r.CustomAPIBaseURL, RouteSettings.PlaceholderAPIURL, v => SaveRoutes(x => x with { CustomAPIBaseURL = v }));
        }
        Field(fields, "Transcription model", r.CustomTranscriptionModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomTranscriptionModel = v }));
        Field(fields, "Notes model", r.CustomAPIModel, RouteSettings.PlaceholderModel, v => SaveRoutes(x => x with { CustomAPIModel = v }));
        customBody.Children.Add(fields);
        if (same) KeyRow("Custom Provider key", ProviderID.CustomTranscription, () => Routes.CustomTranscriptionAPIBaseURL, also: ProviderID.CustomCleanup);
        else { KeyRow("Custom transcription key", ProviderID.CustomTranscription, () => Routes.CustomTranscriptionAPIBaseURL); KeyRow("Custom cleanup key", ProviderID.CustomCleanup, () => Routes.CustomAPIBaseURL); }
        customBody.Children.Add(Caption("Transcription needs POST /v1/audio/transcriptions and notes POST /v1/chat/completions. These are the same Custom Provider settings as in Transcription and Cleanup."));
        var disclosure = Caption("");
        void Disclose()
        {
            var plan = RoutePlan.Make(Routes with { Transcription = TranscriptionRoute.Cloud }, false, s.Key);
            var routes = Routes;
            var host = Uri.TryCreate(routes.CustomAPIBaseURL, UriKind.Absolute, out var uri) ? uri.Host : null;
            var transcription = plan.Transcriber is null ? (plan.Refusal ?? "No cloud transcriber is set up.") + " Enter the address, model and key above."
                : DisclosureText.Summary(plan, ProcessingStage.Transcription) ?? "Meeting audio is sent to the cloud transcriber.";
            disclosure.Text = transcription + "\n\n" + MeetingNotesRoute.CustomProvider.Disclosure(host, uri?.IsLoopback == true, null);
        }
        Disclose();
        discloseCustom = Disclose;
        customBody.Children.Add(disclosure);
        page = outer;
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
