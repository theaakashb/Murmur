// Port of the ShortcutMachine struct in app/Shortcut.swift (the key codes and event taps are platform code, rewritten in Murmur.Platform).
namespace Murmur.Core;

public enum ShortcutState { Idle, Listening, Processing }
public enum ShortcutAction { Start, Finish, Cancel }

/// <summary>
/// Hold: press and keep holding; release finishes. Double press: two quick presses latch a toggle recording that only the next press finishes.
/// Capture starts on the first press, so a hold never loses its first word; a lone short tap is cancelled once <see cref="DoublePressWindow"/>
/// passes without a second press. Time is injected (seconds from any monotonic clock). Not thread-safe: drive it from one thread.
/// </summary>
public sealed class ShortcutMachine
{
    enum Mode { Holding, AwaitingSecondPress, Toggled }
    Mode mode = Mode.Holding;
    double releasedAt;
    double? pressedAt;
    /// <summary>The press that latched or finished a toggle owns its release, which must do nothing.</summary>
    bool ignoreRelease;

    public ShortcutState State { get; private set; } = ShortcutState.Idle;
    public double HoldThreshold { get; }
    /// <summary>Press-to-toggle setting: a short tap latches (the next press finishes), while a hold still finishes on release, so both gestures work in either mode.</summary>
    public bool PressToToggle { get; set; }
    public double DoublePressWindow { get; }

    public ShortcutMachine(double holdThreshold = 0.250, double doublePressWindow = 0.400)
    {
        if (!(holdThreshold > 0 && doublePressWindow > 0)) throw new ArgumentOutOfRangeException(nameof(holdThreshold), "Thresholds must be positive.");
        HoldThreshold = holdThreshold; DoublePressWindow = doublePressWindow;
    }

    public bool AwaitingSecondPress => mode == Mode.AwaitingSecondPress && State == ShortcutState.Listening;

    public ShortcutAction? Press(double time, bool isRepeat = false)
    {
        if (isRepeat || pressedAt is not null) return null;
        pressedAt = time;
        switch (State)
        {
            case ShortcutState.Processing: return null;
            case ShortcutState.Idle:
                State = ShortcutState.Listening; mode = Mode.Holding; ignoreRelease = false;
                return ShortcutAction.Start;
            default:
                ignoreRelease = true;
                if (mode == Mode.AwaitingSecondPress) { mode = Mode.Toggled; return null; }
                State = ShortcutState.Processing;
                return ShortcutAction.Finish;
        }
    }

    public ShortcutAction? Release(double time)
    {
        if (pressedAt is not { } start) return null;
        pressedAt = null;
        if (ignoreRelease) { ignoreRelease = false; return null; }
        if (State != ShortcutState.Listening || mode != Mode.Holding) return null;
        if (time - start >= HoldThreshold)
        {
            State = ShortcutState.Processing;
            return ShortcutAction.Finish;
        }
        if (PressToToggle) mode = Mode.Toggled;
        else { mode = Mode.AwaitingSecondPress; releasedAt = time; }
        return null;
    }

    /// <summary>A lone short tap: no second press arrived in time, so the recording is discarded.</summary>
    public ShortcutAction? DoublePressExpired(double time)
    {
        if (State != ShortcutState.Listening || mode != Mode.AwaitingSecondPress || time - releasedAt < DoublePressWindow) return null;
        State = ShortcutState.Idle;
        return ShortcutAction.Cancel;
    }

    public ShortcutAction? Cancel()
    {
        if (State != ShortcutState.Listening) return null;
        // Keep the press latch until release: Escape cannot restart a held key.
        State = ShortcutState.Idle;
        return ShortcutAction.Cancel;
    }

    public ShortcutAction? Finish()
    {
        if (State != ShortcutState.Listening) return null;
        State = ShortcutState.Processing;
        return ShortcutAction.Finish;
    }

    /// <summary>A start no key press began (the Start button): latched like a toggle, so the next press or Finish ends it.</summary>
    public ShortcutAction? StartToggled()
    {
        if (State != ShortcutState.Idle || pressedAt is not null) return null;
        State = ShortcutState.Listening; mode = Mode.Toggled; ignoreRelease = false;
        return ShortcutAction.Start;
    }

    public void ProcessingFinished() { if (State == ShortcutState.Processing) State = ShortcutState.Idle; }

    /// <summary>Processing that no press started (Undo of a canceled recording): presses wait it out.</summary>
    public bool BeginProcessing()
    {
        if (State != ShortcutState.Idle) return false;
        State = ShortcutState.Processing; return true;
    }

    public ShortcutAction? EventStreamLost()
    {
        var action = Cancel();
        pressedAt = null;
        ignoreRelease = false;
        return action;
    }
}
