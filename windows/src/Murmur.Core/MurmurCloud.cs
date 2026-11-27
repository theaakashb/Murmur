// Port of app/MurmurCloud.swift.
using System.Text.Json;

namespace Murmur.Core;

// Murmur Cloud: Murmur's hosted cleanup and transcription, reached with one Murmur Cloud key.
// Cleanup is Murmur's own JSON (the server builds the prompt), not OpenAI chat; transcription is the
// OpenAI-style multipart upload. The guard stays in the app (Processing.cloudClean).

public static class MurmurCloud
{
    public static readonly string DefaultHost = ProvidersData.Value.MurmurCloud.DefaultHost;
    // shortcut: the Mac bakes a test host into Info.plist at build time (MURMUR_CLOUD_HOST); no Windows build option sets one yet. Add it with the M6 release build.
    public static readonly ProviderUrl BaseURL = MakeBaseURL(null);

    /// <summary>The fixed https base URL for a build-time host, or the default host when none (or an unusable one) is given.</summary>
    public static ProviderUrl MakeBaseURL(string? infoHost)
    {
        var host = string.IsNullOrEmpty(infoHost) ? DefaultHost : infoHost;
        try { return ProviderUrl.Parse($"https://{host}"); }
        catch (FormatException) { return ProviderUrl.Parse($"https://{DefaultHost}"); }
    }

    public static string Host => BaseURL.Host;
    public static readonly string CleanupModel = ProvidersData.Value.MurmurCloud.CleanupModel;
    public static readonly string TranscriptionModel = ProvidersData.Value.MurmurCloud.TranscriptionModel;
    /// <summary>Seconds. The server's cleanup call to its model times out after 30 s; this leaves room for the upload and reply.</summary>
    public static readonly double CleanupDeadline = ProvidersData.Value.MurmurCloud.CleanupDeadline;

    /// <summary><c>GET /v1/models</c> with the key: 200 accepted, 401 bad key, 402 no active plan.</summary>
    public static ProviderRequest ModelsRequest(APIKey key) =>
        new ProviderRequest { Method = "GET", Url = BaseURL.Appending("/v1/models") }
            .WithHeader("Authorization", $"Bearer {key.Secret}").WithHeader("Accept", "application/json");

    public static async Task ValidateAsync(APIKey key, IHttpTransport transport, CancellationToken cancellationToken = default)
    {
        var result = await new ProviderHTTP(ProviderID.MurmurCloud, transport, ProviderLimits.ValidationDeadline).ExecuteAsync(ModelsRequest(key), cancellationToken).ConfigureAwait(false);
        ProviderHTTP.ValidationResult(ProviderID.MurmurCloud, result);
    }
}

/// <summary><c>POST /v1/cleanup</c>. The server rejects any key it does not document (400), so the body is exactly these fields.</summary>
public static class MurmurCloudCleanupFormat
{
    public sealed record Context(string Selected, string Clipboard, string Window);

    public sealed record Body(string Transcript, string Level, bool Fillers, bool Corrections, bool Punctuation, bool Lists,
                              IReadOnlyList<string>? Terms, string? StyleNotes, Context? Context)
    {
        public bool Equals(Body? o) => o is not null && Transcript == o.Transcript && Level == o.Level && Fillers == o.Fillers && Corrections == o.Corrections
            && Punctuation == o.Punctuation && Lists == o.Lists && StyleNotes == o.StyleNotes && Context == o.Context
            && (Terms is null ? o.Terms is null : o.Terms is not null && Terms.SequenceEqual(o.Terms));
        public override int GetHashCode() => HashCode.Combine(Transcript, Level, StyleNotes, Context);

        /// <summary>The JSON Swift's JSONEncoder writes: sorted keys, nil optionals left out.</summary>
        public byte[] Encode()
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, OpenAIChatFormat.Writer))
            {
                w.WriteStartObject();
                if (Context is not null)
                {
                    w.WriteStartObject("context");
                    w.WriteString("clipboard", Context.Clipboard); w.WriteString("selected", Context.Selected); w.WriteString("window", Context.Window);
                    w.WriteEndObject();
                }
                w.WriteBoolean("corrections", Corrections);
                w.WriteBoolean("fillers", Fillers);
                w.WriteString("level", Level);
                w.WriteBoolean("lists", Lists);
                w.WriteBoolean("punctuation", Punctuation);
                if (StyleNotes is not null) w.WriteString("style_notes", StyleNotes);
                if (Terms is not null) { w.WriteStartArray("terms"); foreach (var t in Terms) w.WriteStringValue(t); w.WriteEndArray(); }
                w.WriteString("transcript", Transcript);
                w.WriteEndObject();
            }
            return buffer.ToArray();
        }
    }

    /// <summary>Null for Strict (no polish): the server takes only polish and professional, and Strict never uses a model.</summary>
    public static Body? MakeBody(CleanupInput input)
    {
        var options = input.Options;
        if (!options.Polish || input.Sent.Length == 0) return null;
        return new Body(input.Sent, options.Professional ? "professional" : "polish", options.Fillers, options.Corrections, options.Punctuation, options.Lists,
                        input.PromptTerms.Count == 0 ? null : input.PromptTerms, input.Notes.Length == 0 ? null : input.Notes,
                        input.Context is null ? null : new Context(input.Context.Selected, input.Context.Clipboard, input.Context.Window));
    }

    public static ProviderRequest MakeRequest(CleanupInput input, APIKey key)
    {
        if (MakeBody(input) is not Body body) throw new ProviderException(ProviderError.InvalidConfiguration(ProviderID.MurmurCloud));
        return new ProviderRequest { Method = "POST", Url = MurmurCloud.BaseURL.Appending("/v1/cleanup"), Body = body.Encode() }
            .WithHeader("Authorization", $"Bearer {key.Secret}").WithHeader("Content-Type", "application/json").WithHeader("Accept", "application/json");
    }

    /// <summary><c>{"text": "..."}</c>; any other status or an error object goes through the shared classifier.</summary>
    public static string Parse(HttpResult result)
    {
        if (result.Status is < 200 or >= 300) throw new ProviderException(ProviderErrorClassifier.Classify(ProviderID.MurmurCloud, result));
        if (Json.Object(result.Body) is not JsonElement obj) throw new ProviderException(ProviderError.MalformedResponse(ProviderID.MurmurCloud));
        if (Json.TryGet(obj, "error", out _)) throw new ProviderException(ProviderErrorClassifier.Classify(ProviderID.MurmurCloud, result));
        return Json.String(obj, "text") ?? throw new ProviderException(ProviderError.MalformedResponse(ProviderID.MurmurCloud));
    }
}

public sealed record MurmurCloudCleanupProvider(APIKey ApiKey) : ICleanupProvider
{
    public ProviderID Id => ProviderID.MurmurCloud;
    public IHttpTransport Transport { get; init; } = HttpClientTransport.Default;
    public string? ModelID => MurmurCloud.CleanupModel;
    public CleanupShape Shape => new() { TakesPresets = false };
    public ICleanupProvider ForMeetingNotes() => this;

    public DataDisclosure Disclosure => new(Id, ProcessingStage.Cleanup, MurmurCloud.Host,
        ["the transcript text", "relevant dictionary terms", "any enabled screen context and style notes"],
        ["audio", "history", "the rest of the dictionary"], "");

    /// <summary>Rewrite presets and meeting notes carry no typed input: the server does not take them, so they are refused, not approximated.</summary>
    public async Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default)
    {
        if (messages.Input is not CleanupInput input) throw new ProviderException(ProviderError.InvalidConfiguration(Id));
        var request = MurmurCloudCleanupFormat.MakeRequest(input, ApiKey);
        return MurmurCloudCleanupFormat.Parse(await new ProviderHTTP(Id, Transport, MurmurCloud.CleanupDeadline).ExecuteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default) => MurmurCloud.ValidateAsync(ApiKey, Transport, cancellationToken);
}

/// <summary><c>POST /v1/audio/transcriptions</c>, shaped like the custom provider's upload. No dictionary prompt (VOCABULARY.md).</summary>
public sealed record MurmurCloudTranscriptionProvider(APIKey ApiKey) : IDetailedTranscriber, ISilencePrecheckedTranscriber
{
    public ProviderID Id => ProviderID.MurmurCloud;
    public IHttpTransport Transport { get; init; } = HttpClientTransport.Default;
    public string? ModelID => MurmurCloud.TranscriptionModel;
    public ProviderUrl Endpoint => MurmurCloud.BaseURL.Appending("/v1/audio/transcriptions");

    public DataDisclosure Disclosure => new(Id, ProcessingStage.Transcription, MurmurCloud.Host, ["audio"], ["dictionary", "history"], "");

    public ProviderRequest MakeRequest(AudioPayload audio, string? boundary = null) =>
        OpenAITranscriptionFormat.MakeRequest(Endpoint, ApiKey, MurmurCloud.TranscriptionModel, audio, boundary ?? Multipart.MakeBoundary());

    public async Task<string> TranscribeAsync(AudioPayload audio, CancellationToken cancellationToken = default) =>
        (await TranscribeDetailedAsync(audio, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<TranscriptionResult> TranscribeDetailedAsync(AudioPayload audio, CancellationToken cancellationToken = default)
    {
        var limit = OpenAICompatibleTranscriptionProvider.UploadLimitBytes;
        if (audio.Data.Length > limit) throw new ProviderException(ProviderError.PayloadTooLarge(Id, limit));
        var result = await new ProviderHTTP(Id, Transport, OpenAICompatibleTranscriptionProvider.Deadline).ExecuteAsync(MakeRequest(audio), cancellationToken).ConfigureAwait(false);
        return OpenAITranscriptionFormat.ParseDetailed(Id, result, audio.Levels);
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default) => MurmurCloud.ValidateAsync(ApiKey, Transport, cancellationToken);

    /// <summary>Murmur Cloud transcribes with Whisper too (Routes.swift).</summary>
    public bool ShouldSkip(float peakLevel, double? durationSeconds) => AudioFormat.ShouldSkipForSilence(peakLevel, durationSeconds);
}
