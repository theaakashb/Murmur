// Compares WordDiff with golden/worddiff.json, which windows/golden/golden_worddiff.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class WordDiffGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("worddiff");
    public static IEnumerable<object[]> Runs => GoldenFiles.Cases(Root, "runs", c => $"{c.GetProperty("raw").GetString()} -> {c.GetProperty("final").GetString()}");
    public static IEnumerable<object[]> Reverts => GoldenFiles.Cases(Root, "reverts", c => c.GetProperty("raw").GetString()!);

    [Theory, MemberData(nameof(Runs))]
    public void RunsAndCounts(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("runs")[i];
        string raw = c.GetProperty("raw").GetString()!, final = c.GetProperty("final").GetString()!;
        Assert.Equal(c.GetProperty("changes").GetInt32(), WordDiff.ChangeCount(raw, final));
        var expected = c.GetProperty("runs").EnumerateArray().Select(r =>
            $"{r.GetProperty("kind").GetString()}|{r.GetProperty("text").GetString()}|{string.Join(",", r.GetProperty("marks").EnumerateArray().Select(m => m.GetInt32()))}");
        var actual = WordDiff.Runs(raw, final).Select(r => $"{GoldenFiles.Camel(r.Kind)}|{r.Text}|{string.Join(",", r.Marks)}");
        Assert.Equal(expected, actual);
    }

    [Theory, MemberData(nameof(Reverts))]
    public void RevertTexts(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("reverts")[i];
        Assert.Equal(c.GetProperty("text").GetString(), WordDiff.RevertText(c.GetProperty("raw").GetString()!, c.GetProperty("inserted").GetString()!));
    }
}
