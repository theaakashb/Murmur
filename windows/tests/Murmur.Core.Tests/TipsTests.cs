// Ports app/check_tips.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class TipsTests
{
    const double Day = 1;
    static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    static Tips Fresh() => new(new InMemorySettingsStore());
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void MilestonesSeedSilentlyThenFireOnce()
    {
        var tips = Fresh();
        int seeded = 0;
        Assert.Null(tips.Record(10, 5, Start, () => { seeded++; return (30_000, 3_600, Start.AddDays(-30)); }));
        Assert.True(seeded == 1 && tips.Rung == 25_000 && tips.LifetimeWords == 30_010);
        Assert.Null(tips.HomeMilestone);
        Assert.Equal(30, tips.DaysUsed(Start));
        Assert.Null(tips.Record(5, 1, Start, () => { seeded++; return (0, 0, null); }));
        Assert.Equal(1, seeded);
        Assert.Equal(new Tips.Milestone(50_000, 3_616), tips.Record(20_000, 10, Start, () => (0, 0, null)));
        Assert.Null(tips.Record(1, 0, Start, () => (0, 0, null)));
        Assert.True(tips.Record(10, -500, Start, () => (0, 0, null)) is null && tips.LifetimeSecondsSaved >= 0);
        Assert.Equal(50_000, tips.HomeMilestone);
        tips.DismissHome();
        Assert.Null(tips.HomeMilestone);
        Assert.True(Tips.Next(50_000) == 100_000 && Tips.Next(1_000_000) is null);
        Assert.True(Tips.Duration(14 * 60) == "14 minutes" && Tips.Duration(9_000) == "2.5 hours" && Tips.Duration(3_600) == "1 hour");
        Assert.Equal(52 / Insights.TypingWPM * 60 - 10, Tips.SecondsSaved(new string('x', 5 * 52), 10), 3);
    }

    [Fact]
    public void FloatingTipCap()
    {
        var tips = Fresh();
        Assert.True(tips.CanShow(Tips.ID.VoiceEdit, Start, Utc));
        tips.Shown(Start);
        Assert.False(tips.CanShow(Tips.ID.Milestone, Start.AddSeconds(60), Utc));
        Assert.True(tips.CanShow(Tips.ID.Milestone, Start.AddDays(Day), Utc));
        tips.Dismissed(Tips.ID.VoiceEdit);
        Assert.True(tips.CanShow(Tips.ID.VoiceEdit, Start.AddDays(2 * Day), Utc));
        tips.Dismissed(Tips.ID.VoiceEdit);
        Assert.False(tips.CanShow(Tips.ID.VoiceEdit, Start.AddDays(3 * Day), Utc));
        tips.Completed(Tips.ID.Milestone);
        Assert.False(tips.CanShow(Tips.ID.Milestone, Start.AddDays(3 * Day), Utc));
        tips.SetEnabled(false);
        Assert.False(tips.CanShow(Tips.ID.Cues, Start.AddDays(4 * Day), Utc));
        tips.Reset();
        Assert.True(tips.CanShow(Tips.ID.VoiceEdit, Start.AddDays(4 * Day), Utc) && tips.Enabled);
    }

    [Fact]
    public void VoiceEditSignal()
    {
        Assert.True(Tips.IsRewrite("the launch is on friday this week", "The launch moved to Monday next week."));
        Assert.False(Tips.IsRewrite("the launch is on friday this week", "the launch is on Friday this week"));
        Assert.False(Tips.IsRewrite("ok sure", "no"));
        var tips = Fresh();
        Assert.False(tips.NoteRewrite(Start));
        Assert.False(tips.NoteRewrite(Start.AddDays(8)));
        Assert.True(tips.NoteRewrite(Start.AddDays(9)));
    }

    [Fact]
    public void DotsAreEarnedCappedAndClearedOnOpen()
    {
        var tips = Fresh();
        Assert.Empty(tips.Dots(new(3, 1, 0, 0)));
        var busy = new Tips.Usage(40, 3, 2, 9);
        Assert.Equal([Tips.Pane.Modes, Tips.Pane.Dictionary], tips.Dots(busy).OrderBy(p => (int)p));
        tips.Opened(Tips.Pane.Modes);
        Assert.Equal([Tips.Pane.Dictionary, Tips.Pane.Cues], tips.Dots(busy).OrderBy(p => (int)p));
        Assert.Contains(Tips.Pane.Modes, tips.Eligible(busy));
        tips.Completed(Tips.ID.Modes);
        Assert.DoesNotContain(Tips.Pane.Modes, tips.Eligible(busy));
        tips.SetEnabled(false);
        Assert.True(tips.Dots(busy).Count == 0 && tips.Eligible(busy).Count == 0);
    }
}
