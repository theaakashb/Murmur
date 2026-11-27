// Ports the Foundation-only assertions of app/check_routes.swift (the AI engine block, check_routes:169-179, is in AIEngineTests).
// Skipped (Mac-only): the Apple Intelligence shape checks (AppleCleanup), and the legacy-settings migration assertions
// (settingsChecks after the round trip, keyChecks:419-450), since Windows ports no migrations.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>A UI-thread stand-in: one thread running posted callbacks in order, as Swift's main actor does for CloudRequest.</summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    readonly BlockingCollection<(SendOrPostCallback, object?)> queue = [];
    public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

    public static void Run(Func<Task> body)
    {
        var context = new SingleThreadContext();
        var previous = Current;
        SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (d, state) in context.queue.GetConsumingEnumerable()) d(state);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}

public class RoutesTests
{
    const string Secret = "test-key";
    static readonly APIKey TestKey = APIKey.Create(Secret)!;

    static float[] Tone(double seconds, float amplitude, int rate = 48_000) =>
        [.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => amplitude * (float)Math.Sin(i * 2 * Math.PI * 220 / rate))];

    static float[] Noise(double seconds, float amplitude, int rate = 48_000, int seed = 7)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, (int)(seconds * rate)).Select(_ => (float)(random.NextDouble() * 2 - 1) * amplitude)];
    }

    /// <summary>A verbose_json body with no_speech_prob 0, as Groq returns it.</summary>
    static string VerboseJSON(params (string Text, double Start, double End, double Logprob)[] segments) =>
        JsonSerializer.Serialize(new { text = string.Concat(segments.Select(s => s.Text)), segments = segments.Select(s => new { text = s.Text, start = s.Start, end = s.End, avg_logprob = s.Logprob, no_speech_prob = 0 }) });

    /// <summary>A float32 stereo WAVE_FORMAT_EXTENSIBLE file, the shape AVAudioFile may write.</summary>
    static byte[] FloatStereoWAV(float[] left, float[] right, int rate)
    {
        var b = new List<byte>();
        void P32(int v) { for (var s = 0; s < 32; s += 8) b.Add((byte)((v >> s) & 0xFF)); }
        void P16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }
        var bytes = left.Length * 8;
        b.AddRange("RIFF"u8.ToArray()); P32(4 + 8 + 40 + 8 + 4 + 8 + bytes); b.AddRange("WAVE"u8.ToArray());
        b.AddRange("fmt "u8.ToArray()); P32(40); P16(0xFFFE); P16(2); P32(rate); P32(rate * 8); P16(8); P16(32);
        P16(22); P16(32); P32(3); P16(3); b.AddRange([0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71]);
        b.AddRange("FLLR"u8.ToArray()); P32(4); b.AddRange([0, 0, 0, 0]);
        b.AddRange("data"u8.ToArray()); P32(bytes);
        for (var i = 0; i < left.Length; i++)
            foreach (var v in new[] { left[i], right[i] }) b.AddRange(BitConverter.GetBytes(BitConverter.SingleToUInt32Bits(v)));
        return [.. b];
    }

    sealed class MockCleaner(ProviderError? error = null, string text = "") : ICleanupProvider
    {
        public ProviderID Id => ProviderID.CustomAPI;
        public DataDisclosure Disclosure => new(Id, ProcessingStage.Cleanup, "example.invalid", ["the transcript text"], ["audio"], "test");
        public Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default) =>
            error is null ? Task.FromResult(text) : Task.FromException<string>(new ProviderException(error));
        public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // MARK: Factory

    [Fact]
    public void Factory()
    {
        const string speechURL = "https://speech.example/v1";
        Assert.True(Routes.MakeTranscriptionProvider(TestKey, speechURL, "m") is OpenAICompatibleTranscriptionProvider && Routes.MakeTranscriptionProvider(null, speechURL, "m") is not null);
        Assert.True(Routes.MakeTranscriptionProvider(TestKey, "ftp://x", "m") is null && Routes.MakeTranscriptionProvider(TestKey, speechURL, " ") is null);
        var unset = new RouteSettings();
        Assert.True(unset.CustomAPIBaseURL.Length == 0 && unset.CustomTranscriptionAPIBaseURL.Length == 0 && unset.CustomAPIModel.Length == 0 && unset.CustomTranscriptionModel.Length == 0);
        Assert.Empty(RoutePlan.Make(unset with { Transcription = TranscriptionRoute.Cloud, Cleanup = CleanupChoice.CustomAPI }, true, _ => TestKey).Stages);
        Assert.Null(Routes.MakeCleanupProvider(CleanupChoice.LocalRules, TestKey, "", ""));
        Assert.IsType<OpenAICompatibleCleanupProvider>(Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, "http://localhost:11434", "llama3.2"));
        Assert.Null(Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, "http://localhost:11434", " "));
        Assert.True(((OpenAICompatibleCleanupProvider)Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, "http://localhost:11434", "m", " low ")!).ReasoningEffort == "low"
                    && ((OpenAICompatibleCleanupProvider)Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, "http://localhost:11434", "m", " ")!).ReasoningEffort is null
                    && new RouteSettings().CustomAPIReasoningEffort == "low", "reasoning effort defaults to low, is trimmed, and empty sends none");
        Assert.Null(Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, TestKey, "ftp://x", "m"));
        Assert.IsType<OpenAICompatibleCleanupProvider>(Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, "https://api.example.com/v1", "m"));
        Assert.Equal([CleanupChoice.LocalRules, CleanupChoice.LocalModel, CleanupChoice.CustomAPI, CleanupChoice.MurmurCloud], Enum.GetValues<CleanupChoice>());
        Assert.True(CleanupChoice.LocalModel.ProviderID() == ProviderID.LocalModel && CleanupChoice.LocalModel.ProviderID() != CleanupChoice.CustomAPI.ProviderID() && CleanupChoice.LocalModel.CredentialID() == ProviderID.LocalModel);
        Assert.IsType<LocalModelCleanupProvider>(Routes.MakeCleanupProvider(CleanupChoice.LocalModel, null, "https://api.example.com/v1", "m"));
        var localPlan = RoutePlan.Make(new RouteSettings { Cleanup = CleanupChoice.LocalModel }, true, _ => null);
        Assert.True(localPlan.Refusal is null && localPlan.Stages.Count == 0 && localPlan.Cleaner?.Id == ProviderID.LocalModel, "local model needs no key and discloses nothing leaving the computer");
        Assert.Contains("nothing leaves this Mac", localPlan.Cleaner!.Disclosure.Summary);
        Assert.True(ProviderID.CustomAPI.DisplayName() == "Custom Provider" && CleanupChoice.CustomAPI.DisplayName() == "Custom Provider");
        Assert.True(CleanupChoice.LocalRules.ProcessingRoute() == CleanupRoute.Local && CleanupChoice.CustomAPI.ProcessingRoute() == CleanupRoute.Cloud);
    }

    // MARK: Plans, no fallback

    [Fact]
    public void Plans()
    {
        Func<ProviderID, APIKey?> none = _ => null, all = _ => TestKey;
        var local = RoutePlan.Make(new RouteSettings(), true, all);
        Assert.True(local.UsesLocalModel && local.Cleaner is null && local.Stages.Count == 0 && local.Refusal is null);

        var cloud = new RouteSettings { Transcription = TranscriptionRoute.Cloud };
        var unconfigured = RoutePlan.Make(cloud, true, all);
        Assert.True(unconfigured.Transcriber is null && !unconfigured.UsesLocalModel, "no fallback: an unconfigured cloud route never uses the local model");
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.CustomAPI).UserMessage, unconfigured.Refusal);
        cloud.CustomTranscriptionAPIBaseURL = "https://speech.example/v1"; cloud.CustomTranscriptionModel = "speech-model";
        var configured = RoutePlan.Make(cloud, true, none);
        var summary = configured.Stages.FirstOrDefault()?.Summary ?? "";
        Assert.True(configured.Transcriber is not null && configured.Refusal is null && summary.Contains("audio") && summary.Contains("speech.example"));
        Assert.Equal(summary, DisclosureText.Summary(configured, ProcessingStage.Transcription));

        var separate = new RouteSettings
        {
            Transcription = TranscriptionRoute.Cloud, CustomTranscriptionModel = "speech-model", CustomTranscriptionAPIBaseURL = "https://speech.example/v1",
            CustomAPIBaseURL = "https://cleanup.example/v1", Cleanup = CleanupChoice.CustomAPI, CustomAPIModel = "cleanup-model",
        };
        APIKey speechKey = APIKey.Create("speech-key")!, cleanupKey = APIKey.Create("cleanup-key")!;
        var independent = RoutePlan.Make(separate, true, id => id == ProviderID.CustomTranscription ? speechKey : id == ProviderID.CustomCleanup ? cleanupKey : null);
        Assert.True(((OpenAICompatibleTranscriptionProvider)independent.Transcriber!).Server.ApiKey == speechKey && ((OpenAICompatibleCleanupProvider)independent.Cleaner!).ApiKey == cleanupKey
                    && independent.Stages.Select(s => s.DestinationHost).Contains("speech.example") && independent.Stages.Select(s => s.DestinationHost).Contains("cleanup.example"),
                    "custom transcription and cleanup use independent endpoints and credentials");

        var loopback = new RouteSettings { Cleanup = CleanupChoice.CustomAPI, CustomAPIBaseURL = "http://localhost:11434", CustomAPIModel = "llama3.2" };
        var onMac = RoutePlan.Make(loopback, true, none);
        Assert.True(onMac.Cleaner is not null && onMac.Stages.Count == 0 && onMac.Refusal is null, "loopback server needs no disclosure");
        loopback.CustomAPIBaseURL = "http://192.168.1.20:11434";
        var lan = RoutePlan.Make(loopback, true, all);
        Assert.True(lan.Stages.Count == 1 && lan.Stages[0].Warning is not null, "LAN server is disclosed with a plain-http warning");
        var publicNoKey = RoutePlan.Make(loopback, true, none);
        Assert.True(publicNoKey.Cleaner is not null && publicNoKey.Refusal is null && publicNoKey.Stages.Count == 1);
        loopback.CustomAPIModel = "";
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.CustomAPI).UserMessage, RoutePlan.Make(loopback, true, all).Refusal);

        var cloudCleanup = new RouteSettings { Cleanup = CleanupChoice.CustomAPI, CustomAPIModel = "" };
        var off = RoutePlan.Make(cloudCleanup, false, none);
        Assert.True(off.Cleaner is null && off.Refusal is null && off.Stages.Count == 0, "cleanup with every option off sends nothing and needs no setup");
        var missing = RoutePlan.Make(cloudCleanup, true, none);
        Assert.True(missing.Refusal is not null && missing.Cleaner is null, "cloud cleanup that is not set up refuses instead of using local rules");
    }

    [Fact]
    public void Disclosures()
    {
        Func<ProviderID, APIKey?> all = _ => TestKey;
        var settings = new RouteSettings { Transcription = TranscriptionRoute.Cloud, CustomTranscriptionAPIBaseURL = "https://speech.example/v1", CustomTranscriptionModel = "speech-model" };
        var first = RoutePlan.Make(settings, true, all);
        Assert.True(first.Stages.Count == 1 && first.Stages[0].Stage == ProcessingStage.Transcription && first.Stages[0].Summary.Contains("audio"));
        settings.Cleanup = CleanupChoice.CustomAPI; settings.CustomAPIModel = "m"; settings.CustomAPIBaseURL = "https://cleanup.example/v1";
        Assert.Equal([ProcessingStage.Transcription, ProcessingStage.Cleanup], RoutePlan.Make(settings, true, all).Stages.Select(s => s.Stage));
        settings.CustomTranscriptionAPIBaseURL = "http://10.0.0.5:8080"; settings.CustomAPIBaseURL = "http://10.0.0.7:8080";
        Assert.Equal(new HashSet<string> { "10.0.0.5", "10.0.0.7" }, RoutePlan.Make(settings, true, all).Stages.Select(s => s.DestinationHost).ToHashSet());
        settings.CustomTranscriptionAPIBaseURL = "http://10.0.0.6:8080";
        Assert.Contains("10.0.0.6", RoutePlan.Make(settings, true, all).Stages.Select(s => s.DestinationHost));
    }

    [Fact]
    public void Settings()
    {
        var defaults = new InMemorySettingsStore();
        Assert.Equal(new RouteSettings(), RouteSettings.Load(defaults));
        var settings = new RouteSettings { Transcription = TranscriptionRoute.Cloud, Cleanup = CleanupChoice.CustomAPI, CustomAPIBaseURL = "https://api.example.com/v1", CustomAPIModel = "qwen" };
        settings.Save(defaults);
        Assert.Equal(settings, RouteSettings.Load(defaults));
        defaults.Set(RouteSettings.Key.Cleanup, "gemini");
        Assert.Equal(CleanupChoice.LocalRules, RouteSettings.Load(defaults).Cleanup);
        // Windows: a wrongly typed value reads as the default (spec/settings.md).
        defaults.Set(RouteSettings.Key.Transcription, 5L);
        defaults.Set(RouteSettings.Key.CustomAPIModel, true);
        Assert.True(RouteSettings.Load(defaults) is { Transcription: TranscriptionRoute.Local, CustomAPIModel: "" });
        // No key is ever stored with the settings.
        Assert.DoesNotContain(defaults.Snapshot().Values, v => $"{v}".Contains(Secret));
    }

    // MARK: Audio

    [Fact]
    public void Audio()
    {
        var samples = Tone(1, 0.5f);
        var pcm = WAVAudio.EncodePCM16(samples, 48_000);
        var decoded = WAVAudio.Decode(pcm);
        Assert.True(decoded?.SampleRate == 48_000 && decoded.Samples.Length == 48_000, "PCM16 round trip");
        var prepared = CloudAudio.Prepare(pcm);
        Assert.True(prepared.Payload.Data.Length == 44 + 16_000 * 2 && prepared.Payload.MimeType == "audio/wav", "downsampled to 16 kHz mono PCM16");
        Assert.True(Math.Abs((prepared.Peak ?? 0) - 0.5) < 0.01 && Math.Abs((prepared.Duration ?? 0) - 1) < 0.001, "peak and duration");
        Assert.True(prepared.Payload.Levels?.Count == 10 && prepared.Payload.Levels.All(l => Math.Abs(l - 0.5) < 0.01), "one peak per 100 ms of the audio sent");
        var stereo = FloatStereoWAV(Tone(0.5, 0.8f, 44_100), new float[22_050], 44_100);
        var floatDecoded = WAVAudio.Decode(stereo);
        Assert.True(floatDecoded?.Samples.Length == 22_050 && Math.Abs(floatDecoded.Samples.Max(Math.Abs) - 0.4) < 0.01, "float32 extensible stereo is averaged to mono");
        Assert.Null(WAVAudio.Decode("not a wav"u8.ToArray()));
        var raw = CloudAudio.Prepare("not a wav"u8.ToArray());
        Assert.True(raw.Peak is null && raw.Payload.Data.SequenceEqual("not a wav"u8.ToArray()), "unparsable audio is sent unchanged with no peak");
        Assert.Equal([1f, 2f, 3f], WAVAudio.Downsample([1, 2, 3], 8_000, 16_000));
        var tail = CloudAudio.Prepare(WAVAudio.EncodePCM16([.. Tone(1, 0.5f), .. Enumerable.Repeat(0.001f, 96_000)], 48_000));
        Assert.True(tail.Payload.Data.Length == 44 + 24_000 * 2 && Math.Abs((tail.Duration ?? 0) - 3) < 0.001, "silent tail trimmed to a 0.5 s pad; duration is the recording's");
        Assert.Equal([0.001f, 0, 0.002f], CloudAudio.TrimTrailingSilence([0.001f, 0, 0.002f], 16_000));
        var click = Enumerable.Repeat(0.002f, 48_000).ToArray(); click[20_000] = 0.5f;
        Assert.True(AudioFormat.ShouldSkipForSilence(CloudAudio.Prepare(WAVAudio.EncodePCM16(click, 48_000)).Peak ?? 1), "a lone click does not make a silent clip count as speech");
        float[] word = [.. Enumerable.Repeat(0.002f, 24_000), .. Tone(0.2, 0.3f), .. Enumerable.Repeat(0.002f, 24_000)];
        Assert.False(AudioFormat.ShouldSkipForSilence(CloudAudio.Prepare(WAVAudio.EncodePCM16(word, 48_000)).Peak ?? 0), "a 0.2 s word is speech");
    }

    // MARK: Cloud transcription

    static CloudTranscriptionOutcome Transcript(string t) => new CloudTranscriptionOutcome.Transcript(t);
    static CloudTranscriptionOutcome NoSpeech(bool skipped) => new CloudTranscriptionOutcome.NoSpeech(skipped);

    [Fact]
    public async Task Transcription()
    {
        var speech = WAVAudio.EncodePCM16(Tone(1, 0.3f), 48_000);
        var silence = WAVAudio.EncodePCM16(Enumerable.Repeat(0.001f, 48_000).ToArray(), 48_000);

        // Silence skip: the transcriber is never called for silence.
        var whisperQuiet = new CannedTransport(200, """{"text":"Thank you."}""");
        var custom = new OpenAICompatibleTranscriptionProvider("https://stt.example.test", "whisper-large-v3-turbo", TestKey) { Transport = whisperQuiet };
        Assert.Equal(NoSpeech(true), await CloudTranscription.RunAsync(custom, silence));
        Assert.Equal(0, whisperQuiet.Count);
        Assert.Equal(NoSpeech(true), await CloudTranscription.RunAsync(custom, WAVAudio.EncodePCM16(Tone(0.1, 0.5f), 48_000)));

        // A transcriber without a pre-check: silence is sent, and an empty result is "no speech".
        var whisper = new SpyTranscriber(() => "  ");
        Assert.Equal(NoSpeech(false), await CloudTranscription.RunAsync(whisper, silence));
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(NoSpeech(false), await CloudTranscription.RunAsync(new SpyTranscriber(() => " ."), silence));
        Assert.Equal(Transcript(" 42."), await CloudTranscription.RunAsync(new SpyTranscriber(() => " 42."), silence));

        // Provider text is preserved as returned, including names, lists and literal discussion of vocabulary.
        foreach (var spoken in new[] { "Aakash.", "Aakash, spaceyboi, Open Router.", "Meet at three. Aakash.", "Call Aakash, spaceyboi", "Vocabulary: Aakash, spaceyboi.",
                                       "We adjusted the camera settings. Vocabulary is aakash, spaceyboi. Vocabulary is", "Thanks, Aakash.", "Ask Aakash about it." })
        {
            var customSpeech = new CannedTransport(200, JsonSerializer.Serialize(new { text = spoken }));
            Assert.Equal(Transcript(spoken), await CloudTranscription.RunAsync(custom with { Transport = customSpeech }, speech));
            var body = Encoding.UTF8.GetString(customSpeech.Requests[^1].Body!);
            Assert.True(!body.Contains("name=\"prompt\"") && !body.Contains("spaceyboi"), "custom request has no dictionary prompt");
        }

        // Errors: one call, no retry, no other provider; audio kept for retry.
        var failing = new SpyTranscriber(() => throw new ProviderException(ProviderError.Authentication(ProviderID.CustomAPI)));
        var bystander = new SpyTranscriber(() => "should never run");
        var failed = await CloudTranscription.RunAsync(failing, speech);
        Assert.Equal(new CloudTranscriptionOutcome.Failed(ProviderError.Authentication(ProviderID.CustomAPI).UserMessage, true), failed);
        Assert.True(failing.Calls == 1 && bystander.Calls == 0, "no retry and no fallback provider");
        Assert.EndsWith("Audio is saved for retry for up to 24 hours.", CloudTranscription.Message(failed));
        var limited = await CloudTranscription.RunAsync(new SpyTranscriber(() => throw new ProviderException(ProviderError.RateLimited(ProviderID.CustomAPI, 30))), speech);
        Assert.Equal(new CloudTranscriptionOutcome.Failed(ProviderError.RateLimited(ProviderID.CustomAPI, 30).UserMessage, true), limited);
        Assert.Equal(new CloudTranscriptionOutcome.Cancelled(), await CloudTranscription.RunAsync(new SpyTranscriber(() => throw new ProviderException(ProviderError.Cancelled)), speech));

        // Real request path with a 401 whose body echoes the key: the message has no key.
        var rejected = await CloudTranscription.RunAsync(custom with { Transport = new CannedTransport(401, "{\"error\":{\"message\":\"bad key " + Secret + "\",\"code\":401}}") }, speech);
        var message = CloudTranscription.Message(rejected)!;
        Assert.True(!message.Contains(Secret) && message.Contains(ProviderError.Authentication(ProviderID.CustomAPI).UserMessage));
        Assert.Equal(new CloudTranscriptionOutcome.Failed(CloudTranscription.UnreadableAudio, true),
                     await CloudTranscription.RunAsync(whisper, Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid(), "a.wav")));
    }

    [Fact]
    public async Task PhantomText()
    {
        var speech = WAVAudio.EncodePCM16([.. Tone(1, 0.3f), .. Noise(1, 0.002f)], 48_000);
        var silence = WAVAudio.EncodePCM16(Noise(5, 0.02f), 48_000);
        Assert.Equal(Transcript("Send the report."), await CloudTranscription.RunAsync(new VerboseTranscriber(VerboseJSON((" Send the report.", 0, 1, -0.2), (" Thank you.", 1.2, 1.9, -0.2))), speech));
        Assert.Equal(Transcript(" Thank you."), await CloudTranscription.RunAsync(new VerboseTranscriber(VerboseJSON((" Thank you.", 0.3, 0.9, -0.2))), speech));
        Assert.Equal(NoSpeech(false), await CloudTranscription.RunAsync(new VerboseTranscriber(VerboseJSON((" I'm going to go ahead and get started.", 0, 5, -1.4))), silence));
        Assert.Equal(NoSpeech(false), await CloudTranscription.RunAsync(new VerboseTranscriber(VerboseJSON((" .", 0, 1, -0.2))), speech));
        // The no-speech log line names why and carries scores, never words.
        var invented = await CloudTranscription.RunDetailedAsync(new VerboseTranscriber(VerboseJSON((" Hello there friend.", 0, 5, -1.4))), silence);
        Assert.True(invented.Detail.NoSpeechReason == NoSpeechReason.LowConfidence && invented.Detail.NoSpeechLogLine.Contains("segments=1 logprob-min-max=-1.40/-1.40") && !invented.Detail.NoSpeechLogLine.Contains("Hello"));
        var twoWindows = WAVAudio.EncodePCM16(Tone(0.2, 0.3f), 48_000);
        var tooShort = await CloudTranscription.RunDetailedAsync(new VerboseTranscriber(VerboseJSON((" Hello.", 0, 0.2, -0.259))), twoWindows);
        Assert.True(tooShort.Outcome == NoSpeech(false) && tooShort.Detail.Segments?[0].LoudWindows == 2 && tooShort.Detail.NoSpeechReason == NoSpeechReason.InsufficientLoudness);
        Assert.True(tooShort.Detail.NoSpeechLogLine.Contains("reason=insufficientLoudness") && !tooShort.Detail.NoSpeechLogLine.Contains("Hello"));
        var uncertain = await CloudTranscription.RunDetailedAsync(new VerboseTranscriber(VerboseJSON((" Hello.", 0, 0.2, -1.17))), twoWindows);
        Assert.True(uncertain.Outcome == NoSpeech(false) && uncertain.Detail.NoSpeechReason == NoSpeechReason.LowConfidence);
        var threeWindows = WAVAudio.EncodePCM16(Tone(0.3, 0.3f), 48_000);
        var boundary = await CloudTranscription.RunDetailedAsync(new VerboseTranscriber(VerboseJSON((" Hello.", 0, 0.3, -0.259))), threeWindows);
        Assert.True(boundary.Outcome == Transcript(" Hello.") && boundary.Detail.Segments?[0].LoudWindows == 3 && boundary.Detail.NoSpeechReason is null);
        var punctuation = await CloudTranscription.RunDetailedAsync(new VerboseTranscriber(VerboseJSON((" .", 0, 1, -0.2))), speech);
        Assert.Equal(NoSpeechReason.PunctuationOnly, punctuation.Detail.NoSpeechReason);
        var precheckProvider = new OpenAICompatibleTranscriptionProvider("https://stt.example.test", "whisper-1", TestKey) { Transport = new CannedTransport(200, """{"text":"unused"}""") };
        var skipped = await CloudTranscription.RunDetailedAsync(precheckProvider, WAVAudio.EncodePCM16(Enumerable.Repeat(0.001f, 48_000).ToArray(), 48_000));
        Assert.True(skipped.Detail.NoSpeechReason == NoSpeechReason.Precheck && skipped.Detail.NoSpeechLogLine.StartsWith("no-speech reason=precheck clip="));
        Assert.Equal(Transcript(" Send the report."), await CloudTranscription.RunAsync(new VerboseTranscriber(VerboseJSON((" Send the report.", 0, 1, -0.8))), speech));
    }

    // MARK: Keys

    [Fact]
    public async Task Keys()
    {
        // Destination-bound keys: a key is only ever read for the endpoint it was saved for.
        var binding = new InMemorySettingsStore();
        var boundKeys = new InMemoryCredentialStore(new Dictionary<ProviderID, APIKey> { [ProviderID.CustomAPI] = TestKey });
        var unbound = Assert.Throws<CredentialException>(() => CustomAPIKeyBinding.Read("https://first.example/v1", binding, boundKeys));
        Assert.Equal(CredentialError.DestinationMismatch, unbound.Error);
        CustomAPIKeyBinding.Save(TestKey, "https://FIRST.example:443/v1/", binding, boundKeys);
        Assert.Equal(TestKey, CustomAPIKeyBinding.Read("https://first.example/v1", binding, boundKeys));
        foreach (var address in new[] { "https://second.example/v1", "http://first.example/v1", "https://first.example:8443/v1", "https://first.example/tenant/v1" })
            Assert.Equal(CredentialError.DestinationMismatch, Assert.Throws<CredentialException>(() => CustomAPIKeyBinding.Read(address, binding, boundKeys)).Error);
        var relaunched = new InMemorySettingsStore(binding.Snapshot().ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(TestKey, CustomAPIKeyBinding.Read("https://first.example/v1", relaunched, boundKeys));
        Assert.DoesNotContain(binding.Snapshot().Values, v => $"{v}".Contains(Secret));
        Assert.True(APIKey.Create("sk bad 99") is null && !KeyText.Invalid.Contains("sk bad 99"));
        Assert.Equal([ProviderID.CustomTranscription, ProviderID.CustomCleanup, ProviderID.MurmurCloud], KeyText.Providers);
        var changedKeys = new InMemoryCredentialStore(new Dictionary<ProviderID, APIKey> { [ProviderID.CustomCleanup] = TestKey });
        binding.Set(CustomAPIKeyBinding.PreferenceFor(ProviderID.CustomCleanup), "https://other.example");
        Assert.Equal(CredentialError.DestinationMismatch, Assert.Throws<CredentialException>(() => CustomAPIKeyBinding.Read("https://changed.example/v1", binding, changedKeys, ProviderID.CustomCleanup)).Error);
        // A failed save never leaves the old binding behind.
        var failingStore = new FailingStore();
        Assert.Throws<CredentialException>(() => CustomAPIKeyBinding.Save(TestKey, "https://new.example/v1", binding, failingStore, ProviderID.CustomCleanup));
        Assert.Null(binding.Get(CustomAPIKeyBinding.PreferenceFor(ProviderID.CustomCleanup)));

        var echo = new HttpResult(401, null, Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"API key not valid: " + Secret + "\",\"code\":401}}"));
        foreach (var provider in new[] { ProviderID.LocalServer, ProviderID.CustomAPI, ProviderID.CustomTranscription, ProviderID.CustomCleanup })
        {
            var msg = await KeyCheck.RunAsync(provider, TestKey, "http://localhost:11434", new MockTransport(echo));
            var errorProvider = provider is ProviderID.CustomTranscription or ProviderID.CustomCleanup ? ProviderID.CustomAPI : provider;
            Assert.True(!msg.Contains(Secret) && msg == ProviderError.Authentication(errorProvider).UserMessage, $"validate {provider.RawValue()} message has no key");
        }
        var ok = new MockTransport(new HttpResult(200, null, """{"data":[{"id":"llama3.2"}],"object":"list"}"""u8.ToArray()));
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.Openrouter).UserMessage, await KeyCheck.RunAsync(ProviderID.Openrouter, TestKey, "", ok));
        Assert.Equal(KeyText.Validated(ProviderID.CustomAPI), await KeyCheck.RunAsync(ProviderID.CustomAPI, TestKey, "https://api.example.com/v1", ok));
        Assert.Equal(KeyText.Validated(ProviderID.CustomAPI), await KeyCheck.RunAsync(ProviderID.CustomAPI, null, "https://api.example.com/v1", ok));
        Assert.Equal(KeyText.Validated(ProviderID.LocalServer), await KeyCheck.RunAsync(ProviderID.LocalServer, null, "http://localhost:11434", ok));
        var refused = MockTransport.Failing(TransportError.CannotConnectToHost);
        foreach (var provider in new[] { ProviderID.CustomAPI, ProviderID.CustomTranscription, ProviderID.CustomCleanup })
        {
            Assert.Equal("No local model server is running at localhost:11434. Start it, or check the address in Settings.", await KeyCheck.RunAsync(provider, null, "http://localhost:11434", refused));
            Assert.Equal(ProviderError.Network(ProviderID.CustomAPI).UserMessage, await KeyCheck.RunAsync(provider, null, "https://api.example.com/v1", refused));
        }
        var found = await KeyCheck.DiscoverModelsAsync("http://localhost:11434", null, ok);
        Assert.True(found.Models!.SequenceEqual(["llama3.2"]) && KeyCheck.DiscoveryMessage(found) == "Found 1 model.");
        var badURL = await KeyCheck.DiscoverModelsAsync("file:///etc", TestKey);
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.LocalServer).UserMessage, KeyCheck.DiscoveryMessage(badURL));
        // The local model: kept as a choice, reports not downloaded until M3 ports it.
        Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded.UserMessage(), await KeyCheck.RunAsync(ProviderID.LocalModel, null, ""));
    }

    sealed class FailingStore : ICredentialStore
    {
        public APIKey? Read(ProviderID provider) => null;
        public void Save(APIKey key, ProviderID provider) => throw new CredentialException(CredentialError.Keychain(-1));
        public void Delete(ProviderID provider) { }
        public void DeleteAll() { }
    }

    // MARK: Murmur Cloud routes

    [Fact]
    public async Task MurmurCloudRoutes()
    {
        var routes = new RouteSettings { Transcription = TranscriptionRoute.MurmurCloud, Cleanup = CleanupChoice.MurmurCloud };
        Assert.True(CleanupChoice.MurmurCloud.ProviderID() == ProviderID.MurmurCloud && CleanupChoice.MurmurCloud.CredentialID() == ProviderID.MurmurCloud
                    && CleanupChoice.MurmurCloud.ProcessingRoute() == CleanupRoute.Cloud && RouteRawValues.TranscriptionRouteFrom("murmur-cloud") == TranscriptionRoute.MurmurCloud
                    && routes.ApiBaseURL(ProviderID.MurmurCloud) is null, "Murmur Cloud is its own provider; its key has no address binding");
        var store = new InMemoryCredentialStore(new Dictionary<ProviderID, APIKey> { [ProviderID.MurmurCloud] = TestKey });
        Assert.Equal(TestKey, CustomAPIKeyBinding.ReadKey(ProviderID.MurmurCloud, routes, new InMemorySettingsStore(), store));
        var asked = new List<ProviderID>();
        var both = RoutePlan.Make(routes, true, id => { asked.Add(id); return id == ProviderID.MurmurCloud ? TestKey : null; });
        Assert.True(both.Transcriber is MurmurCloudTranscriptionProvider && both.Cleaner is MurmurCloudCleanupProvider && both.Refusal is null && asked.ToHashSet().SetEquals([ProviderID.MurmurCloud]));
        Assert.True(both.Stages.Select(s => s.Stage).SequenceEqual([ProcessingStage.Transcription, ProcessingStage.Cleanup]) && both.Stages.All(s => s.DestinationHost == "api.murmurdictation.com"));
        Assert.IsAssignableFrom<ISilencePrecheckedTranscriber>(both.Transcriber);
        routes.Transcription = TranscriptionRoute.Local;
        var cleanupOnly = RoutePlan.Make(routes, true, id => id == ProviderID.MurmurCloud ? TestKey : null);
        Assert.True(cleanupOnly.Transcriber is null && cleanupOnly.Cleaner is MurmurCloudCleanupProvider && cleanupOnly.Stages.Select(s => s.Stage).SequenceEqual([ProcessingStage.Cleanup])
                    && !cleanupOnly.Stages[0].LeavesDevice.Contains("audio"));
        routes.Transcription = TranscriptionRoute.MurmurCloud;
        var noKey = RoutePlan.Make(routes, true, _ => null);
        Assert.True(noKey.Transcriber is null && noKey.Cleaner is null && noKey.Refusal == ProviderError.InvalidConfiguration(ProviderID.MurmurCloud).UserMessage);
        Assert.Equal(2, noKey.Stages.Count);

        var ok = new CannedTransport(200, """{"data":[]}""");
        Assert.Equal("Murmur Cloud accepted the key.", await KeyCheck.RunAsync(ProviderID.MurmurCloud, TestKey, "", ok));
        Assert.Equal(1, ok.Count);
        Assert.Equal(ProviderError.Quota(ProviderID.MurmurCloud).UserMessage, await KeyCheck.RunAsync(ProviderID.MurmurCloud, TestKey, "", new CannedTransport(402)));
        Assert.Equal(ProviderError.Authentication(ProviderID.MurmurCloud).UserMessage, await KeyCheck.RunAsync(ProviderID.MurmurCloud, TestKey, "", new CannedTransport(401)));
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.MurmurCloud).UserMessage, await KeyCheck.RunAsync(ProviderID.MurmurCloud, null, ""));
    }

    [Fact]
    public void Shapes()
    {
        ICleanupProvider local = new LocalModelCleanupProvider();
        Assert.True(local.Shape == new CleanupShape { TakesContext = true, SectionTarget = 150, KeepsCorrectionsAttached = true, TakesPresets = true } && local.ModelID == LocalCleanupIdentity.ModelID);
        ICleanupProvider custom = new OpenAICompatibleCleanupProvider("https://api.example.com/v1", "m", APIKey.Create("shape-key"), ProviderID.CustomAPI);
        Assert.True(custom.Shape == new CleanupShape() && custom.ModelID == "m" && ((OpenAICompatibleCleanupProvider)custom.ForMeetingNotes()).Timeout == OpenAICompatibleCleanupProvider.MaximumTimeout);
        Assert.Same(local, local.ForMeetingNotes());
    }

    [Fact]
    public void Endpoints()
    {
        var settings = new RouteSettings { CustomAPIBaseURL = "https://cleanup.example/v1", CustomTranscriptionAPIBaseURL = "https://speech.example/v1" };
        Assert.True(settings.ApiBaseURL(ProviderID.CustomCleanup) == "https://cleanup.example/v1" && settings.ApiBaseURL(ProviderID.CustomTranscription) == "https://speech.example/v1");
        Assert.True(settings.ApiBaseURL(ProviderID.CustomAPI) is null && settings.ApiBaseURL(ProviderID.LocalServer) is null);
    }

    // MARK: Cancellation prevents insertion

    [Fact]
    public void Cancel() => SingleThreadContext.Run(async () =>
    {
        var request = new CloudRequest();
        var delivered = new List<CloudTranscriptionOutcome>();

        // Cancel while the provider call is in flight.
        var slow = new SpyTranscriber(() => "late text", delaySeconds: 5);
        var speech = WAVAudio.EncodePCM16(Tone(1, 0.3f), 16_000);
        request.Start(ct => CloudTranscription.RunAsync(slow, speech, ct), delivered.Add);
        Assert.True(request.IsActive);
        for (var i = 0; i < 200 && slow.Calls == 0; i++) await Task.Delay(5);
        Assert.Equal(1, slow.Calls);
        var started = Stopwatch.StartNew();
        request.Cancel();
        Assert.False(request.IsActive);
        await Task.Delay(300);
        Assert.True(delivered.Count == 0 && started.Elapsed.TotalSeconds < 4, "canceled request never delivers");

        // Cancel after the work finished but before the UI thread delivered it.
        request.Start(_ => Task.FromResult("done"), _ => delivered.Add(Transcript("should not insert")));
        request.Cancel();
        await Task.Delay(100);
        Assert.Empty(delivered);

        // A superseding request cancels the first one.
        var texts = new List<string>();
        request.Start(async ct => { try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { } return "first"; }, texts.Add);
        request.Start(_ => Task.FromResult("second"), texts.Add);
        for (var i = 0; i < 200 && texts.Count == 0; i++) await Task.Delay(5);
        await Task.Delay(100);
        Assert.True(texts.SequenceEqual(["second"]) && !request.IsActive, $"only the latest request delivers: {string.Join(",", texts)}");
    });

    [Fact]
    public async Task CloudCleanupFailureIsAProviderErrorNotAFallback()
    {
        // The route layer surfaces the error; the local-rules fallback is ProcessingCoordinator's (CloudCleanup below).
        ICleanupProvider failing = new MockCleaner(ProviderError.Quota(ProviderID.CustomAPI));
        var e = await Assert.ThrowsAsync<ProviderException>(() => failing.CleanupAsync(new CleanupMessages("s", "u")));
        Assert.Equal(SessionDisposition.PreserveNeedsChange, e.Error.Disposition);
    }

    static ProcessingCoordinator Coordinator(Func<string, bool>? isCommonWord = null) => new(TempDirs.New("routes"), new InMemorySettingsStore(), isCommonWord);

    // MARK: dictionaryTranscriptionChecks

    [Fact]
    public async Task DictionaryTranscription()
    {
        var p = Coordinator();
        p.Dictionary.Add("Aakash", ["Akash"]);
        p.Dictionary.Add("spaceyboi", ["spacey boy"], EntrySource.Learned);
        p.Dictionary.Add("Shelfmate", [], EntrySource.Learned);
        p.Update(p.Settings with { Options = CleanupOptions.Faithful });
        const string raw = "Call Akash and spacey boy.";
        var transport = new CannedTransport(200, JsonSerializer.Serialize(new { text = raw }));
        var provider = new OpenAICompatibleTranscriptionProvider("https://stt.example.test", "whisper-1", TestKey) { Transport = transport };
        var outcome = await CloudTranscription.RunAsync(provider, WAVAudio.EncodePCM16(Tone(1, 0.3f), 48_000));
        var text = Assert.IsType<CloudTranscriptionOutcome.Transcript>(outcome).Text;
        Assert.True(text == raw, "raw provider text remains available for history");
        Assert.True(p.Process(text).Text == "Call Aakash and spaceyboi.", "manual and learned variants correct without ASR hints or cleanup");
        var body = Encoding.UTF8.GetString(transport.Requests[^1].Body ?? []);
        Assert.True(!body.Contains("name=\"prompt\"") && !body.Contains("Shelfmate"), "whole dictionary stays out of custom ASR");
        Assert.True(p.Process("Send a shelf made report.").Text == "Send a shelf made report.", "preferred-only learned entry does not make faithful correction fuzzy");
        p.Update(p.Settings with { Route = CleanupRoute.Cloud, Options = CleanupOptions.Standard with { Polish = true } });
        p.CloudCleaner = new MockCleaner(text: "Send a Shelfmate report.");
        Assert.True((await p.ProcessAsync("Send a shelf made report.")).Text == "Send a Shelfmate report.", "preferred-only learned spelling still corrected by guarded cleanup");
        p.CloudCleaner = new MockCleaner(ProviderError.Timeout(ProviderID.CustomAPI));
        Assert.True((await p.ProcessAsync(text)).Text == "Call Aakash and spaceyboi.", "cleanup failure keeps manual and learned exact corrections");
    }

    // MARK: languageSelectionChecks

    [Fact]
    public async Task LanguageSelection()
    {
        Assert.Equal(LocalSpeechModel.ParakeetEnglish, DictationLanguages.English.LocalModel);
        Assert.Equal(LocalSpeechModel.ParakeetTDT, DictationLanguages.Create(["en", "es", "fr"])!.LocalModel);
        if (HasCultureData)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(["hi"], DictationLanguages.Matching("  HINDI ", culture));
            Assert.Contains("ar", DictationLanguages.Matching("العربية", culture));
            Assert.Contains("es", DictationLanguages.Matching("espanol", culture));
        }
        var broader = DictationLanguages.Create(["en", "hi", "ar"])!;
        Assert.Equal(LocalSpeechModel.WhisperTurbo, broader.LocalModel);
        Assert.Equal(LocalSpeechModel.WhisperTurbo, DictationLanguages.Create(["mt", "hi"])!.LocalModel);
        Assert.True(DictationLanguages.Create([]) is null && DictationLanguages.Create(["invalid"]) is null, "invalid selections are rejected");

        var audio = WAVAudio.EncodePCM16(Tone(1, 0.3f, 16_000), 16_000);
        var folder = TempDirs.New("language");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "speech.wav");
        await File.WriteAllBytesAsync(path, audio);
        var transport = new CannedTransport(200, "{\"text\":\"नमस्ते، مرحبًا\"}");
        var provider = new MurmurCloudTranscriptionProvider(TestKey) { Transport = transport };
        const string spoken = "नमस्ते، مرحبًا";
        var english = await CloudTranscription.RunDetailedAsync(provider, audio);
        Assert.True(english.Outcome == Transcript(spoken) && Body(transport).Contains("name=\"language\"\r\n\r\nen\r\n"), "the default request still sends English");
        var multilingual = await CloudTranscription.RunDetailedAsync(provider, path, broader);
        Assert.Equal(Transcript(spoken), multilingual.Outcome);
        var body = Body(transport);
        Assert.True(!body.Contains("name=\"language\"") && !body.Contains("name=\"prompt\""), "multilingual multipart omits language and prompt");
        Assert.Null(CloudAudio.Prepare([0], broader.CloudHint).Payload.Language);
    }

    static bool HasCultureData
    {
        get
        {
            try { System.Globalization.CultureInfo.GetCultureInfo("fr"); return true; }
            catch (System.Globalization.CultureNotFoundException) { return false; }
        }
    }

    static string Body(CannedTransport transport) => Encoding.UTF8.GetString(transport.Requests[^1].Body ?? []);

    // MARK: lexiconChecks

    [Fact]
    public void Lexicon()
    {
        HashSet<string> lexicon = ["lunch", "launch", "casual", "causal", "team"];
        var p = Coordinator(lexicon.Contains);
        p.Update(p.Settings with { LearningEnabled = true });
        static CorrectionLedger.RecordOutcome Observe(ProcessingCoordinator c, string s, string t) =>
            c.Observe(new ObservedCorrection { SessionId = Guid.NewGuid(), Source = s, Target = t, ObservedAt = DateTime.UtcNow });
        Assert.True(Observe(p, "lunch", "launch") == CorrectionLedger.RecordOutcome.Rejected(CandidateRejection.OrdinaryWords), "lexicon rejects lunch -> launch");
        Assert.True(Observe(p, "casual", "causal") == CorrectionLedger.RecordOutcome.Rejected(CandidateRejection.OrdinaryWords), "lexicon rejects casual -> causal");
        Assert.True(Observe(p, "Acme", "Akme") == CorrectionLedger.RecordOutcome.Provisional, "lexicon keeps a name respelling");
        Assert.True(Observe(Coordinator(), "casual", "causal") == CorrectionLedger.RecordOutcome.Provisional, "without a lexicon the swap passes (known limit)");
    }

    // MARK: cleanupChecks

    [Fact]
    public async Task CloudCleanup()
    {
        var p = Coordinator();
        p.Update(p.Settings with { Route = CleanupChoice.CustomAPI.ProcessingRoute(), Options = CleanupOptions.Standard with { Polish = true } });
        p.CloudCleaner = new MockCleaner(text: "Call Akme.");
        Assert.True((await p.ProcessAsync("Um, call Akme.")).Text == "Call Akme.", "configured cloud cleanup inserts");
        p.CloudCleaner = new MockCleaner(ProviderError.Quota(ProviderID.CustomAPI));
        var result6 = await p.ProcessAsync("Um, call Akme.");
        Assert.True(result6.Text == "Call Akme.", $"cleanup error uses local rules: {result6.Text}");
        Assert.True(result6.ProviderError == ProviderError.Quota(ProviderID.CustomAPI), "provider error kept for the notice");
        p.CloudCleaner = null;
        var result7 = await p.ProcessAsync("Um, call Akme.");
        Assert.True(result7.Text == "Call Akme.", "unconfigured cloud cleanup uses local rules");
        Assert.True(result7.ProviderError is null, "error cleared per call");
    }

    // MARK: murmurCloudChecks, the coordinator half

    [Fact]
    public async Task MurmurCloudThroughTheCoordinator()
    {
        // The server gets the text after this PC's edits, the guard judges the reply, History records the server's prompt.
        var p = Coordinator();
        p.Update(p.Settings with { Route = CleanupChoice.MurmurCloud.ProcessingRoute(), Options = CleanupOptions.Standard with { Polish = true, Spoken = true } });
        var server = new CannedTransport(200, """{"text":"Revenue is up 10%."}""");
        p.CloudCleaner = new MurmurCloudCleanupProvider(TestKey) { Transport = server };
        var result = await p.ProcessAsync("revenue is up ten percent");
        var body = JsonDocument.Parse(server.Requests[^1].Body!).RootElement;
        Assert.True(body.GetProperty("transcript").GetString() == "revenue is up 10%" && body.GetProperty("level").GetString() == "polish", "the server gets the transcript after spoken commands");
        Assert.True(result.Text == "Revenue is up 10%." && result.PromptHash == "murmur-cloud-server", "the guarded reply is inserted; the prompt hash names the server's prompt");
        // A preset is refused without a request; the ordinary cleanup that follows carries none of the preset's instructions.
        p.EngineCleaner = p.CloudCleaner; // writing modes run on the AI engine
        var email = p.Settings.Style(WritingMode.Presets[1]);
        var before = server.Count;
        await p.ProcessAsync("revenue is up ten percent", email);
        var presetBody = JsonDocument.Parse(server.Requests[^1].Body!).RootElement;
        Assert.True(email.Instructions.Length > 0 && server.Count == before + 1 && !presetBody.TryGetProperty("style_notes", out _),
                    "a preset falls back to one ordinary cleanup with no preset instructions as style notes");
    }

    // MARK: main(): every destination setting stays on this PC when restoring (check_routes.swift:144-146)

    [Fact]
    public void EveryDestinationSettingStaysOnThisPCWhenRestoring()
    {
        Assert.Equal(new HashSet<string> { RouteSettings.Key.Transcription, RouteSettings.Key.LegacyCloudTranscriber, RouteSettings.Key.Cleanup, RouteSettings.Key.CustomAPIBaseURL,
                                           RouteSettings.Key.CustomTranscriptionAPIBaseURL, RouteSettings.Key.LegacyServerURL, Prefs.CleanupRoute.Key },
                     DataBackup.DestinationSettings.ToHashSet());
    }
}
