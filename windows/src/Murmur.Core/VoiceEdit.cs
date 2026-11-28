// Port of app/VoiceEdit.swift.
namespace Murmur.Core;

/// <summary>A readable selection captured by the app. Element identity must be stable across captures of the same field.</summary>
public sealed record VoiceEditTarget(int ProcessId, string ElementId, string? Value, (int Location, int Length)? Range,
    string? Selection, bool ReadableText, bool Secure)
{
    public bool Matches(VoiceEditTarget current) =>
        !Secure && !current.Secure && ReadableText && current.ReadableText &&
        ProcessId == current.ProcessId && ElementId == current.ElementId &&
        Value == current.Value && Range == current.Range && Selection == current.Selection;
}

public interface IVoiceEditTarget
{
    VoiceEditTarget? Capture();
    /// <summary>Recheck the expected field and selection immediately before insertion. Fail closed if they cannot be verified.</summary>
    bool TryReplace(VoiceEditTarget expected, string text);
}

public sealed record VoiceEditSession(string EngineName, string Destination, bool OnDevice, VoiceEditTarget Target,
    string Selection, Func<string, string, CancellationToken, Task<string>> Run)
{
    public Guid Generation { get; init; } = Guid.NewGuid();
    public string ProcessingLabel => OnDevice ? "Editing on this PC…" : $"Editing with {EngineName} ({Destination})…";
    public string CompletedLabel => $"Edited with {EngineName}. Ctrl+Z in the app undoes it.";
}

public static class VoiceEdit
{
    public abstract record SessionResult
    {
        public sealed record Ready(VoiceEditSession Session) : SessionResult;
        public sealed record Refused(string Message, bool Dictates = true) : SessionResult;
    }

    public static bool VoiceEditReady(CleanupChoice cleanup, RouteSettings routes) =>
        AIEngines.Derive(cleanup) is { } engine && engine.UnavailableReason(routes) is null;

    public static SessionResult? VoiceEditForDictationKey(string shortcut, Func<SessionResult> session)
    {
        if (shortcut != EditTrigger.SameKey) return null;
        var result = session();
        return result is SessionResult.Refused { Dictates: true } ? null : result;
    }

    public static SessionResult CreateSession(bool secureInput, CleanupChoice cleanup, RouteSettings routes, Func<ProviderID, APIKey?> key,
        IVoiceEditTarget target, int ownProcessId)
    {
        if (secureInput) return new SessionResult.Refused("Voice edit is unavailable while Secure Input is active.");
        if (AIEngines.Derive(cleanup) is not { } engine)
            return new SessionResult.Refused("Voice edit needs a Custom Provider.");
        if (engine.UnavailableReason(routes) is { } reason)
            return new SessionResult.Refused(reason);
        var focus = target.Capture();
        if (focus is null) return new SessionResult.Refused("Select some text first.");
        if (focus.Secure) return new SessionResult.Refused("Voice edit does not work in password fields.");
        if (focus.ProcessId == ownProcessId) return new SessionResult.Refused("Voice edit works on text in other apps.");
        if (!focus.ReadableText || focus.Selection is not { } selection || !EditTrigger.CapturesKey(false, selection))
            return new SessionResult.Refused("Select some text first. Murmur could not read a selection in this app.");

        const string name = "your Custom Provider";
        if (SelectionRefusal(selection, name, onDevice: false) is { } failure) return failure;
        if (engine.Provider(routes, key) is not { } provider)
            return new SessionResult.Refused("Voice edit is not set up. Check AI features in Settings.");
        return new SessionResult.Ready(new(name, provider.Disclosure.DestinationHost, false, focus, selection,
            (text, instruction, token) => provider.CleanupAsync(EditPrompt.Build(text, instruction), token)));
    }

    public static SessionResult.Refused? SelectionRefusal(string selection, string engineName, bool onDevice)
    {
        var cap = onDevice ? EditPrompt.AppleSelectionLimit : EditPrompt.RemoteSelectionLimit;
        return SwiftText.Count(selection) > cap
            ? new SessionResult.Refused($"The selection is too long for {engineName} ({cap} characters at most). Select less.", Dictates: false)
            : null;
    }

    public static (string Instruction, string? Error) CheckInstruction(string instruction)
    {
        var words = SwiftText.TrimWhitespaceAndNewlines(instruction);
        if (words.Length == 0) return (words, "No instruction was heard. The selection was not changed.");
        if (SwiftText.Count(words) > EditPrompt.InstructionLimit)
            return (words, "That instruction is too long. The selection was not changed.");
        return (words, null);
    }

    public static ShortcutAction? PressSeparate(ShortcutMachine machine, bool sessionAvailable, bool isBusy, bool editing)
    {
        if (!sessionAvailable) return null;
        if (editing && machine.State == ShortcutState.Listening) return machine.Finish();
        return !isBusy && machine.State == ShortcutState.Idle ? machine.StartToggled() : null;
    }

    public static ShortcutAction? ReleaseSeparate(ShortcutMachine machine, bool editing, double heldSeconds) =>
        editing && machine.State == ShortcutState.Listening && heldSeconds >= machine.HoldThreshold ? machine.Finish() : null;

    public static bool ReplaceVoiceEditSelection(VoiceEditSession edit, IVoiceEditTarget target, string text) =>
        target.Capture() is { } current && edit.Target.Matches(current) && target.TryReplace(edit.Target, text);
}
