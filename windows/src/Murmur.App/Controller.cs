using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

internal enum Phase { Ready, Listening, Processing, Error, Saved }

/// <summary>
/// The dictation controller (app/Controller.swift, spec/pipeline.md). Bound to the UI thread: the keyboard hook thread only posts events here.
/// Every stage checks the session's generation token, so a cancel or a newer session drops stale results.
/// </summary>
internal sealed partial class Controller : IDisposable
{
    sealed class Session
    {
        public required int Gen { get; init; }
        public required RoutePlan Plan { get; init; }
        /// <summary>This session dictation languages, read once at start (app/Controller.swift sessionLanguages).</summary>
        public required DictationLanguages Languages { get; init; }
        public ActiveEdit? VoiceEdit { get; init; }
        public bool SeparateEdit { get; init; }
        public required RouteSettings Routes { get; init; }
        public required CleanupOptions Options { get; init; }
        public required bool Insert { get; init; }
        public required bool LeadingSpace { get; init; }
        public required string WavPath { get; init; }
        public required DateTime StartedUtc { get; init; }
        public required Stopwatch Clock { get; init; }
        public required RecordingFeed Feed { get; init; }
        public ChunkJob? Chunks { get; set; }
        public Chunker? Chunker { get; set; }
        public CancellationTokenSource? Live { get; set; }

        public void StopLive()
        {
            Live?.Cancel();
            Live?.Dispose();
            Live = null;
        }

        public IRecorder? Recorder { get; set; }
        public Task<FocusInfo?> Focus { get; set; } = Task.FromResult<FocusInfo?>(null);
        public Task<WritingMode?> Mode { get; set; } = Task.FromResult<WritingMode?>(null);
        public double Tail { get; set; } = 0.2;
        public double FinishedAt { get; set; }
        public CancellationTokenSource Cts { get; init; } = new();
        /// <summary>True while the transcription runs: a cancel then deletes the audio (app/Controller.swift cancelCloudRequest).</summary>
        public volatile bool Transcribing;
        /// <summary>The local transcription hit its deadline; the error event says so (the Mac has no deadline).</summary>
        public volatile bool DeadlineHit;
    }

    const double RecordingLimit = CaptureSession.RecordingLimit;
    readonly AppServices s;
    readonly Bubble bubble;
    readonly DispatcherQueue ui;
    readonly ShortcutMachine machine = new();
    readonly DispatcherQueueTimer tick;
    readonly KeyboardHook hook;
    volatile int dictationVk = KeyEvent.VkRControl;
    Chord pasteChord = Chord.PasteLast;
    volatile TaskCompletionSource<int>? recordingKey;
    bool dictationDown;
    double pressedAt;
    Session? session;
    /// <summary>A recorder started from the hook thread at key down (spec/pipeline.md section 1: a hold never loses its first word). BeginSession adopts it; anything else drops it.</summary>
    sealed record EarlyStart(string WavPath, RecordingFeed Feed, Task<IRecorder> Recorder);
    EarlyStart? early;
    int generation;
    volatile int phaseRaw;
    Phase phase { get => (Phase)phaseRaw; set => phaseRaw = (int)value; } // read by the hook thread
    string? lastText;

    public Controller(AppServices services, Bubble bubble)
    {
        s = services; this.bubble = bubble; ui = services.UI;
        bubble.TopPlacement = () => s.Settings.Get(Prefs.DictationIndicator) == DictationIndicator.Top;
        tick = ui.CreateTimer();
        tick.Interval = TimeSpan.FromMilliseconds(100);
        tick.Tick += (_, _) => OnTick();
        ReloadShortcuts();
        InitMeetings();
        TipCards = new TipPresenter(services, this);
        TipCards.Refresh(); // the Mac seeds the dots when History loads at launch (Controller+History.swift:12)
        // Injected keys are ignored (our own Ctrl+V, other tools) except under the synthetic-audio test hook, where the harness drives the key with SendInput.
        hook = new KeyboardHook(OnKey, ignoreInjected: Environment.GetEnvironmentVariable("MURMUR_SYNTHETIC_AUDIO") is not { Length: > 0 });
        TestEvents.Emit("hook-installed");
    }

    /// <summary>Milestone and voice-edit tips, and the Settings dots (app/TipsUI.swift).</summary>
    public TipPresenter TipCards { get; }

    public bool VoiceEditReady
    {
        get { var routes = RouteSettings.Load(s.Settings); return VoiceEdit.VoiceEditReady(routes.Cleanup, routes, provider => s.Key(provider) is not null); }
    }

    public bool IsBusy => phase is Phase.Listening or Phase.Processing;

    /// <summary>Reads the dictation key and paste-last chord from settings (called at start and after Settings changes them).</summary>
    public void ReloadShortcuts()
    {
        dictationVk = ShortcutSpec.KeyOf(s.Settings.Get(Prefs.Shortcut));
        ReloadVoiceEditShortcut();
        pasteChord = Chord.PasteLast; // shortcut: pasteLastShortcut is fixed to Ctrl+Win+Shift+V until Settings can record chords
    }

    /// <summary>Waits for the next key press (Settings "Record key"); 0 means Escape was pressed. The key is swallowed.</summary>
    public Task<int> RecordNextKey()
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        recordingKey = source;
        return source.Task.ContinueWith(t => { recordingKey = null; return t.Result; });
    }

    // ---- keyboard (hook thread) ----

    bool OnKey(in KeyEvent e)
    {
        if (recordingKey is { } capture)
        {
            if (e.IsDown) capture.TrySetResult(e.VirtualKey == KeyEvent.VkEscape ? 0 : e.VirtualKey);
            return true;
        }
        if (pasteChord.Matches(e)) { ui.TryEnqueue(PasteLast); return true; }
        if (HandleVoiceEditKey(e)) return true;
        int vk = dictationVk;
        if (e.VirtualKey == vk)
        {
            bool repeat = e.IsDown && dictationDown, down = e.IsDown;
            dictationDown = down;
            double t = e.Timestamp / (double)Stopwatch.Frequency;
            if (down && !repeat && phase != Phase.Listening && early is null) early = StartEarly();
            ui.TryEnqueue(() => { if (down) Pressed(t, repeat); else Released(t); });
            return !ShortcutSpec.IsModifier(vk);
        }
        OfferLearningKey(e);
        if (e.IsDown)
        {
            if (e.VirtualKey == KeyEvent.VkEscape) ui.TryEnqueue(EscapePressed);
            else if (dictationDown && !ShortcutSpec.IsModifier(e.VirtualKey)) ui.TryEnqueue(OtherKeyWhileHeld);
        }
        return false;
    }

    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // ---- trigger (UI thread) ----

    void Pressed(double t, bool repeat)
    {
        machine.PressToToggle = s.Settings.Get(Prefs.PressToToggle);
        if (!repeat) pressedAt = t;
        Apply(machine.Press(t, repeat), t);
        DropEarly(); // a press that did not start a session (finish, toggle, refused) leaves no recording behind
    }

    /// <summary>Runs on the hook thread: queues the recorder start so the hook returns at once.</summary>
    EarlyStart StartEarly()
    {
        var wavPath = Path.Combine(s.Locations.RetryAudio, Guid.NewGuid().ToString().ToUpperInvariant() + ".wav");
        var feed = new RecordingFeed();
        return new EarlyStart(wavPath, feed, Task.Run(() =>
        {
            PrivateFiles.PrepareDirectory(s.Locations.RetryAudio);
            return CreateRecorder(wavPath, feed.Push);
        }));
    }

    IRecorder CreateRecorder(string wavPath, Action<float[]> onSamples)
    {
        void Meter(float[] samples)
        {
            double energy = 0;
            foreach (var sample in samples) energy += sample * sample;
            if (samples.Length > 0) bubble.SetLevel((float)Math.Min(1, Math.Sqrt(energy / samples.Length) * 8));
            onSamples(samples);
        }
        return Environment.GetEnvironmentVariable("MURMUR_SYNTHETIC_AUDIO") is { Length: > 0 } folder
            ? SyntheticRecorder.Start(folder, wavPath, Meter)
            : MicRecorder.Start(s.Settings.Get(Prefs.InputDeviceUID), wavPath, Meter);
    }

    void DropEarly()
    {
        if (early is not { } e) return;
        early = null;
        e.Feed.Attach(null);
        _ = e.Recorder.ContinueWith(t => { if (t.IsCompletedSuccessfully) { try { t.Result.Stop(); } catch (Exception) { } } try { File.Delete(e.WavPath); } catch (Exception) { } });
    }

    void Released(double t) => Apply(machine.Release(t), t);

    (WritingMode Mode, string Text)? SpokenMode(string transcript)
    {
        try { return WritingModes.Trigger(WritingModes.Load(s.Settings), transcript); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError) { return null; }
    }

    public bool Listening => phase == Phase.Listening;

    /// <summary>Home's Start Dictation (Controller.startDictationFromHome on the Mac): a toggled recording; Stop or the shortcut finishes it.</summary>
    public void StartDictationFromHome() { if (machine.State == ShortcutState.Idle && !IsBusy) Apply(machine.StartToggled()); }

    public void StopRecording() => Apply(machine.Finish());

    /// <summary>Right Ctrl+C and the like: a key pressed during the first moments of a hold means a shortcut, not dictation.</summary>
    void OtherKeyWhileHeld()
    {
        if (machine.State == ShortcutState.Listening && Now() - pressedAt < machine.HoldThreshold) Apply(machine.Cancel());
    }

    void EscapePressed()
    {
        if (machine.State == ShortcutState.Listening) Apply(machine.Cancel());
        else if (machine.State == ShortcutState.Processing && session is { } ss) CancelProcessing(ss);
    }

    void Apply(ShortcutAction? action, double? eventTime = null)
    {
        switch (action)
        {
            case ShortcutAction.Start: if (!BeginSession()) machine.Cancel(); break;
            case ShortcutAction.Finish: FinishSession(eventTime); break;
            case ShortcutAction.Cancel: Discard(); break;
        }
    }

    void OnTick()
    {
        if (session is not { } ss || phase != Phase.Listening) { tick.Stop(); return; }
        if (machine.DoublePressExpired(Now()) == ShortcutAction.Cancel) Discard();
        else if (ss.Clock.Elapsed.TotalSeconds >= RecordingLimit && machine.Finish() == ShortcutAction.Finish) { ss.Tail = 0; FinishSession(); }
    }

    // ---- start guards and recording ----

    bool Stale(Session ss) => ss.Gen != generation;

    /// <summary>Tips.record (app/TipsUI.swift recordDictation): lifetime words and time saved, seeded from History the first time; a crossed rung queues the milestone card.</summary>
    void RecordTips(HistoryEntry entry)
    {
        try { TipCards.RecordDictation(entry.FinalText, entry.Seconds); }
        catch (Exception) { }
    }

    bool BeginSession()
    {
        var routes = RouteSettings.Load(s.Settings);
        var edit = ProbeVoiceEdit(routes);
        if (edit?.Result is VoiceEdit.SessionResult.Refused refused) return StartFailed("voice-edit-refused", refused.Message);
        var activeEdit = edit?.Active;
        if (DictationLanguages.Create(s.Settings.Get(Prefs.DictationLanguages)) is not { } languages)
            return StartFailed("languages-invalid", "Choose at least one supported dictation language.");
        var english = languages.Equals(DictationLanguages.English);
        var options = routes.Cleanup.EffectiveOptions(ProcessingSettings.Load(s.Settings).Options with { TrustModel = false });
        var plan = RoutePlan.Make(routes, english && activeEdit is null && options.AnyEnabled, s.Key);
        if (plan.Refusal is { } refusal) return StartFailed("route-refused", refusal);
        // A cloud route that is not configured never falls back to local; a local route needs the model files and the helper.
        if (plan.UsesLocalModel)
        {
            if (!s.Local.HelperPresent) return StartFailed("local-helper-missing", "The speech helper is missing. Reinstall Murmur.");
            if (!SpeechModelReady(languages.LocalModel)) return StartFailed("local-model-missing", "The speech model is not installed yet. Download it in Settings, or pick a Custom Provider or Murmur Cloud.");
            if (english && s.Local.ModelDamaged) return StartFailed("local-model-damaged", "The speech model failed its checksum check. Remove it in Settings and download it again.");
        }
        // Load the cleanup model while the user speaks, so that cost is not inside the dictation.
        (plan.Cleaner as LocalModelCleanupProvider)?.Prewarm();

        // The recorder was started from the hook thread at key down; a UUID per recording, created exclusively by the recorder.
        var pre = early ?? StartEarly();
        early = null;
        var wavPath = pre.WavPath;
        var ss = new Session
        {
            Gen = ++generation, Plan = plan, Languages = languages, Routes = routes, Options = options, WavPath = wavPath, Feed = pre.Feed,
            VoiceEdit = activeEdit, SeparateEdit = separateEditStart, Cts = activeEdit?.Cancellation ?? new(),
            Insert = s.Settings.Get(Prefs.AutomaticInsertion), LeadingSpace = s.Settings.Get(Prefs.IntelligentLeadingSpace),
            StartedUtc = DateTime.UtcNow, Clock = Stopwatch.StartNew(),
        };
        try
        {
            ss.Recorder = pre.Recorder.GetAwaiter().GetResult(); // normally finished already: it started at key down
        }
        catch (AudioCaptureException e)
        {
            generation++;
            return StartFailed("capture-" + e.Kind.ToString().ToLowerInvariant(), e.Kind switch
            {
                AudioCaptureError.NoDevice => "No microphone was found.",
                AudioCaptureError.AccessDenied => "Windows is blocking microphone access. Turn it on in Settings > Privacy & security > Microphone.",
                _ => e.Message,
            });
        }
        session = ss;
        bubble.SetLevel(0);
        // The password check goes through UI Automation, which can block: it runs beside the recording, and a password field discards it.
        ss.Focus = Task.Run(() => FocusProbe.Capture(readText: false));
        if (activeEdit is null && english) ss.Mode = Task.Run(() => s.SelectModeAsync(ss.Focus, ss.Cts.Token));
        _ = WatchFocus(ss);
        phase = Phase.Listening;
        TestEvents.Emit("recording-started");
        if (s.Settings.Get(Prefs.RecordingSounds)) Native.Cue(start: true);
        bubble.ShowListening();
        AttachSpeech(ss);
        tick.Start();
        return true;
    }

    void AttachSpeech(Session ss)
    {
        if (ss.Plan.UsesLocalModel && ss.Languages.LocalModel == LocalSpeechModel.ParakeetEnglish)
        {
            var job = s.Local.StartChunks(ss.WavPath, ss.Cts.Token);
            ss.Chunks = job;
            ss.Chunker = new Chunker(job.Enqueue, job.EnqueueBoundary);
        }

        Channel<float[]>? live = null;
        var liveEnabled = s.Settings.Get(Prefs.LiveWords);
        var liveReady = s.LiveWordsSlot.Row.IsReady;
        var english = ss.Languages.Equals(DictationLanguages.English);
        if (ss.VoiceEdit is null && english && LiveWordsFacts.Starts(liveEnabled, liveReady, ss.Plan.Transcriber is not null, s.Settings.Get(Prefs.LiveWordsOnCloud)))
        {
            // Roughly two seconds of 20 ms blocks. A slow preview must never back up capture.
            var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(96) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            live = channel;
            ss.Live = new CancellationTokenSource();
            s.Local.StartLiveWords(channel.Reader, text => ui.TryEnqueue(() =>
            {
                if (Stale(ss) || phase != Phase.Listening || ss.Live is null ||
                    !ss.Focus.IsCompletedSuccessfully || ss.Focus.Result?.IsPassword == true) return;
                bubble.SetPartial(text);
                if (!string.IsNullOrWhiteSpace(text)) TestEvents.Emit("partial");
            }), ss.Live.Token);
            bubble.ShowLiveWords();
        }
        else TestEvents.Emit("live-words-skipped", ("reason", !liveEnabled ? "disabled" : !english ? "language" : ss.VoiceEdit is not null ? "voice-edit" : !liveReady ? "model-not-ready" : "route-disabled"));

        ss.Feed.Attach(ss.Chunker is null && live is null ? null : samples =>
        {
            live?.Writer.TryWrite(samples);
            if (ss.Chunks?.Pending == true) ss.Chunker!.Append(samples);
        });
    }

    bool StartFailed(string reason, string message)
    {
        phase = Phase.Error;
        TestEvents.Emit("error", ("stage", "start"), ("reason", reason));
        bubble.ShowError(message, 8);
        return false;
    }

    async Task WatchFocus(Session ss)
    {
        var focus = await ss.Focus;
        if (Stale(ss) || focus is not { IsPassword: true }) return;
        machine.Cancel();
        Discard(error: "Murmur does not dictate into password fields.", reason: "password-field");
    }

    // ---- stop, cancel ----

    /// <summary>A short tap with no second press, Escape, or a password field: the recording is dropped.</summary>
    void Discard(string? error = null, string? reason = null)
    {
        if (session is not { } ss) return;
        generation++;
        session = null;
        tick.Stop();
        ss.StopLive();
        ss.Cts.Cancel();
        ss.Feed.Attach(null);
        _ = Task.Run(() => { try { ss.Recorder?.Stop(); File.Delete(ss.WavPath); } catch (Exception) { } });
        if (error is null) { phase = Phase.Ready; bubble.Hide(); return; }
        phase = Phase.Error;
        TestEvents.Emit("error", ("stage", "start"), ("reason", reason));
        bubble.ShowError(error, 8);
    }

    void CancelProcessing(Session ss)
    {
        generation++;
        ss.Cts.Cancel();
        session = null;
        machine.ProcessingFinished();
        phase = Phase.Ready;
        bubble.ShowNotice("Canceled.", 2);
        // Like the Mac's cancelCloudRequest: a cancel during transcription deletes the audio.
        if (ss.Transcribing) TryDelete(ss.WavPath);
    }

    void FinishSession(double? eventTime = null)
    {
        if (session is not { } ss) return;
        ss.FinishedAt = eventTime ?? Now();
        tick.Stop();
        phase = Phase.Processing;
        ss.StopLive();
        if (s.Settings.Get(Prefs.RecordingSounds)) Native.Cue(start: false);
        bubble.ShowProcessing();
        _ = RunAsync(ss);
    }

    // ---- the pipeline ----

    async Task RunAsync(Session ss)
    {
        try { await RunCore(ss); }
        catch (OperationCanceledException) { }
        catch (Exception) { Fail(ss, "pipeline", "unexpected", "Something went wrong. The dictation was not pasted."); }
        finally
        {
            if (ss.VoiceEdit is not null) TryDelete(ss.WavPath);
            if (!Stale(ss)) { session = null; machine.ProcessingFinished(); }
        }
    }

    void Fail(Session ss, string stage, string reason, string message, bool quick = false)
    {
        if (Stale(ss)) return;
        phase = Phase.Error;
        TestEvents.Emit("error", ("stage", stage), ("reason", reason));
        bubble.ShowError(message, quick ? 2 : 8);
    }

    /// <summary>The local route under one deadline. The helper chain (chunks, whole-file retry, one-shot) can add up to 15 minutes of 300 s limits; on the deadline the helpers are killed and the audio is kept for retry.</summary>
    async Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> TranscribeLocalWithinDeadline(Session ss, AudioCaptureResult capture)
    {
        var (answer, timedOut) = await ProcessingDeadline.RunAsync(ct => TranscribeLocalAsync(ss, capture.Samples16k, ct), ProcessingDeadline.Transcription(capture.Seconds), ss.Cts.Token);
        // A deadline that cancelled the helper is a failure with the audio kept, not the user's cancel (which deletes it).
        if (answer is { } done && !(timedOut && done.Outcome is CloudTranscriptionOutcome.Cancelled)) return done;
        if (!timedOut) return (new CloudTranscriptionOutcome.Cancelled(), new CloudTranscriptionDetail { ClipSeconds = capture.Seconds });
        ss.DeadlineHit = true;
        return (new CloudTranscriptionOutcome.Failed("Transcription took too long.", true), new CloudTranscriptionDetail { ClipSeconds = capture.Seconds });
    }

    async Task RunCore(Session ss)
    {
        var english = ss.Languages.Equals(DictationLanguages.English);
        // Audio is cut at release plus a 0.2 s tail.
        double remaining = ss.FinishedAt + ss.Tail - Now();
        if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining));
        var capture = await Task.Run(() =>
        {
            try
            {
                var result = ss.Recorder!.Stop();
                if (!ss.Cts.IsCancellationRequested) ss.Chunker?.Finish();
                return result;
            }
            finally
            {
                ss.Feed.Attach(null);
                ss.Chunks?.Finish();
            }
        });
        if (Stale(ss)) return;
        TestEvents.Emit("recording-stopped", ("seconds", Math.Round(capture.Seconds, 2)));
        if (capture.Samples16k.Length == 0)
        {
            Fail(ss, "capture", "no-audio", ss.Recorder!.Failure is null ? "No audio was captured." : "The microphone stopped. No audio was captured.");
            TryDelete(ss.WavPath);
            return;
        }
        // UI Automation can hang: after the bound the dictation goes on without a focus (unknown, not a password field); WatchFocus still discards it if the probe answers password in time.
        var focus = await ProcessingDeadline.WaitOr(ss.Focus, ProcessingDeadline.Focus, null);
        if (Stale(ss)) return;
        if (focus is { IsPassword: true }) { Fail(ss, "start", "password-field", "Murmur does not dictate into password fields."); TryDelete(ss.WavPath); return; }

        // Transcribe: one request, no retry, no fallback between transcribers.
        var provider = ss.Plan.Transcriber;
        var transcribeStart = Stopwatch.GetTimestamp();
        ss.Transcribing = true;
        var (outcome, detail) = provider is null
            ? await TranscribeLocalWithinDeadline(ss, capture)
            : await CloudTranscription.RunDetailedAsync(provider, WAVAudio.EncodePCM16(capture.Samples16k, AudioConverter.TargetRate), ss.Languages, ss.Cts.Token);
        ss.Transcribing = false;
        if (Stale(ss)) return;
        if (outcome is not CloudTranscriptionOutcome.Transcript transcript)
        {
            // Audio stays for retry (24 h) unless the provider's error discards it.
            if (outcome is CloudTranscriptionOutcome.Failed { KeepAudio: false } or CloudTranscriptionOutcome.Cancelled) TryDelete(ss.WavPath);
            Fail(ss, "transcription", outcome switch { CloudTranscriptionOutcome.NoSpeech => "no-speech", CloudTranscriptionOutcome.Cancelled => "cancelled", _ => ss.DeadlineHit ? "deadline" : "failed" },
                CloudTranscription.Message(outcome) ?? CloudTranscription.UnknownFailure, quick: outcome is CloudTranscriptionOutcome.NoSpeech);
            return;
        }
        TestEvents.Emit("transcribed");
        var transcribeSeconds = Stopwatch.GetElapsedTime(transcribeStart).TotalSeconds;

        var mode = await ProcessingDeadline.WaitOr(ss.Mode, ProcessingDeadline.Focus, null); // null for a non-English session: it neither selects nor speaks a mode
        if (Stale(ss)) return;
        // Mac parity (Controller.deliver): a spoken trigger ("Email mode. …") beats the app or website matched at start. It is cut from the
        // text sent to cleanup, never from History's raw transcript.
        var spoken = transcript.Text;
        if (ss.VoiceEdit is null && english && SpokenMode(spoken) is { } said) (mode, spoken) = said;

        // Raw goes to History first, so a crash during cleanup loses nothing.
        var entry = new HistoryEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(), RecordedAt = ss.StartedUtc, RawText = transcript.Text, FinalText = transcript.Text,
            InsertionStatusRaw = InsertionStatus.SavedNotInserted.Raw(), Seconds = Math.Round(capture.Seconds, 2), App = focus?.ProcessName,
            WritingModeID = mode?.Id.ToString().ToUpperInvariant(), WritingModeName = ss.VoiceEdit is not null ? "Voice edit" : mode?.Name,
            TranscriptionProvider = provider?.Id.RawValue() ?? LocalTranscriber.ProviderId, TranscriptionModel = provider is null ? SpeechModelRepository(ss.Languages.LocalModel) : provider.ModelID,
            Transcription = new TranscriptionDetail { ClipSeconds = detail.ClipSeconds },
        };
        var history = s.History;
        try { history.Save(entry); }
        catch (Exception)
        {
            Fail(ss, "history", "save-failed", ss.VoiceEdit is null
                ? $"Could not save the transcript. Audio is saved for retry for up to {RetryWindow.Text}."
                : "Could not save the instruction. The selection was not changed.");
            return;
        }
        if (ss.VoiceEdit is not null)
        {
            await RunVoiceEdit(ss, entry, history);
            return;
        }
        KeepOrDelete(ss.WavPath, entry.Id);

        // A non-English dictation sends nothing to cleanup: only the manual dictionary variants apply (app/Controller.swift deliver).
        var engine = mode is null || !english ? null : AIEngines.Derive(ss.Routes.Cleanup)?.Provider(ss.Routes, s.Key);
        var cleanupStart = Stopwatch.GetTimestamp();
        var result = await s.Processor.ProcessAsync(new DictationRequest(spoken, ss.Options, ss.Routes.Cleanup, ss.Plan.Cleaner, focus?.ProcessName, mode, engine, ManualDictionaryOnly: !english), ss.Cts.Token);
        if (Stale(ss)) return;
        TestEvents.Emit("cleaned", ("path", JsonNamingPolicy.CamelCase.ConvertName(result.Path.ToString())));
        entry = entry.With(result.FinalText, fallback: result.Fallback is { } f ? JsonNamingPolicy.CamelCase.ConvertName(f.ToString()) : null, rejected: result.RejectedOutput) with
        {
            CleanupOutcome = result.Path, AppVersion = AppInfo.Version,
            CleanupProvider = english ? ss.Plan.Cleaner?.Id.RawValue() ?? "local-rules" : null, CleanupModel = english ? ss.Plan.Cleaner?.ModelID : null,
            Timing = new StageTiming { Transcription = transcribeSeconds, Cleanup = Stopwatch.GetElapsedTime(cleanupStart).TotalSeconds, Capture = capture.Seconds },
        };
        try { s.History.Save(entry); }
        catch (Exception) { Fail(ss, "history", "save-failed", "Could not save the processed transcript. The raw transcript is in History."); return; }

        if (result.Held)
        {
            entry = entry with { InsertionStatusRaw = InsertionStatus.HeldCleanup.Raw() };
            try { s.History.Save(entry); } catch (Exception) { }
            phase = Phase.Error;
            TestEvents.Emit("error", ("stage", "cleanup"), ("reason", "processing-unavailable"));
            bubble.ShowError(s.DataProblem ?? "Cleanup is unavailable, so the transcript was kept in History.", 8);
            return;
        }

        // Only filler words were heard: keep the raw transcript, insert nothing, show no error.
        if (result.FinalText.Length == 0)
        {
            phase = Phase.Ready;
            bubble.ShowNotice("Only filler words were heard, so nothing was inserted.", 5);
            return;
        }
        RecordTips(entry);
        if (!ss.Insert) { ShowNotPasted(entry, result.FinalText); return; }

        var target = await Task.Run(() => FocusProbe.Capture(readText: ss.LeadingSpace));
        if (Stale(ss)) return;
        if (target is { IsMurmur: true } && !SetupShowing) { ShowNotPasted(entry, result.FinalText); return; }
        var text = ss.LeadingSpace ? TextRules.WithLeadingSpace(result.FinalText, target?.Value, target?.Selection) : result.FinalText;
        entry = entry.With(text, fallback: entry.CleanupFallback, rejected: entry.CleanupRejectedOutput);
        bubble.Hide();
        var (final, delivery) = await Task.Run(() => Deliver(entry, text, target, watchCorrections: english));
        if (Stale(ss)) return;
        switch (delivery)
        {
            case DeliveryResult<PasteOutcome>.PendingSaveFailed:
                Fail(ss, "history", "pending-save-failed", "Could not save the text for insertion, so nothing was pasted. Copy it from here or from History.");
                bubble.ShowCard("Not pasted", text);
                break;
            case DeliveryResult<PasteOutcome>.StatusSaveFailed:
                lastText = text; phase = Phase.Error;
                TestEvents.Emit("error", ("stage", "history"), ("reason", "status-save-failed"));
                bubble.ShowError(Delivery.StatusSaveFailureMessage("Pasted."), 8);
                break;
            case DeliveryResult<PasteOutcome>.Delivered d:
                ReportPaste(d.Value, final.Id, text);
                if (d.Value.Status != InsertionStatus.Failed)
                {
                    // A mode that couldn't rewrite says so; otherwise the Mac's "N changes" chip.
                    if (ModeFeedback.Notice(mode, engine is not null, result.Fallback) is { } modeNotice) bubble.ShowNotice(modeNotice, 6);
                    else OfferChanges(final, text);
                }
                break;
        }
    }

    sealed record PasteOutcome(InsertionStatus Status, PasteFailure? Failure);

    /// <summary>The delivery bracket (insertion-pending, one paste, final status). Runs off the UI thread: paste and read-back use UI Automation and the clipboard.</summary>
    (HistoryEntry Entry, DeliveryResult<PasteOutcome> Result) Deliver(HistoryEntry entry, string text, FocusInfo? target, bool watchCorrections = true)
    {
        var result = Delivery.Deliver<HistoryEntry, PasteOutcome>(ref entry, (e, status) => e with { InsertionStatusRaw = status }, InsertionStatus.InsertionPending.Raw(),
            e => s.History.Save(e), (ref HistoryEntry e) =>
            {
                var outcome = PasteAndVerify(text, e.Id, watchCorrections);
                if (outcome.Status != InsertionStatus.Failed) e = e with { App = target?.ProcessName ?? e.App };
                return new Insertion<PasteOutcome>(outcome.Status.Raw(), outcome);
            });
        return (entry, result);
    }

    /// <summary>Pastes where the cursor is now, then reads the field back for up to 0.5 s to mark verified or unverified (History only; never a reason to paste twice).</summary>
    PasteOutcome PasteAndVerify(string text, string? historyId = null, bool watchCorrections = true)
    {
        var learn = watchCorrections ? BeginLearning() : null;
        var outcome = PasteAndVerifyCore(text);
        if (watchCorrections) _ = Task.Run(() => StartLearning(learn, text, outcome.Status, historyId));
        return outcome;
    }

    PasteOutcome PasteAndVerifyCore(string text)
    {
        var pasted = s.Paste.Paste(text, stillTarget: () => FocusProbe.Capture(readText: false) is not { IsMurmur: true } || SetupShowing);
        if (!pasted.Pasted) return new PasteOutcome(InsertionStatus.Failed, pasted.Failure);
        _ = WatchClipboardRestore();
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(25);
            if (FocusProbe.Capture(readText: true) is { Value: { } value } && value.Contains(text, StringComparison.Ordinal)) return new PasteOutcome(InsertionStatus.Verified, null);
        }
        return new PasteOutcome(InsertionStatus.Unverified, null);
    }

    /// <summary>ClipboardPaste restores 0.5 s after the paste; the event fires once our staged item is gone from the clipboard.</summary>
    async Task WatchClipboardRestore()
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            if (!Native.MurmurClipboardItemPresent()) { TestEvents.Emit("clipboard-restored"); return; }
        }
    }

    void ReportPaste(PasteOutcome outcome, string historyId, string text)
    {
        lastText = text;
        phase = Phase.Ready;
        TestEvents.Emit("paste", ("status", outcome.Status.Raw()), ("reason", outcome.Failure is { } why ? why.ToString() : null), ("historyId", historyId));
        if (outcome.Status == InsertionStatus.Failed)
        {
            phase = Phase.Error;
            bubble.ShowCard("Could not paste the dictation. Copy it here, or select a text field and press Ctrl+Win+Shift+V.", text);
        }
    }

    /// <summary>Mac parity (offerModeRecovery): after any insertion that changed the words, the changes chip (8 s) with the words before cleanup.</summary>
    void OfferChanges(HistoryEntry saved, string inserted)
    {
        var mine = saved.Cleanup?.Input ?? saved.RawText;
        var changes = WordDiff.ChangeCount(mine, inserted.Trim());
        if (changes > 0) bubble.ShowChanges(changes, saved.WritingModeID is not null, mine);
    }

    void ShowNotPasted(HistoryEntry entry, string text)
    {
        lastText = text;
        phase = Phase.Saved;
        entry = entry.With(text, InsertionStatus.SavedNotInserted, entry.CleanupFallback, entry.CleanupRejectedOutput);
        try { s.History.Save(entry); } catch (Exception) { }
        TestEvents.Emit("paste", ("status", "failed"), ("reason", "not-inserted"), ("historyId", entry.Id));
        bubble.ShowCard("Saved, not pasted", text);
    }

    /// <summary>Ctrl+Win+Shift+V: pastes the last dictation where the cursor is now. Ignored while busy.</summary>
    void PasteLast()
    {
        if (lastText is not { } text || IsBusy) return;
        _ = Task.Run(() =>
        {
            var outcome = PasteAndVerify(text);
            ui.TryEnqueue(() =>
            {
                TestEvents.Emit("paste", ("status", outcome.Status.Raw()), ("reason", outcome.Failure?.ToString()));
                if (outcome.Status == InsertionStatus.Failed) bubble.ShowCard("Could not paste the dictation. Copy it here, then paste it yourself.", text);
            });
        });
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    /// <summary>Test hook MURMUR_KEEP_RECORDINGS (spec/test-hooks.md): keeps the recording as diagnostics\recordings\HISTORY-ID.wav for engine comparisons; otherwise deletes it.</summary>
    void KeepOrDelete(string wavPath, string historyId)
    {
        if (Environment.GetEnvironmentVariable("MURMUR_KEEP_RECORDINGS") is not { Length: > 0 }) { TryDelete(wavPath); return; }
        try
        {
            var folder = Path.Combine(s.Root, "diagnostics", "recordings");
            PrivateFiles.PrepareDirectory(folder);
            File.Move(wavPath, Path.Combine(folder, historyId + ".wav"));
        }
        catch (Exception) { TryDelete(wavPath); }
    }

    public void Dispose()
    {
        generation++;
        session?.StopLive();
        session?.Cts.Cancel();
        session?.Feed.Attach(null);
        tick.Stop();
        hook.Dispose();
        EndLearning();
        ShutdownMeetings();
        try { session?.Recorder?.Stop(); } catch (Exception) { }
        s.Local.Dispose();
        LocalCleanupRunner.Shared.Shutdown();
        s.Paste.FlushRestore();
    }
}
