// Compares Murmur.Core with golden/learning.json, which windows/golden/golden_learning.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Reading the golden files: the case lists, the shared synthetic lexicon and the lexicon modes the cases name.</summary>
internal static class GoldenFiles
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "golden");

    /// <summary>windows/golden/lexicon.txt: the lexicon both sides use for golden runs (exact-word lookup).</summary>
    public static readonly HashSet<string> Lexicon = File.ReadAllLines(Path.Combine(Folder, "lexicon.txt")).Where(l => l.Length > 0).ToHashSet();

    public static JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, name + ".json"))).RootElement;

    /// <summary>"none" is no lexicon (null), "file" the shared lexicon, "list" the case's own words.</summary>
    public static Func<string, bool>? Lex(JsonElement c)
    {
        switch (c.GetProperty("lex").GetString())
        {
            case "file": return Lexicon.Contains;
            case "list": var set = c.GetProperty("words").EnumerateArray().Select(w => w.GetString()!).ToHashSet(); return set.Contains;
            default: return null;
        }
    }

    public static string[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();

    public static string? Optional(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    public static string Show(string s) => (s.Length > 36 ? s[..36] + "..." : s).Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>One theory row per case: the index, and a short label that only names the case in the test report.</summary>
    public static IEnumerable<object[]> Cases(JsonElement root, string section, Func<JsonElement, string> label) =>
        root.GetProperty(section).EnumerateArray().Select((c, i) => new object[] { i, $"{i}: {Show(label(c))}" });

    public static string Camel(Enum value) => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

public class LearningGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("learning");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Filter => GoldenFiles.Cases(Root, "filter", c => $"{c.GetProperty("source").GetString()} -> {c.GetProperty("target").GetString()} [{c.GetProperty("lex").GetString()}]");
    public static IEnumerable<object[]> Replacements => GoldenFiles.Cases(Root, "replacements", c => $"{c.GetProperty("original").GetString()} -> {c.GetProperty("current").GetString()}");
    public static IEnumerable<object[]> Plausible => GoldenFiles.Cases(Root, "plausible", c => $"{c.GetProperty("original").GetString()} [{c.GetProperty("lex").GetString()}]");
    public static IEnumerable<object[]> Keys => GoldenFiles.Cases(Root, "keys", c => c.GetProperty("input").GetString()!);
    public static IEnumerable<object[]> Embeds => GoldenFiles.Cases(Root, "embeds", c => $"{c.GetProperty("a").GetString()} / {c.GetProperty("b").GetString()}");
    public static IEnumerable<object[]> Ledger => GoldenFiles.Cases(Root, "ledger", c => c.GetProperty("label").GetString()!);

    [Theory, MemberData(nameof(Filter))]
    public void FilterVerdict(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("filter", i);
        var manual = GoldenFiles.Strings(c.GetProperty("manual")).ToHashSet();
        var verdict = CorrectionFilter.Evaluate(c.GetProperty("source").GetString()!, c.GetProperty("target").GetString()!, manual, GoldenFiles.Lex(c));
        var (kind, a, b) = verdict switch
        {
            CandidateVerdict.Accepted x => ("accepted", x.Source, x.Target),
            CandidateVerdict.Rejected x => ("rejected", GoldenFiles.Camel(x.Reason), ""),
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal((c.GetProperty("kind").GetString(), c.GetProperty("a").GetString(), c.GetProperty("b").GetString()), (kind, a, b));
    }

    [Theory, MemberData(nameof(Replacements))]
    public void ReplacementsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("replacements", i);
        var result = CorrectionFilter.Replacements(c.GetProperty("original").GetString()!, c.GetProperty("current").GetString()!);
        Assert.Equal(GoldenFiles.Optional(c, "failure"), result.Failure is { } f ? GoldenFiles.Camel(f) : null);
        var expected = c.TryGetProperty("changes", out var changes) ? changes.EnumerateArray().Select(GoldenFiles.Strings).ToList() : null;
        var actual = result.Changes?.Select(ch => new[] { ch.Source }.Concat(ch.Targets).ToArray()).ToList();
        Assert.Equal(expected?.Count, actual?.Count);
        if (expected is not null) for (int k = 0; k < expected.Count; k++) Assert.Equal(expected[k], actual![k]);
    }

    [Theory, MemberData(nameof(Plausible))]
    public void PlausibleEditMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("plausible", i);
        Assert.Equal(GoldenFiles.Optional(c, "result"), CorrectionFilter.PlausibleEdit(c.GetProperty("original").GetString()!, GoldenFiles.Strings(c.GetProperty("candidates")), GoldenFiles.Lex(c)));
    }

    [Theory, MemberData(nameof(Keys))]
    public void KeysAndTokens(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("keys", i);
        var input = c.GetProperty("input").GetString()!;
        Assert.Equal(c.GetProperty("letters").GetString(), CorrectionFilter.Letters(input));
        Assert.Equal(c.GetProperty("phonetic").GetString(), CorrectionFilter.PhoneticKey(input));
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("tokens")), TermText.Tokens(input).Select(t => $"{t.Start},{t.End},{t.Text}"));
        Assert.Equal(GoldenFiles.Optional(c, "phraseKey"), TermText.PhraseKey(input));
        Assert.Equal(c.GetProperty("collapsed").GetString(), TermText.CollapseWhitespace(input));
        Assert.Equal(c.GetProperty("folded").GetString(), TermText.Fold(input));
    }

    [Theory, MemberData(nameof(Embeds))]
    public void EmbedsOrdinary(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("embeds", i);
        Assert.Equal(c.GetProperty("value").GetBoolean(), CorrectionFilter.EmbedsOrdinary(c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!));
    }

    [Theory, MemberData(nameof(Ledger))]
    public void LedgerSequence(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("ledger", i);
        var ops = c.GetProperty("ops").EnumerateArray().Select(GoldenFiles.Strings).ToList();
        var results = GoldenFiles.Strings(c.GetProperty("results"));
        var states = GoldenFiles.Strings(c.GetProperty("states"));
        var dir = TempDirs.New("golden-ledger");
        var env = new LedgerEnv(dir);
        for (int k = 0; k < ops.Count; k++)
        {
            string result;
            try { result = env.Run(ops[k]); }
            catch (StoreError e) { result = "error:" + e.Message; }
            catch (Exception) { result = "error"; }
            Assert.Equal(results[k], result);
            Assert.Equal(states[k], env.State());
        }
    }
}

/// <summary>Replays a golden ledger op sequence; the same ops run in golden_learning.swift.</summary>
sealed class LedgerEnv
{
    static readonly DateTime T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime;
    readonly string dir;
    DictionaryStore dict;
    CorrectionLedger ledger;
    readonly Dictionary<string, Guid> sessions = [];
    Func<string, bool>? lex;

    public LedgerEnv(string dir)
    {
        this.dir = dir;
        dict = new DictionaryStore(dir);
        ledger = new CorrectionLedger(dir, dict);
    }

    Guid Sid(string name) => sessions.TryGetValue(name, out var id) ? id : sessions[name] = Guid.NewGuid();
    List<DictionaryEntry> Learned => dict.Entries.Where(e => e.Source == EntrySource.Learned).ToList();
    static DateTime At(string offset) => T0.AddSeconds(double.Parse(offset, System.Globalization.CultureInfo.InvariantCulture));
    static string[] Variants(string s) => s.Length == 0 ? [] : s.Split('|');
    static string Source(EntrySource s) => s == EntrySource.Learned ? "learned" : "manual";

    public string State()
    {
        var entries = string.Join(";", dict.Entries.Select(e => $"{e.Preferred}={string.Join("|", e.Variants)}/{Source(e.Source)}/{(e.Enabled ? 1 : 0)}"));
        var evidence = string.Join(";", ledger.Evidence.Select(e => $"{e.Source}>{e.Target}"));
        var blocked = string.Join(";", ledger.Blocked.Select(b => b switch
        {
            CorrectionLedger.BlockedItem.Heard h => "heard:" + h.Source,
            CorrectionLedger.BlockedItem.Spelling s => "spelling:" + s.Target,
            _ => throw new InvalidOperationException(),
        }));
        return string.Join(" # ", "E:" + entries, "V:" + evidence, $"P:{ledger.ProvisionalSessions.Count}", "S:" + string.Join(",", ledger.SuppressedSources.Order(StringComparer.Ordinal)),
            "T:" + string.Join(";", ledger.Tombstones.Select(t => $"{t.Source}>{t.Target}").Order(StringComparer.Ordinal)), "B:" + blocked, $"L:{(ledger.LearningEnabled ? 1 : 0)}");
    }

    static string Outcome(CorrectionLedger.RecordOutcome o) =>
        o.Kind == CorrectionLedger.RecordKind.Rejected ? "rejected:" + GoldenFiles.Camel(o.Rejection!.Value) : GoldenFiles.Camel(o.Kind);

    public string Run(string[] op)
    {
        var a = op.Skip(1).ToArray();
        switch (op[0])
        {
            case "lex":
                lex = a[0] switch { "file" => GoldenFiles.Lexicon.Contains, "list" => a.Skip(1).ToHashSet().Contains, _ => null };
                ledger.IsCommonWord = lex; return "ok";
            case "record": return Outcome(ledger.Record(new ObservedCorrection { SessionId = Sid(a[0]), Source = a[1], Target = a[2], ObservedAt = At(a[3]) }));
            case "settle": return string.Join(",", ledger.Settle(Sid(a[0])).Select(e => e.Preferred));
            case "revert": ledger.Revert(Sid(a[0])); return "ok";
            case "undo": ledger.UndoLearned(Learned[int.Parse(a[0])].Id); return "ok";
            case "undoStale": ledger.UndoLearned(Guid.NewGuid()); return "ok";
            case "deleteLearned": ledger.DeleteLearned(Learned[int.Parse(a[0])].Id); return "ok";
            case "deleteAny": ledger.DeleteLearned(dict.Entries[int.Parse(a[0])].Id); return "ok";
            case "dictDelete": dict.Delete(Learned[int.Parse(a[0])].Id); return "ok";
            case "reset": ledger.Reset(); return "ok";
            case "unblockHeard": ledger.Unblock(new CorrectionLedger.BlockedItem.Heard(a[0])); return "ok";
            case "unblockSpelling": ledger.Unblock(new CorrectionLedger.BlockedItem.Spelling(a[0])); return "ok";
            case "learning": ledger.SetLearningEnabled(a[0] == "on"); return "ok";
            case "edit": return string.Join(",", ledger.LearnFromEdit(a[0], a[1], At(a[2])).Select(e => e.Preferred));
            case "addManual": dict.Add(a[0], Variants(a[1])); return "ok";
            case "addLearned": dict.Add(a[0], Variants(a[1]), EntrySource.Learned); return "ok";
            case "apply": return dict.Apply(a[0]);
            case "reopen":
                dict = new DictionaryStore(dir, lex); ledger = new CorrectionLedger(dir, dict); return "ok";
            case "legacy":
                var json = a[0].Replace("{ID}", Learned[0].Id.ToString().ToUpperInvariant()).Replace("{OLD}", "11111111-1111-1111-1111-111111111111");
                PrivateFiles.Write(Encoding.UTF8.GetBytes(json), Path.Combine(dir, CorrectionLedger.FileName));
                ledger = new CorrectionLedger(dir, dict); ledger.IsCommonWord = lex; return "ok";
            default: throw new InvalidOperationException("unknown op " + op[0]);
        }
    }
}
