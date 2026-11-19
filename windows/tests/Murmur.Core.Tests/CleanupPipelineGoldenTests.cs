// Compares CleanupPipeline.Process and CleanupSections.Split with golden/cleanup_pipeline.json, which
// windows/golden/golden_cleanup_pipeline.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupPipelineGoldenTests
{
    static readonly JsonElement Root = CleanupGolden.Load("cleanup_pipeline");
    public static IEnumerable<object[]> Process => CleanupGolden.Cases(Root, "process");
    public static IEnumerable<object[]> Sections => CleanupGolden.Cases(Root, "sections");

    static string Encode(CleanupOutcome o, string input) => o switch
    {
        CleanupOutcome.Unchanged u => u.Text == input ? "u" : "u:" + u.Text,
        CleanupOutcome.Cleaned c => c.Text == input ? "c=" : "c:" + c.Text,
        _ => CleanupGolden.Outcome(o),
    };

    [Theory, MemberData(nameof(Process))]
    public void ProcessOutcomes(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("process")[i];
        var s = c.GetProperty("input").GetString()!;
        var want = c.GetProperty("outcomes").EnumerateArray().Select(x => x.GetString()).ToList();
        for (int k = 0; k < CleanupGolden.OptionSets.Count; k++)
        {
            var (name, options) = CleanupGolden.OptionSets[k];
            var got = Encode(CleanupPipeline.Process(s, options, new RuleCleaner()), s);
            Assert.True(want[k] == got, $"{name}: want {want[k]}, got {got}");
        }
        if (CleanupGolden.Optional(c, "own") is { } own)
            Assert.Equal(c.GetProperty("ownOutcome").GetString(), Encode(CleanupPipeline.Process(s, CleanupGolden.Decode(own), new RuleCleaner()), s));
    }

    [Theory, MemberData(nameof(Sections))]
    public void SplitSections(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("sections")[i];
        var s = c.GetProperty("input").GetString()!;
        List<string> Want(string key) => c.TryGetProperty(key, out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [s];
        Assert.Equal(Want("ten"), CleanupSections.Split(s, 10));
        Assert.Equal(Want("tenAttached"), CleanupSections.Split(s, 10, keepingCorrectionsAttached: true));
        Assert.Equal(Want("standard"), CleanupSections.Split(s));
        Assert.Equal(Want("target150Attached"), CleanupSections.Split(s, 150, keepingCorrectionsAttached: true));
    }
}
