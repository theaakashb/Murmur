using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

// Managed local cleanup helper, the Windows counterpart of module-one/cleanup/Runner (LocalCleanupRunner):
// one JSON object per line on stdin, one reply per line on stdout.
//   {"id":1,"text":"...","tone":"formal"|"casual","terms":["..."]}  ->  {"id":1,"text":"..."} | {"id":1,"error":"too-long"|"failed"}
// Notes mode (--notes) and the GPU probe (--probe) are in NotesMode.cs. No networking, no transcript logging. A fresh context per request. argv[0] is the verified model folder (its .gguf is loaded) or the GGUF itself.
// The app owns cancellation by terminating this process.

if (args.Length < 1) return 2;
if (args[0] == "--probe") return NotesMode.Probe();
if (args.Contains("--notes")) return await NotesMode.Run(args, CpuTopology.PhysicalCoreCount());
const int MaximumInputTokens = 400;   // Over system line + header + text, counted on the plain string as main.swift does.
const int MaximumOutputTokens = 512;
const string System_ = "Clean up this dictation.";

static void Send(object o)
{
    var json = JsonSerializer.Serialize(o);
    Console.Out.Write(json + "\n");
    Console.Out.Flush();
}

static string Header(string tone, IEnumerable<string> terms)
{
    var lines = new List<string>();
    if (tone == "formal") lines.Add("[tone: formal]");
    var vocab = terms.Take(12).Select(t => t.Replace(";", ",").Replace("\n", " ")).ToList();
    if (vocab.Count > 0) lines.Add("[vocab: " + string.Join("; ", vocab) + "]");
    return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
}

// Qwen3.5's chat template with enable_thinking false: an empty think block opens the assistant turn.
static string Chatml(string prompt) =>
    $"<|im_start|>system\n{System_}<|im_end|>\n<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

// The app passes the model folder (as the Mac's runner does); the folder holds exactly the pinned GGUF.
var modelPath = Directory.Exists(args[0]) ? Directory.EnumerateFiles(args[0], "*.gguf").FirstOrDefault() ?? args[0] : args[0];
bool gpu = args.Length > 1 && args[1] == "--gpu";
ModelParams contextParams;
LLamaWeights weights;
try
{
    NativeLibraryConfig.All.WithCuda(false).WithVulkan(gpu).WithAutoFallback(!gpu);
    if (Environment.GetEnvironmentVariable("MURMUR_CLEANUP_DEBUG") != "1") NativeLogConfig.llama_log_set((_, _) => { });
    if (gpu && !NativeApi.llama_supports_gpu_offload()) { Send(new { ready = false }); return 3; }
    contextParams = new ModelParams(modelPath) { ContextSize = 2048, GpuLayerCount = gpu ? int.MaxValue : 0 };
    if (!gpu)
        contextParams.Threads = contextParams.BatchThreads = Math.Min(8, CpuTopology.PhysicalCoreCount());
    weights = LLamaWeights.LoadFromFile(contextParams);
    if (gpu)
    {
        using var probe = weights.CreateContext(contextParams);
    }
}
catch (Exception e) { if (Environment.GetEnvironmentVariable("MURMUR_CLEANUP_DEBUG") == "1") Console.Error.WriteLine(e); Send(new { ready = false }); return 3; }
using var loadedWeights = weights;

if (args.Length > 1 && args[1] == "--tokenize")   // Parity test aid: prints token IDs of each stdin line (JSON string) for plain and templated text.
{
    Send(new { ready = true });
    while (Console.ReadLine() is { } l)
        Send(new { ids = weights.Tokenize(JsonSerializer.Deserialize<string>(l)!, false, true, Encoding.UTF8).Select(t => (int)t).ToArray() });
    return 0;
}

Send(new { ready = true });
while (Console.ReadLine() is { } line)
{
    int id; string text, tone = "casual"; List<string> terms = [];
    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        id = root.GetProperty("id").GetInt32();
        text = root.GetProperty("text").GetString()!;
        if (root.TryGetProperty("tone", out var t) && t.ValueKind == JsonValueKind.String) tone = t.GetString()!;
        if (root.TryGetProperty("terms", out var ts) && ts.ValueKind == JsonValueKind.Array)
            terms = ts.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }
    catch { Send(new { id = -1, error = "failed" }); continue; }

    var prompt = Header(tone, terms) + text;
    try
    {
        if (weights.Tokenize(System_ + "\n" + prompt, false, false, Encoding.UTF8).Length > MaximumInputTokens) { Send(new { id, error = "too-long" }); continue; }
        var executor = new StatelessExecutor(weights, contextParams);
        var inference = new InferenceParams { MaxTokens = MaximumOutputTokens, SamplingPipeline = new GreedySamplingPipeline() };
        var output = new StringBuilder();
        await foreach (var piece in executor.InferAsync(Chatml(prompt), inference)) output.Append(piece);
        Send(new { id, text = output.ToString() });
    }
    catch { Send(new { id, error = "failed" }); }
}
return 0;

static class CpuTopology
{
    const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref int length);

    public static int PhysicalCoreCount()
    {
        int logical = Environment.ProcessorCount;
        if (!OperatingSystem.IsWindows()) return logical;

        int length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref length);
        if (length <= 0) return logical;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return logical;
            int count = 0;
            // Each processor-core record starts with a relationship and a byte size.
            for (int offset = 0; offset < length;)
            {
                if (length - offset < 8) return logical;
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || size > length - offset) return logical;
                offset += size;
                count++;
            }
            return count > 0 ? Math.Min(count, logical) : logical;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
