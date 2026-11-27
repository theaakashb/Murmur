// Port of app/Controller+Meetings.swift ("Meeting languages and model") and the meetingLanguages setting of app/Controller.swift.
namespace Murmur.Core;

/// <summary>Meetings have their own languages, separate from dictation. Until the first-time setup saves them, they follow the dictation languages.</summary>
public static class MeetingLanguages
{
    public static readonly Setting<IReadOnlyList<string>> Setting = new("meetingLanguages", []);

    /// <summary>Saved by the Meetings setup (or Settings); empty until then.</summary>
    public static IReadOnlyList<string> Saved(ISettingsStore settings) => Setting.Read(settings.Get(Setting.Key));

    /// <summary>The setup has not run yet: the Meetings screen asks for languages first.</summary>
    public static bool NeedsSetup(ISettingsStore settings) => Saved(settings).Count == 0;

    /// <summary>The saved meeting languages, or the dictation languages until they are saved.</summary>
    public static IReadOnlyList<string> Effective(ISettingsStore settings) =>
        Saved(settings) is { Count: > 0 } saved ? saved : Prefs.DictationLanguages.Read(settings.Get(Prefs.DictationLanguages.Key));

    /// <summary>The smallest on-device model that covers the languages (the same rule as dictation): English alone Parakeet, mixed languages Whisper.</summary>
    public static LocalSpeechModel Model(IEnumerable<string> codes) => DictationLanguages.Create(codes)?.LocalModel ?? LocalSpeechModel.ParakeetEnglish;

    public static LocalSpeechModel Model(ISettingsStore settings) => Model(Effective(settings));

    /// <summary>Settings → Meetings → Advanced: one switch sends both meeting transcription and notes to the Custom Provider.</summary>
    public static bool UsesCustomProvider(ISettingsStore settings) =>
        MeetingRoute.Load(settings) == MeetingTranscription.Cloud || MeetingNotesRoutes.Load(settings) == MeetingNotesRoute.CustomProvider;

    /// <summary>On: cloud transcription and Custom Provider notes. Off: this PC, and transcript-only if notes were on the Custom Provider (a Local model choice stays).</summary>
    public static void SetUsesCustomProvider(ISettingsStore settings, bool on)
    {
        settings.Set("meetingTranscription", on ? "cloud" : "local");
        if (on) settings.Set("meetingNotesRoute", MeetingNotesRoute.CustomProvider.SettingValue());
        else if (MeetingNotesRoutes.Load(settings) == MeetingNotesRoute.CustomProvider) settings.Set("meetingNotesRoute", MeetingNotesRoute.None.SettingValue());
    }
}
