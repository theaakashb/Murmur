using Murmur.Core;

namespace Murmur.Core.Tests;

public class MeetingLanguagesTests
{
    [Fact]
    public void FollowsDictationUntilSaved()
    {
        var s = new InMemorySettingsStore();
        s.Set("dictationLanguages", new[] { "fr" });
        Assert.True(MeetingLanguages.NeedsSetup(s));
        Assert.Equal(["fr"], MeetingLanguages.Effective(s));
        s.Set(MeetingLanguages.Setting.Key, new[] { "en" });
        Assert.False(MeetingLanguages.NeedsSetup(s));
        Assert.Equal(["en"], MeetingLanguages.Effective(s));
    }

    [Fact]
    public void ModelCoversEveryLanguage()
    {
        Assert.Equal(LocalSpeechModel.ParakeetEnglish, MeetingLanguages.Model(["en"]));
        Assert.Equal(LocalSpeechModel.ParakeetTDT, MeetingLanguages.Model(["en", "fr"]));
        Assert.Equal(LocalSpeechModel.WhisperTurbo, MeetingLanguages.Model(["en", "hi"]));
    }

    [Fact]
    public void CustomProviderSwitchSetsBothAndKeepsLocalNotes()
    {
        var s = new InMemorySettingsStore();
        Assert.False(MeetingLanguages.UsesCustomProvider(s));
        MeetingLanguages.SetUsesCustomProvider(s, true);
        Assert.Equal(MeetingTranscription.Cloud, MeetingRoute.Load(s));
        Assert.Equal(MeetingNotesRoute.CustomProvider, MeetingNotesRoutes.Load(s));
        MeetingLanguages.SetUsesCustomProvider(s, false);
        Assert.Equal(MeetingTranscription.Local, MeetingRoute.Load(s));
        Assert.Equal(MeetingNotesRoute.None, MeetingNotesRoutes.Load(s));
        s.Set("meetingTranscription", "cloud"); s.Set("meetingNotesRoute", MeetingNotesRoute.LocalModel.SettingValue());
        MeetingLanguages.SetUsesCustomProvider(s, false);
        Assert.Equal(MeetingNotesRoute.LocalModel, MeetingNotesRoutes.Load(s));
    }
}
