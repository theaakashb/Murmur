// Compares the pure functions in Tips with golden/tips.json (windows/golden/golden_tips.swift) and its rungs with shared/tips.json.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class TipsGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("tips");
    public static IEnumerable<object[]> Durations => GoldenFiles.Cases(Root, "durations", c => c.GetProperty("seconds").ToString());
    public static IEnumerable<object[]> Saved => GoldenFiles.Cases(Root, "saved", c => $"{c.GetProperty("final").GetString()} {c.GetProperty("seconds")}");
    public static IEnumerable<object[]> Rewrites => GoldenFiles.Cases(Root, "rewrites", c => c.GetProperty("initial").GetString()!);
    public static IEnumerable<object[]> Next => GoldenFiles.Cases(Root, "next", c => c.GetProperty("after").ToString());

    [Theory, MemberData(nameof(Durations))]
    public void DurationText(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("durations")[i];
        Assert.Equal(c.GetProperty("text").GetString(), Tips.Duration(c.GetProperty("seconds").GetDouble()));
    }

    [Theory, MemberData(nameof(Saved))]
    public void SecondsSaved(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("saved")[i];
        var s = c.GetProperty("seconds");
        double? seconds = s.ValueKind == JsonValueKind.Number ? s.GetDouble() : s.ValueKind == JsonValueKind.String ? (s.GetString() == "inf" ? double.PositiveInfinity : double.NaN) : null;
        Assert.Equal(c.GetProperty("saved").GetDouble(), Tips.SecondsSaved(c.GetProperty("final").GetString()!, seconds), 9);
    }

    [Theory, MemberData(nameof(Rewrites))]
    public void RewriteRule(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("rewrites")[i];
        Assert.Equal(c.GetProperty("rewrite").GetBoolean(), Tips.IsRewrite(c.GetProperty("initial").GetString()!, c.GetProperty("edited").GetString()!));
    }

    [Theory, MemberData(nameof(Next))]
    public void NextRung(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("next")[i];
        Assert.Equal(c.GetProperty("next").ValueKind == JsonValueKind.Null ? (int?)null : c.GetProperty("next").GetInt32(), Tips.Next(c.GetProperty("after").GetInt32()));
    }
}
