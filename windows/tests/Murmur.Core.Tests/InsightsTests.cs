// Ports app/check_insights.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class InsightsTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_792_065_600); // noon UTC on 2026-10-15
    static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
    static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 0.001;

    static InsightsItem Item(double daysAgo, string raw = "hello there", string? final = null, double? seconds = null, string? app = null, bool expanded = false) =>
        new(Now.AddSeconds(-daysAgo * 86400), raw, final ?? raw, seconds, app, expanded);

    static Insights.Stats? Stats(IEnumerable<InsightsItem> items, TimeZoneInfo? zone = null, DateTimeOffset? at = null) =>
        Insights.Compute(items, true, at ?? Now, zone ?? Utc) as Insights.Stats;

    [Fact]
    public void HistoryOffOrEmptyIsSaidNotZeroed()
    {
        Assert.IsType<Insights.HistoryOff>(Insights.Compute([Item(0)], false, Now, Utc));
        Assert.IsType<Insights.Empty>(Insights.Compute([], true, Now, Utc));
        Assert.IsType<Insights.Empty>(Insights.Compute([Item(0, "um", "")], true, Now, Utc));
    }

    [Fact]
    public void PaceAndTimeSaved()
    {
        var paced = Stats([Item(0, "abcd abcd abcd abcd abcd abcd abcd abcd abcd abcde", seconds: 6)]);
        Assert.True(Near(paced?.WordsPerMinute, 100));
        Assert.True(Near(paced?.SecondsSaved, 5.637));
        Assert.True(paced?.Words == 10 && paced?.TimedDictations == 1);
        var mixed = Stats([Item(0, new string('x', 50), seconds: 6), Item(0, new string('x', 50), seconds: 18)]);
        Assert.True(Near(mixed?.WordsPerMinute, 50));
        Assert.True(Near(Stats([Item(0, "short", seconds: 60)])?.SecondsSaved, 0));
        var untimed = Stats([Item(0, "one two three")]);
        Assert.True(untimed?.WordsPerMinute is null && untimed!.SecondsSaved is null && untimed.Words == 3 && untimed.TimedDictations == 0);
        Assert.True(Near(mixed?.RecordingSeconds, 24));
        Assert.Null(untimed?.RecordingSeconds);
        Assert.True(Insights.Duration(40) == "40 s" && Insights.Duration(720) == "12 min" && Insights.Duration(3900) == "1 h 5 min");
    }

    [Fact]
    public void WordsChanged()
    {
        Assert.Equal(3, Insights.ChangedWords("um so the meeting is at three", "So the meeting is at 3."));
        Assert.Equal(0, Insights.ChangedWords("same words", " same words"));
        Assert.Equal(2, Insights.ChangedWords("ship it to cube control", "ship it to kubectl"));
        Assert.Equal(2, Stats([Item(0, "um hello", "Hello")])?.WordsChanged);
        var expanded = new InsightsItem(Now, "insert my link here", "https://example.com/a/b", null, null, true);
        Assert.True(Stats([expanded])?.WordsChanged == 0 && Stats([expanded])?.Words == 1);
    }

    [Fact]
    public void AppsDailyAndHourly()
    {
        var apps = Stats([Item(0, app: "com.apple.TextEdit"), Item(0, app: "com.apple.mail"), Item(1, app: "com.apple.mail"), Item(1)])!;
        Assert.Equal([new Insights.AppCount("com.apple.mail", 2, 4), new Insights.AppCount("com.apple.TextEdit", 1, 2)], apps.Apps);
        Assert.Equal(1, apps.WithoutApp);
        var activity = Stats([Item(0, "one two three", seconds: 8, app: "a"), Item(0, "one", seconds: double.PositiveInfinity, app: "b"), Item(1, "one two", seconds: -1, app: "b")])!;
        Assert.True(activity.DailyWords[DateOnly.FromDateTime(Now.UtcDateTime)] == 4 && activity.DailyWords.Count == 2);
        Assert.True(activity.HourlyDictations.Sum() == 3 && activity.HourlyDictations[Now.UtcDateTime.Hour] == 3);
        Assert.True(Near(activity.AverageWords, 2) && Near(activity.RecordingSeconds, 8));
        Assert.Equal(["a", "b"], activity.Apps.Select(a => a.App));
        var byWords = Stats([Item(0, "one two three four", app: "a"), Item(0, "one", app: "b"), Item(1, "two", app: "b")])!;
        Assert.Equal("a", byWords.Apps[0].App);
        var tokyo = Zone("Asia/Tokyo");
        Assert.Equal(1, Stats([Item(0)], tokyo)?.HourlyDictations[TimeZoneInfo.ConvertTime(Now, tokyo).Hour]);
    }

    [Fact]
    public void Streaks()
    {
        Assert.Equal((3, 3), Pair(Stats([Item(0), Item(1), Item(2), Item(5), Item(6)])!));
        Assert.Equal(2, Stats([Item(1), Item(2)])?.CurrentStreak);
        Assert.Equal((0, 4), Pair(Stats([Item(2), Item(3), Item(4), Item(5)])!));
        var one = Stats([Item(0), Item(0.1), Item(0.2)])!;
        Assert.True(one.Days.Count == 1 && one.CurrentStreak == 1);
    }

    static (int, int) Pair(Insights.Stats s) => (s.CurrentStreak, s.LongestStreak);

    [Fact]
    public void StreaksFollowTheTimeZone()
    {
        // 23:30 UTC on Oct 14 and 00:30 UTC on Oct 15: two days in UTC, one in New York.
        var late = DateTimeOffset.FromUnixTimeSeconds(1_792_020_600);
        var early = DateTimeOffset.FromUnixTimeSeconds(1_792_024_200);
        InsightsItem[] pair = [new(late, "a", "a", null, null), new(early, "a", "a", null, null)];
        Assert.Equal((2, 2), (Stats(pair)!.Days.Count, Stats(pair)!.LongestStreak));
        var ny = Stats(pair, Zone("America/New_York"))!;
        Assert.Equal((1, 1), (ny.Days.Count, ny.LongestStreak));
        Assert.Equal(1, Stats(pair, Zone("Asia/Tokyo"))?.CurrentStreak);
    }

    [Fact]
    public void StreakSurvivesDaylightSavingChange()
    {
        var ny = Zone("America/New_York");
        DateTimeOffset At(int month, int day, int hour) => new(new DateTime(2026, month, day, hour, 0, 0), ny.GetUtcOffset(new DateTime(2026, month, day, hour, 0, 0)));
        // Oct 30, Oct 31, Nov 1, Nov 2 (Nov 1 has 25 hours in New York).
        InsightsItem[] items = [new(At(10, 30, 9), "a", "a", null, null), new(At(10, 31, 9), "a", "a", null, null), new(At(11, 1, 9), "a", "a", null, null), new(At(11, 2, 9), "a", "a", null, null)];
        var s = Stats(items, ny, At(11, 2, 18))!;
        Assert.Equal((4, 4), Pair(s));
    }
}
