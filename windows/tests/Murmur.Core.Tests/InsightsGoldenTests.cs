// Compares Insights with golden/insights.json, which windows/golden/golden_insights.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class InsightsGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("insights");
    public static IEnumerable<object[]> Scenarios => GoldenFiles.Cases(Root, "scenarios", c => c.GetProperty("label").GetString()!);
    public static IEnumerable<object[]> Changed => GoldenFiles.Cases(Root, "changed", c => c.GetProperty("raw").GetString()!);
    public static IEnumerable<object[]> Durations => GoldenFiles.Cases(Root, "durations", c => c.GetProperty("seconds").ToString());

    static double? Number(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
        : v.ValueKind == JsonValueKind.String && v.GetString() == "inf" ? double.PositiveInfinity : null;

    [Theory, MemberData(nameof(Scenarios))]
    public void ComputeMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("scenarios")[i];
        var items = c.GetProperty("items").EnumerateArray().Select(x => new InsightsItem(
            DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(x.GetProperty("recordedAt").GetDouble() * 1000)), x.GetProperty("raw").GetString()!, x.GetProperty("final").GetString()!,
            Number(x, "seconds"), GoldenFiles.Optional(x, "app"), x.GetProperty("expanded").GetBoolean())).ToList();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(c.GetProperty("zone").GetString()!);
        var result = Insights.Compute(items, c.GetProperty("historyOn").GetBoolean(), DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(c.GetProperty("now").GetDouble() * 1000)), zone);
        switch (c.GetProperty("result").GetString())
        {
            case "historyOff": Assert.IsType<Insights.HistoryOff>(result); return;
            case "empty": Assert.IsType<Insights.Empty>(result); return;
        }
        var s = Assert.IsType<Insights.Stats>(result);
        var e = c.GetProperty("stats");
        Assert.Equal(e.GetProperty("dictations").GetInt32(), s.Dictations);
        Assert.Equal(e.GetProperty("words").GetInt32(), s.Words);
        Assert.Equal(e.GetProperty("wordsChanged").GetInt32(), s.WordsChanged);
        Assert.Equal(e.GetProperty("timedDictations").GetInt32(), s.TimedDictations);
        Assert.Equal(e.GetProperty("withoutApp").GetInt32(), s.WithoutApp);
        Assert.Equal(e.GetProperty("currentStreak").GetInt32(), s.CurrentStreak);
        Assert.Equal(e.GetProperty("longestStreak").GetInt32(), s.LongestStreak);
        Assert.Equal(e.GetProperty("dayCount").GetInt32(), s.Days.Count);
        Assert.Equal(e.GetProperty("averageWords").GetDouble(), s.AverageWords, 9);
        foreach (var (name, actual) in new[] { ("wordsPerMinute", s.WordsPerMinute), ("secondsSaved", s.SecondsSaved), ("recordingSeconds", s.RecordingSeconds) })
        {
            if (Number(e, name) is { } want) Assert.Equal(want, actual!.Value, 9); else Assert.Null(actual);
        }
        Assert.Equal(e.GetProperty("hourly").EnumerateArray().Select(h => h.GetInt32()), s.HourlyDictations);
        Assert.Equal(e.GetProperty("daily").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetInt32()}"),
            s.DailyWords.OrderBy(p => p.Key).Select(p => $"{p.Key:yyyy-MM-dd}={p.Value}"));
        Assert.Equal(e.GetProperty("apps").EnumerateArray().Select(a => $"{a.GetProperty("app").GetString()}|{a.GetProperty("count").GetInt32()}|{a.GetProperty("words").GetInt32()}"),
            s.Apps.Select(a => $"{a.App}|{a.Count}|{a.Words}"));
    }

    [Theory, MemberData(nameof(Changed))]
    public void ChangedWordsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("changed")[i];
        Assert.Equal(c.GetProperty("count").GetInt32(), Insights.ChangedWords(c.GetProperty("raw").GetString()!, c.GetProperty("final").GetString()!));
    }

    [Theory, MemberData(nameof(Durations))]
    public void DurationText(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("durations")[i];
        Assert.Equal(c.GetProperty("text").GetString(), Insights.Duration(c.GetProperty("seconds").GetDouble()));
    }
}
