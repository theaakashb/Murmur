using System.Globalization;
using Murmur.Core;
using Xunit;

namespace Murmur.Core.Tests;

public class DictationLanguagesTests
{
    // Core tests run in invariant-globalization mode, where only code matching is available.
    static bool HasCultureData
    {
        get
        {
            try { CultureInfo.GetCultureInfo("fr"); return true; }
            catch (CultureNotFoundException) { return false; }
        }
    }

    [Fact]
    public void CreateRejectsEmptyAndUnsupported()
    {
        Assert.Null(DictationLanguages.Create(Array.Empty<string>()));
        Assert.Null(DictationLanguages.Create(new[] { "en", "xx" }));
        Assert.NotNull(DictationLanguages.Create(new[] { "en", "fr" }));
    }

    [Fact]
    public void ModelChoice()
    {
        Assert.Equal(LocalSpeechModel.ParakeetEnglish, DictationLanguages.English.LocalModel);
        Assert.Equal(LocalSpeechModel.ParakeetTDT, DictationLanguages.Create(new[] { "en", "fr" })!.LocalModel);
        Assert.Equal(LocalSpeechModel.ParakeetTDT, DictationLanguages.Create(new[] { "fr" })!.LocalModel);
        Assert.Equal(LocalSpeechModel.WhisperTurbo, DictationLanguages.Create(new[] { "fr", "ja" })!.LocalModel);
    }

    [Fact]
    public void CloudHintOnlyForEnglish()
    {
        Assert.Equal("en", DictationLanguages.English.CloudHint);
        Assert.Null(DictationLanguages.Create(new[] { "en", "fr" })!.CloudHint);
        Assert.Null(DictationLanguages.Create(new[] { "fr" })!.CloudHint);
    }

    [Fact]
    public void EqualityIsBySet()
    {
        Assert.Equal(DictationLanguages.Create(new[] { "fr", "en" }), DictationLanguages.Create(new[] { "en", "fr" }));
        Assert.NotEqual(DictationLanguages.English, DictationLanguages.Create(new[] { "fr" }));
    }

    [Fact]
    public void AvailableIsUnion()
    {
        Assert.True(DictationLanguages.Parakeet.IsSubsetOf(DictationLanguages.AvailableCodes));
        Assert.True(DictationLanguages.Whisper.IsSubsetOf(DictationLanguages.AvailableCodes));
        Assert.Equal(DictationLanguages.Parakeet.Union(DictationLanguages.Whisper).Count(), DictationLanguages.AvailableCodes.Count);
    }

    [Fact]
    public void MatchingByCodeNameAndNativeName()
    {
        var en = CultureInfo.InvariantCulture;
        Assert.Contains("fr", DictationLanguages.Matching("FR", en));
        if (HasCultureData)
        {
            Assert.Contains("fr", DictationLanguages.Matching("FRENCH", en));
            Assert.Contains("fr", DictationLanguages.Matching("français", en));
            Assert.Contains("fr", DictationLanguages.Matching("francais", en));
        }
        Assert.Contains("de", DictationLanguages.Matching("de", en));
        Assert.Equal(DictationLanguages.AvailableCodes.Count, DictationLanguages.Matching("  ", en).Count);
        Assert.Empty(DictationLanguages.Matching("zzzzqq", en));
    }

    [Fact]
    public void MatchingIsSortedByNameThenCode()
    {
        var en = CultureInfo.InvariantCulture;
        var all = DictationLanguages.Matching("", en);
        Assert.Equal(DictationLanguages.AvailableCodes.Count, all.Count);
        if (HasCultureData) Assert.Equal("af", all[0]);
        else Assert.Equal(all.OrderBy(c => c, StringComparer.Ordinal), all);
    }
}
