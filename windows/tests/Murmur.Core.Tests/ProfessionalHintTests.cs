// Ports app/check_professional_hint.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ProfessionalHintTests
{
    static readonly string Filler = string.Concat(Enumerable.Repeat("and then we talked about the plan ", 6)); // 42 words, no sentence end
    static readonly string RunOn = Filler + "no I mean the other plan";
    static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    const double Day = 1;

    static void Run(ProfessionalHintState s, int n, double days, bool enabled = true, bool active = false)
    {
        for (int i = 0; i < n; i++) s.Consider($"e{i}", RunOn, Start.AddDays(days), enabled, active);
    }

    [Fact]
    public void Signals()
    {
        Assert.Equal([ProfessionalSignal.Reason.RunOn, ProfessionalSignal.Reason.Correction], ProfessionalSignal.Reasons(RunOn));
        Assert.Empty(ProfessionalSignal.Reasons("Let's meet Thursday at the cafe."));
        Assert.Empty(ProfessionalSignal.Reasons(Filler));
        Assert.Contains(ProfessionalSignal.Reason.RunOn, ProfessionalSignal.Reasons(Filler + Filler));
        Assert.Empty(ProfessionalSignal.Reasons("First we ship. Second we test. Finally we write up."));
        Assert.Equal([ProfessionalSignal.Reason.Correction, ProfessionalSignal.Reason.List], ProfessionalSignal.Reasons("First we ship. Second we test. Scratch that, we write up."));
        Assert.Equal([ProfessionalSignal.Reason.Correction, ProfessionalSignal.Reason.Fillers], ProfessionalSignal.Reasons("um so uh the thing um is ready no wait it is not"));
        Assert.Equal("Professional would break this long run-on into clear sentences and keep only what you meant after your correction.",
            ProfessionalSignal.Sentence([ProfessionalSignal.Reason.RunOn, ProfessionalSignal.Reason.Correction]));
    }

    [Fact]
    public void OfferGate()
    {
        var s = new ProfessionalHintState();
        Run(s, 12, 0);
        Assert.True(s.OfferID is null && s.Dictations == 12);
        Run(s, 1, 3 * Day);
        Assert.True(s.OfferID == "e0" && s.LastOffered is not null);
        var open = s with { };
        Run(s, 3, 4 * Day);
        Assert.True(s.OfferID == open.OfferID && s.LastOffered == open.LastOffered);
        s.OfferID = null; Run(s, 1, 5 * Day);
        Assert.Null(s.OfferID);
        Run(s, 1, 11 * Day);
        Assert.NotNull(s.OfferID);
        s.OfferID = null; s.Dismissals = 2; Run(s, 1, 30 * Day);
        Assert.Null(s.OfferID);
    }

    [Fact]
    public void SwitchesAndActing()
    {
        var off = new ProfessionalHintState { Dictations = 20, FirstSeen = Start };
        Run(off, 1, 9 * Day, enabled: false);
        Assert.Null(off.OfferID);
        Run(off, 1, 9 * Day, active: true);
        Assert.Null(off.OfferID);
        var acted = new ProfessionalHintState { Dictations = 20, FirstSeen = Start, Acted = true };
        Run(acted, 1, 9 * Day);
        Assert.Null(acted.OfferID);
    }

    [Fact]
    public void StatePersistsOnlyCountersAndChoices()
    {
        var store = new InMemorySettingsStore();
        var hint = new ProfessionalHint(store);
        for (int i = 0; i < 12; i++) hint.Consider($"e{i}", RunOn, Start, false);
        hint.Consider("offer", RunOn, Start.AddDays(3), false);
        Assert.Equal("offer", hint.State.OfferID);
        hint.Dismiss();
        Assert.True(hint.State.OfferID is null && hint.State.Dismissals == 1);
        hint.Finish();
        Assert.True(hint.State.Acted);
        hint.SetEnabled(false);
        Assert.False(hint.Enabled);
    }
}
