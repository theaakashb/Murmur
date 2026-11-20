// Ports checkWelcomeGuide in app/check_app_services.swift. Windows has no Accessibility or Input Monitoring step: a saved one resumes at Transcription.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class WelcomeGuideTests
{
    static (WelcomeGuide Guide, InMemorySettingsStore Store) Make() { var store = new InMemorySettingsStore(); return (new(store), store); }

    [Fact]
    public void NewInstallOpensAtWelcomeAndPhasesAreOrdered()
    {
        var (guide, _) = Make();
        Assert.True(guide.OpensAtLaunch(false) && guide.ResumeStep() == WelcomeGuide.Step.Welcome);
        var phases = WelcomeGuide.Steps().Select(WelcomeGuide.PhaseFor).Aggregate(new List<WelcomeGuide.Phase>(), (l, p) => { if (l.Count == 0 || l[^1] != p) l.Add(p); return l; });
        Assert.Equal([WelcomeGuide.Phase.Welcome, WelcomeGuide.Phase.GetReady, WelcomeGuide.Phase.FirstWords], phases);
    }

    [Fact]
    public void SavedStepsResumeAtAStepThatStillExists()
    {
        var (guide, store) = Make();
        store.Set(WelcomeGuide.StepKey, (long)WelcomeGuide.Step.Meetings);
        Assert.Equal(WelcomeGuide.Step.Model, guide.ResumeStep());
        store.Set(WelcomeGuide.StepKey, 0L);
        Assert.Equal(WelcomeGuide.Step.Microphone, guide.ResumeStep());
        store.Set(WelcomeGuide.StepKey, 3L);
        Assert.Equal(WelcomeGuide.Step.Model, guide.ResumeStep());
        store.Set(WelcomeGuide.StepKey, 4L);
        Assert.Equal(WelcomeGuide.Step.TryIt, guide.ResumeStep());
        foreach (var step in WelcomeGuide.Steps(missingRightCtrl: true))
        {
            guide.Save(step);
            Assert.Equal(step, guide.ResumeStep(missingRightCtrl: true));
        }
        store.Set(WelcomeGuide.StepKey, 42L);
        Assert.Equal(WelcomeGuide.Step.TryIt, guide.ResumeStep());
        store.Set(WelcomeGuide.StepKey, -3L);
        Assert.Equal(WelcomeGuide.Step.Welcome, guide.ResumeStep());
        guide.Save(WelcomeGuide.Step.InputMonitoring);
        Assert.Equal(WelcomeGuide.Step.Transcription, guide.ResumeStep());
        Assert.Equal(WelcomeGuide.Step.RecordShortcut, guide.ResumeStep(missingRightCtrl: true));
        guide.Save(WelcomeGuide.Step.RecordShortcut);
        Assert.Equal(WelcomeGuide.Step.Transcription, guide.ResumeStep());
    }

    [Fact]
    public void RouteReviewCannotBeBypassed()
    {
        var (guide, _) = Make();
        Assert.Equal(WelcomeGuide.Step.Transcription, guide.NextUnconfirmed());
        guide.Confirm(WelcomeGuide.Step.Transcription);
        Assert.Equal(WelcomeGuide.Step.Cleanup, guide.NextUnconfirmed());
        guide.Confirm(WelcomeGuide.Step.Cleanup);
        guide.Confirm(WelcomeGuide.Step.Meetings);
        Assert.True(guide.NextUnconfirmed() is null && !guide.IsConfirmed(WelcomeGuide.Step.Meetings));
        guide.Save(WelcomeGuide.Step.Model);
        Assert.Equal(WelcomeGuide.Step.Model, guide.ResumeStep());
        Assert.True(guide.OpensAtLaunch(true));
    }

    [Fact]
    public void CompletionReplayAndUpgrade()
    {
        var (guide, store) = Make();
        guide.Complete();
        Assert.True(!guide.OpensAtLaunch(false) && Convert.ToInt32(store.Get(WelcomeGuide.CompletedKey)) == WelcomeGuide.Version);
        Assert.Equal(WelcomeGuide.Step.Welcome, guide.ResumeStep());
        store.Set(WelcomeGuide.CompletedKey, (long)(WelcomeGuide.Version - 1));
        Assert.True(guide.OpensAtLaunch(true));
        guide.Complete();
        guide.Save(WelcomeGuide.Step.InputMonitoring);
        guide.Replay();
        Assert.True(guide.ResumeStep() == WelcomeGuide.Step.Welcome && !guide.OpensAtLaunch(false));
        Assert.Equal(WelcomeGuide.Step.Transcription, guide.NextUnconfirmed());

        var (upgraded, _) = Make();
        Assert.False(upgraded.OpensAtLaunch(true));
        upgraded.Save(WelcomeGuide.Step.Microphone);
        Assert.False(upgraded.OpensAtLaunch(true));
        var (fresh, _) = Make();
        Assert.True(fresh.OpensAtLaunch(false));
    }
}
