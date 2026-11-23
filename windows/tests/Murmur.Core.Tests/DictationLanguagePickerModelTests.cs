// The pure parts of app/DictationLanguagePicker.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class DictationLanguagePickerModelTests
{
    [Fact]
    public void FlagsAreRegionsOrNullForTheGlobe()
    {
        Assert.Equal("us", DictationLanguagePickerModel.FlagRegion("en"));
        Assert.Equal("in", DictationLanguagePickerModel.FlagRegion("hi"));
        Assert.Null(DictationLanguagePickerModel.FlagRegion("haw"));
    }

    [Fact]
    public void EveryFlagRegionHasAnAssetThatIsBundled()
    {
        var app = Path.Combine(ModelTestSupport.RepoRoot(), "windows", "src", "Murmur.App");
        var project = File.ReadAllText(Path.Combine(app, "Murmur.App.csproj"));
        Assert.Contains(@"Assets\Flags\*.svg", project);
        Assert.NotEmpty(DictationLanguagePickerModel.FlagRegions);
        foreach (var region in DictationLanguagePickerModel.FlagRegions)
        {
            var file = Path.Combine(app, "Assets", "Flags", region + ".svg");
            Assert.True(File.Exists(file), $"missing flag asset {region}.svg");
            Assert.Contains("<svg", File.ReadAllText(file));
        }
        Assert.Equal(DictationLanguagePickerModel.FlagRegions.Count, Directory.GetFiles(Path.Combine(app, "Assets", "Flags"), "*.svg").Length);
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
