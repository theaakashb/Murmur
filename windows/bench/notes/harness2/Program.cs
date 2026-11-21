// Temporary Windows harness: runs the ported C# MeetingNotesWriter against a llama.cpp loopback server,
// mirroring app/tools/measure_meeting_notes.swift's llama route (long workloads only). Emits aggregates only.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murmur.Core;

static class P
{
    record Entry(string Id, string MyNotes, string[][] Lines);
    static readonly List<Dictionary<string, object?>> Requests = [];
    static int Calls, Overflows;

    static async Task<int> Main(string[] a)
    {
        string Arg(string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 ? a[i + 1] : d; }
        string corpusPath = Arg("--corpus", ""), outPath = Arg("--out", "out.json"), label = Arg("--label", "run");
        int port = int.Parse(Arg("--port", "18085")), maxTokens = int.Parse(Arg("--max-tokens", "1200"));
        bool bounded = a.Contains("--bounded");
        var workloads = Arg("--workloads", "30,60").Split(',');
        int budget = int.Parse(Arg("--budget", "5000")); bool cap = a.Contains("--cap"), split = a.Contains("--split"), concise = a.Contains("--concise"); int fan = int.Parse(Arg("--fan", "0")); bool prompted = cap || bounded; var opts = new Opts(cap, split, fan, budget);
        var corpus = new List<Entry>();
        foreach (var m in JsonNode.Parse(File.ReadAllText(corpusPath))!["meetings"]!.AsArray())
            corpus.Add(new(m!["id"]!.GetValue<string>(), m["myNotes"]!.GetValue<string>(),
                m["lines"]!.AsArray().Select(l => new[] { l![0]!.GetValue<string>(), l[1]!.GetValue<string>() }).ToArray()));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        var results = new List<Dictionary<string, object?>>();
        var proc = Process.GetCurrentProcess();

        Meeting Build(IEnumerable<Entry> entries, double spacing, double offsetStep0 = 0, bool gap = false)
        {
            var m = new Meeting { Id = Guid.NewGuid().ToString(), RecordedAt = DateTime.UtcNow, Source = new MeetingSource("Zoom", "us.zoom.xos") };
            bool first = true; double offset = 0;
            foreach (var e in entries)
            {
                for (int i = 0; i < e.Lines.Length; i++)
                {
                    double s = offset + i * spacing;
                    m.Insert(e.Lines[i][0] == "me" ? MeetingStream.Me : MeetingStream.Them, s, s + spacing / 2, e.Lines[i][1]);
                }
                if (first) m.MyNotes = e.MyNotes; first = false;
                offset += e.Lines.Length * spacing + (gap ? 20 : 0);
            }
            return m;
        }

        string qpath = Arg("--quality", "");
        if (qpath != "")
        {
            // Port of the scoring in app/tools/measure_meeting_notes.swift (aggregates only; no item text is stored).
            int unsupported = 0, invented = 0, missD = 0, missA = 0, labD = 0, labA = 0, kept = 0, dropped = 0, fails = 0, n = 0, ptsTot = 0, decTot = 0, actTot = 0;
            var secs = new List<double>();
            var qr = new Dictionary<string, int>();
            Writer2.Truncated = Writer2.Splits = Writer2.SplitRecovered = Writer2.CapHits = 0;
            var qmeetings = JsonNode.Parse(File.ReadAllText(qpath))!["meetings"]!.AsArray();
            foreach (var m0 in qmeetings)
            {
                var lines = m0!["lines"]!.AsArray().Select(l => new[] { l![0]!.GetValue<string>(), l[1]!.GetValue<string>() }).ToArray();
                string myNotes = m0["myNotes"]!.GetValue<string>();
                var decs = m0["decisions"]!.AsArray().Select(d => d!.AsArray().Select(x => x!.GetValue<int>()).ToHashSet()).ToList();
                var acts = m0["actions"]!.AsArray().Select(x => x!["lines"]!.AsArray().Select(y => y!.GetValue<int>()).ToHashSet()).ToList();
                var traps = m0["traps"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                var m = new Meeting { Id = Guid.NewGuid().ToString(), RecordedAt = DateTime.UtcNow, Source = new MeetingSource("Zoom", "us.zoom.xos") };
                for (int i = 0; i < lines.Length; i++) { double s = i * 20.0; m.Insert(lines[i][0] == "me" ? MeetingStream.Me : MeetingStream.Them, s, s + 10, lines[i][1]); }
                m.MyNotes = myNotes;
                var number = MeetingNotesPrompt.Number(m).ToDictionary(x => x.Line.Id, x => x.Number);
                var sw0 = Stopwatch.StartNew();
                MeetingWriter.Result outcome;
                try { outcome = await Writer2.Write(m, opts, (msgs, ct) => Generate(http, port, maxTokens, prompted, concise, msgs, ct)); }
                catch { fails++; continue; }
                secs.Add(sw0.Elapsed.TotalSeconds); n++;
                var notes = outcome.Notes; dropped += notes.Dropped;
                foreach (var kv in outcome.Reasons) qr[kv.Key.ToString()] = qr.GetValueOrDefault(kv.Key.ToString()) + kv.Value;
                string said = (string.Join(" ", lines.Select(l => l[1])) + " " + myNotes).ToLowerInvariant();
                var saidNums = NotesGrounding.Numbers(said);
                HashSet<int> cited(NoteItem it) => it.Lines.Where(number.ContainsKey).Select(id => number[id]).ToHashSet();
                string? trapped(NoteItem it) { var t = string.Join(" ", it.Text, it.Owner ?? "", it.Due ?? "").ToLowerInvariant(); return traps.FirstOrDefault(x => t.Contains(x.ToLowerInvariant())); }
                string? newFact(NoteItem it)
                {
                    var text = string.Join(". ", it.Text, it.Owner ?? "", it.Due ?? "");
                    foreach (var num in NotesGrounding.Numbers(text)) if (!saidNums.Contains(num)) return num;
                    foreach (var sentence in text.Split(['.', '!', '?', ':', ';']))
                    {
                        var words = NotesGrounding.Tokens(sentence);
                        for (int i = 1; i < words.Length; i++)
                        {
                            var w = words[i];
                            if (w.Length > 0 && char.IsUpper(w[0]) && w is not ("I" or "Me" or "Them"))
                            {
                                var lw = w.ToLowerInvariant(); var bare = lw.EndsWith("'s") ? lw[..^2] : lw;
                                if (!said.Contains(bare)) return w;
                            }
                        }
                    }
                    return null;
                }
                foreach (var (kind, list) in new[] { ("point", notes.KeyPoints), ("decision", notes.Decisions), ("action", notes.Actions) })
                    foreach (var it in list)
                    {
                        kept++;
                        var c = cited(it); bool flag = trapped(it) is not null;
                        if (kind == "decision" && !decs.Any(d => c.Overlaps(d))) flag = true;
                        if (kind == "action" && !acts.Any(a => c.Overlaps(a))) flag = true;
                        if (flag) unsupported++;
                        if (newFact(it) is not null) invented++;
                        else if (trapped(it) is { } trap && NotesGrounding.Numbers(trap).Count + NotesGrounding.Names(". x " + trap).Count > 0) invented++;
                    }
                ptsTot += notes.KeyPoints.Count; decTot += notes.Decisions.Count; actTot += notes.Actions.Count;
                labD += decs.Count; labA += acts.Count;
                missD += decs.Count(d => !notes.Decisions.Any(x => cited(x).Overlaps(d)));
                missA += acts.Count(a => !notes.Actions.Any(x => cited(x).Overlaps(a)));
            }
            var sorted = secs.Order().ToList();
            var q = new Dictionary<string, object?>
            {
                ["label"] = label, ["set"] = Path.GetFileName(qpath), ["meetings"] = qmeetings.Count, ["failed"] = fails, ["completed"] = n,
                ["cap"] = cap, ["split"] = split, ["fan"] = fan, ["itemsKept"] = kept, ["points"] = ptsTot, ["decisions"] = decTot, ["actions"] = actTot, ["dropped"] = dropped, ["reasons"] = qr,
                ["unsupported"] = unsupported, ["unsupportedPerMeeting"] = Math.Round(unsupported / (double)Math.Max(n, 1), 2), ["invented"] = invented,
                ["missedDecisions"] = missD, ["labelledDecisions"] = labD, ["missedActions"] = missA, ["labelledActions"] = labA,
                ["truncatedItems"] = Writer2.Truncated, ["splits"] = Writer2.Splits, ["capHits"] = Writer2.CapHits,
                ["medianSeconds"] = sorted.Count == 0 ? 0 : Math.Round(sorted[sorted.Count / 2], 1), ["maxSeconds"] = sorted.Count == 0 ? 0 : Math.Round(sorted[^1], 1),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(q, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{label} quality {Path.GetFileName(qpath)}: failed {fails}/{qmeetings.Count} unsupported {unsupported} invented {invented} missedD {missD}/{labD} missedA {missA}/{labA}");
            return 0;
        }

        foreach (var w in workloads)
        {
            int words = w == "30" ? 4500 : 9000; double duration = w == "30" ? 1800 : 3600;
            var entries = new List<Entry>(); int count = 0;
            while (count < words)
                foreach (var e in corpus)
                    if (count < words) { entries.Add(e); count += e.Lines.Sum(l => l[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length); }
            int totalLines = entries.Sum(e => e.Lines.Length);
            double spacing = duration / (totalLines - 0.5);
            var meeting = Build(entries, spacing);
            double last = meeting.Lines.Last().End;
            if (Math.Abs(last - duration) >= 0.01) throw new Exception($"duration precondition failed {last}");
            var numbered = MeetingNotesPrompt.Number(meeting);
            var chunkSizes = MeetingNotesWriter.Chunks(numbered, budget).Select(c => c.Sum(l => SwiftText.Count(MeetingNotesPrompt.Render(l)) + 1)).ToArray();
            Requests.Clear(); Calls = 0; Overflows = 0;
            var sw = Stopwatch.StartNew(); var startUtc = DateTime.UtcNow;
            var rec = new Dictionary<string, object?>
            {
                ["label"] = label, ["workload"] = w + " minutes", ["words"] = count, ["entries"] = entries.Count, ["lines"] = meeting.Lines.Count,
                ["visibleLines"] = numbered.Length, ["chunks"] = chunkSizes.Length, ["chunkChars"] = chunkSizes, ["budget"] = budget,
                ["cap"] = cap, ["split"] = split, ["fan"] = fan, ["concise"] = concise, ["lastEnd"] = last, ["startUtc"] = startUtc.ToString("o"), ["maxTokens"] = maxTokens, ["bounded"] = bounded,
            };
            try
            {
                Writer2.Truncated = Writer2.Splits = Writer2.SplitRecovered = Writer2.CapHits = 0; var outcome = await Writer2.Write(meeting, opts, (msgs, ct) => Generate(http, port, maxTokens, prompted, concise, msgs, ct));
                var n = outcome.Notes;
                rec["result"] = "completed"; rec["seconds"] = sw.Elapsed.TotalSeconds;
                rec["items"] = $"{n.KeyPoints.Count}/{n.Decisions.Count}/{n.Actions.Count}"; rec["dropped"] = n.Dropped;
                rec["reasons"] = outcome.Reasons.ToDictionary(k => k.Key.ToString(), k => k.Value);
                rec["titleKept"] = n.Title is not null; rec["summaryKept"] = n.Summary is not null;
            }
            catch (Exception ex)
            {
                rec["result"] = "failed"; rec["seconds"] = sw.Elapsed.TotalSeconds; rec["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            rec["endUtc"] = DateTime.UtcNow.ToString("o");
            rec["truncatedItems"] = Writer2.Truncated; rec["splits"] = Writer2.Splits; rec["splitRecovered"] = Writer2.SplitRecovered; rec["capHits"] = Writer2.CapHits; rec["requests"] = Calls; rec["overflows"] = Overflows;
            rec["requestDetail"] = Requests.ToList();
            rec["completionTokens"] = Requests.Sum(r => (int)(r.GetValueOrDefault("completion") ?? 0));
            rec["promptTokens"] = Requests.Sum(r => (int)(r.GetValueOrDefault("prompt") ?? 0));
            rec["extractRequests"] = Requests.Count(r => (string)r["stage"]! == "extract");
            rec["mergeRequests"] = Requests.Count(r => (string)r["stage"]! == "merge");
            rec["harnessPeakWorkingSet"] = proc.PeakWorkingSet64;
            results.Add(rec);
            Console.WriteLine($"{label} {w}m: {rec["result"]} {(double)rec["seconds"]!:F0}s req={Calls} chunks={chunkSizes.Length} {rec.GetValueOrDefault("error")}");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    static async Task<string> Generate(HttpClient http, int port, int maxTokens, bool bounded, bool concise, CleanupMessages msgs, CancellationToken ct)
    {
        Calls++;
        string stage = msgs.User.StartsWith("<parts-") ? "merge" : "extraction";
        var system = bounded ? msgs.System + "\nReturn at most 8 POINT, 10 DECISION and 12 ACTION lines. Prefer the most important supported items." : msgs.System;
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = msgs.User }),
            ["temperature"] = 0, ["seed"] = 1234, ["max_tokens"] = maxTokens,
        };
        var d = new Dictionary<string, object?> { ["stage"] = stage == "merge" ? "merge" : "extract", ["systemChars"] = system.Length, ["userChars"] = msgs.User.Length };
        var sw = Stopwatch.StartNew();
        lock (Requests) Requests.Add(d);
        using var resp = await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        d["seconds"] = sw.Elapsed.TotalSeconds; d["status"] = (int)resp.StatusCode;
        var root = JsonNode.Parse(text);
        d["prompt"] = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0; d["completion"] = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        if (!resp.IsSuccessStatusCode)
        {
            d["finish"] = "http-error"; d["errorType"] = root?["error"]?["type"]?.GetValue<string>();
            if ((string?)d["errorType"] == "exceed_context_size_error")
            {
                Overflows++;
                throw new ProviderException(ProviderError.PayloadTooLarge(ProviderID.CustomAPI, null));
            }
            throw new NotesFailure("llama.cpp request failed.");
        }
        var choice = root!["choices"]![0]!;
        var finish = choice["finish_reason"]?.GetValue<string>(); d["finish"] = finish;
        if (finish == "length") throw new OutputExhausted(stage);
        var content = choice["message"]?["content"]?.GetValue<string>();
        if (finish != "stop" || string.IsNullOrEmpty(content)) throw new NotesFailure("llama.cpp did not return a complete reply.");
        return content;
    }
}

