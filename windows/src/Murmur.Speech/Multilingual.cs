using SherpaOnnx;

namespace Murmur.Speech;

/// <summary>The non-English local paths (module-one/models/Runner/Sources/ModelBench/Multilingual.swift): --parakeet-tdt and --whisper-turbo.
/// Neither takes dictionary hotwords.</summary>
interface IMultilingualAsr : IDisposable
{
    string Transcribe(float[] samples);
}

static class Multilingual
{
    public const string ParakeetTdt = "--parakeet-tdt", WhisperTurbo = "--whisper-turbo";

    /// <summary>Both multilingual models run on CPU whatever --gpu says (LocalSpeechModels.MayUseGpu): TDT is faster on CPU, and DirectML crashes sherpa-onnx while creating the Whisper recognizer.</summary>
    public static IMultilingualAsr Create(string flag, string folder) => flag == ParakeetTdt ? new TdtAsr(folder, "cpu") : new WhisperAsr(folder, "cpu");

    /// <summary>The Silero VAD model, beside MurmurSpeech.exe (MIT; pinned with the Windows models later).</summary>
    public static string VadModel => Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");
}

/// <summary>Parakeet TDT v3 (25 European languages): the NeMo transducer files of the English path, greedy search, no hotwords.</summary>
sealed class TdtAsr : IMultilingualAsr
{
    readonly Asr asr;
    public TdtAsr(string folder, string provider) { asr = new Asr(folder, null, null, provider); }
    public string Transcribe(float[] samples) => asr.Transcribe(samples);
    public void Dispose() => asr.Dispose();
}

/// <summary>Whisper large-v3-turbo int8: language auto-detected, task transcribe. Whisper decodes 30 s windows, so the audio is cut at silence with Silero VAD
/// (the Mac's chunkingStrategy .vad) and the chunks' texts are joined with spaces.</summary>
sealed class WhisperAsr : IMultilingualAsr
{
    public const string Encoder = "turbo-encoder.int8.onnx", Decoder = "turbo-decoder.int8.onnx", Tokens = "turbo-tokens.txt";
    const int Rate = WavReader.Rate, MinSamples = 4000;
    const float MaxChunkSeconds = 30f, PieceSeconds = 28f;

    readonly OfflineRecognizer recognizer;
    readonly VadModelConfig vad;

    public WhisperAsr(string folder, string provider)
    {
        var files = new[] { Encoder, Decoder, Tokens }.Select(f => Path.Combine(folder, f)).ToArray();
        foreach (var f in files.Append(Multilingual.VadModel)) if (!File.Exists(f)) throw new FileNotFoundException("model file missing");
        var c = new OfflineRecognizerConfig();
        c.ModelConfig.Whisper.Encoder = files[0]; c.ModelConfig.Whisper.Decoder = files[1];
        c.ModelConfig.Whisper.Language = ""; c.ModelConfig.Whisper.Task = "transcribe";
        c.ModelConfig.Tokens = files[2]; c.ModelConfig.NumThreads = Math.Min(4, Environment.ProcessorCount);
        c.ModelConfig.Provider = provider; c.ModelConfig.Debug = 0;
        c.DecodingMethod = "greedy_search";
        recognizer = new OfflineRecognizer(c);
        using (var probe = recognizer.CreateStream())
            if (probe.Handle == IntPtr.Zero) { recognizer.Dispose(); throw new InvalidDataException("Speech model initialization failed."); }
        vad = new VadModelConfig { SampleRate = Rate, NumThreads = 1, Provider = "cpu", Debug = 0 };
        vad.SileroVad.Model = Multilingual.VadModel;
        vad.SileroVad.Threshold = 0.5f; vad.SileroVad.MinSilenceDuration = 0.5f; vad.SileroVad.MinSpeechDuration = 0.25f;
        vad.SileroVad.WindowSize = 512; vad.SileroVad.MaxSpeechDuration = 20f;
    }

    public string Transcribe(float[] samples)
    {
        // A recording that fits one Whisper window needs no cutting; building pieces from VAD segments clipped the opening words.
        if (samples.Length <= (int)(MaxChunkSeconds * Rate)) return Decode(samples);
        var parts = new List<string>();
        foreach (var chunk in Chunks(samples))
        {
            var text = Decode(chunk);
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join(" ", parts);
    }

    List<float[]> Chunks(float[] samples)
    {
        // Silero VAD only chooses where to cut: its segment starts come late (the first words of a clip were lost when pieces were built from the segments),
        // so the pieces are consecutive slices of the whole recording, each at most PieceSeconds long, cut in the silence the VAD found.
        var cuts = new List<int>();
        int previousEnd = -1;
        var detector = new VoiceActivityDetector(vad, 2 * MaxChunkSeconds);
        try
        {
            const int window = 512 * 64;
            for (int i = 0; i < samples.Length; i += window)
            {
                detector.AcceptWaveform(samples.AsSpan(i, Math.Min(window, samples.Length - i)).ToArray());
                Drain();
            }
            detector.Flush();
            Drain();
        }
        finally { detector.Dispose(); }
        var pieces = new List<float[]>();
        int max = (int)(PieceSeconds * Rate), start = 0;
        while (samples.Length - start > (int)(MaxChunkSeconds * Rate))
        {
            int cut = cuts.Where(c => c > start && c - start <= max).DefaultIfEmpty(start + max).Max();
            pieces.Add(samples[start..cut]);
            start = cut;
        }
        pieces.Add(samples[start..]);
        return pieces;

        void Drain()
        {
            while (!detector.IsEmpty())
            {
                var segment = detector.Front();
                int from = (int)Math.Min(int.MaxValue, segment.Start), to = (int)Math.Min(int.MaxValue, (long)segment.Start + segment.Samples.Length);
                // The middle of the silence before this segment is a cut point.
                if (previousEnd >= 0 && from > previousEnd) cuts.Add(previousEnd + (from - previousEnd) / 2);
                else if (previousEnd >= 0) cuts.Add(from);
                previousEnd = to;
                detector.Pop();
            }
        }
    }

    string Decode(float[] samples)
    {
        if (samples.Length < MinSamples) Array.Resize(ref samples, MinSamples);
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(Rate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    public void Dispose() => recognizer.Dispose();
}
