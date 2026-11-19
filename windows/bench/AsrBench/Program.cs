using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murmur.Core;

// Writes JSON lines: one "environment" line, then per fixture a "single" line (one-shot process: launch + load + transcribe, wall clock)
// and "warm" lines (the app's WarmRunner: --serve process kept alive; pass 1 then pass 2, wall clock of one request). No audio is written; transcripts go only to the output file.
var a = args.ToList();
string Arg(string name, string? fallback = null) => a.IndexOf(name) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : fallback ?? throw new ArgumentException("missing " + name);
var helper = Arg("--helper"); var models = Arg("--models"); var fixtures = Arg("--fixtures"); var outPath = Arg("--out");
var vocabulary = Arg("--vocabulary", ""); var lexicon = Arg("--lexicon", ""); var label = Arg("--label", "onnx");
var launch = HelperLaunch.Make(models, vocabulary.Length > 0 ? vocabulary : null, lexicon.Length > 0 ? lexicon : null) ?? throw new InvalidOperationException("model folder incomplete");
var wavs = Directory.GetFiles(fixtures, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToArray();
if (wavs.Length == 0) throw new InvalidOperationException("no wavs");
using var output = new StreamWriter(outPath, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
void Write(JsonObject o) { output.WriteLine(o.ToJsonString()); output.Flush(); }

Write(new JsonObject
{
    ["type"] = "environment", ["engine"] = label, ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ["arch"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
    ["cpu"] = Cpu(), ["logical_cpus"] = Environment.ProcessorCount, ["ram_gb"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
    ["dotnet"] = Environment.Version.ToString(), ["threads"] = Environment.GetEnvironmentVariable("MURMUR_ASR_THREADS") ?? "default", ["vocabulary"] = vocabulary.Length > 0,
});

foreach (var wav in wavs)
{
    var clock = Stopwatch.StartNew();
    var line = await ModelHelper.RunOnceAsync(helper, launch, wav);
    var answer = ModelHelper.Answer(line);
    Write(Row("single", wav, answer, clock.Elapsed.TotalSeconds, pass: 1));
}

using var runner = new WarmRunner();
var ready = Stopwatch.StartNew();
await runner.PrepareAsync(helper, launch);
Write(new JsonObject { ["type"] = "warm-ready", ["engine"] = label, ["wall_seconds"] = ready.Elapsed.TotalSeconds });
for (int pass = 1; pass <= 2; pass++)
    foreach (var wav in wavs)
    {
        var clock = Stopwatch.StartNew();
        var line = await runner.TranscribeAsync(wav, helper, launch);
        Write(Row("warm", wav, ModelHelper.Answer(line), clock.Elapsed.TotalSeconds, pass));
    }
return 0;

JsonObject Row(string mode, string wav, JsonObject? answer, double wall, int pass) => new()
{
    ["type"] = "clip", ["engine"] = label, ["mode"] = mode, ["pass"] = pass, ["wav"] = Path.GetFileName(wav), ["ok"] = answer?["text"] is not null,
    ["text"] = answer?["text"]?.GetValue<string>() ?? "", ["wall_seconds"] = wall,
    ["helper_load_seconds"] = answer?["load_seconds"]?.DeepClone(), ["helper_cold_seconds"] = answer?["cold_transcribe_seconds"]?.DeepClone(),
    ["helper_warm_seconds"] = answer?["warm_transcribe_seconds"]?.DeepClone(), ["helper_prepare_seconds"] = answer?["audio_prepare_seconds"]?.DeepClone(),
};

static string Cpu()
{
    try
    {
        if (OperatingSystem.IsWindows()) return Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string ?? "unknown";
        if (OperatingSystem.IsMacOS())
        {
            using var p = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string") { RedirectStandardOutput = true })!;
            return p.StandardOutput.ReadToEnd().Trim();
        }
    }
    catch (Exception) { }
    return "unknown";
}
