// Compares Murmur.Core with golden/dictionary.json, which windows/golden/golden_dictionary.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class DictionaryGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("dictionary");
    static readonly DateTime Base = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime;
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    static readonly Lazy<List<DictionaryStore>> Stores = new(() => Root.GetProperty("dictionaries").EnumerateArray().Select((d, i) =>
    {
        var store = new DictionaryStore(TempDirs.New("golden-dict" + i));
        int n = 0;
        foreach (var e in d.GetProperty("entries").EnumerateArray())
            store.Add(e.GetProperty("preferred").GetString()!, GoldenFiles.Strings(e.GetProperty("variants")), ParseSource(e.GetProperty("source").GetString()!),
                e.GetProperty("enabled").GetBoolean(), Base.AddSeconds(n++ * 60));
        return store;
    }).ToList());

    static EntrySource ParseSource(string s) => s == "learned" ? EntrySource.Learned : EntrySource.Manual;

    public static IEnumerable<object[]> Apply => GoldenFiles.Cases(Root, "apply", c => $"d{c.GetProperty("dict").GetInt32()} {c.GetProperty("lex").GetString()} {c.GetProperty("input").GetString()}");
    public static IEnumerable<object[]> Relevant => GoldenFiles.Cases(Root, "relevant", c => $"d{c.GetProperty("dict").GetInt32()} {c.GetProperty("input").GetString()}");
    public static IEnumerable<object[]> Info => GoldenFiles.Cases(Root, "info", c => $"d{c.GetProperty("dict").GetInt32()} {c.GetProperty("lex").GetString()}");
    public static IEnumerable<object[]> Variants => GoldenFiles.Cases(Root, "variants", c => $"d{c.GetProperty("dict").GetInt32()} {c.GetProperty("phrase").GetString()}");
    public static IEnumerable<object[]> Sounds => GoldenFiles.Cases(Root, "sounds", c => $"{c.GetProperty("a").GetString()} / {c.GetProperty("b").GetString()}");
    public static IEnumerable<object[]> Suggestions => GoldenFiles.Cases(Root, "suggestions", c => $"{c.GetProperty("dictations").GetArrayLength()} dictations, lex {c.GetProperty("lex").GetString()}");
    public static IEnumerable<object[]> StoreSequences => GoldenFiles.Cases(Root, "store", c => c.GetProperty("label").GetString()!);

    [Theory, MemberData(nameof(Apply))]
    public void ApplyMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("apply", i);
        Assert.Equal(c.GetProperty("output").GetString(), Stores.Value[c.GetProperty("dict").GetInt32()].Apply(c.GetProperty("input").GetString()!, GoldenFiles.Lex(c)));
    }

    [Theory, MemberData(nameof(Relevant))]
    public void RelevantEntriesMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("relevant", i);
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("result")), Stores.Value[c.GetProperty("dict").GetInt32()].RelevantEntries(c.GetProperty("input").GetString()!).Select(e => e.Preferred));
    }

    [Theory, MemberData(nameof(Info))]
    public void VocabularyAndSkippedVariants(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("info", i);
        var store = Stores.Value[c.GetProperty("dict").GetInt32()];
        var lex = GoldenFiles.Lex(c);
        store.IsCommonWord = lex;
        try
        {
            Assert.Equal(GoldenFiles.Strings(c.GetProperty("vocabulary")), store.VocabularyTerms);
            Assert.Equal(GoldenFiles.Strings(c.GetProperty("helper")), store.HelperVocabulary.Select(h => h.Text + (h.Aliases is null ? "" : "=" + string.Join("|", h.Aliases))));
        }
        finally { store.IsCommonWord = null; }
        Assert.Equal(c.GetProperty("command").EnumerateArray().Select(x => x.GetBoolean()), store.Entries.Select(DictionaryStore.IsCommandTarget));
        var expectedSkip = c.GetProperty("skip").EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetBoolean()).ToArray()).ToList();
        var actualSkip = store.Entries.Select(e => e.Variants.Select(v => DictionaryStore.SkipLearnedVariant(v, e, lex)).ToArray()).ToList();
        Assert.Equal(expectedSkip.Count, actualSkip.Count);
        for (int k = 0; k < expectedSkip.Count; k++) Assert.Equal(expectedSkip[k], actualSkip[k]);
    }

    [Theory, MemberData(nameof(Variants))]
    public void EntryForVariantMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("variants", i);
        Assert.Equal(GoldenFiles.Optional(c, "preferred"), Stores.Value[c.GetProperty("dict").GetInt32()].EntryForVariant(c.GetProperty("phrase").GetString()!)?.Preferred);
    }

    [Theory, MemberData(nameof(Sounds))]
    public void SoundsLikeMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("sounds", i);
        Assert.Equal(c.GetProperty("value").GetBoolean(), DictionaryStore.SoundsLike(c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!));
    }

    [Theory, MemberData(nameof(Suggestions))]
    public void SuggestionsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("suggestions", i);
        var entries = c.GetProperty("entries").EnumerateArray().Select(e => new DictionaryEntry
        {
            Id = Guid.NewGuid(), Preferred = e.GetProperty("preferred").GetString()!, Variants = GoldenFiles.Strings(e.GetProperty("variants")),
            Source = EntrySource.Manual, CreatedAt = Base, Enabled = true,
        }).ToList();
        var dictations = c.GetProperty("dictations").EnumerateArray().Select(d => (d[0].GetString()!, d[1].GetString()!)).ToList();
        var found = DictionarySuggestions.Find(dictations, entries, GoldenFiles.Strings(c.GetProperty("dismissed")).ToHashSet(), GoldenFiles.Lex(c) ?? (_ => false),
            c.GetProperty("minDictations").GetInt32(), c.GetProperty("minSingle").GetInt32(), c.GetProperty("limit").GetInt32());
        var expected = c.GetProperty("result").EnumerateArray().Select(r => new DictionarySuggestion(r.GetProperty("preferred").GetString()!, GoldenFiles.Strings(r.GetProperty("heard")), r.GetProperty("dictations").GetInt32())).ToList();
        Assert.Equal(expected.Count, found.Count);
        for (int k = 0; k < expected.Count; k++) Assert.Equal(expected[k], found[k]);
    }

    static string Describe(DictionaryStore store) => string.Join(";", store.Entries.Select(e => $"{e.Preferred}={string.Join("|", e.Variants)}/{(e.Source == EntrySource.Learned ? "learned" : "manual")}/{(e.Enabled ? 1 : 0)}"));

    [Theory, MemberData(nameof(StoreSequences))]
    public void StoreSequence(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("store", i);
        var ops = c.GetProperty("ops").EnumerateArray().Select(GoldenFiles.Strings).ToList();
        var results = GoldenFiles.Strings(c.GetProperty("results"));
        var states = GoldenFiles.Strings(c.GetProperty("states"));
        var dir = TempDirs.New("golden-store");
        var store = new DictionaryStore(dir);
        int clock = 0;
        static string[] Variants(string s) => s.Length == 0 ? [] : s.Split('|');
        for (int k = 0; k < ops.Count; k++)
        {
            var op = ops[k]; var a = op.Skip(1).ToArray();
            clock++;
            Guid Id(string s) => int.Parse(s) < store.Entries.Count ? store.Entries[int.Parse(s)].Id : Guid.NewGuid();
            string result;
            try
            {
                switch (op[0])
                {
                    case "add": store.Add(a[0], Variants(a[1]), ParseSource(a[2]), a[3] == "1", Base.AddSeconds(clock)); result = "ok"; break;
                    case "update":
                        var current = int.Parse(a[0]) < store.Entries.Count ? store.Entries[int.Parse(a[0])]
                            : new DictionaryEntry { Id = Guid.NewGuid(), Preferred = "x", Variants = [], Source = EntrySource.Manual, CreatedAt = Base, Enabled = true };
                        store.Update(current with { Preferred = a[1], Variants = Variants(a[2]), Enabled = a[3] == "1" }); result = "ok"; break;
                    case "delete": store.Delete(Id(a[0])); result = "ok"; break;
                    case "resetLearned": result = "removed:" + store.ResetLearned().Count; break;
                    case "reopen": store = new DictionaryStore(dir); result = "ok"; break;
                    case "apply": result = store.Apply(a[0]); break;
                    case "damage": File.WriteAllBytes(Path.Combine(dir, DictionaryStore.FileName), Encoding.UTF8.GetBytes("not json")); result = "ok"; break;
                    case "version": File.WriteAllBytes(Path.Combine(dir, DictionaryStore.FileName), Encoding.UTF8.GetBytes("""{"entries":[],"version":2}""")); result = "ok"; break;
                    case "reopenOrFail":
                        try { store = new DictionaryStore(dir); result = "ok"; }
                        catch (StoreError e) { result = "error:" + e.Message; }
                        catch (Exception) { result = "error"; }
                        break;
                    default: throw new InvalidOperationException("unknown op " + op[0]);
                }
            }
            catch (StoreError e) { result = "error:" + e.Message; }
            Assert.Equal(results[k], result);
            Assert.Equal(states[k], Describe(store));
        }
    }
}
