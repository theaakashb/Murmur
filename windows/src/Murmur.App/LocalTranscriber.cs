using System.Text.Json;
using Murmur.Core;

namespace Murmur.App;

/// <summary>
/// The local transcription route: the sherpa-onnx helper (Murmur.Speech) kept warm through <see cref="WarmRunner"/>, with the one-shot helper as the fallback
/// when the warm process cannot answer. The Mac's flow (Controller.swift transcribe/runTranscription/launchRunner) minus chunking.
/// shortcut: no ChunkJob. Windows has no chunker in the recorders yet, so a dictation is transcribed whole after release; add chunks with the long-recording work.
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

    /// <summary>Set by whichever work bundles the English word list; until then dictionary boosting stays off (see <see cref="Launch"/>).</summary>
    string LexiconFile => Path.Combine(appDirectory, "Speech", "english-words.txt");

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

    /// <summary>The launch for the current model folder and dictionary. Dictionary boosting needs the word list: BoostFilter keeps a replacement only when a replaced word is not ordinary,
    /// and without the list every word is ordinary, so the vocabulary would cost a second decode for nothing. Rewrites the vocabulary file only when its content changed.</summary>
    HelperLaunch? Launch()
    {
        string? vocabulary = null, lexicon = null;
        if (File.Exists(LexiconFile))
        {
            try
            {
                var terms = s.Dictionary.HelperVocabulary;
                if (terms.Count > 0)
                {
                    var json = JsonSerializer.SerializeToUtf8Bytes(new { terms = terms.Select(t => t.Aliases is null ? (object)new { text = t.Text } : new { text = t.Text, aliases = t.Aliases }) });
                    if (PrivateFiles.Read(VocabularyFile) is not { } current || !current.AsSpan().SequenceEqual(json)) PrivateFiles.Write(json, VocabularyFile);
                    vocabulary = VocabularyFile; lexicon = LexiconFile;
                }
                else if (File.Exists(VocabularyFile)) File.Delete(VocabularyFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { vocabulary = null; lexicon = null; }
        }
        return Verify() ? HelperLaunch.Make(ModelDirectory, vocabulary, lexicon) : null;
    }

    /// <summary>Starts the helper in the background so the model load overlaps the recording.</summary>
    public void Prepare()
    {
        if (!HelperPresent) return;
        _ = Task.Run(() => { if (Launch() is { } launch) return warm.PrepareAsync(HelperPath, launch); return Task.CompletedTask; });
    }

    /// <summary>Transcribes the recording. The warm helper gets the 16 kHz samples in memory (the recorder's own conversion); if it cannot answer, one one-shot helper reads the retry WAV, which is in the device's format.</summary>
    public async Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> TranscribeAsync(float[] samples, string wavPath, CancellationToken ct)
    {
        // The cloud pre-check's level: the second-highest 100 ms peak, so a lone click does not make silence count as speech. The Mac has no local pre-check, but this model
        // answers digital silence with a word ("Yeah."), so Windows skips silent clips before the helper.
        var blocks = new List<float>();
        for (int i = 0; i < samples.Length; i += 1600) blocks.Add(samples.Skip(i).Take(1600).Max(x => Math.Abs(x)));
        float peak = blocks.OrderDescending().Skip(1).FirstOrDefault(blocks.Count > 0 ? blocks.Max() : 0);
        var detail = new CloudTranscriptionDetail { ClipSeconds = samples.Length / (double)AudioFormat.SampleRate, SilencePeak = peak, Precheck = Precheck.Passed };
        if (AudioFormat.ShouldSkipForSilence(peak, detail.ClipSeconds))
        {
            detail.Precheck = Precheck.Skipped; detail.NoSpeechReason = NoSpeechReason.Precheck;
            return (new CloudTranscriptionOutcome.NoSpeech(true), detail);
        }
        var failed = (new CloudTranscriptionOutcome.Failed(FailureMessage, true), detail);
        if (!HelperPresent || await Task.Run(Launch).ConfigureAwait(false) is not { } launch) return failed;
        var line = await warm.TranscribeAsync(samples, HelperPath, launch, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
        line ??= await ModelHelper.RunOnceAsync(HelperPath, launch, wavPath, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
        if (ModelHelper.Answer(line)?["text"]?.GetValue<string>() is not { } text) return failed;
        // The model answered but heard nothing; audio stays until the retry window ends.
        if (!text.Any(char.IsLetterOrDigit)) return (new CloudTranscriptionOutcome.NoSpeech(false), detail);
        return (new CloudTranscriptionOutcome.Transcript(text), detail);
    }

    /// <summary>Ends the warm helper so the model files can be deleted.</summary>
    public void Release() => warm.Stop();

    public void Dispose() => warm.Dispose();
}
