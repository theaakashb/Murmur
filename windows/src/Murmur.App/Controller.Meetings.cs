using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>
/// Meeting capture wiring (app/MeetingRecorder.swift, app/Controller.swift meeting parts): the Core <see cref="MeetingRecorder"/> over WASAPI, local chunk
/// transcription through the warm speech helper, notes through the Custom Provider route, and the call detector. The consent and notes-route sheets are MeetingSheets; the Meetings screen and the call offer panel are views.
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
        MeetingStore? store = null;
        try { store = new MeetingStore(s.Locations.Meetings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError or RetentionError) { }
        meetings.Wire(new(store, store is null ? null : s.Locations.MeetingAudio, s.Settings.Get(Prefs.HistoryMode).PersistsTranscripts(),
            new WasapiMeetingAudio(() => s.Settings.Get(Prefs.InputDeviceUID) is { Length: > 0 } id ? id : null),
            s.Local.HelperPresent ? s.Local.TranscribeChunkAsync : null, () => meetingClock.Elapsed.TotalSeconds, action => ui.TryEnqueue(() => action()),
            text => s.Dictionary?.Apply(text) ?? text, LoadNotesRoute, WriteMeetingNotes, () => !testing && MeetingConsent.Needed(s.Settings)));
        meetings.Changed += OnMeetingChanged;
        meetings.Purge(s.Settings.Get(Prefs.Retention).Cutoff(DateTime.UtcNow));
        meetings.RecoverLeftovers();
        meetingTick = ui.CreateTimer();
        meetingTick.Interval = TimeSpan.FromSeconds(MeetingLimits.TickInterval);
        meetingTick.Tick += (_, _) => MeetingTick();
        meetingTick.Start();
    }

    void ShutdownMeetings()
    {
        meetingTick?.Stop();
        meetings.Shutdown();
    }

    /// <summary>Delete All or Restore replaced the data folders: forget everything held in memory and open the new ones. (Call after <c>AppServices.ReopenData</c>.)</summary>
    public void ResetMeetings()
    {
        meetings.Cancel();
        meetings.Shutdown();
        meetings.Meetings.Clear();
        MeetingStore? store = null;
        try { store = new MeetingStore(s.Locations.Meetings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError or RetentionError) { }
        meetings.Wire(new(store, store is null ? null : s.Locations.MeetingAudio, s.Settings.Get(Prefs.HistoryMode).PersistsTranscripts(), new WasapiMeetingAudio(() => null), null,
            () => meetingClock.Elapsed.TotalSeconds, action => ui.TryEnqueue(() => action()), t => t, LoadNotesRoute, null, () => false));
    }

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
        if (!meetings.Start(source) && meetings.ConsentPending is { } pending)
        {
            meetings.ConsentPending = null;
            // The Mac's consent sheet: the meeting starts only after the box is ticked and Start is pressed.
            sheets!.Consent(pending, yes =>
            {
                if (!yes) return;
                MeetingConsent.Acknowledge(s.Settings);
                meetings.Start(pending);
                TestEvents.Emit(meetings.Live is null ? "meeting-not-started" : "meeting-started", ("callAudioProblem", meetings.SystemAudioProblem is not null));
                ShowMeetingMessage();
            });
            return false;
        }
        TestEvents.Emit(meetings.Live is null ? "meeting-not-started" : "meeting-started", ("callAudioProblem", meetings.SystemAudioProblem is not null));
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
