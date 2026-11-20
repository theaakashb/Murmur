using System.Collections.Concurrent;
using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>
/// Learning from corrections (app/Controller+Learning.swift). After a paste the dictated span is watched until focus leaves the field, 60 s pass,
/// attribution is lost, the next insertion starts or the learning switch goes off. A field UI Automation can read back is polled; any other field is
/// followed by the keys typed into it (never stored or logged), unless "Learn only from readable fields" is on. Only settled corrections that pass
/// the ledger's filter are kept. Diagnostics are fixed event names and counts. Password fields are never read or followed.
/// </summary>
internal sealed partial class Controller
{
    const double StableSettleDelay = 2, PollSeconds = 0.25;

    sealed class LearningWatch
    {
        public required Guid Session { get; init; }
        public required DictationObservation Observation { get; init; }
        public required FocusProbe.LearningTarget Target { get; init; }
        public required ProcessingCoordinator Processing { get; init; }
        public required HistoryRouter<HistoryEntry> History { get; init; }
        public string? HistoryId { get; init; }
        public KeystrokeSpan? Span { get; init; }
        public string Route => Span is null ? "ax" : "keys";
        public ConcurrentQueue<KeyEvent> Keys { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0);
        public volatile bool Ending, Aborted, Departed, PendingFlag;
        public Task Worker { get; set; } = Task.CompletedTask;
        // UI thread only.
        public string Current = "";
        public double ChangedAt;
        public bool Pending, SettledEarly, Closed;
        public string? LastPosted;
    }

    sealed record LearningStart(FocusProbe.LearningTarget Target, FocusProbe.LearningSnapshot? Before, int Gen);

    LearningWatch? learning; // read by the hook thread
    int learningGen;

    static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Before a paste (worker thread): ends the previous watch, then captures the field and its text so the pasted span's position is known.</summary>
    LearningStart? BeginLearning()
    {
        int gen = Interlocked.Increment(ref learningGen);
        EndLearning();
        if (!s.Settings.Get(Prefs.LearningEnabled) || s.Processing is null) return null;
        // Null for a password field, our own windows and elements UI Automation cannot identify: nothing is read or followed there.
        var target = FocusProbe.LearningTarget.Capture(out _);
        return target is null ? null : new LearningStart(target, target.Read(), gen);
    }

    /// <summary>After a paste (worker thread): starts the UI Automation watch when the field reads back the pasted text at the expected place, otherwise the keystroke watch.</summary>
    void StartLearning(LearningStart? start, string text, InsertionStatus status, string? historyId)
    {
        if (start is null || status == InsertionStatus.Failed || text.Length == 0) return;
        var processing = s.Processing;
        if (processing is null || !s.Settings.Get(Prefs.LearningEnabled)) return;
        DictationObservation? observation = null;
        string miss = "unreadable";
        if (start.Before is { } before)
        {
            miss = "text-not-at-offset";
            int at = before.Selection.Location;
            var expected = string.Concat(before.Value.AsSpan(0, at), text, before.Value.AsSpan(before.Selection.End));
            for (int i = 0; i < 10 && observation is null; i++)
            {
                if (i > 0) Thread.Sleep(50);
                if (start.Target.Read() is not { } after) { miss = "no-readback"; continue; }
                if (!string.Equals(after.Value, expected, StringComparison.Ordinal)) continue;
                observation = DictationObservation.Create(after.Value, new Utf16Range(at, text.Length), Seconds());
                miss = observation is null ? "span-cuts-word" : "";
            }
        }
        KeystrokeSpan? span = null;
        if (observation is null && !s.Settings.Get(Prefs.ReadableLearning))
        {
            observation = DictationObservation.Create(text, new Utf16Range(0, text.Length), Seconds());
            if (observation is not null)
            {
                span = new KeystrokeSpan(text) { Resolve = candidates => CorrectionFilter.PlausibleEdit(text, candidates, Lexicon.English.IsCommonWord) };
                LearningKeyboard.MouseButtonUsed(); // clears the "pressed since" bit left by the click that placed the caret
            }
        }
        if (observation is null) { TestEvents.Emit("learning-watch", ("route", "none"), ("miss", miss)); return; }
        var w = new LearningWatch { Session = observation.SessionId, Observation = observation, Target = start.Target, Processing = processing, History = s.History, HistoryId = historyId, Span = span, Current = text };
        w.ChangedAt = Seconds();
        if (Volatile.Read(ref learningGen) != start.Gen) return; // a newer insertion began while this one was reading back
        w.Worker = Task.Factory.StartNew(() => RunLearning(w), TaskCreationOptions.LongRunning);
        if (Interlocked.CompareExchange(ref learning, w, null) is not null) { w.Aborted = true; w.Signal.Release(); return; }
        TestEvents.Emit("learning-watch", ("route", w.Route), ("miss", miss.Length == 0 ? null : miss));
    }

    /// <summary>Closes the current watch with a last look at the field, then settles it. Used by the next insertion and by shutdown.</summary>
    void EndLearning()
    {
        if (Interlocked.Exchange(ref learning, null) is not { } w) return;
        w.Ending = true;
        w.Signal.Release();
        w.Worker.Wait(1500);
    }

    /// <summary>The learning switch went off (Settings): stop every route at once and drop unsettled evidence.</summary>
    public void LearningSettingChanged()
    {
        if (s.Processing is { } p && p.Settings.LearningEnabled != s.Settings.Get(Prefs.LearningEnabled))
            p.Update(p.Settings with { LearningEnabled = s.Settings.Get(Prefs.LearningEnabled) }, persist: false);
        if (s.Settings.Get(Prefs.LearningEnabled)) return;
        if (Interlocked.Exchange(ref learning, null) is not { } w) return;
        w.Aborted = true;
        w.Signal.Release();
        try { w.Processing.Ledger.Revert(w.Session); } catch (Exception) { }
        w.Closed = true;
        TestEvents.Emit("learning-end", ("reason", "switched-off"));
    }

    /// <summary>Hook thread: forwards a key press to the keystroke watch only. Stays cheap; UI Automation validation and translation happen on the watch's own thread.</summary>
    void OfferLearningKey(in KeyEvent e)
    {
        if (learning is not { Span: not null } w || !e.IsDown || LearningKeys.IsModifier(e.VirtualKey)) return;
        if (!w.Target.IsNativeFocusCurrent() || LearningKeyboard.MouseButtonUsed()) w.Departed = true; else w.Keys.Enqueue(e);
        w.Signal.Release();
    }

    void RunLearning(LearningWatch w)
    {
        try
        {
            while (true)
            {
                w.Signal.Wait(TimeSpan.FromSeconds(PollSeconds));
                if (w.Aborted) break;
                LearningTick(w, w.Ending);
                if (w.Observation.Stopped) break;
            }
        }
        catch (Exception) { w.Observation.Finish(); }
        Interlocked.CompareExchange(ref learning, null, w);
    }

    void LearningTick(LearningWatch w, bool ending)
    {
        bool same, readable;
        string value; Utf16Range selection;
        if (w.Span is { } span)
        {
            while (w.Keys.TryDequeue(out var e))
            {
                // Exact identity is checked before any key is translated.
                if (!w.Target.IsCurrent()) { span.Depart(); w.Keys.Clear(); break; }
                span.Apply(LearningKeys.Map(e.VirtualKey, e.Modifiers.HasFlag(KeyModifiers.Ctrl), e.Modifiers.HasFlag(KeyModifiers.Shift),
                    e.Modifiers.HasFlag(KeyModifiers.Alt), e.Modifiers.HasFlag(KeyModifiers.Win),
                    () => LearningKeyboard.Translate(e.VirtualKey, e.ScanCode, e.Modifiers, w.Target.ThreadId)));
            }
            // A click moves the caret, which keystrokes cannot follow: the fallback ends and what was typed so far stands.
            if (w.Departed || LearningKeyboard.MouseButtonUsed()) span.Depart();
            span.Refresh();
            same = span.State != KeystrokeSpan.SpanState.Departed && w.Target.IsCurrent();
            readable = span.State != KeystrokeSpan.SpanState.Lost;
            (value, selection) = (span.Value, span.Selection);
        }
        else
        {
            same = w.Target.IsCurrent();
            var snapshot = same ? w.Target.Read() : null;
            // Another field taking focus is a departure, not lost access.
            readable = !same || snapshot is not null;
            (value, selection) = snapshot is null ? ("", default) : (snapshot.Value, snapshot.Selection);
        }
        double now = Seconds();
        w.Observation.Observe(value, selection, now, same, readable);
        if (ending) w.Observation.Finish();
        var observed = w.Observation.Edited;
        var edited = observed ?? w.Observation.Initial;
        bool stopped = w.Observation.Stopped;
        var reason = w.Observation.Reason;
        if (edited == w.LastPosted && !stopped && !w.PendingFlag) return;
        w.LastPosted = edited;
        ui.TryEnqueue(() => ApplyLearning(w, edited, stopped, reason, now, observed));
    }

    /// <summary>UI thread: the ledger and the dictionary belong here. Drops the result if the data was replaced or the switch went off meanwhile.</summary>
    void ApplyLearning(LearningWatch w, string edited, bool stopped, DictationObservation.StopReason? reason, double now, string? observed)
    {
        if (w.Closed) return;
        if (!ReferenceEquals(s.Processing, w.Processing) || !s.Settings.Get(Prefs.LearningEnabled)) { w.Closed = true; w.Aborted = true; return; }
        try
        {
            if (edited != w.Current)
            {
                var outcome = w.Processing.ObserveSpan(w.Session, w.Observation.Initial, edited);
                // The ledger updates the shared dictionary. Speech reads its current vocabulary at the next helper launch.
                w.Current = edited; w.ChangedAt = now;
                w.Pending = outcome is { Kind: CorrectionLedger.RecordKind.Provisional };
                w.PendingFlag = w.Pending;
                TestEvents.Emit("learning-edit", ("outcome", outcome?.Kind.ToString() ?? "Reverted"));
            }
            if (stopped)
            {
                w.Closed = true; w.PendingFlag = false;
                RecordAfterPaste(w, observed, reason);
                var learned = w.Processing.Settle(w.Session);
                AnnounceLearned(learned);
                TestEvents.Emit("learning-end", ("reason", reason?.RawValue()), ("route", w.Route), ("learned", learned.Count));
            }
            else if (w.Pending && now - w.ChangedAt >= StableSettleDelay)
            {
                w.Pending = false; w.PendingFlag = false; w.SettledEarly = true;
                var learned = w.Processing.Settle(w.Session);
                AnnounceLearned(learned);
                TestEvents.Emit("learning-settled", ("learned", learned.Count));
            }
        }
        catch (Exception)
        {
            w.Closed = true; w.Aborted = true;
            try { w.Processing.Ledger.Revert(w.Session); } catch (Exception) { }
        }
    }

    void RecordAfterPaste(LearningWatch watch, string? edited, DictationObservation.StopReason? reason)
    {
        (string Text, bool Changed)? seen = edited is null ? null : AfterPaste.TrimmingAppended(watch.Observation.Initial, edited);
        var after = new AfterPaste { Text = seen?.Text, Changed = seen?.Changed, Route = watch.Route, EndReason = reason?.RawValue() };

        var tips = new Tips(s.Settings);
        var routes = RouteSettings.Load(s.Settings);
        if (seen is { Changed: true } rewrite && !tips.IsDone(Tips.ID.VoiceEdit) &&
            Tips.IsRewrite(watch.Observation.Initial, rewrite.Text) &&
            VoiceEdit.VoiceEditReady(routes.Cleanup, routes, provider => s.Key(provider) is not null))
            tips.NoteRewrite(DateTimeOffset.Now);

        if (watch.HistoryId is not { } id || !ReferenceEquals(s.History, watch.History)) return;
        try
        {
            if (watch.History.Entries().FirstOrDefault(entry => entry.Id == id) is { } entry)
                watch.History.Update(entry with { AfterPaste = after });
        }
        catch (Exception) { TestEvents.Emit("after-paste-save-failed"); }
    }

    void AnnounceLearned(IReadOnlyList<DictionaryEntry> learned)
    {
        if (learned.LastOrDefault() is { } entry && phase == Phase.Ready)
            bubble.ShowNotice($"Added {entry.Preferred} to your dictionary.", 5);
    }
}
