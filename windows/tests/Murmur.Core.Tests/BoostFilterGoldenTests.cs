// Compares Murmur.Core with golden/boost_filter.json, which windows/golden/golden_boost_filter.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class BoostFilterGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("boost_filter");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Filter => GoldenFiles.Cases(Root, "filter", c => $"{c.GetProperty("lex").GetString()} {c.GetProperty("raw").GetString()}");
    public static IEnumerable<object[]> Unknown => GoldenFiles.Cases(Root, "unknown", c => c.GetProperty("input").GetString()!);
    public static IEnumerable<object[]> Words => GoldenFiles.Cases(Root, "words", c => c.GetProperty("input").GetString()!);
    public static IEnumerable<object[]> Similarity => GoldenFiles.Cases(Root, "similarity", c => $"{c.GetProperty("a").GetString()} / {c.GetProperty("b").GetString()}");

    [Theory, MemberData(nameof(Filter))]
    public void FilterAndReplacements(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("filter", i);
        var raw = c.GetProperty("raw").GetString()!; var rescored = c.GetProperty("rescored").GetString()!;
        var aliases = GoldenFiles.Strings(c.GetProperty("aliases")).ToHashSet();
        var common = GoldenFiles.Lex(c)!;
        Assert.Equal(c.GetProperty("output").GetString(), BoostFilter.Filter(raw, rescored, aliases, common));
        var expected = c.GetProperty("reps").EnumerateArray().Select(r => (r.GetProperty("start").GetInt32(), r.GetProperty("end").GetInt32(), r.GetProperty("from").GetString()!, r.GetProperty("to").GetString()!)).ToList();
        Assert.Equal(expected, BoostFilter.Replacements(raw, rescored, aliases, common).Select(r => (r.Start, r.End, r.From, r.To)));
    }

    [Theory, MemberData(nameof(Unknown))]
    public void WithoutUnknown(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("unknown", i);
        Assert.Equal(c.GetProperty("output").GetString(), BoostFilter.WithoutUnknown(c.GetProperty("input").GetString()!));
    }

    [Theory, MemberData(nameof(Words))]
    public void WordsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("words", i);
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("words")), BoostFilter.Words(c.GetProperty("input").GetString()!).Select(w => $"{w.Start},{w.End},{w.Folded}"));
    }

    [Theory, MemberData(nameof(Similarity))]
    public void SimilarityMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("similarity", i);
        Assert.Equal(c.GetProperty("value").GetDouble(), BoostFilter.Similarity(c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!));
    }
}
