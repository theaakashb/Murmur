// Windows only (the Mac has Apple Intelligence as a local engine, so a rewrite mode always has one there).
namespace Murmur.Core;

/// <summary>What to tell the user after a dictation used a rewrite mode (Prompt, Email, Notes) that did not rewrite. Without this a spoken
/// "Prompt mode" on a local route strips the trigger and pastes the plain text, and nothing shows that it was heard.</summary>
public static class ModeFeedback
{
    public static string? Notice(WritingMode? mode, bool hasEngine, CleanupFailureReason? fallback)
    {
        if (mode?.Transform is null) return null;
        if (!hasEngine) return $"{mode.Name} needs Murmur Cloud or a Custom Provider to rewrite (Settings → Providers). Pasted as dictated.";
        return fallback is null ? null : $"{mode.Name} could not rewrite this dictation. Pasted as cleaned.";
    }
}
