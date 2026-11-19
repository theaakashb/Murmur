using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

internal enum Phase { Ready, Listening, Processing, Error, Saved }

/// <summary>
/// The dictation controller (app/Controller.swift, spec/pipeline.md). Bound to the UI thread: the keyboard hook thread only posts events here.
/// Every stage checks the session's generation token, so a cancel or a newer session drops stale results.
/// </summary>
internal sealed class Controller : IDisposable
{
    sealed class Session
    {
        public required int Gen { get; init; }
        public required RoutePlan Plan { get; init; }
        public required RouteSettings Routes { get; init; }
        public required CleanupOptions Options { get; init; }
        public required bool Insert { get; init; }
        public required bool LeadingSpace { get; init; }
        public required string WavPath { get; init; }
        public required DateTime StartedUtc { get; init; }
        public required Stopwatch Clock { get; init; }
        public IRecorder? Recorder { get; set; }
        public Task<FocusInfo?> Focus { get; set; } = Task.FromResult<FocusInfo?>(null);
        public double Tail { get; set; } = 0.2;
        public CancellationTokenSource Cts { get; } = new();
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
    volatile float level;
    Session? session;
    /// <summary>A recorder started from the hook thread at key down (spec/pipeline.md section 1: a hold never loses its first word). BeginSession adopts it; anything else drops it.</summary>
    sealed record EarlyStart(string WavPath, Task<IRecorder> Recorder);
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
        // Injected keys are ignored (our own Ctrl+V, other tools) except under the synthetic-audio test hook, where the harness drives the key with SendInput.
        hook = new KeyboardHook(OnKey, ignoreInjected: Environment.GetEnvironmentVariable("MURMUR_SYNTHETIC_AUDIO") is not { Length: > 0 });
        TestEvents.Emit("hook-installed");
    }

    public bool IsBusy => phase is Phase.Listening or Phase.Processing;

    /// <summary>Reads the dictation key and paste-last chord from settings (called at start and after Settings changes them).</summary>
    public void ReloadShortcuts()
    {
        dictationVk = ShortcutSpec.KeyOf(s.Settings.Get(Prefs.Shortcut));
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
        Apply(machine.Press(t, repeat));
        DropEarly(); // a press that did not start a session (finish, toggle, refused) leaves no recording behind
    }

    /// <summary>Runs on the hook thread: queues the recorder start so the hook returns at once.</summary>
    EarlyStart StartEarly()
    {
        var wavPath = Path.Combine(s.Locations.RetryAudio, Guid.NewGuid().ToString().ToUpperInvariant() + ".wav");
        return new EarlyStart(wavPath, Task.Run(() =>
        {
            PrivateFiles.PrepareDirectory(s.Locations.RetryAudio);
            return CreateRecorder(wavPath);
        }));
    }

    IRecorder CreateRecorder(string wavPath) =>
        Environment.GetEnvironmentVariable("MURMUR_SYNTHETIC_AUDIO") is { Length: > 0 } folder
            ? SyntheticRecorder.Start(folder, wavPath, p => level = p)
            : MicRecorder.Start(s.Settings.Get(Prefs.InputDeviceUID), wavPath, p => level = p);

    void DropEarly()
    {
        if (early is not { } e) return;
        early = null;
        _ = e.Recorder.ContinueWith(t => { if (t.IsCompletedSuccessfully) { try { t.Result.Stop(); } catch (Exception) { } } try { File.Delete(e.WavPath); } catch (Exception) { } });
    }

    void Released(double t) => Apply(machine.Release(t));

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

    void Apply(ShortcutAction? action)
    {
        switch (action)
        {
            case ShortcutAction.Start: if (!BeginSession()) machine.Cancel(); break;
            case ShortcutAction.Finish: FinishSession(); break;
            case ShortcutAction.Cancel: Discard(); break;
        }
    }

    void OnTick()
    {
        if (session is not { } ss || phase != Phase.Listening) { tick.Stop(); return; }
        bubble.SetLevel(level);
        if (machine.DoublePressExpired(Now()) == ShortcutAction.Cancel) Discard();
        else if (ss.Clock.Elapsed.TotalSeconds >= RecordingLimit && machine.Finish() == ShortcutAction.Finish) { ss.Tail = 0; FinishSession(); }
    }

    // ---- start guards and recording ----

    bool Stale(Session ss) => ss.Gen != generation;

    bool BeginSession()
    {
        var routes = RouteSettings.Load(s.Settings);
        var options = new CleanupOptions(s.Settings.Get(Prefs.CleanupFillers), s.Settings.Get(Prefs.CleanupCorrections), s.Settings.Get(Prefs.CleanupLists),
            s.Settings.Get(Prefs.CleanupPunctuation), s.Settings.Get(Prefs.CleanupPolish), s.Settings.Get(Prefs.CleanupProfessional), s.Settings.Get(Prefs.CleanupSpoken));
        var plan = RoutePlan.Make(routes, options.AnyEnabled, s.Key);
        if (plan.Refusal is { } refusal) return StartFailed("route-refused", refusal);
        // A cloud route that is not configured never falls back to local; a local route needs the model files and the helper.
        if (plan.UsesLocalModel)
        {
            if (!s.Local.HelperPresent) return StartFailed("local-helper-missing", "The speech helper is missing. Reinstall Murmur.");
            if (!s.Local.ModelFilesPresent) return StartFailed("local-model-missing", "The speech model is not installed yet. Download it in Settings, or pick a Custom Provider or Murmur Cloud.");
            if (s.Local.ModelDamaged) return StartFailed("local-model-damaged", "The speech model failed its checksum check. Remove it in Settings and download it again.");
            s.Local.Prepare();
        }
        // Load the cleanup model while the user speaks, so that cost is not inside the dictation.
        (plan.Cleaner as LocalModelCleanupProvider)?.Prewarm();

        // The recorder was started from the hook thread at key down; a UUID per recording, created exclusively by the recorder.
        var pre = early ?? StartEarly();
        early = null;
        var wavPath = pre.WavPath;
        var ss = new Session
        {
            Gen = ++generation, Plan = plan, Routes = routes, Options = options, WavPath = wavPath,
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
        level = 0;
        // The password check goes through UI Automation, which can block: it runs beside the recording, and a password field discards it.
        ss.Focus = Task.Run(() => FocusProbe.Capture(readText: false));
        _ = WatchFocus(ss);
        phase = Phase.Listening;
        TestEvents.Emit("recording-started");
        if (s.Settings.Get(Prefs.RecordingSounds)) Native.Cue(start: true);
        bubble.ShowListening();
        tick.Start();
        return true;
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
        ss.Cts.Cancel();
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
    }

    void FinishSession()
    {
        if (session is not { } ss) return;
        tick.Stop();
        phase = Phase.Processing;
        if (s.Settings.Get(Prefs.RecordingSounds)) Native.Cue(start: false);
        bubble.ShowProcessing("Transcribing...");
        _ = RunAsync(ss);
    }

    // ---- the pipeline ----

    async Task RunAsync(Session ss)
    {
        try { await RunCore(ss); }
        catch (OperationCanceledException) { }
        catch (Exception) { Fail(ss, "pipeline", "unexpected", "Something went wrong. The dictation was not pasted."); }
        finally { if (!Stale(ss)) { session = null; machine.ProcessingFinished(); } }
    }

    void Fail(Session ss, string stage, string reason, string message, bool quick = false)
    {
        if (Stale(ss)) return;
        phase = Phase.Error;
        TestEvents.Emit("error", ("stage", stage), ("reason", reason));
        bubble.ShowError(message, quick ? 2 : 8);
    }

    async Task RunCore(Session ss)
    {
        // Audio is cut at release plus a 0.2 s tail.
        if (ss.Tail > 0) await Task.Delay(TimeSpan.FromSeconds(ss.Tail));
        if (Stale(ss)) return;
        var capture = await Task.Run(() => ss.Recorder!.Stop());
        if (Stale(ss)) return;
        TestEvents.Emit("recording-stopped", ("seconds", Math.Round(capture.Seconds, 2)));
        if (capture.Samples16k.Length == 0)
        {
            Fail(ss, "capture", "no-audio", ss.Recorder!.Failure is null ? "No audio was captured." : "The microphone stopped. No audio was captured.");
            TryDelete(ss.WavPath);
            return;
        }
        var focus = await ss.Focus;
        if (Stale(ss)) return;
        if (focus is { IsPassword: true }) { Fail(ss, "start", "password-field", "Murmur does not dictate into password fields."); TryDelete(ss.WavPath); return; }

        // Transcribe: one request, no retry, no fallback between transcribers.
        var provider = ss.Plan.Transcriber;
        var transcribeStart = Stopwatch.GetTimestamp();
        var (outcome, detail) = provider is null
            ? await s.Local.TranscribeAsync(capture.Samples16k, ss.WavPath, ss.Cts.Token)
            : await CloudTranscription.RunDetailedAsync(provider, WAVAudio.EncodePCM16(capture.Samples16k, AudioConverter.TargetRate), ss.Cts.Token);
        if (Stale(ss)) return;
        if (outcome is not CloudTranscriptionOutcome.Transcript transcript)
        {
            // Audio stays for retry (24 h) unless the provider's error discards it.
            if (outcome is CloudTranscriptionOutcome.Failed { KeepAudio: false } or CloudTranscriptionOutcome.Cancelled) TryDelete(ss.WavPath);
            Fail(ss, "transcription", outcome switch { CloudTranscriptionOutcome.NoSpeech => "no-speech", CloudTranscriptionOutcome.Cancelled => "cancelled", _ => "failed" },
                CloudTranscription.Message(outcome) ?? CloudTranscription.UnknownFailure, quick: outcome is CloudTranscriptionOutcome.NoSpeech);
            return;
        }
        TestEvents.Emit("transcribed");
        var transcribeSeconds = Stopwatch.GetElapsedTime(transcribeStart).TotalSeconds;

        // Raw goes to History first, so a crash during cleanup loses nothing.
        var entry = new HistoryEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(), RecordedAt = ss.StartedUtc, RawText = transcript.Text, FinalText = transcript.Text,
            InsertionStatusRaw = InsertionStatus.SavedNotInserted.Raw(), Seconds = Math.Round(capture.Seconds, 2), App = focus?.ProcessName,
            TranscriptionProvider = provider?.Id.RawValue() ?? LocalTranscriber.ProviderId, TranscriptionModel = provider is null ? LocalTranscriber.ModelId : provider.ModelID,
            Transcription = new TranscriptionDetail { ClipSeconds = detail.ClipSeconds },
        };
        try { s.History.Save(entry); }
        catch (Exception) { Fail(ss, "history", "save-failed", $"Could not save the transcript. Audio is saved for retry for up to {RetryWindow.Text}."); return; }
        KeepOrDelete(ss.WavPath, entry.Id);
        bubble.ShowProcessing("Cleaning up...");

        var cleanupStart = Stopwatch.GetTimestamp();
        var result = await s.Processor.ProcessAsync(new DictationRequest(transcript.Text, ss.Options, ss.Routes.Cleanup, ss.Plan.Cleaner, focus?.ProcessName, null), ss.Cts.Token);
        if (Stale(ss)) return;
        TestEvents.Emit("cleaned", ("path", JsonNamingPolicy.CamelCase.ConvertName(result.Path.ToString())));
        entry = entry.With(result.FinalText, fallback: result.Fallback is { } f ? JsonNamingPolicy.CamelCase.ConvertName(f.ToString()) : null, rejected: result.RejectedOutput) with
        {
            CleanupOutcome = result.Path, AppVersion = AppInfo.Version,
            CleanupProvider = ss.Plan.Cleaner?.Id.RawValue() ?? "local-rules", CleanupModel = ss.Plan.Cleaner?.ModelID,
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
        if (!ss.Insert) { ShowNotPasted(entry, result.FinalText); return; }

        var target = await Task.Run(() => FocusProbe.Capture(readText: ss.LeadingSpace));
        if (Stale(ss)) return;
        if (target is { IsMurmur: true }) { ShowNotPasted(entry, result.FinalText); return; }
        var text = ss.LeadingSpace ? TextRules.WithLeadingSpace(result.FinalText, target?.Value, target?.Selection) : result.FinalText;
        entry = entry.With(text, fallback: entry.CleanupFallback, rejected: entry.CleanupRejectedOutput);
        bubble.Hide();
        var (final, delivery) = await Task.Run(() => Deliver(entry, text, target));
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
                break;
        }
    }

    sealed record PasteOutcome(InsertionStatus Status, PasteFailure? Failure);

    /// <summary>The delivery bracket (insertion-pending, one paste, final status). Runs off the UI thread: paste and read-back use UI Automation and the clipboard.</summary>
    (HistoryEntry Entry, DeliveryResult<PasteOutcome> Result) Deliver(HistoryEntry entry, string text, FocusInfo? target)
    {
        var result = Delivery.Deliver<HistoryEntry, PasteOutcome>(ref entry, (e, status) => e with { InsertionStatusRaw = status }, InsertionStatus.InsertionPending.Raw(),
            e => s.History.Save(e), (ref HistoryEntry e) =>
            {
                var outcome = PasteAndVerify(text);
                if (outcome.Status != InsertionStatus.Failed) e = e with { App = target?.ProcessName ?? e.App };
                return new Insertion<PasteOutcome>(outcome.Status.Raw(), outcome);
            });
        return (entry, result);
    }

    /// <summary>Pastes where the cursor is now, then reads the field back for up to 0.5 s to mark verified or unverified (History only; never a reason to paste twice).</summary>
    PasteOutcome PasteAndVerify(string text)
    {
        var pasted = s.Paste.Paste(text, stillTarget: () => FocusProbe.Capture(readText: false) is not { IsMurmur: true });
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
        session?.Cts.Cancel();
        tick.Stop();
        hook.Dispose();
        try { session?.Recorder?.Stop(); } catch (Exception) { }
        s.Local.Dispose();
        LocalCleanupRunner.Shared.Shutdown();
        s.Paste.FlushRestore();
    }
}
