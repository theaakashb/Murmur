namespace Murmur.Core;

/// <summary>
/// When each route picker shows its Custom Provider fields, as the Mac does (SettingsPaneViews.swift).
/// Transcription: Local shows a help line only, Custom Provider (Cloud) shows the URL and model.
/// Cleanup: the fields show only on the Custom API route and never in the Faithful style (which has no cleanup route at all).
/// Meeting notes and meeting transcription pickers, and voice edit, have no provider fields.
/// </summary>
public static class ProviderFieldGating
{
    public static bool ShowTranscriptionFields(TranscriptionRoute route) => route == TranscriptionRoute.Cloud;

    public static bool ShowCleanupFields(CleanupChoice route, CleanupStyle style) =>
        style != CleanupStyle.Faithful && route == CleanupChoice.CustomAPI;

    /// <summary>The cleanup route row (and everything under it) is hidden for Faithful.</summary>
    public static bool ShowCleanupRoute(CleanupStyle style) => style != CleanupStyle.Faithful;
}
