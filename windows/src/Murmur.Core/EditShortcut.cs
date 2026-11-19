// Port of app/EditShortcut.swift and the event latch in app/Shortcut.swift.
namespace Murmur.Core;

/// <summary>Key codes and modifier bits are supplied by the keyboard hook or recorder.</summary>
public readonly record struct EditChord(int KeyCode, int Modifiers)
{
    public bool Matches(EditChord other, int relevantModifiers) =>
        KeyCode == other.KeyCode && (Modifiers & relevantModifiers) == (other.Modifiers & relevantModifiers);
}

public static class EditShortcut
{
    public static string? EditRefusal(EditChord chord, EditChord? dictation, int relevantModifiers,
        Func<EditChord, string?> shortcutRefusal, IReadOnlyDictionary<EditChord, string> reserved)
    {
        chord = chord with { Modifiers = chord.Modifiers & relevantModifiers };
        if (shortcutRefusal(chord) is { } reason) return reason;
        if (reserved.TryGetValue(chord, out var message)) return message;
        return dictation is { } binding && chord.Matches(binding, relevantModifiers)
            ? "That is your dictation shortcut. Pick another key." : null;
    }

    public static string? VoiceEditConflict(EditChord chord, EditChord? separateEdit, int relevantModifiers) =>
        separateEdit is { } binding && chord.Matches(binding, relevantModifiers)
            ? "That is your voice edit shortcut. Pick another key." : null;
}

public enum EditKeyAction { Pass, Swallow, Press, Release }

/// <summary>Drive from the keyboard hook's thread. The first press owns its repeats and release.</summary>
public sealed class EditShortcutCapture
{
    int? heldKey;

    public EditKeyAction Handle(EditChord? binding, EditChord chord, int relevantModifiers, bool down, bool isRepeat, Func<bool> shouldCapture)
    {
        if (chord.KeyCode == heldKey)
        {
            if (down) return EditKeyAction.Swallow;
            heldKey = null;
            return EditKeyAction.Release;
        }
        if (heldKey is not null || !down || isRepeat || binding is not { } edit ||
            !edit.Matches(chord, relevantModifiers) || !shouldCapture()) return EditKeyAction.Pass;
        heldKey = chord.KeyCode;
        return EditKeyAction.Press;
    }

    public void Reset() => heldKey = null;
}
