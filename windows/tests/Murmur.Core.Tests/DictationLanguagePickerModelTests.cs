// The pure parts of app/DictationLanguagePicker.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class DictationLanguagePickerModelTests
{
    [Fact]
    public void FlagsAreRegionalIndicatorsOrAGlobe()
    {
        Assert.Equal("\U0001F1FA\U0001F1F8", DictationLanguagePickerModel.Flag("en"));
        Assert.Equal("\U0001F1EE\U0001F1F3", DictationLanguagePickerModel.Flag("hi"));
        Assert.Equal("\U0001F310", DictationLanguagePickerModel.Flag("haw"));
    }

    [Fact]
    public void EmptySearchListsSuggestedFirstWithoutDuplicates()
    {
        var results = DictationLanguagePickerModel.Results("  ", ["ar", "de", "en", "zh"]);
        Assert.Equal(["en", "es", "fr", "hi", "ar", "de", "zh"], results);
        Assert.Equal(["zh", "de"], DictationLanguagePickerModel.Results("z", ["zh", "de"]));
    }

    [Fact]
    public void LastLanguageCannotBeRemoved()
    {
        Assert.Equal(["en", "es"], DictationLanguagePickerModel.Toggle(["en"], "es", true));
        Assert.Equal(["en"], DictationLanguagePickerModel.Toggle(["en", "es"], "es", false));
        Assert.Equal(["en"], DictationLanguagePickerModel.Toggle(["en"], "en", false));
        Assert.Equal(["en"], DictationLanguagePickerModel.Toggle(["en"], "en", true));
        Assert.True(DictationLanguagePickerModel.IsLocked(["en"], "en"));
        Assert.False(DictationLanguagePickerModel.IsLocked(["en", "es"], "en"));
    }
}
