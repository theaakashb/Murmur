using System.Text.Json;
using Murmur.Core;

// Usage: CorpusReport CORPUS_JSON BASELINE.jsonl OTHER.jsonl...   (files from run_corpus.py)
// Per file: count, too-long/failed, exact match (and match after trimming) against the baseline, strict-guard pass rate
// (the corpus case's own options with Polish, Professional and TrustModel off, as the Mac applies to the local model),
// and the request timing. Prints counts and ids only, never text.
var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(args[0]))!.GroupBy(c => c.input).ToDictionary(g => g.Key, g => g.First());
var loadMs = new Dictionary<string, int>();
Dictionary<string, Row> Load(string path)
{
    var lines = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    loadMs[path] = lines[0].GetProperty("load_ms").GetInt32();
    return lines.Skip(1).Select(e => new Row(e.GetProperty("id").GetString()!, e.GetProperty("input").GetString()!,
        e.TryGetProperty("text", out var t) ? t.GetString() : null, e.TryGetProperty("error", out var er) ? er.GetString() : null, e.GetProperty("ms").GetInt32()))
        .ToDictionary(r => r.Id);
}
var baseline = Load(args[1]);
foreach (var path in args.Skip(1))
{
    var rows = Load(path);
    int exact = 0, trimmed = 0, tooLong = 0, failed = 0, pass = 0, answered = 0;
    var diffs = new List<string>();
    foreach (var (id, r) in rows)
    {
        if (r.Error == "too-long") tooLong++; else if (r.Error != null) failed++;
        if (r.Text == null) continue;
        answered++;
        var o = cases[r.Input].options;
        var strict = new CleanupOptions(o.fillers, o.corrections, o.lists, o.punctuation);
        if (CleanupGuard.Check(r.Input, r.Text, strict).IsPass) pass++;
        if (baseline[id].Text == r.Text) exact++;
        else { diffs.Add(id); if (baseline[id].Text?.Trim() == r.Text.Trim()) trimmed++; }
    }
    var ms = rows.Values.Select(r => r.Ms).Order().ToList();
    Console.WriteLine($"{Path.GetFileName(path)}: n={rows.Count} answered={answered} too-long={tooLong} failed={failed} exact-vs-baseline={exact}/{rows.Count} ({100.0 * exact / rows.Count:F1}%) whitespace-only-diffs={trimmed} guard-pass={pass}/{answered} " +
                      $"load={loadMs[path]}ms median={ms[ms.Count / 2]}ms p90={ms[ms.Count * 9 / 10]}ms max={ms[^1]}ms total={ms.Sum() / 1000.0:F1}s");
    if (path != args[1]) Console.WriteLine("  differing ids: " + string.Join(" ", diffs));
}

record Row(string Id, string Input, string? Text, string? Error, int Ms);
record Opts(bool fillers = true, bool corrections = true, bool lists = true, bool punctuation = false);
record Case(string id, string input, Opts options);
