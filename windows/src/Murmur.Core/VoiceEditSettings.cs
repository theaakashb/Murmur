// Port of app/VoiceEditSettings.swift (shortcut conflict rule).
namespace Murmur.Core;

public static class VoiceEditSettings
{
    public static string? VoiceEditConflict(string stored, EditChord chord, Func<string, EditChord?> parse, int relevantModifiers) =>
        stored == EditTrigger.SameKey ? null : EditShortcut.VoiceEditConflict(chord, parse(stored), relevantModifiers);
}
