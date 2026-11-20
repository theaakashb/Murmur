using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>
/// Meeting capture wiring (app/MeetingRecorder.swift, app/Controller.swift meeting parts): the Core <see cref="MeetingRecorder"/> over WASAPI, local chunk
/// transcription through the warm speech helper, notes through the Custom Provider route, and the call detector. No visuals beyond two message boxes
/// (consent, first notes question) and the tray entry; the Meetings screen and the call offer panel come from the UI worker.
/// </summary>
internal sealed partial class Controller
{
    readonly MeetingRecorder meetings = new();
    readonly CallDetector callDetector = new();
    readonly Stopwatch meetingClock = Stopwatch.StartNew();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? meetingTick;
    int detectionBusy;
    double lastDetection;
    bool testing => Environment.GetEnvironmentVariable("MURMUR_DATA_DIR") is { Length: > 0 };

    public MeetingRecorder Meetings => meetings;
    public bool MeetingLive => meetings.Live is not null;
    /// <summary>A call app started using the microphone (CallDetector). The UI worker shows the offer; <see cref="StartMeeting"/> with its Source accepts it.</summary>
    public event Action<CallOffer>? CallOffered;

    void InitMeetings()
    {
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
            if (Native.MessageBoxW(IntPtr.Zero, "Murmur records your microphone and the audio of the call or apps you choose, and transcribes both on this PC. Recording other people may require their consent where you or they are. You are responsible for letting participants know.\n\nStart recording?",
                    "Before your first meeting notes", 0x24 | 0x40000) != 6) return false;
            MeetingConsent.Acknowledge(s.Settings);
            return meetings.Start(pending);
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

    /// <summary>The first-Stop question as a message box, until the Meetings screen has its own sheet. Yes sends the transcript to the Custom Provider; No keeps transcripts only.</summary>
    void AskNotesRoute()
    {
        var routes = RouteSettings.Load(s.Settings);
        var host = Uri.TryCreate(routes.CustomAPIBaseURL, UriKind.Absolute, out var uri) ? uri.Host : null;
        bool local = uri?.IsLoopback == true;
        var text = MeetingNotesRoute.CustomProvider.Disclosure(host, local, null) + "\n\nWrite notes with your Custom Provider?";
        var yes = Native.MessageBoxW(IntPtr.Zero, text, "Meeting notes", 0x24 | 0x40000) == 6;
        s.Settings.Set("meetingNotesRoute", yes ? "custom-api" : "none");
        meetings.NotesRouteChosen(yes ? MeetingNotesRoute.CustomProvider : MeetingNotesRoute.None);
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
