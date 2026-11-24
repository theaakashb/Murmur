using Murmur.Core;

namespace Murmur.Core.Tests;

public class OnboardingTextTests
{
    [Fact]
    public void OnlyAFinishedDownloadNotifiesAndOnlyWhenMurmurIsNotInFront()
    {
        Assert.True(OnboardingText.ShouldNotify(null, murmurIsForeground: false));
        Assert.False(OnboardingText.ShouldNotify(null, murmurIsForeground: true));
        Assert.False(OnboardingText.ShouldNotify(ModelDownloadError.Cancelled, false));
        Assert.False(OnboardingText.ShouldNotify(ModelDownloadError.Busy, false));
    }

    [Fact]
    public void BodiesNameTheModelAndTheNextStep()
    {
        Assert.Equal("Parakeet finished downloading. Hold Right Ctrl to start dictating.", OnboardingText.NoticeBody(ModelNoticeKind.Speech, "Parakeet", "Right Ctrl"));
        Assert.Equal("Gemma finished downloading. Cleanup is on.", OnboardingText.NoticeBody(ModelNoticeKind.Cleanup, "Gemma", "Right Ctrl"));
        Assert.Equal("Murmur is ready", OnboardingText.NoticeTitle);
        Assert.Equal("Open Murmur", OnboardingText.NoticeButton);
    }

    [Fact]
    public void ClickOpensWelcomeWhenItIsOpenElseHome()
    {
        Assert.Equal(ModelNoticeTarget.Welcome, OnboardingText.NoticeTarget(true));
        Assert.Equal(ModelNoticeTarget.Home, OnboardingText.NoticeTarget(false));
    }

    [Fact]
    public void DownloadButtonCarriesTheNameAndSize() =>
        Assert.Equal("Download Parakeet (670.6 MB)", OnboardingText.DownloadButton("Parakeet", "670.6 MB"));
}
