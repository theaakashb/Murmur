// Port of app/Session.swift.
using System.Diagnostics;

namespace Murmur.Core;

/// <summary>The part of the Mac's <c>RetryAudio</c> (Capture.swift, not ported here) that <see cref="CaptureSession"/> uses: a new recording file, and removing one.</summary>
public interface IRetryAudioStore
{
    string Create();
    void Delete(string path);
}

public enum CaptureState { Idle, Listening, Processing }

/// <summary>
/// Coordinates one recording at a time: start, finish (after a short release tail), cancel, and a capture started during processing.
/// Not thread-safe: call it from one thread, and make <see cref="Schedule"/> run its action on that thread. Swift uses the main thread and
/// <c>Timer</c>; here the timer is injected so checks can drive it.
/// </summary>
public sealed class CaptureSession
{
    /// <summary>A dictation finishes and is processed after five minutes, so one left on by accident stops (user decision, October 6, 2026).</summary>
    public const double RecordingLimit = 300;

    readonly IRetryAudioStore store;
    readonly Func<string, bool> startCapture; // false or throwing means capture did not start
    readonly Func<bool> stopCapture;
    readonly Action<string, Action> process;
    IDisposable? timer;
    Action? pendingTail;
    string? early;
    DateTime? earlyStarted;

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public string? Audio { get; private set; }
    /// <summary>Capture started by a press during processing; the next start adopts it.</summary>
    public string? Early => early;

    /// <summary>Runs an action after a delay in seconds on the session's thread; dispose the result to cancel. Required.</summary>
    public required Func<double, Action, IDisposable> Schedule { get; init; }
    public Action? FinishRecording { get; set; }
    public Action? ResetShortcut { get; set; }
    public Action? ResumeCapture { get; set; }
    /// <summary>Capture keeps running this long after release so the last word, still in the input pipeline when the key comes up, is not cut off. 0 stops synchronously.</summary>
    public double ReleaseTail { get; set; }
    /// <summary>The key-release time (see <see cref="Uptime"/>) for the next finish, when it is known earlier than the UI thread handled it. Consumed by finish.</summary>
    public double? ReleasedAt { get; set; }
    /// <summary>The clock for the recording limit; checks substitute a fixed one.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    /// <summary>Monotonic seconds, for the release tail.</summary>
    public Func<double> Uptime { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    /// <summary>Sees a canceled recording's finished file just before it is deleted (Undo reads it into memory).</summary>
    public Action<string>? WillDeleteCanceled { get; set; }
    /// <summary>Returns a capture started before the UI thread handled the press, for start to adopt.</summary>
    public Func<string?>? TakePrestarted { get; set; }

    public CaptureSession(IRetryAudioStore store, Func<string, bool> start, Func<bool> stop, Action<string, Action> process)
    {
        this.store = store; startCapture = start; stopCapture = stop; this.process = process;
    }

    public void Receive(ShortcutAction action, Action completed)
    {
        switch (action)
        {
            case ShortcutAction.Start: Start(completed); break;
            case ShortcutAction.Finish: FinishNow(completed); break;
            default:
                if (State == CaptureState.Listening)
                {
                    stopCapture();
                    if (Audio is not null) WillDeleteCanceled?.Invoke(Audio);
                    Discard();
                }
                else CancelEarly();
                break;
        }
    }

    void Start(Action completed)
    {
        if (State != CaptureState.Idle) return;
        var adopted = early ?? TakePrestarted?.Invoke();
        if (adopted is not null)
        {
            // A capture started while the last dictation processed counts from its own start, so it never records past the limit.
            var elapsed = early is null ? 0 : earlyStarted is { } started ? (Now() - started).TotalSeconds : 0;
            var remaining = Math.Max(0, RecordingLimit - elapsed);
            early = null; earlyStarted = null; Audio = adopted; State = CaptureState.Listening;
            timer = Schedule(remaining, () => FinishRecording?.Invoke());
            ResumeCapture?.Invoke();
            return;
        }
        try
        {
            var url = store.Create(); Audio = url;
            if (!startCapture(url)) throw new IOException("Capture did not start.");
            State = CaptureState.Listening;
            timer = Schedule(RecordingLimit, () => FinishRecording?.Invoke());
        }
        catch (Exception)
        {
            Discard(); ResetShortcut?.Invoke(); completed();
        }
    }

    void FinishNow(Action completed)
    {
        if (State != CaptureState.Listening || Audio is not { } url) { completed(); return; }
        timer?.Dispose(); timer = null;
        State = CaptureState.Processing;
        void StopAndProcess()
        {
            if (!stopCapture()) { Discard(); completed(); return; }
            process(url, () => { Audio = null; State = CaptureState.Idle; completed(); });
        }
        if (ReleaseTail > 0)
        {
            pendingTail = StopAndProcess;
            var remaining = ReleasedAt is { } released ? Math.Max(0, released + ReleaseTail - Uptime()) : ReleaseTail;
            ReleasedAt = null;
            Schedule(remaining, RunTail);
        }
        else StopAndProcess();
    }

    void RunTail() { var finish = pendingTail; pendingTail = null; finish?.Invoke(); }

    /// <summary>Starts capture for a press during processing, cutting any release tail short first so two recordings never overlap. Returns false when nothing could start.</summary>
    public bool StartEarly()
    {
        if (State != CaptureState.Processing || early is not null) return false;
        RunTail();
        string url;
        try { url = store.Create(); } catch (Exception) { return false; }
        try
        {
            if (!startCapture(url)) throw new IOException("Capture did not start.");
            early = url; earlyStarted = Now(); return true;
        }
        catch (Exception) { try { store.Delete(url); } catch (Exception) { } return false; }
    }

    public void CancelEarly()
    {
        if (early is not { } url) return;
        early = null; earlyStarted = null; stopCapture();
        try { store.Delete(url); } catch (Exception) { }
    }

    public void Discard()
    {
        timer?.Dispose(); timer = null;
        if (Audio is { } audio) { try { store.Delete(audio); } catch (Exception) { /* the capture file stays; the retry-audio sweep removes it */ } }
        Audio = null; State = CaptureState.Idle;
    }

    public void Shutdown()
    {
        CancelEarly();
        if (State == CaptureState.Listening) { stopCapture(); Discard(); }
        timer?.Dispose();
    }
}

/// <summary>Holds one item for <see cref="Lifetime"/> seconds, then lets it go (a canceled recording's audio, in memory only, so Undo can still process it).
/// <see cref="Take"/> hands it over once, inside the window.</summary>
public sealed class UndoWindow<TItem>(double lifetime = 5)
{
    public double Lifetime { get; } = lifetime;
    /// <summary>The clock; checks substitute a fixed one.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    (TItem Item, DateTime Until)? held;

    public void Hold(TItem item) => held = (item, Now().AddSeconds(Lifetime));
    public bool IsOpen => held is { } h && Now() < h.Until;
    /// <summary>The item while the window is open, then nothing: it is released either way.</summary>
    public TItem? Take()
    {
        var open = IsOpen;
        var item = held is { } h ? h.Item : default;
        held = null;
        return open ? item : default;
    }
    public void Discard() => held = null;
}
