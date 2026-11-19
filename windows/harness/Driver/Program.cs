// Session-1 driver for the M2 exit harness.
//   Driver run <config.json>            20 synthetic dictations through the running app (needs Murmur.exe + FakeProvider)
//   Driver selftest-targets <workdir>   launch each target, type with SendInput, read back through UI Automation (no app)
//   Driver selftest-clipboard           put text with the exclusion formats on the clipboard and check the watcher sees them
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Murmur.Harness.Driver;

record Check(string Name, string Result, string Detail); // Result: pass, fail, skip
record Dictation(string Id, string Target, double Seconds, List<Check> Checks, double? LatencyMs, string? InsertionStatus, string? ReadMethod)
{
    public bool Passed => Checks.All(c => c.Result != "fail");
}
record Fixture(string Id, string Wav, string Spoken, string Cleanup, double Seconds);
record Config(string DataDir, string OutDir, string ProviderUrl, string FixturesDir, string PageFile, int Count);

static class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        try
        {
            return args.FirstOrDefault() switch
            {
                "run" => Run(JsonSerializer.Deserialize<Config>(File.ReadAllText(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).GetAwaiter().GetResult(),
                "selftest-targets" => SelfTestTargets(args[1], args.Skip(2).ToArray()),
                "selftest-clipboard" => SelfTestClipboard(),
                "probe" => Probe(args[1], args[2]),
                "keytest" => KeyTest(args[1], args[2]),
                _ => Fail("usage: Driver run <config.json> | selftest-targets <dir> | selftest-clipboard"),
            };
        }
        catch (Exception e) { Console.WriteLine("DRIVER ERROR " + e); return 2; }
    }
    static int Fail(string m) { Console.WriteLine(m); return 2; }

    // ------------------------------------------------------------------ run
    static async Task<int> Run(Config cfg)
    {
        Directory.CreateDirectory(cfg.OutDir);
        var fixtures = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(cfg.FixturesDir, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var pageUrl = new Uri(Path.GetFullPath(cfg.PageFile)).AbsoluteUri;
        var targets = new List<Target> { new NotepadTarget(), new EdgeTarget { PageUrl = pageUrl }, new CodeTarget() };
        var plan = fixtures.Take(cfg.Count).Select((f, i) => (f, t: targets[i % targets.Count])).ToList();
        var eventsPath = Path.Combine(cfg.DataDir, "diagnostics", "events.jsonl");
        var synth = Path.Combine(cfg.DataDir, "synthetic");
        var http = new HttpClient { BaseAddress = new Uri(cfg.ProviderUrl) };
        using var clip = new ClipboardWatcher();
        var results = new List<Dictation>();
        var screenshots = new List<string>();
        var historyNote = "";

        Console.WriteLine("Waiting for the app's ready event...");
        var ev = new EventTail(eventsPath);
        if (ev.WaitFor(e => e.Name == "ready", 60) is null) { Console.WriteLine("FATAL: no ready event from the app"); WriteResults(cfg, results, screenshots, "app never became ready"); return 1; }
        ev.WaitFor(e => e.Name == "hook-installed", 10);

        // All targets are opened up front and kept open: when one closes, Windows hands the foreground to whatever is next in
        // z-order (on the VM an elevated PowerShell), and injected input cannot reach an elevated foreground window.
        var started = new List<Target>();
        foreach (var t in targets)
        {
            var mine = plan.Where(p => p.t == t).Select(p => p.f).ToList();
            if (mine.Count == 0) continue;
            try { t.Start(cfg.OutDir); started.Add(t); Console.WriteLine($"started {t.Name}"); }
            catch (Exception e)
            {
                foreach (var f in mine) results.Add(new Dictation(f.Id, t.Name, f.Seconds, new() { new("target-start", "fail", e.Message) }, null, null, null));
                t.Close();
            }
        }
        try
        {
            foreach (var t in started)
            {
                var mine = plan.Where(p => p.t == t).Select(p => p.f).ToList();
                Console.WriteLine($"== {t.Name}: {mine.Count} dictations");
                foreach (var f in mine)
                {
                    var d = await Dictate(cfg, t, f, synth, http, ev, clip);
                    results.Add(d);
                    Console.WriteLine($"  {(d.Passed ? "PASS" : "FAIL")} {f.Id} latency={d.LatencyMs:F0}ms " + string.Join(" ", d.Checks.Where(c => c.Result == "fail").Select(c => $"[{c.Name}: {c.Detail}]")));
                }
                var shot = Path.Combine(cfg.OutDir, $"screenshot-{t.Name}.png");
                try { t.Screenshot(shot); screenshots.Add(shot); } catch (Exception e) { Console.WriteLine("  screenshot failed: " + e.Message); }
            }
        }
        finally { foreach (var t in started) t.Close(); }
        WriteResults(cfg, results, screenshots, historyNote);
        return results.All(r => r.Passed) && results.Count == Math.Min(cfg.Count, fixtures.Count) ? 0 : 1;
    }

    static async Task<Dictation> Dictate(Config cfg, Target t, Fixture f, string synth, HttpClient http, EventTail ev, ClipboardWatcher clip)
    {
        var checks = new List<Check>();
        void Add(string n, bool ok, string d = "") => checks.Add(new(n, ok ? "pass" : "fail", d));
        void Skip(string n, string d) => checks.Add(new(n, "skip", d));
        double? latency = null; string? status = null;
        try
        {
            if (!t.Clear()) Add("editor-cleared", false, "editor did not read back empty");
            var sentinel = "sentinel-" + Guid.NewGuid();
            Native.SetClipboard(sentinel, false);
            Directory.CreateDirectory(synth);
            File.WriteAllText(Path.Combine(synth, "next.txt"), Path.GetFullPath(Path.Combine(cfg.FixturesDir, f.Wav)));
            var r = await http.PostAsync("/control/next", new StringContent(JsonSerializer.Serialize(new { id = f.Id }), Encoding.UTF8, "application/json"));
            r.EnsureSuccessStatusCode();
            if (!t.Focus())
            {
                try
                {
                    var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                    using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                    bmp.Save(Path.Combine(cfg.OutDir, $"focus-failure-{t.Name}.png"));
                } catch (Exception) { }
                Add("target-focus", false, "could not bring the target to the foreground; foreground process is " + Native.ForegroundProcess() + "; " + t.FocusLog); return new(f.Id, t.Name, f.Seconds, checks, null, null, null); }
            Thread.Sleep(300);
            clip.Sentinel = sentinel; clip.DictationTexts = new() { f.Cleanup.Trim() }; // refined below from History
            ev.Mark(); clip.Clear();

            // hold Right Ctrl for the WAV duration + 0.4 s
            Native.RightCtrl(true);
            Thread.Sleep(100); var keyDown = Native.IsRightCtrlDown();
            Thread.Sleep(TimeSpan.FromSeconds(f.Seconds + 0.3));
            Native.RightCtrl(false);
            var release = DateTime.UtcNow;

            var paste = ev.WaitFor(e => e.Name == "paste" || e.Name == "error", 20);
            if (paste is null) { Add("paste-event", false, $"no paste event within 20 s; driver saw Right Ctrl down: {keyDown}; target in foreground: {Native.GetForegroundWindow() == t.Hwnd}; events: " + string.Join(",", ev.Seen())); return new(f.Id, t.Name, f.Seconds, checks, null, null, null); }
            if (paste.Name == "error") { Add("paste-event", false, $"error event stage={paste.Str("stage")} reason={paste.Str("reason")}"); return new(f.Id, t.Name, f.Seconds, checks, null, null, null); }
            Add("paste-event", true);
            latency = (paste.Time - release).TotalMilliseconds;
            status = paste.Str("status");
            var historyId = paste.Str("historyId");

            // (d) insertion status
            Add("insertion-status", status is "verified" or "unverified", $"status={status} reason={paste.Str("reason")}");

            // expected final text from History
            var finalText = await ReadHistoryFinal(cfg.DataDir, historyId);
            if (finalText is null) { Add("history-entry", false, "history entry/finalText not found for " + historyId); finalText = f.Cleanup; }
            else Add("history-entry", true);
            clip.DictationTexts = new() { finalText.Trim() };
            Add("cleanup-matches-manifest", finalText.Trim() == f.Cleanup.Trim(), "advisory: shows whether the fake provider's cleanup reached the paste unchanged");

            // (a) UI Automation read-back
            string? read = null;
            for (int i = 0; i < 12 && (read = t.Read()) is not null && !read.Contains(finalText.Trim()); i++) Thread.Sleep(250);
            bool uia = read is not null;
            if (uia) Add("readback-contains-final-text", read!.Contains(finalText.Trim()), $"via {t.ReadMethod}; field length {read.Length}, expected {finalText.Trim().Length}");
            else Skip("readback-contains-final-text", "editor exposes no text through UI Automation (see copy fallback below)");

            // (b) clipboard restored
            var restored = ev.WaitFor(e => e.Name == "clipboard-restored", 10);
            Add("clipboard-restored-event", restored is not null);
            string? now = null;
            var end = DateTime.UtcNow.AddSeconds(2);
            do { now = Native.GetClipboardText(); if (now == sentinel) break; Thread.Sleep(100); } while (DateTime.UtcNow < end);
            Add("clipboard-holds-sentinel", now == sentinel, now == sentinel ? "restored" : "clipboard differs from the sentinel (content not logged)");

            // (c) formats seen while the app staged its item
            Thread.Sleep(500);
            var changes = clip.Snapshot();
            var (staged, missing) = ClipboardWatcher.CheckExclusions(changes);
            Add("staged-item-has-exclusion-formats", staged && missing.Length == 0,
                staged ? (missing.Length == 0 ? "all three formats present" : "missing: " + string.Join(",", missing)) : "the dictation item was never seen on the clipboard");
            Add("sentinel-restored-seen", changes.Any(c => c.Kind == "sentinel"), "clipboard changes: " + string.Join(" > ", changes.Select(c => c.Kind)));

            // (c) clipboard history
            await Task.Delay(1500);
            var (ok, texts, note) = await ClipboardHistory.ReadAsync();
            if (!ok) Skip("history-has-no-dictation-text", "not verifiable on this machine: " + note + "; relying on the format check");
            else Add("history-has-no-dictation-text", !texts.Any(x => x.Contains(finalText.Trim(), StringComparison.Ordinal)), note);

            // (a, fallback) editors that expose no UIA text: select all + copy, verification only
            if (!uia)
            {
                t.Focus(); Thread.Sleep(150);
                Native.Chord(Native.VK_CONTROL, 'A'); Native.Chord(Native.VK_CONTROL, 'C'); Thread.Sleep(400);
                var copied = Native.GetClipboardText();
                t.ReadMethod = "select-all + copy (verification only)";
                Add("readback-contains-final-text", copied?.Contains(finalText.Trim()) == true, "via select-all + copy fallback");
            }
        }
        catch (Exception e) { Add("driver-exception", false, e.GetType().Name + ": " + e.Message); }
        return new(f.Id, t.Name, f.Seconds, checks, latency, status, t.ReadMethod);
    }

    static async Task<string?> ReadHistoryFinal(string dataDir, string? id)
    {
        if (id is null) return null;
        var dir = Path.Combine(dataDir, "history");
        for (int i = 0; i < 20; i++) // the final write lands just after the paste
        {
            var file = Path.Combine(dir, id + ".json");
            if (File.Exists(file))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                    if (doc.RootElement.TryGetProperty("insertionStatus", out var s) && s.GetString() == "insertion-pending") { await Task.Delay(250); continue; }
                    if (doc.RootElement.TryGetProperty("finalText", out var ft) && ft.ValueKind == JsonValueKind.String) return ft.GetString();
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(250);
        }
        return null;
    }

    static void WriteResults(Config cfg, List<Dictation> results, List<string> shots, string note)
    {
        File.WriteAllText(Path.Combine(cfg.OutDir, "results.json"), JsonSerializer.Serialize(new { total = results.Count, passed = results.Count(r => r.Passed), screenshots = shots.Select(Path.GetFileName), note, dictations = results }, Json));
        var sb = new StringBuilder();
        sb.AppendLine($"Murmur M2 exit harness: {results.Count(r => r.Passed)}/{results.Count} dictations passed");
        foreach (var g in results.GroupBy(r => r.Target))
            sb.AppendLine($"  {g.Key}: {g.Count(r => r.Passed)}/{g.Count()}  read-back: {g.Select(r => r.ReadMethod).FirstOrDefault(m => m is not null) ?? "n/a"}");
        var lat = results.Where(r => r.LatencyMs.HasValue).Select(r => r.LatencyMs!.Value).OrderBy(x => x).ToList();
        if (lat.Count > 0) sb.AppendLine($"  release-to-paste latency ms: min {lat[0]:F0}, median {lat[lat.Count / 2]:F0}, max {lat[^1]:F0}");
        sb.AppendLine("\nPer check (pass/fail/skip):");
        foreach (var g in results.SelectMany(r => r.Checks).GroupBy(c => c.Name))
            sb.AppendLine($"  {g.Key,-36} {g.Count(c => c.Result == "pass")} pass, {g.Count(c => c.Result == "fail")} fail, {g.Count(c => c.Result == "skip")} skip");
        sb.AppendLine("\nPer dictation:");
        foreach (var r in results)
        {
            sb.AppendLine($"  {(r.Passed ? "PASS" : "FAIL")} {r.Target,-8} {r.Id,-18} {(r.LatencyMs.HasValue ? $"{r.LatencyMs:F0} ms" : "no paste"),-10} status={r.InsertionStatus}");
            foreach (var c in r.Checks.Where(c => c.Result != "pass")) sb.AppendLine($"       {c.Result.ToUpper()} {c.Name}: {c.Detail}");
        }
        File.WriteAllText(Path.Combine(cfg.OutDir, "summary.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
    }

    // ------------------------------------------------------------------ self tests
    static int SelfTestTargets(string work, string[] only)
    {
        var page = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "textarea.html"));
        var targets = new List<Target> { new NotepadTarget(), new EdgeTarget { PageUrl = new Uri(page).AbsoluteUri }, new CodeTarget() };
        int bad = 0;
        foreach (var t in targets.Where(t => only.Length == 0 || only.Contains(t.Name)))
        {
            try
            {
                t.Start(work);
                var known = "Selftest 4721 café, \"quoted\"; ok?";
                t.Clear(); t.Focus(); Thread.Sleep(300);
                Native.TypeText(known);
                string? read = null;
                for (int i = 0; i < 12 && ((read = t.Read()) is null || !read.Contains(known)); i++) Thread.Sleep(250);
                try { t.Screenshot(Path.Combine(work, t.Name + "-selftest.png")); } catch (Exception) { }
                var ok = read is not null && read.Contains(known);
                Console.WriteLine($"fg-is-target={Native.GetForegroundWindow() == t.Hwnd}");
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {t.Name}: typed {known.Length} chars, read-back {(read is null ? "unavailable" : read.Length + " chars")} via {t.ReadMethod}; clear-after={t.Clear()} (len now {t.Read()?.Length})");
                if (!ok) { bad++; Console.WriteLine("  expected: " + string.Join(" ", known.Select(c => ((int)c).ToString("X")))); Console.WriteLine("  got:      " + string.Join(" ", (read ?? "").Select(c => ((int)c).ToString("X")))); }
                // The app inserts by paste, so the read-back must also see pasted text, not only typed text.
                var pasted = "Pasted 4721 text, with \"quotes\".";
                t.Clear(); t.Focus(); Native.SetClipboard(pasted, true); Thread.Sleep(200);
                Native.Chord(Native.VK_CONTROL, 'V');
                for (int i = 0; i < 12 && ((read = t.Read()) is null || !read.Contains(pasted)); i++) Thread.Sleep(250);
                var pok = read is not null && read.Contains(pasted);
                Console.WriteLine($"{(pok ? "PASS" : "FAIL")} {t.Name}: pasted text read back ({(read is null ? "unavailable" : read.Length + " chars")})");
                if (!pok) bad++;
                t.Clear();
            }
            catch (Exception e) { Console.WriteLine($"FAIL {t.Name}: {e.Message}"); bad++; }
            finally { t.Close(); }
        }
        return bad;
    }

    /// <summary>Dev aid: prints the editable UIA elements of a target (names, types, patterns; never content).</summary>
    static int Probe(string name, string work)
    {
        var page = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "textarea.html"));
        Target t = name switch { "notepad" => new NotepadTarget(), "edge" => new EdgeTarget { PageUrl = new Uri(page).AbsoluteUri }, _ => new CodeTarget() };
        t.Start(work);
        Thread.Sleep(2000);
        t.Dump();
        t.Focus(); Native.TypeText("0123456789abcdefghij0123456789"); Thread.Sleep(1500);
        Console.WriteLine("typed: " + t.Read()?.Length);
        Native.Chord(Native.VK_CONTROL, 'A'); Thread.Sleep(1000); Console.WriteLine("after ctrl+a: " + t.Read()?.Length);
        Native.Tap(Native.VK_DELETE); Thread.Sleep(1000); Console.WriteLine("after delete: " + t.Read()?.Length);
        t.Close();
        return 0;
    }

    /// <summary>Dev aid: does an injected Right Ctrl register in the system key state, with and without the target in front?</summary>
    static int KeyTest(string name, string work)
    {
        bool Press() { Native.RightCtrl(true); Thread.Sleep(150); var d = Native.IsRightCtrlDown(); Native.RightCtrl(false); Thread.Sleep(150); return d; }
        Console.WriteLine("no target: " + Press());
        var page = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "textarea.html"));
        Target t = name switch { "notepad" => new NotepadTarget(), "edge" => new EdgeTarget { PageUrl = new Uri(page).AbsoluteUri }, _ => new CodeTarget() };
        if (name == "edge-after-notepad") { var n = new NotepadTarget(); n.Start(work); Console.WriteLine("notepad fg=" + (Native.GetForegroundWindow() == n.Hwnd)); n.Close(); Thread.Sleep(1000); Console.WriteLine("after notepad close, fg process: " + Native.ForegroundProcess()); t = new EdgeTarget { PageUrl = new Uri(page).AbsoluteUri }; }
        t.Start(work);
        Console.WriteLine("fg process after start: " + Native.ForegroundProcess());
        Console.WriteLine($"fg-is-target={Native.GetForegroundWindow() == t.Hwnd}; target started: " + Press());
        Console.WriteLine("Focus() => " + t.Focus() + ", fg process " + Native.ForegroundProcess());
        t.Focus(); Console.WriteLine("after Focus: " + Press());
        t.Clear(); Console.WriteLine("after Clear: " + Press());
        t.Close();
        return 0;
    }

    static int SelfTestClipboard()
    {
        int bad = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) bad++; }
        using var w = new ClipboardWatcher();
        w.Sentinel = "sentinel-selftest"; w.DictationTexts = new() { "synthetic dictation text" };
        Native.SetClipboard("sentinel-selftest", false); Thread.Sleep(300);
        Native.SetClipboard("  synthetic dictation text", true); Thread.Sleep(300);
        Native.SetClipboard("sentinel-selftest", false); Thread.Sleep(300);
        var cs = w.Snapshot();
        Console.WriteLine("changes: " + string.Join(" > ", cs.Select(c => c.Kind)));
        Check(cs.Select(c => c.Kind).SequenceEqual(new[] { "sentinel", "dictation", "sentinel" }), "listener recorded sentinel > dictation > sentinel");
        var (staged, missing) = ClipboardWatcher.CheckExclusions(cs);
        Check(staged && missing.Length == 0, "all three exclusion formats seen on the dictation item");
        Native.SetClipboard("synthetic dictation text", false); Thread.Sleep(300);
        var (s2, m2) = ClipboardWatcher.CheckExclusions(w.Snapshot());
        Check(s2 && m2.Length == 3, "a dictation item without the formats is reported missing all three");
        var h = ClipboardHistory.ReadAsync().GetAwaiter().GetResult();
        Console.WriteLine($"clipboard history: verifiable={h.ok} ({h.note}); items with text={h.texts.Count}");
        return bad;
    }
}

/// <summary>Reads events.jsonl incrementally (the app appends while we read).</summary>
sealed class EventTail
{
    public sealed record Ev(string Name, DateTime Time, JsonElement Raw)
    {
        public string? Str(string k) => Raw.TryGetProperty(k, out var v) ? v.ToString() : null;
    }
    readonly string path; long offset; readonly List<Ev> seen = new(); int cursor;
    public EventTail(string path) { this.path = path; }
    public string[] Seen() => seen.Skip(cursor).Select(e => e.Name).ToArray();
    /// <summary>Events before this point are ignored by WaitFor.</summary>
    public void Mark() { Pull(); cursor = seen.Count; }

    void Pull()
    {
        if (!File.Exists(path)) return;
        byte[] buf;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length < offset) offset = 0;
            fs.Seek(offset, SeekOrigin.Begin);
            buf = new byte[fs.Length - offset];
            fs.ReadExactly(buf);
        }
        int end = Array.LastIndexOf(buf, (byte)'\n') + 1; // only complete lines; a partial one is read next time
        offset += end;
        foreach (var line in Encoding.UTF8.GetString(buf, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var el = JsonDocument.Parse(line).RootElement.Clone();
                seen.Add(new Ev(el.GetProperty("event").GetString()!, el.GetProperty("t").GetDateTime().ToUniversalTime(), el));
            }
            catch (JsonException) { }
        }
    }

    public Ev? WaitFor(Func<Ev, bool> match, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            Pull();
            for (int i = cursor; i < seen.Count; i++) if (match(seen[i])) { cursor = i + 1; return seen[i]; }
            Thread.Sleep(50);
        } while (DateTime.UtcNow < end);
        return null;
    }
}
