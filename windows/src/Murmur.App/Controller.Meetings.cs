using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>
/// Meeting capture wiring (app/MeetingRecorder.swift, app/Controller.swift meeting parts): the Core <see cref="MeetingRecorder"/> over WASAPI, local chunk
/// transcription through the warm speech helper or the cloud transcriber, speaker separation, notes through the Custom Provider route, and the call detector. The consent and notes-route sheets are MeetingSheets; the Meetings screen and the call offer panel are views.
/// </summary>
internal sealed partial class Controller
{
    readonly MeetingRecorder meetings = new();
    readonly CallDetector callDetector = new();
    readonly Stopwatch meetingClock = Stopwatch.StartNew();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? meetingTick;
    MeetingSheets? sheets;
    bool notesSheetOpen;
    int detectionBusy;
    double lastDetection;
    bool testing => Environment.GetEnvironmentVariable("MURMUR_DATA_DIR") is { Length: > 0 };

    public MeetingRecorder Meetings => meetings;
    public bool MeetingLive => meetings.Live is not null;
    /// <summary>A call app started using the microphone (CallDetector). The UI worker shows the offer; <see cref="StartMeeting"/> with its Source accepts it.</summary>
    public event Action<CallOffer>? CallOffered;

    void InitMeetings()
    {
        sheets = new MeetingSheets(s);
        WireMeetings();
        meetings.Changed += OnMeetingChanged;
        meetings.Purge(s.Settings.Get(Prefs.Retention).Cutoff(DateTime.UtcNow));
        meetings.RecoverLeftovers();
        meetingTick = ui.CreateTimer();
        meetingTick.Interval = TimeSpan.FromSeconds(MeetingLimits.TickInterval);
        meetingTick.Tick += (_, _) => MeetingTick();
        meetingTick.Start();
    }

    /// <summary>(Re)points the recorder at the current data folders. Everything it needs comes in here, so Delete All and Restore can wire it again.</summary>
    void WireMeetings()
    {
        MeetingStore? store = null;
        try { store = new MeetingStore(s.Locations.Meetings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError or RetentionError) { }
        meetings.Wire(new(store, store is null ? null : s.Locations.MeetingAudio, s.Settings.Get(Prefs.HistoryMode).PersistsTranscripts(),
            new WasapiMeetingAudio(() => s.Settings.Get(Prefs.InputDeviceUID) is { Length: > 0 } id ? id : null),
            s.Local.HelperPresent ? s.Local.TranscribeChunkAsync : null, () => meetingClock.Elapsed.TotalSeconds, action => ui.TryEnqueue(() => action()),
            text => s.Dictionary?.Apply(text) ?? text, LoadNotesRoute, WriteMeetingNotes, () => !testing && MeetingConsent.Needed(s.Settings),
            Path.Combine(s.Locations.RetryAudio, "speakers"), s.Local.HelperPresent ? s.Local.TranscribeChunkTimedAsync : null, DiarizeAsync));
        RefreshSpeakerModels();
    }

    void ShutdownMeetings()
    {
        meetingTick?.Stop();
        meetings.Shutdown();
    }

    /// <summary>Delete All or Restore replaced the data folders: forget everything held in memory and open the new ones. (Called after <c>AppServices.ReopenData</c>.)</summary>
    public void ResetMeetings()
    {
        meetings.Cancel();
        meetings.Shutdown();
        meetings.Meetings.Clear();
        WireMeetings();
    }

    // --- speaker separation ---
    string SegmentationFolder => s.SpeakerSegmentationSlot.Downloader?.Target ?? "";
    string EmbeddingModel => Path.Combine(s.SpeakerEmbeddingSlot.Downloader?.Target ?? "", ModelFacts.SpeakerEmbeddingFile);
    bool SpeakerSwitch(string key) => s.Settings.Get(key) as bool? ?? false;

    bool SpeakerModelsVerified()
    {
        try
        {
            if (!File.Exists(Path.Combine(SegmentationFolder, ModelFacts.SpeakerSegmentationFile)) || !File.Exists(EmbeddingModel)) return false;
            foreach (var slot in new[] { s.SpeakerSegmentationSlot, s.SpeakerEmbeddingSlot })
            {
                if (slot is not { Downloader: { } downloader, Manifest: { } manifest }) return false;
                ModelVerificationCache.Shared.Verify(downloader.Target, manifest);
            }
            return true;
        }
        catch (RetentionError) { return false; }
    }

    /// <summary>Whether a meeting started now keeps the call's audio for separation: the switch is on and both models are present and match their pins. Read when a meeting starts. Settings calls this after the switch or a download changes.</summary>
    public void RefreshSpeakerModels()
    {
        bool on = SpeakerSwitch(SpeakerFacts.SettingKey);
        meetings.SpeakersAvailable = on && SpeakerModelsVerified();
        meetings.SeparateRoomSpeakers = on && SpeakerSwitch(SpeakerFacts.RoomKey);
        meetings.SpeakersWanted = on;
    }

    Task<IReadOnlyList<SpeakerSegment>?> DiarizeAsync(string audio, double threshold, CancellationToken ct) =>
        SpeakerRunner.RunAsync(s.Local.HelperPath, SegmentationFolder, EmbeddingModel, audio, threshold, SpeakerModelsVerified, ct);

    MeetingNotesRoute? LoadNotesRoute() => MeetingNotesRoutes.Load(s.Settings) ?? (testing ? MeetingNotesRoute.None : null);

    /// <summary>Notes on the Custom Provider route (spec "Meeting capture"). A missing address, model or key is a content-free NotesFailure the recorder shows.</summary>
    Task<MeetingNotesWriter.Outcome> WriteMeetingNotes(Meeting meeting, bool lecture, CancellationToken ct)
    {
        var route = MeetingNotesRoutes.Load(s.Settings) ?? MeetingNotesRoute.None;
        var routes = RouteSettings.Load(s.Settings);
        var provider = route == MeetingNotesRoute.None ? null
            : Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, s.Key(ProviderID.CustomCleanup), routes.CustomAPIBaseURL, routes.CustomAPIModel, routes.CustomAPIReasoningEffort);
        return MeetingNotesWriter.WriteAsync(meeting, route, provider, lecture, cancellationToken: ct);
    }

    /// <summary>Starts a meeting: <paramref name="source"/> is a detected call app, or everything except Murmur plus the microphone when null. Returns false when nothing started (the reason is in <c>Meetings.Message</c>).</summary>
    public bool StartMeeting(MeetingSource? source = null)
    {
        source ??= MeetingSource.Everything;
        if (meetings.Live is not null) { meetings.Message = "A meeting is already being recorded."; ShowMeetingMessage(); return false; }
        RefreshSpeakerModels();
        // The route is fixed when the meeting starts. A cloud route that is not configured refuses; it never falls back to this PC.
        Func<string, CancellationToken, Task<MeetingCloudResult>>? cloud = null;
        string? cloudName = null;
        if (MeetingRoute.Load(s.Settings) == MeetingTranscription.Cloud)
        {
            var plan = RoutePlan.Make(RouteSettings.Load(s.Settings) with { Transcription = TranscriptionRoute.Cloud }, false, s.Key);
            if (plan.Transcriber is not { } provider)
            {
                meetings.Message = (plan.Refusal ?? "The cloud transcriber is not set up.") + " Set it up in Settings > Transcription, or choose This PC for meetings.";
                ShowMeetingMessage();
                return false;
            }
            cloudName = provider.Id.DisplayName();
            cloud = async (path, ct) => await CloudTranscription.RunAsync(provider, path, ct).ConfigureAwait(false) switch
            {
                CloudTranscriptionOutcome.Transcript t => new MeetingCloudResult.Text(t.Text),
                CloudTranscriptionOutcome.NoSpeech => new MeetingCloudResult.NoSpeech(),
                _ => new MeetingCloudResult.Failed(),
            };
        }
        else if (!s.Local.HelperPresent || !s.Local.ModelFilesPresent)
        {
            meetings.Message = "Meeting notes transcribe on this PC. Download the speech model in Settings first.";
            ShowMeetingMessage();
            return false;
        }
        if (!meetings.Start(source, cloud, cloudName) && meetings.ConsentPending is { } pending)
        {
            meetings.ConsentPending = null;
            // The Mac's consent sheet: the meeting starts only after the box is ticked and Start is pressed.
            sheets!.Consent(pending, yes =>
            {
                if (!yes) return;
                MeetingConsent.Acknowledge(s.Settings);
                meetings.Start(pending, cloud, cloudName);
                TestEvents.Emit(meetings.Live is null ? "meeting-not-started" : "meeting-started", ("callAudioProblem", meetings.SystemAudioProblem is not null), ("cloud", cloud is not null), ("speakers", meetings.Live?.Speakers is not null));
                ShowMeetingMessage();
            });
            return false;
        }
        TestEvents.Emit(meetings.Live is null ? "meeting-not-started" : "meeting-started", ("callAudioProblem", meetings.SystemAudioProblem is not null), ("cloud", cloud is not null), ("speakers", meetings.Live?.Speakers is not null));
        ShowMeetingMessage();
        return meetings.Live is not null;
    }

    public void StopMeeting() => meetings.Stop();

    void ShowMeetingMessage()
    {
        if (meetings.Message is { } message) { bubble.ShowError(message, 8); meetings.Message = null; }
    }

    void OnMeetingChanged()
    {
        if (meetings.NotesChoicePending is not null) AskNotesRoute();
    }

    /// <summary>The first-Stop question as the Mac's sheet; the chosen route is saved and handed to the recorder.</summary>
    void AskNotesRoute()
    {
        if (notesSheetOpen) return;
        notesSheetOpen = true;
        sheets!.NotesChoice(route =>
        {
            notesSheetOpen = false;
            s.Settings.Set("meetingNotesRoute", route == MeetingNotesRoute.CustomProvider ? "custom-api" : "none");
            meetings.NotesRouteChosen(route);
        });
    }

    void MeetingTick()
    {
        meetings.Tick();
        ShowMeetingMessage();
        double now = meetingClock.Elapsed.TotalSeconds;
        if (now - lastDetection < 2 || Interlocked.Exchange(ref detectionBusy, 1) == 1) return;
        lastDetection = now;
        bool live = meetings.Live is not null, dictating = phase is Phase.Listening or Phase.Processing;
        _ = Task.Run(() =>
        {
            try { return ProcessTargets.InputApps(AudioSessions.Processes(), AudioSessions.Clients(), AudioSessions.OwnPid); }
            catch (Exception) { return []; }
        }).ContinueWith(task => ui.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref detectionBusy, 0);
            var offer = callDetector.Update(task.Result, meetingClock.Elapsed.TotalSeconds,
                new(CallDetectionSettings.Enabled(s.Settings), CallDetectionSettings.Never(s.Settings), live || meetings.Live is not null, dictating));
            if (offer is null) return;
            TestEvents.Emit("call-offer");
            CallOffered?.Invoke(offer);
        }), TaskContinuationOptions.ExecuteSynchronously);
    }
}
