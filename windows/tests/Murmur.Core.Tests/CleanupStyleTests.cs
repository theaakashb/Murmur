// Ports the cleanup style checks in app/check_controller.swift and app/check_ui.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupStyleTests
{
    [Fact]
    public void ExplicitStyleSelectionMapsToFlagsAndRemembersTheProvider()
    {
        using var _ = LocalCleanupModels.Use(LocalCleanupModels.Installed); // the shipped model has no levels on its route
        var polish = CleanupStyles.Select(CleanupStyle.Polish, CleanupChoice.LocalRules, CleanupOptions.Faithful);
        Assert.Equal(CleanupChoice.MurmurCloud, polish.Route);
        Assert.Equal(CleanupOptions.Standard with { Polish = true }, polish.Options);

        var natural = CleanupStyles.Select(CleanupStyle.Natural, CleanupChoice.CustomAPI, polish.Options);
        Assert.Equal(CleanupChoice.CustomAPI, natural.Route);
        Assert.Equal(CleanupOptions.Standard, natural.Options);

        var professional = CleanupStyles.Select(CleanupStyle.Professional, natural.Route, natural.Options);
        Assert.Equal(CleanupChoice.CustomAPI, professional.Route);
        Assert.True(professional.Options.Polish && professional.Options.Professional);

        var faithful = CleanupStyles.Select(CleanupStyle.Faithful, professional.Route, professional.Options);
        Assert.Equal(CleanupOptions.Faithful, faithful.Options);
        Assert.Equal(CleanupStyle.Faithful, CleanupStyles.Current(faithful.Route, faithful.Options));
    }

    [Theory]
    [InlineData(CleanupChoice.LocalRules)]
    [InlineData(CleanupChoice.LocalModel)]
    public void SavedModeCannotRunCloudFeaturesOnALocalRoute(CleanupChoice local)
    {
        using var _ = LocalCleanupModels.Use(LocalCleanupModels.Installed); // the shipped model has no levels on its route
        var mode = new WritingMode
        {
            Options = CleanupOptions.Standard with { Polish = true, Professional = true },
            Tone = Tone.Casual,
            Instructions = "Rewrite as an email",
            Transform = ModeTransform.Email,
            ScreenContext = true,
        };
        var context = new ScreenContext { Selected = "Fixture selection" };
        var saved = DictationStyle.ForMode(mode, CleanupOptions.Standard, false, context);
        Assert.Same(context, saved.Context);
        var effective = local.EffectiveStyle(saved);
        Assert.Equal(CleanupOptions.Standard, effective.Options);
        Assert.Equal(Tone.Casual, effective.Tone);
        Assert.Empty(effective.Instructions);
        Assert.Null(effective.Transform);
        Assert.Null(effective.Context);
        Assert.False(effective.UsesEngine);
        Assert.Same(saved, CleanupChoice.MurmurCloud.EffectiveStyle(saved));
    }

    [Theory]
    [InlineData(CleanupChoice.LocalRules)]
    [InlineData(CleanupChoice.LocalModel)]
    public void LocalRoutesUseNaturalAndKeepTheSavedCloudLevel(CleanupChoice local)
    {
        using var _ = LocalCleanupModels.Use(LocalCleanupModels.Installed); // the shipped model has no levels on its route
        var saved = CleanupOptions.Standard with { Polish = true, Professional = true, TrustModel = true };
        Assert.Equal(CleanupStyle.Natural, CleanupStyles.Current(local, saved));
        Assert.Equal(CleanupOptions.Standard, local.EffectiveOptions(saved));
        Assert.Equal(CleanupStyle.Professional, CleanupStyles.Current(CleanupChoice.MurmurCloud, saved));
        Assert.Equal(saved, CleanupChoice.CustomAPI.EffectiveOptions(saved));
    }
}
