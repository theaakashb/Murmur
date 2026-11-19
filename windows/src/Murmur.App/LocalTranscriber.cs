using System.Text.Json;
using System.Threading.Channels;
using Murmur.Core;

namespace Murmur.App;

/// <summary>
/// The local transcription route: the sherpa-onnx helper (Murmur.Speech) kept warm through <see cref="WarmRunner"/>, with the one-shot helper as the fallback
/// when the warm process cannot answer.
/// </summary>
internal sealed class LocalTranscriber : IDisposable
{
    public const string ProviderId = "local";
    public const string ModelId = "csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming";
    const string FailureMessage = "Local transcription failed.";

    readonly AppServices s;
    readonly WarmRunner warm = new();
    readonly string appDirectory = AppContext.BaseDirectory;

    public LocalTranscriber(AppServices services) { s = services; warm.OnDictionaryStatus = failed => TestEvents.Emit("dictionary-support", ("unavailable", failed)); }

    /// <summary>Prefs.modelDirectory, or the pinned folder under <c>&lt;data root&gt;\Models</c> (the same one the Settings row downloads into) when it is empty.</summary>
    public string ModelDirectory => s.Settings.Get(Prefs.ModelDirectory) is { Length: > 0 } custom ? custom : s.SpeechSlot.Downloader?.Target ?? Path.Combine(ModelFacts.ModelsRoot(s.Root), "missing-speech-model");

    /// <summary>Beside Murmur.exe in Speech\ (the published Murmur.Speech folder); MURMUR_SPEECH_HELPER overrides it for tests and the harness.</summary>
    public string HelperPath => Environment.GetEnvironmentVariable("MURMUR_SPEECH_HELPER") is { Length: > 0 } over ? over : Path.Combine(appDirectory, "Speech", "MurmurSpeech.exe");

    readonly object verifyGate = new();
    volatile bool damaged;
    /// <summary>True after a launch found the pinned model failing its checksums; cleared when the files verify again.</summary>
    public bool ModelDamaged => damaged;

    /// <summary>Verifies the pinned model against artifact-pins.json (HelperLaunch.make on the Mac); hashing happens once per launch of the app and again only if a file changes
    /// (ModelVerificationCache). A custom model folder is the user's own and is not checked. Blocks while hashing: call it off the UI thread.</summary>
    public bool Verify()
    {
        if (s.Settings.Get(Prefs.ModelDirectory) is { Length: > 0 } || s.SpeechSlot.Manifest is not { } manifest) return true;
        lock (verifyGate)
        {
            try { ModelVerificationCache.Shared.Verify(ModelDirectory, manifest); damaged = false; }
            catch (RetentionError) { damaged = true; }
        }
        return !damaged;
    }

    public bool ModelFilesPresent => ModelHelper.HasModelFiles(ModelDirectory);
    public bool HelperPresent => File.Exists(HelperPath);

    string VocabularyFile => Path.Combine(s.Locations.Dictionary, "parakeet-vocabulary.json");

    /// <summary>The launch for the current model folder and dictionary. The speech helper holds the English lexicon (Core) for BoostFilter, so no word-list file is passed. Rewrites the vocabulary file only when its content changed.</summary>
    HelperLaunch? Launch()
    {
        string? vocabulary = null;
        {
            try
            {
                var terms = s.Dictionary?.HelperVocabulary;
                if (terms is { Count: > 0 })
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(new { terms = terms.Select(t => t.Aliases is null ? (object)new { text = t.Text } : new { text = t.Text, aliases = t.Aliases }) });
                    if (PrivateFiles.Read(VocabularyFile) is not { } current || !current.AsSpan().SequenceEqual(json)) PrivateFiles.Write(json, VocabularyFile);
                    vocabulary = VocabularyFile;
                }
                else if (File.Exists(VocabularyFile)) File.Delete(VocabularyFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { vocabulary = null; }
        }
        return Verify() ? HelperLaunch.Make(ModelDirectory, vocabulary) : null;
    }

    public ChunkJob StartChunks(string wavPath, CancellationToken ct)
    {
        var helper = HelperPath;
        var preparing = Task.Run(async () =>
        {
            try
            {
                var launch = Launch();
                if (launch is not null) await warm.PrepareAsync(helper, launch, ct).ConfigureAwait(false);
                return launch;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError) { return null; }
        });
        return new ChunkJob(async (samples, boundary, cancellation) =>
        {
            if (await preparing.ConfigureAwait(false) is not { } launch) return null;
            if (!boundary)
            {
                // The Windows helper can hallucinate on a quiet tail after a cut.
                if (AudioFormat.ShouldSkipForSilence(SecondPeak(samples)))
                    return """{"text":""}""";
                var line = await warm.TranscribeAsync(samples, helper, launch, cancellation).ConfigureAwait(false);
                if (ModelHelper.StringField(ModelHelper.Answer(line), "text") is not null)
                    TestEvents.Emit("chunk-transcribed");
                return line;
            }
            if (!launch.HasVocabulary) return null;
            // The boundary protocol takes a WAV path. Give it the retry folder's ACL and lifetime.
            var clip = Path.Combine(Path.GetDirectoryName(wavPath)!, Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                PrivateFiles.Write(WAVAudio.EncodePCM16(samples, AudioFormat.SampleRate), clip);
                return await warm.BoundaryAsync(clip, helper, launch, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError) { return null; }
            finally
            {
                try { File.Delete(clip); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }, ct);
    }

    public void StartLiveWords(ChannelReader<float[]> samples, Action<string> onPartial, CancellationToken ct)
    {
        if (!HelperPresent || s.LiveWordsSlot is not { Manifest: { } manifest, Downloader.Target: { } folder } ||
            !ModelHelper.HasModelFiles(folder)) return;
        var helper = HelperPath;
        _ = Task.Run(async () =>
        {
            try { ModelVerificationCache.Shared.Verify(folder, manifest); }
            catch (Exception e) when (e is RetentionError or IOException or UnauthorizedAccessException) { return; }
            await ModelHelper.StreamAsync(helper, folder, samples, onPartial, ct).ConfigureAwait(false);
        });
    }

    static float SecondPeak(float[] samples)
    {
        const int window = 1600; // 100 ms at 16 kHz
        float first = 0, second = 0;
        for (int i = 0; i < samples.Length; i += window)
        {
            float peak = 0;
            foreach (var sample in samples.AsSpan(i, Math.Min(window, samples.Length - i)))
                peak = Math.Max(peak, Math.Abs(sample));
            if (peak >= first) { second = first; first = peak; }
            else second = Math.Max(second, peak);
        }
        return samples.Length <= window ? first : second;
    }

    /// <summary>Transcribes the recording. The warm helper gets the 16 kHz samples in memory (the recorder's own conversion); if it cannot answer, one one-shot helper reads the retry WAV, which is in the device's format.</summary>
    public async Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> TranscribeAsync(float[] samples, string wavPath, ChunkJob? chunks, CancellationToken ct)
    {
        // The cloud pre-check's level: the second-highest 100 ms peak, so a lone click does not make silence count as speech. The Mac has no local pre-check, but this model
        // answers digital silence with a word ("Yeah."), so Windows skips silent clips before the helper.
        float peak = SecondPeak(samples);
        var detail = new CloudTranscriptionDetail { ClipSeconds = samples.Length / (double)AudioFormat.SampleRate, SilencePeak = peak, Precheck = Precheck.Passed };
        if (AudioFormat.ShouldSkipForSilence(peak, detail.ClipSeconds))
        {
            detail.Precheck = Precheck.Skipped; detail.NoSpeechReason = NoSpeechReason.Precheck;
            return (new CloudTranscriptionOutcome.NoSpeech(true), detail);
        }
        var failed = (new CloudTranscriptionOutcome.Failed(FailureMessage, true), detail);
        var text = chunks is null ? null : await chunks.Result.ConfigureAwait(false);
        if (ct.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
        if (text is null)
        {
            if (!HelperPresent || await Task.Run(Launch).ConfigureAwait(false) is not { } launch) return failed;
            var line = chunks is null
                ? await warm.TranscribeAsync(samples, HelperPath, launch, ct).ConfigureAwait(false)
                : await warm.TranscribeAsync(wavPath, HelperPath, launch, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
            line ??= await ModelHelper.RunOnceAsync(HelperPath, launch, wavPath, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
            text = ModelHelper.StringField(ModelHelper.Answer(line), "text");
            if (text is null) return failed;
        }
        // The model answered but heard nothing; audio stays until the retry window ends.
        if (!text.Any(char.IsLetterOrDigit)) return (new CloudTranscriptionOutcome.NoSpeech(false), detail);
        return (new CloudTranscriptionOutcome.Transcript(text), detail);
    }

    /// <summary>Ends the warm helper so the model files can be deleted.</summary>
    public void Release() => warm.Stop();

    public void Dispose() => warm.Dispose();
}
