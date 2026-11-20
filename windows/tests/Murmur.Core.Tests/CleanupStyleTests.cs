// Ports the cleanup style checks in app/check_controller.swift and app/check_ui.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupStyleTests
{
    [Fact]
    public void ExplicitStyleSelectionMapsToFlagsAndRemembersTheProvider()
    {
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
    public void LocalRoutesUseNaturalAndKeepTheSavedCloudLevel(CleanupChoice local)
    {
        var saved = CleanupOptions.Standard with { Polish = true, Professional = true, TrustModel = true };
        Assert.Equal(CleanupStyle.Natural, CleanupStyles.Current(local, saved));
        Assert.Equal(CleanupOptions.Standard, local.EffectiveOptions(saved));
        Assert.Equal(CleanupStyle.Professional, CleanupStyles.Current(CleanupChoice.MurmurCloud, saved));
        Assert.Equal(saved, CleanupChoice.CustomAPI.EffectiveOptions(saved));
    }
}
