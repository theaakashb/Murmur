using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murmur.Core;
using SherpaOnnx;

namespace Murmur.Speech;

// The ModelBench CLI and line protocol (module-one/models/Runner/Sources/ModelBench/ModelBench.swift) on sherpa-onnx.
// Nothing here logs a transcript, a path, a term or an exception message: every failure is a content-free line.
static class Program
{
    const string Usage = "Usage: MurmurSpeech MODEL_DIRECTORY (AUDIO_FILE [--single] | --serve | --stream SAMPLE_RATE | --diarize KEPT_WAV THRESHOLD --embedding EMBEDDING_ONNX) [--gpu] [--vocabulary VOCABULARY_JSON] [--lexicon WORD_LIST] [--parakeet-tdt | --whisper-turbo]";

    static readonly Stream StdOut = Console.OpenStandardOutput();

    static int Main(string[] argv)
    {
        var args = argv.ToList();
        bool gpu = args.Remove("--gpu");
        // The multilingual runners (Multilingual.swift): MODEL_DIRECTORY (AUDIO_FILE [--single] | --serve) plus the runner flag; no dictionary hotwords.
        string? runner = args.Contains(Multilingual.ParakeetTdt) ? Multilingual.ParakeetTdt : args.Contains(Multilingual.WhisperTurbo) ? Multilingual.WhisperTurbo : null;
        if (runner is not null)
        {
            if (args.Count(a => a is Multilingual.ParakeetTdt or Multilingual.WhisperTurbo) != 1) return Fail(Usage);
            args.Remove(runner);
            return Other(runner, args); // --gpu was already removed: the multilingual models run on CPU
        }
        string? vocabulary = Take(args, "--vocabulary"), lexicon = Take(args, "--lexicon"), embedding = Take(args, "--embedding");
        // The Mac's CTC dictionary model has no Windows counterpart: dictionary support here is hotwords on the same model.
        if (args.Contains("--ctc") || args.Contains("--vocabulary") || args.Contains("--lexicon")) return Fail(Usage);
        try
        {
            if (gpu && (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(AppContext.BaseDirectory, "DirectML.dll"))))
                return Fail("GPU runtime unavailable");
            var provider = gpu ? "directml" : "cpu";
            if (args.Count == 3 && args[1] == "--stream" && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate is >= 8000 and <= 192000 && vocabulary is null)
                return Live(args[0], rate, provider);
            if (args.Count == 4 && args[1] == "--diarize")
            {
                if (embedding is null || !double.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out var threshold) || !(threshold > 0 && threshold < 2)) return Fail(Usage);
                Emit(new() { ["segments"] = Diarizer.Run(args[0], embedding, args[2], threshold) });
                return 0;
            }
            if (args.Count == 2 && args[1] == "--serve") return Serve(args[0], vocabulary, lexicon, provider);
            bool single = args.Count == 3 && args[2] == "--single";
            if (args.Count != 2 && !single) return Fail(Usage);
            return Once(args[0], args[1], single, vocabulary, lexicon, provider);
        }
        catch (Exception) { return Fail("transcription failed"); }
    }

    static int Other(string runner, List<string> args)
    {
        bool single = args.Count == 3 && args[2] == "--single";
        if (args.Count != 2 && !single) return Fail(Usage);
        // Hotword options are English-only; an unknown option here is a usage error.
        if (args.Skip(1).Any(a => a.StartsWith("--") && a is not ("--serve" or "--single"))) return Fail(Usage);
        try
        {
            using var asr = Multilingual.Create(runner, args[0]); // CPU only; --gpu is ignored here
            if (args[1] != "--serve") { Emit(new() { ["text"] = asr.Transcribe(WavReader.Read(args[1])) }); return 0; }
            Emit(new() { ["ready"] = true });
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while (input.ReadLine() is { } line)
            {
                try { Emit(OtherAnswer(asr, line)); }
                catch (Exception) { Emit(new() { ["error"] = "transcription failed" }); }
            }
            return 0;
        }
        catch (Exception) { return Fail("transcription failed"); }
    }

    static SortedDictionary<string, object?> OtherAnswer(IMultilingualAsr asr, string line)
    {
        float[] samples;
        if (line.StartsWith('{'))
        {
            // Only {"samples": BASE64} (16 kHz mono little-endian Float32) is understood.
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("samples", out var encoded) || encoded.ValueKind != JsonValueKind.String) return new() { ["error"] = "invalid audio request" };
                var bytes = Convert.FromBase64String(encoded.GetString()!);
                if (bytes.Length == 0 || bytes.Length % 4 != 0) return new() { ["error"] = "invalid audio request" };
                samples = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            }
            catch (Exception e) when (e is JsonException or FormatException) { return new() { ["error"] = "invalid audio request" }; }
        }
        else samples = WavReader.Read(line);
        return new() { ["text"] = asr.Transcribe(samples) };
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    /// <summary>Removes "--name VALUE" and returns VALUE.</summary>
    static string? Take(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1]; args.RemoveRange(i, 2); return value;
    }

    static int Once(string models, string audio, bool single, string? vocabulary, string? lexicon, string provider)
    {
        var clock = Stopwatch.StartNew();
        using var asr = new Asr(models, vocabulary, lexicon, provider);
        double load = clock.Elapsed.TotalSeconds; clock.Restart();
        var samples = WavReader.Read(audio);
        double prepare = clock.Elapsed.TotalSeconds; clock.Restart();
        var text = asr.Transcribe(samples);
        double cold = clock.Elapsed.TotalSeconds; clock.Restart();
        var o = new SortedDictionary<string, object?>
        {
            ["text"] = text, ["load_seconds"] = load, ["audio_prepare_seconds"] = prepare, ["cold_transcribe_seconds"] = cold,
            ["vocabulary_terms"] = asr.Terms, ["vocabulary_error"] = asr.VocabularyError,
        };
        if (!single) { o["warm_text"] = asr.Transcribe(samples); o["warm_transcribe_seconds"] = clock.Elapsed.TotalSeconds; }
        Emit(o);
        return 0;
    }

    /// <summary>Loads once, prints {"ready":true}, then answers one JSON line per request until stdin closes. A failed request answers {"error":…} and the loop goes on.</summary>
    static int Serve(string models, string? vocabulary, string? lexicon, string provider)
    {
        var clock = Stopwatch.StartNew();
        using var asr = new Asr(models, vocabulary, lexicon, provider);
        Emit(new() { ["ready"] = true, ["load_seconds"] = clock.Elapsed.TotalSeconds, ["vocabulary_terms"] = asr.Terms, ["vocabulary_error"] = asr.VocabularyError });
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (input.ReadLine() is { } line)
        {
            try { Emit(Answer(asr, line)); }
            catch (Exception) { Emit(new() { ["error"] = "transcription failed" }); }
        }
        return 0;
    }

    static SortedDictionary<string, object?> Answer(Asr asr, string line)
    {
        if (line.StartsWith('{'))
        {
            JsonElement request;
            try { request = JsonDocument.Parse(line).RootElement; } catch (JsonException) { return new() { ["error"] = "unknown request" }; }
            if (request.ValueKind != JsonValueKind.Object) return new() { ["error"] = "unknown request" };
            if (request.TryGetProperty("samples", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                // 16 kHz mono little-endian Float32 from memory (an undone cancel); never written to disk.
                var bytes = Convert.FromBase64String(encoded.GetString()!);
                if (bytes.Length == 0 || bytes.Length % 4 != 0) return new() { ["error"] = "transcription failed" };
                var samples = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                return new() { ["text"] = asr.Transcribe(samples) };
            }
            if (request.TryGetProperty("timed", out var timed) && timed.ValueKind == JsonValueKind.String)
            {
                var (text, words) = asr.TranscribeTimed(WavReader.Read(timed.GetString()!));
                var o = new SortedDictionary<string, object?> { ["text"] = text };
                if (words is not null) o["words"] = words;
                return o;
            }
            if (request.TryGetProperty("boundary", out var clip) && clip.ValueKind == JsonValueKind.String)
            {
                object[] found;
                try { found = asr.BoundaryReplacements(WavReader.Read(clip.GetString()!)).Select(r => (object)new SortedDictionary<string, object?> { ["from"] = r.From, ["to"] = r.To }).ToArray(); }
                catch (Exception) { found = []; }
                return new() { ["replacements"] = found };
            }
            return new() { ["error"] = "unknown request" };
        }
        var begin = Stopwatch.StartNew();
        var audio = WavReader.Read(line);
        double prepare = begin.Elapsed.TotalSeconds; begin.Restart();
        var result = asr.Transcribe(audio);
        return new() { ["text"] = result, ["audio_prepare_seconds"] = prepare, ["warm_transcribe_seconds"] = begin.Elapsed.TotalSeconds };
    }

    /// <summary>Live words, display only: raw mono little-endian Float32 at <paramref name="rate"/> from stdin, {"partial":TEXT} whenever the running transcript changes.</summary>
    static int Live(string models, double rate, string provider)
    {
        var c = new OnlineRecognizerConfig();
        var files = Asr.Files(models);
        foreach (var f in files) if (!File.Exists(f)) return Fail("transcription failed");
        c.ModelConfig.Transducer.Encoder = files[0]; c.ModelConfig.Transducer.Decoder = files[1]; c.ModelConfig.Transducer.Joiner = files[2];
        c.ModelConfig.Tokens = files[3]; c.ModelConfig.NumThreads = Math.Min(4, Environment.ProcessorCount);
        c.ModelConfig.Provider = provider;
        c.DecodingMethod = "greedy_search";
        using var recognizer = new OnlineRecognizer(c);
        using var stream = recognizer.CreateStream();
        if (stream.Handle == IntPtr.Zero) return Fail("transcription failed");
        Emit(new() { ["ready"] = true });
        using var input = Console.OpenStandardInput();
        var buffer = new byte[64 * 1024]; var carry = new List<byte>(); var last = "";
        void Pump(bool finished = false)
        {
            var decoded = false;
            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
                decoded = true;
            }
            if (!decoded && !finished) return;
            var text = recognizer.GetResult(stream).Text.Trim();
            if (text != last) { last = text; Emit(new() { ["partial"] = text }); }
        }
        for (int n; (n = input.Read(buffer, 0, buffer.Length)) > 0;)
        {
            carry.AddRange(buffer.AsSpan(0, n).ToArray());
            int count = carry.Count / 4;
            if (count == 0) continue;
            var samples = new float[count];
            Buffer.BlockCopy(carry.GetRange(0, count * 4).ToArray(), 0, samples, 0, count * 4);
            carry.RemoveRange(0, count * 4);
            stream.AcceptWaveform((int)rate, samples);
            Pump();
        }
        stream.InputFinished();
        Pump(finished: true);
        return 0;
    }

    /// <summary>Every line the app reads goes through here, so no transcript, partial or replacement carries the model's "&lt;unk&gt;" piece.</summary>
    static void Emit(SortedDictionary<string, object?> o)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(Scrub(o));
        lock (StdOut) { StdOut.Write(json); StdOut.WriteByte((byte)'\n'); StdOut.Flush(); }
    }

    static object? Scrub(object? value) => value switch
    {
        string s => BoostFilter.WithoutUnknown(s),
        double[][] words => words,
        object[] list => list.Select(Scrub).ToArray(),
        SortedDictionary<string, object?> d => d.ToDictionary(p => p.Key, p => Scrub(p.Value)),
        _ => value,
    };
}
