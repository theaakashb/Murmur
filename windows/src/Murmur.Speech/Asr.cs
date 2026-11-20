using System.Text.Json;
using Murmur.Core;
using SherpaOnnx;

namespace Murmur.Speech;

/// <summary>One decode: the text and, per token, its start time and (when the model reports it) duration in seconds.</summary>
sealed record Decoded(string Text, string[] Tokens, float[] Times, float[] Durations);

/// <summary>
/// The model folder's NeMo transducer. Dictionary support replaces the Mac's CTC rescoring with sherpa-onnx hotwords:
/// the raw transcript comes from greedy search, the boosted one from modified beam search with the dictionary terms as
/// hotwords, and BoostFilter keeps only the replacements it keeps on the Mac. No transcript is ever logged.
/// </summary>
sealed class Asr : IDisposable
{
    readonly OfflineRecognizer raw;
    readonly OfflineRecognizer? hot;
    readonly HashSet<string> aliases;
    readonly Func<string, bool> isCommonWord;
    public int Terms { get; }
    public bool VocabularyError { get; }

    public static string[] Files(string dir) => [Path.Combine(dir, "encoder.int8.onnx"), Path.Combine(dir, "decoder.int8.onnx"), Path.Combine(dir, "joiner.int8.onnx"), Path.Combine(dir, "tokens.txt")];

    static int Threads => int.TryParse(Environment.GetEnvironmentVariable("MURMUR_ASR_THREADS"), out var n) && n is >= 1 and <= 64 ? n : Math.Min(4, Environment.ProcessorCount);
    // Tuning knob for the benchmark; the default is sherpa-onnx's own.
    static float HotwordsScore => float.TryParse(Environment.GetEnvironmentVariable("MURMUR_HOTWORDS_SCORE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1.5f;

    static OfflineRecognizerConfig Config(string dir, string method, string? hotwordsFile, string? bpeVocab, string provider)
    {
        var files = Files(dir);
        foreach (var f in files) if (!File.Exists(f)) throw new FileNotFoundException("model file missing");
        var c = new OfflineRecognizerConfig();
        c.ModelConfig.Transducer.Encoder = files[0]; c.ModelConfig.Transducer.Decoder = files[1]; c.ModelConfig.Transducer.Joiner = files[2];
        c.ModelConfig.Tokens = files[3]; c.ModelConfig.ModelType = "nemo_transducer";
        c.ModelConfig.NumThreads = Threads; c.ModelConfig.Provider = provider; c.ModelConfig.Debug = 0;
        c.DecodingMethod = method;
        if (hotwordsFile is not null) { c.HotwordsFile = hotwordsFile; c.ModelConfig.ModelingUnit = "bpe"; c.ModelConfig.BpeVocab = bpeVocab ?? "";  c.HotwordsScore = HotwordsScore; c.MaxActivePaths = 4; }
        return c;
    }

    public Asr(string dir, string? vocabularyPath, string? lexiconPath, string provider = "cpu")
    {
        raw = new OfflineRecognizer(Config(dir, "greedy_search", null, null, provider));
        using (var probe = raw.CreateStream())
            if (probe.Handle == IntPtr.Zero) { raw.Dispose(); throw new InvalidDataException("Speech model initialization failed."); }
        // BoostFilter keeps a replacement only when a replaced word is not ordinary: the bundled English lexicon (Core) decides. --lexicon is accepted and ignored.
        isCommonWord = Lexicon.English.IsCommonWord;
        aliases = [];
        if (vocabularyPath is null) return;
        string? folder = null;
        try
        {
            var vocabulary = Vocabulary.Load(vocabularyPath);
            var words = vocabulary.HotwordLines();
            if (words.Count == 0) return;
            // sherpa-onnx 2.0.1 encodes hotwords itself and needs a sentencepiece vocabulary file, which the model folder lacks: build one from tokens.txt.
            folder = Path.Combine(Path.GetTempPath(), "murmur-hotwords-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var hotwordsFile = Path.Combine(folder, "hotwords.txt"); var bpeFile = Path.Combine(folder, "bpe.vocab");
            File.WriteAllLines(hotwordsFile, words);
            File.WriteAllLines(bpeFile, File.ReadLines(Path.Combine(dir, "tokens.txt")).Select(l => l.LastIndexOf(' ') is var i and > 0 ? l[..i] : l).Where(p => p != "<unk>").Select(p => p + "\t-1"));
            hot = new OfflineRecognizer(Config(dir, "modified_beam_search", hotwordsFile, bpeFile, provider));
            using var probe = hot.CreateStream();
            if (probe.Handle == IntPtr.Zero) throw new InvalidDataException("Dictionary model initialization failed.");
            Terms = vocabulary.Terms.Count;
            aliases = vocabulary.Terms.SelectMany(t => t.Aliases).Select(a => string.Join(" ", BoostFilter.Words(a).Select(w => w.Folded))).ToHashSet();
        }
        catch (Exception) { hot?.Dispose(); hot = null; Terms = 0; VocabularyError = true; }
        finally { if (folder is not null) try { Directory.Delete(folder, true); } catch (Exception) { } }
    }

    const int MinSamples = 4000;
    // Benchmark knob for the hotwords spike: answer with the boosted text before BoostFilter, to see what the filter blocks.
    static readonly bool Unfiltered = Environment.GetEnvironmentVariable("MURMUR_ASR_UNFILTERED") == "1";

    static Decoded Decode(OfflineRecognizer r, float[] samples)
    {
        // A clip shorter than one feature frame makes onnxruntime abort the process, so very short clips get trailing silence.
        if (samples.Length < MinSamples) Array.Resize(ref samples, MinSamples);
        using var stream = r.CreateStream();
        stream.AcceptWaveform(WavReader.Rate, samples);
        r.Decode(stream);
        var x = stream.Result;
        return new Decoded(x.Text.Trim(), x.Tokens ?? [], x.Timestamps ?? [], x.Durations ?? []);
    }

    public Decoded DecodeRaw(float[] samples) => Decode(raw, samples);

    /// <summary>The boosted transcript, or null when dictionary support is off.</summary>
    string? Boosted(float[] samples) => hot is null ? null : Decode(hot, samples).Text;

    public string Transcribe(float[] samples) => Transcribe(samples, DecodeRaw(samples));

    string Transcribe(float[] samples, Decoded rawResult)
    {
        if (Boosted(samples) is not { } boosted || boosted == rawResult.Text) return rawResult.Text;
        if (Unfiltered) return boosted;
        return BoostFilter.Filter(rawResult.Text, boosted, aliases, isCommonWord);
    }

    /// <summary>The text plus one [start, end] per word of the raw transcript; words are left out when the dictionary changed the word count.</summary>
    public (string Text, double[][]? Words) TranscribeTimed(float[] samples)
    {
        var d = DecodeRaw(samples);
        var text = BoostFilter.WithoutUnknown(Transcribe(samples, d));
        var words = new List<double[]>();
        for (int i = 0; i < d.Tokens.Length && i < d.Times.Length; i++)
        {
            var token = d.Tokens[i];
            if (token.Trim() == BoostFilter.Unknown) continue;
            double start = d.Times[i];
            // The model reports no durations: a token lasts one 80 ms encoder frame, or less when the next token comes sooner.
            double frame = i < d.Durations.Length && d.Durations[i] > 0 ? d.Durations[i] : 0.08;
            double end = i + 1 < d.Times.Length ? Math.Min(d.Times[i + 1], start + frame) : start + frame;
            // A piece starting with "▁" (or a space) begins a word.
            if (token.StartsWith('▁') || token.StartsWith(' ') || words.Count == 0) words.Add([start, end]);
            else words[^1][1] = end;
        }
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == words.Count ? (text, words.ToArray()) : (text, null);
    }

    public (string From, string To)[] BoundaryReplacements(float[] samples)
    {
        if (hot is null) return [];
        var rawText = DecodeRaw(samples).Text; var boosted = Decode(hot, samples).Text;
        if (boosted == rawText) return [];
        return BoostFilter.Replacements(rawText, boosted, aliases, isCommonWord).Select(r => (r.From, r.To)).ToArray();
    }

    public void Dispose() { raw.Dispose(); hot?.Dispose(); }
}

/// <summary>FluidAudio's vocabulary JSON: {"terms":[{"text":…,"aliases":[…]}]}.</summary>
sealed record VocabularyTerm(string Text, string[] Aliases);

sealed class Vocabulary
{
    public List<VocabularyTerm> Terms { get; } = [];

    public static Vocabulary Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var v = new Vocabulary();
        foreach (var t in doc.RootElement.GetProperty("terms").EnumerateArray())
        {
            var text = t.GetProperty("text").GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var aliases = t.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
            v.Terms.Add(new VocabularyTerm(text, aliases));
        }
        return v;
    }

    /// <summary>The hotwords, one per line: each term as typed and in lower case (the model's pieces are case sensitive).</summary>
    public List<string> HotwordLines() =>
        Terms.SelectMany(t => new[] { t.Text, t.Text.ToLowerInvariant() }).Select(x => string.Join(" ", x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(x => x.Length > 0).Distinct().ToList();
}
