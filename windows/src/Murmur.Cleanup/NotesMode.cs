using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

// The notes mode of the helper: the on-device meeting notes model (Gemma 3 4B, Q4_K_M), the same LLamaSharp stack as cleanup.
//   Murmur.Cleanup.exe <model folder> --notes [--gpu]
//   {"id":1,"system":"...","user":"..."}  ->  {"id":1,"text":"..."} | {"id":1,"error":"too-long"|"output-cap"|"failed"}
// "too-long": the prompt does not leave room for a reply in the context. "output-cap": the reply stopped at the reply-token cap.
// Context 8,192 tokens, greedy decoding, 1,200 reply tokens: the settings the notes benchmark measured. No networking and no transcript is logged.
// On CPU the process lowers its own priority, so a long run never competes with the foreground.
//   Murmur.Cleanup.exe --probe   ->  {"gpu":true|false}: whether the Vulkan backend loads and can offload layers (the check behind "Use GPU when available").
static class NotesMode
{
    const int ContextTokens = 8192;
    const int MaximumOutputTokens = 1200;

    static void Send(object o)
    {
        Console.Out.Write(JsonSerializer.Serialize(o) + "\n");
        Console.Out.Flush();
    }

    public static int Probe()
    {
        bool gpu = false;
        try
        {
            NativeLibraryConfig.All.WithCuda(false).WithVulkan(true).WithAutoFallback(false);
            NativeLogConfig.llama_log_set((_, _) => { });
            gpu = NativeApi.llama_supports_gpu_offload();
        }
        catch (Exception) { }
        Send(new { gpu });
        return 0;
    }

    // Gemma 3's chat template: no system role, so the instructions lead the user turn.
    static string Template(string system, string user) =>
        $"<start_of_turn>user\n{system}\n\n{user}<end_of_turn>\n<start_of_turn>model\n";

    public static async Task<int> Run(string[] args, int physicalCores)
    {
        var modelPath = Directory.Exists(args[0]) ? Directory.EnumerateFiles(args[0], "*.gguf").FirstOrDefault() ?? args[0] : args[0];
        bool gpu = args.Contains("--gpu");
        ModelParams parameters;
        LLamaWeights weights;
        try
        {
            NativeLibraryConfig.All.WithCuda(false).WithVulkan(gpu).WithAutoFallback(!gpu);
            if (Environment.GetEnvironmentVariable("MURMUR_CLEANUP_DEBUG") != "1") NativeLogConfig.llama_log_set((_, _) => { });
            if (gpu && !NativeApi.llama_supports_gpu_offload()) { Send(new { ready = false }); return 3; }
            parameters = new ModelParams(modelPath) { ContextSize = ContextTokens, GpuLayerCount = gpu ? int.MaxValue : 0 };
            if (!gpu) parameters.Threads = parameters.BatchThreads = physicalCores;
            weights = LLamaWeights.LoadFromFile(parameters);
            if (gpu) { using var probe = weights.CreateContext(parameters); }
        }
        catch (Exception e) { if (Environment.GetEnvironmentVariable("MURMUR_CLEANUP_DEBUG") == "1") Console.Error.WriteLine(e); Send(new { ready = false }); return 3; }
        using var loaded = weights;
        if (!gpu)
        {
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        }

        Send(new { ready = true, gpu });
        while (Console.ReadLine() is { } line)
        {
            int id = -1; string system, user;
            try
            {
                using var doc = JsonDocument.Parse(line);
                id = doc.RootElement.GetProperty("id").GetInt32();
                system = doc.RootElement.GetProperty("system").GetString()!;
                user = doc.RootElement.GetProperty("user").GetString()!;
            }
            catch { Send(new { id, error = "failed" }); continue; }
            try
            {
                var prompt = Template(system, user);
                // BOS is added by the tokenizer; leave room for the whole reply.
                if (weights.Tokenize(prompt, true, true, Encoding.UTF8).Length + MaximumOutputTokens > ContextTokens) { Send(new { id, error = "too-long" }); continue; }
                var executor = new StatelessExecutor(weights, parameters);
                var inference = new InferenceParams { MaxTokens = MaximumOutputTokens, AntiPrompts = ["<end_of_turn>"], SamplingPipeline = new GreedySamplingPipeline() };
                var output = new StringBuilder();
                int pieces = 0;
                await foreach (var piece in executor.InferAsync(prompt, inference)) { output.Append(piece); pieces++; }
                var text = output.ToString();
                int end = text.IndexOf("<end_of_turn>", StringComparison.Ordinal);
                if (end >= 0) text = text[..end];
                if (pieces >= MaximumOutputTokens && end < 0) Send(new { id, error = "output-cap" });
                else Send(new { id, text });
            }
            catch { Send(new { id, error = "failed" }); }
        }
        return 0;
    }
}
