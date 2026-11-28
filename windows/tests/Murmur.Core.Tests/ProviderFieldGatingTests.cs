using Murmur.Core;

namespace Murmur.Core.Tests;

public class ProviderFieldGatingTests
{
    [Theory]
    [InlineData(TranscriptionRoute.Local, false)]
    [InlineData(TranscriptionRoute.Cloud, true)]
    public void TranscriptionFieldsShowOnlyForTheCustomProviderRoute(TranscriptionRoute route, bool shown) =>
        Assert.Equal(shown, ProviderFieldGating.ShowTranscriptionFields(route));

    [Fact]
    public void CleanupFieldsShowOnlyForCustomApiAndNeverForFaithful()
    {
        foreach (var route in Enum.GetValues<CleanupChoice>())
            foreach (var style in Enum.GetValues<CleanupStyle>())
                Assert.Equal(route == CleanupChoice.CustomAPI && style != CleanupStyle.Faithful, ProviderFieldGating.ShowCleanupFields(route, style));
    }

    [Fact]
    public void FaithfulHidesTheCleanupRoute()
    {
        Assert.False(ProviderFieldGating.ShowCleanupRoute(CleanupStyle.Faithful));
        foreach (var style in new[] { CleanupStyle.Natural, CleanupStyle.Polish, CleanupStyle.Professional })
            Assert.True(ProviderFieldGating.ShowCleanupRoute(style));
    }
}
