// Compares CleanupGuard, TransformGuard and the pair-based CleanupPipeline functions with golden/cleanup_guard.json,
// which windows/golden/golden_cleanup_guard.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupGuardGoldenTests
{
    static readonly JsonElement Root = CleanupGolden.Load("cleanup_guard");
    public static IEnumerable<object[]> Text => CleanupGolden.Cases(Root, "text");
    public static IEnumerable<object[]> Pairs => CleanupGolden.Cases(Root, "pairs", "raw");
    public static IEnumerable<object[]> Mutations => CleanupGolden.Cases(Root, "mutations", "label");
    public static IEnumerable<object[]> Terms => CleanupGolden.Cases(Root, "terms", "raw");
    public static IEnumerable<object[]> WordPairs => CleanupGolden.Cases(Root, "wordPairs", "a");

    static string? Ints(HashSet<int> s) => s.Count == 0 ? null : string.Join(",", s.Order());

    [Fact]
    public void OptionSetsMatchTheGenerator()
    {
        Assert.Equal(Root.GetProperty("optionSets").EnumerateArray().Select(x => x[0].GetString() + ":" + CleanupGolden.Decode(x[1].GetString()!)),
                     CleanupGolden.OptionSets.Select(x => x.Name + ":" + x.Options));
        Assert.Equal(Root.GetProperty("trustedSets").EnumerateArray().Select(x => x[0].GetString() + ":" + CleanupGolden.Decode(x[1].GetString()!)),
                     CleanupGolden.TrustedSets.Select(x => x.Name + ":" + x.Options));
    }

    [Theory, MemberData(nameof(Text))]
    public void TextFunctions(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("text")[i];
        var s = c.GetProperty("input").GetString()!;
        var words = CleanupGuard.Words(s);
        Assert.Equal(c.GetProperty("words").EnumerateArray().Select(x => x.GetString()),
                     words.Select(w => $"{w.Text}|{w.Lower}|" + string.Concat(new[] { w.SentenceStart, w.CommaBefore, w.CommaAfter, w.EndAfter }.Select(b => b ? "1" : "0"))));
        Assert.Equal(CleanupGolden.Optional(c, "window"), Ints(CleanupGuard.CorrectionWindow(s)));
        Assert.Equal(CleanupGolden.Optional(c, "loose"), Ints(CleanupGuard.CorrectionWindow(s, loose: true)));
        Assert.Equal(CleanupGolden.Optional(c, "commaFree"), Ints(CleanupGuard.CorrectionWindow(s, loose: true, commaFreeOnly: true)));
        Assert.Equal(CleanupGolden.Optional(c, "commaFreeApplied") ?? s, CleanupGuard.ApplyCommaFreeCorrections(s));
        Assert.Equal(CleanupGolden.Optional(c, "explicit") ?? s, CleanupGuard.ApplyExplicitRepairs(s));
        Assert.Equal(CleanupGolden.Optional(c, "cued") ?? s, CleanupGuard.ApplyCuedSentenceRepairs(s));
        var rep = CleanupGuard.ReplacedSentences(words);
        var replaced = rep.Cued.Count == 0 && rep.Restated.Count == 0 && rep.Cuts.Count == 0 ? null
            : $"{Ints(rep.Cued)};{Ints(rep.Restated)};" + string.Join(",", rep.Cuts.Select(x => $"{x.Lo}-{x.Hi}"));
        Assert.Equal(CleanupGolden.Optional(c, "replaced"), replaced);
        var markers = c.TryGetProperty("markers", out var m) ? m.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        Assert.Equal(markers, CleanupGuard.ListMarkerValues(s).Order(StringComparer.Ordinal));
        Assert.Equal(c.GetProperty("nonList").GetInt32(), CleanupGuard.NonListLineCount(s));
    }

    static string Judged(CleanupOutcome o, string @out) => o is CleanupOutcome.Cleaned c && c.Text == @out ? "c" : CleanupGolden.Outcome(o);

    [Theory, MemberData(nameof(Pairs))]
    public void PairVerdicts(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("pairs")[i];
        var raw = c.GetProperty("raw").GetString()!; var @out = c.GetProperty("out").GetString()!;
        var want = c.GetProperty("v").GetString()!.Split(',');
        for (int k = 0; k < CleanupGolden.OptionSets.Count; k++)
        {
            var (name, options) = CleanupGolden.OptionSets[k];
            var got = CleanupGolden.Verdict(CleanupGuard.Check(raw, @out, options));
            Assert.True(want[k] == got, $"{name}: want {want[k]}, got {got}");
        }
        if (CleanupGolden.Optional(c, "own") is { } own)
            Assert.Equal(c.GetProperty("ownVerdict").GetString(), CleanupGolden.Verdict(CleanupGuard.Check(raw, @out, CleanupGolden.Decode(own))));
        Assert.Equal(c.GetProperty("truncated").GetBoolean(), CleanupGuard.Truncated(raw, @out));
        Assert.Equal(c.GetProperty("answers").GetBoolean(), CleanupGuard.Answers(raw, @out));
        Assert.Equal(c.GetProperty("transform").GetBoolean(), TransformGuard.Accepts(raw, @out));
        Assert.Equal(CleanupGolden.Optional(c, "punctuated"), CleanupPipeline.Punctuated(raw, @out));
        Assert.Equal(c.GetProperty("trusted").GetString(), string.Join("|", CleanupGolden.TrustedSets.Select(t => Judged(CleanupPipeline.Judge(raw, @out, t.Options, []), @out))));
    }

    [Theory, MemberData(nameof(Mutations))]
    public void MutationVerdicts(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("mutations")[i];
        var raw = c.GetProperty("raw").GetString()!; var @out = c.GetProperty("out").GetString()!;
        var own = CleanupGolden.Decode(c.GetProperty("options").GetString()!);
        var polish = own with { Polish = true };
        var sets = new[] { own, polish, polish with { Professional = true }, own with { Spoken = true } };
        Assert.Equal(c.GetProperty("v").GetString(), string.Join(",", sets.Select(o => CleanupGolden.Verdict(CleanupGuard.Check(raw, @out, o)))));
    }

    [Theory, MemberData(nameof(Terms))]
    public void TermVerdicts(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("terms")[i];
        var raw = c.GetProperty("raw").GetString()!; var @out = c.GetProperty("out").GetString()!;
        var terms = c.GetProperty("terms").EnumerateArray().Select(x => x.GetString()!).ToList();
        string Verdicts(Func<string, string, bool>? sounds) => string.Join(",", CleanupGolden.OptionSets.Select(o => CleanupGolden.Verdict(CleanupGuard.Check(raw, @out, o.Options, terms, sounds))));
        Assert.Equal(c.GetProperty("v").GetString(), Verdicts(null));
        Assert.Equal(c.GetProperty("sounds").GetString(), Verdicts(CleanupGolden.SoundsLike));
    }

    [Theory, MemberData(nameof(WordPairs))]
    public void WordPairChecks(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("wordPairs")[i];
        var a = c.GetProperty("a").GetString()!; var b = c.GetProperty("b").GetString()!;
        Assert.Equal(c.GetProperty("flip").GetBoolean(), CleanupGuard.PolarityFlip(a, b));
        Assert.Equal(c.GetProperty("swap").GetBoolean(), CleanupGuard.PolishSwap(a, b));
    }
}
