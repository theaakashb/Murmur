// Compares RuleCleaner with golden/rule_cleaner.json, which windows/golden/golden_rule_cleaner.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Shared by the cleanup golden tests: file loading, case labels and the option sets of windows/golden/cleanup_cases.swift.</summary>
internal static class CleanupGolden
{
    public static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", name + ".json"))).RootElement;

    public static IEnumerable<object[]> Cases(JsonElement root, string section, string labelKey = "input") =>
        root.GetProperty(section).EnumerateArray().Select((c, i) => new object[] { i, Show(c.TryGetProperty(labelKey, out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "") });

    public static string Show(string s) => (s.Length > 40 ? s[..40] + "..." : s).Replace("\n", "\\n").Replace("\r", "\\r");

    public static string? Optional(JsonElement c, string key) => c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static readonly IReadOnlyList<(string Name, CleanupOptions Options)> OptionSets =
    [
        ("standard", CleanupOptions.Standard), ("faithful", CleanupOptions.Faithful), ("default", new CleanupOptions()),
        ("fillers", new CleanupOptions(fillers: true, corrections: false, lists: false)),
        ("corrections", new CleanupOptions(fillers: false, corrections: true, lists: false)),
        ("lists", new CleanupOptions(fillers: false, corrections: false, lists: true)),
        ("punctuation", new CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true)),
        ("spoken", new CleanupOptions(fillers: false, corrections: false, lists: false, spoken: true)),
        ("polish", new CleanupOptions(punctuation: true, polish: true)),
        ("professional", new CleanupOptions(punctuation: true, professional: true)),
        ("polishSpoken", new CleanupOptions(punctuation: true, polish: true, spoken: true)),
        ("professionalSpoken", new CleanupOptions(punctuation: true, professional: true, spoken: true)),
        ("leanPolish", new CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true, polish: true)),
        ("leanProfessional", new CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true, professional: true)),
    ];

    public static readonly IReadOnlyList<(string Name, CleanupOptions Options)> TrustedSets =
    [
        ("trustedPolish", new CleanupOptions(punctuation: true, polish: true, spoken: true, trustModel: true)),
        ("trustedProfessional", new CleanupOptions(polish: true, professional: true, trustModel: true)),
        ("trustedStrict", new CleanupOptions(punctuation: true, spoken: true, trustModel: true)),
    ];

    /// <summary>GoldenCases.encode: the letters of the options that are on.</summary>
    public static CleanupOptions Decode(string code) => new CleanupOptions(fillers: code.Contains('f'), corrections: code.Contains('c'), lists: code.Contains('l'),
        punctuation: code.Contains('p'), polish: code.Contains('o'), professional: code.Contains('r'), spoken: code.Contains('s'), trustModel: code.Contains('t'));

    /// <summary>GoldenCases.soundsLike: same first letter, length within two (in Characters).</summary>
    public static bool SoundsLike(string heard, string term)
    {
        var h = SwiftText.Characters(heard); var t = SwiftText.Characters(term);
        bool sameFirst = h.Count == 0 ? t.Count == 0 : t.Count > 0 && SwiftText.CharEquals(h[0], t[0]);
        return sameFirst && Math.Abs(h.Count - t.Count) <= 2;
    }

    public static string Outcome(CleanupOutcome o) => o switch
    {
        CleanupOutcome.Cleaned c => "c:" + c.Text,
        CleanupOutcome.Unchanged u => "u:" + u.Text,
        CleanupOutcome.HeldForRecovery h => "h:" + JsonSerializer.Serialize(h.Reason, SharedData.JsonOptions).Trim('"'),
        _ => throw new InvalidOperationException(),
    };

    public static string Verdict(CleanupGuardResult r) => r.Reason is { } reason ? JsonSerializer.Serialize(reason, SharedData.JsonOptions).Trim('"') : "-";
}

public class RuleCleanerGoldenTests
{
    static readonly JsonElement Root = CleanupGolden.Load("rule_cleaner");
    public static IEnumerable<object[]> Text => CleanupGolden.Cases(Root, "text");
    public static IEnumerable<object[]> Corpus => CleanupGolden.Cases(Root, "corpus", "id");
    public static IEnumerable<object[]> Grouped => CleanupGolden.Cases(Root, "grouped");

    [Theory, MemberData(nameof(Text))]
    public void RuleFunctions(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("text")[i];
        var s = c.GetProperty("input").GetString()!;
        string Want(string key) => CleanupGolden.Optional(c, key) ?? s;
        Assert.Equal(Want("spoken"), RuleCleaner.SpokenCommands(s));
        Assert.Equal(Want("fillers"), RuleCleaner.RemoveFillers(s));
        Assert.Equal(Want("corrections"), RuleCleaner.ApplyCorrections(s));
        Assert.Equal(Want("merged"), RuleCleaner.MergeSplitCues(s));
        Assert.Equal(Want("instead"), RuleCleaner.ApplyInsteadCorrections(s));
        Assert.Equal(Want("comma"), RuleCleaner.ApplyCommaCorrections(s));
        Assert.Equal(Want("list"), RuleCleaner.FormatList(s));
        Assert.Equal(Want("capitalized"), RuleCleaner.CapitalizeSentences(s));
        Assert.Equal(CleanupGolden.Optional(c, "email"), RuleCleaner.EmailLayout(s));
        var clean = c.TryGetProperty("clean", out var cl) ? cl : default;
        foreach (var (name, options) in CleanupGolden.OptionSets)
        {
            var want = clean.ValueKind == JsonValueKind.Object && clean.TryGetProperty(name, out var v) ? v.GetString() : s;
            Assert.True(want == new RuleCleaner().Clean(s, options), $"clean with {name}: want {want}, got {new RuleCleaner().Clean(s, options)}");
        }
    }

    [Theory, MemberData(nameof(Corpus))]
    public void CorpusClean(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("corpus")[i];
        Assert.Equal(c.GetProperty("clean").GetString(), new RuleCleaner().Clean(c.GetProperty("input").GetString()!, CleanupGolden.Decode(c.GetProperty("options").GetString()!)));
    }

    [Theory, MemberData(nameof(Grouped))]
    public void GroupedDigits(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("grouped")[i];
        var v = c.GetProperty("value").GetInt64();
        Assert.Equal(c.GetProperty("western").GetString(), RuleCleaner.GroupedDigits(v, false));
        Assert.Equal(c.GetProperty("indian").GetString(), RuleCleaner.GroupedDigits(v, true));
    }
}
