// Port of app/ProviderTypes.swift.
namespace Murmur.Core;

// Rules enforced here:
// - Exactly one HTTP request per call. No automatic retry (a retry could duplicate output)
//   and no fallback to another provider or model.
// - Every request has an explicit total deadline. Cancelling the token cancels the request.
// - Error messages are built from fixed strings, the provider name and status codes. They never
//   include response bodies, API keys or transcript text.

/// <summary>Recorded audio sent to a transcription provider.</summary>
public sealed record AudioPayload(byte[] Data, string Filename = "audio.wav", string MimeType = "audio/wav", string? Language = "en")
{
    /// <summary>Peak level of each 100 ms window of the audio sent, so a transcript segment can be checked against the sound under it.
    /// Null when the audio was not decoded. Never sent.</summary>
    public IReadOnlyList<float>? Levels { get; init; }
}

/// <summary>Why a transcript was judged no speech. Raw values are only logged, never stored.</summary>
public enum NoSpeechReason { Precheck, LowConfidence, InsufficientLoudness, Empty, PunctuationOnly }

/// <summary>The silence pre-check's result. Stored in History as its raw value (passed, skipped, unchecked).</summary>
public enum Precheck { Passed, Skipped, Unchecked }

/// <summary>A transcriber's answer with the detail History keeps. Segments only where the model reports them.</summary>
public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscriptSegment>? Segments = null, NoSpeechReason? NoSpeechReason = null)
{
    public bool Equals(TranscriptionResult? o) => o is not null && Text == o.Text && NoSpeechReason == o.NoSpeechReason
        && (Segments is null ? o.Segments is null : o.Segments is not null && Segments.SequenceEqual(o.Segments));
    public override int GetHashCode() => HashCode.Combine(Text, NoSpeechReason);
}

/// <summary>One Whisper segment: its scores and times, the 100 ms windows under it that reach the silence level,
/// and whether the parser dropped it from the transcript.</summary>
public sealed record TranscriptSegment
{
    public required string Text { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    public double? AvgLogprob { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
    public int? LoudWindows { get; init; }
    public bool Dropped { get; init; }
}

public enum ProcessingStage { Transcription, Cleanup }

public static class ProcessingStageExtensions
{
    public static string RawValue(this ProcessingStage stage) => stage == ProcessingStage.Transcription ? "transcription" : "cleanup";
}

/// <summary>What leaves the device for one route. Show it before the first cloud use.</summary>
public sealed record DataDisclosure(ProviderID Provider, ProcessingStage Stage, string DestinationHost, IReadOnlyList<string> LeavesDevice,
                                    IReadOnlyList<string> StaysOnDevice, string RetentionNote, string? Warning = null)
{
    /// <summary>True when nothing in this route leaves the computer (a loopback local server).</summary>
    public bool StaysOnThisMac => LeavesDevice.Count == 0;

    public string Summary
    {
        get
        {
            string main;
            if (StaysOnThisMac)
                main = $"{Provider.DisplayName()} {Stage.RawValue()} runs on this Mac ({DestinationHost}); nothing leaves this Mac. {RetentionNote}";
            else
            {
                static string List(IReadOnlyList<string> items) => items.Count < 2 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
                main = $"Sends {List(LeavesDevice)} to {DestinationHost}. {RetentionNote}";
            }
            return Warning is null ? main : main + " " + Warning;
        }
    }

    public bool Equals(DataDisclosure? o) => o is not null && Provider == o.Provider && Stage == o.Stage && DestinationHost == o.DestinationHost
        && LeavesDevice.SequenceEqual(o.LeavesDevice) && StaysOnDevice.SequenceEqual(o.StaysOnDevice) && RetentionNote == o.RetentionNote && Warning == o.Warning;
    public override int GetHashCode() => HashCode.Combine(Provider, Stage, DestinationHost, RetentionNote, Warning);
}

/// <summary>The caller's choice for the session after a provider error.</summary>
public enum SessionDisposition
{
    /// <summary>Keep audio or raw text. Offer an explicit retry later with the same route.</summary>
    PreserveRetryLater,
    /// <summary>Keep audio or raw text. Retrying unchanged will fail; the user must change a key, plan, model, or route first.</summary>
    PreserveNeedsChange,
    /// <summary>The user cancelled. Discard according to the cancellation policy.</summary>
    Discard,
}

/// <summary>Swift's <c>ProviderError</c> case names; <see cref="ProviderError.Kind"/> is the camelCase name History and the timing log store.</summary>
public enum ProviderErrorKind
{
    Authentication, Quota, RateLimited, Network, Timeout, PayloadTooLarge, Server, MalformedResponse, ContentBlocked,
    Rejected, InvalidConfiguration, ServerNotRunning, ModelUnavailable, Cancelled, OutputTruncated,
}

/// <summary>Swift <c>enum ProviderError</c>: one case and its payload. Thrown inside <see cref="ProviderException"/>.
/// Carries no key, response body or transcript text: only the provider, status codes, a retry delay, a byte limit and a host:port.</summary>
public sealed record ProviderError
{
    public ProviderErrorKind Case { get; private init; }
    /// <summary>Null only for <see cref="Cancelled"/>.</summary>
    public ProviderID? Provider { get; private init; }
    public double? RetryAfter { get; private init; }
    public int? LimitBytes { get; private init; }
    public long? Status { get; private init; }
    /// <summary>"host:port" taken from the configured base URL, never from request content.</summary>
    public string? Address { get; private init; }
    public OnDeviceModelUnavailable? Unavailable { get; private init; }

    ProviderError() { }

    public static ProviderError Authentication(ProviderID p) => new() { Case = ProviderErrorKind.Authentication, Provider = p };
    public static ProviderError Quota(ProviderID p) => new() { Case = ProviderErrorKind.Quota, Provider = p };
    public static ProviderError RateLimited(ProviderID p, double? retryAfter) => new() { Case = ProviderErrorKind.RateLimited, Provider = p, RetryAfter = retryAfter };
    public static ProviderError Network(ProviderID p) => new() { Case = ProviderErrorKind.Network, Provider = p };
    public static ProviderError Timeout(ProviderID p) => new() { Case = ProviderErrorKind.Timeout, Provider = p };
    public static ProviderError PayloadTooLarge(ProviderID p, int? limitBytes) => new() { Case = ProviderErrorKind.PayloadTooLarge, Provider = p, LimitBytes = limitBytes };
    public static ProviderError Server(ProviderID p, long? status) => new() { Case = ProviderErrorKind.Server, Provider = p, Status = status };
    public static ProviderError MalformedResponse(ProviderID p) => new() { Case = ProviderErrorKind.MalformedResponse, Provider = p };
    /// <summary>The provider's safety or moderation filter declined the input.</summary>
    public static ProviderError ContentBlocked(ProviderID p) => new() { Case = ProviderErrorKind.ContentBlocked, Provider = p };
    /// <summary>Other 4xx, e.g. an unknown model or a bad parameter.</summary>
    public static ProviderError Rejected(ProviderID p, long status) => new() { Case = ProviderErrorKind.Rejected, Provider = p, Status = status };
    /// <summary>Missing key or an invalid local setting; no request was sent.</summary>
    public static ProviderError InvalidConfiguration(ProviderID p) => new() { Case = ProviderErrorKind.InvalidConfiguration, Provider = p };
    /// <summary>Connection refused by a user-run model server: nothing is listening at <paramref name="address"/>.</summary>
    public static ProviderError ServerNotRunning(ProviderID p, string address) => new() { Case = ProviderErrorKind.ServerNotRunning, Provider = p, Address = address };
    /// <summary>An on-device model that cannot run now; nothing was generated.</summary>
    public static ProviderError ModelUnavailable(ProviderID p, OnDeviceModelUnavailable reason) => new() { Case = ProviderErrorKind.ModelUnavailable, Provider = p, Unavailable = reason };
    /// <summary>The reply stopped at the output-token cap (finish_reason "length"): the answer is cut off. Meeting notes split the request once and retry; every other use treats it like <see cref="MalformedResponse"/>.</summary>
    public static ProviderError OutputTruncated(ProviderID p) => new() { Case = ProviderErrorKind.OutputTruncated, Provider = p };
    public static readonly ProviderError Cancelled = new() { Case = ProviderErrorKind.Cancelled };

    /// <summary>The case name, for History and the timing log.</summary>
    public string Kind => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(Case.ToString());

    public SessionDisposition Disposition => Case switch
    {
        ProviderErrorKind.RateLimited or ProviderErrorKind.Network or ProviderErrorKind.Timeout or ProviderErrorKind.Server
            or ProviderErrorKind.MalformedResponse or ProviderErrorKind.OutputTruncated or ProviderErrorKind.ServerNotRunning => SessionDisposition.PreserveRetryLater,
        ProviderErrorKind.Cancelled => SessionDisposition.Discard,
        _ => SessionDisposition.PreserveNeedsChange,
    };

    /// <summary>Short message for the bubble or history. Contains no key, response body or transcript text.</summary>
    public string UserMessage
    {
        get
        {
            var name = Provider?.DisplayName() ?? "The provider";
            switch (Case)
            {
                case ProviderErrorKind.Authentication: return $"{name} rejected the API key. Check the key in Settings.";
                case ProviderErrorKind.Quota: return $"{name} reports no remaining quota or credits for this account. Check your plan or billing, or choose another route.";
                case ProviderErrorKind.RateLimited:
                    if (RetryAfter is double seconds && double.IsFinite(seconds) && seconds > 0 && Math.Ceiling(seconds) < long.MaxValue)
                        return $"{name} is rate limiting requests. Try again in {(long)Math.Ceiling(seconds)} seconds.";
                    return $"{name} is rate limiting requests. Try again shortly.";
                case ProviderErrorKind.Network when Provider == ProviderID.LocalServer:
                    return "Couldn't reach the local model server. Check that it is running and that the address in Settings is correct.";
                case ProviderErrorKind.Network: return $"Couldn't reach {name}. Check your internet connection.";
                case ProviderErrorKind.Timeout when Provider == ProviderID.LocalServer:
                    return "The local model server didn't respond in time. The model may still be loading; try again, or choose a smaller model.";
                case ProviderErrorKind.Timeout: return $"{name} didn't respond in time.";
                case ProviderErrorKind.PayloadTooLarge:
                    return LimitBytes is int limit ? $"The recording is too large for {name} (limit {limit / 1_000_000} MB)." : $"The request is too large for {name}.";
                case ProviderErrorKind.Server: return Status is long status ? $"{name} had a server problem (HTTP {status})." : $"{name} had a server problem.";
                case ProviderErrorKind.MalformedResponse or ProviderErrorKind.OutputTruncated: return $"{name} returned a response Murmur couldn't use.";
                case ProviderErrorKind.ContentBlocked: return $"{name} declined to process this dictation (content filter).";
                case ProviderErrorKind.Rejected: return $"{name} rejected the request (HTTP {Status}). Check the model setting.";
                case ProviderErrorKind.InvalidConfiguration when Provider == ProviderID.LocalServer:
                    return "The local model server isn't set up. Check the server address (http or https, no user name or password) and the model name in Settings.";
                case ProviderErrorKind.InvalidConfiguration when Provider == ProviderID.CustomAPI:
                    return "The custom API provider isn't set up. Check the API base URL, model name and key in Settings.";
                case ProviderErrorKind.InvalidConfiguration: return $"{name} isn't set up. Add an API key and model in Settings.";
                case ProviderErrorKind.ServerNotRunning: return $"No local model server is running at {Address}. Start it, or check the address in Settings.";
                case ProviderErrorKind.ModelUnavailable: return Unavailable!.Value.UserMessage();
                default: return "Cancelled.";
            }
        }
    }

    public override string ToString() => UserMessage;
}

/// <summary>Throws a <see cref="ProviderError"/>. The message is the content-free user message; no inner exception is ever attached.</summary>
public sealed class ProviderException(ProviderError error) : Exception(error.UserMessage)
{
    public ProviderError Error { get; } = error;
    public override string ToString() => $"ProviderException({Error.Kind}): {Message}";
}

/// <summary>Why an on-device model can't run.</summary>
public enum OnDeviceModelUnavailable { DeviceNotEligible, AppleIntelligenceOff, ModelNotReady, ModelNotDownloaded, ModelDamaged }

public static class OnDeviceModelUnavailableExtensions
{
    public static string UserMessage(this OnDeviceModelUnavailable reason) => reason switch
    {
        OnDeviceModelUnavailable.DeviceNotEligible => "This Mac can't run Apple Intelligence. Choose another cleanup route in Settings.",
        OnDeviceModelUnavailable.AppleIntelligenceOff => "Apple Intelligence is off. Turn it on in System Settings → Apple Intelligence & Siri, or choose another cleanup route.",
        OnDeviceModelUnavailable.ModelNotReady => "The Apple Intelligence model isn't ready yet; it may still be downloading. Try again later.",
        OnDeviceModelUnavailable.ModelDamaged => "The local cleanup model files are damaged. Remove the model in Settings and download it again, or choose another cleanup route.",
        _ => "The local cleanup model is not downloaded. Download it in Settings or choose another cleanup route.",
    };
}

/// <summary>Limits and deadlines shared by the provider adapters.</summary>
public static class ProviderLimits
{
    /// <summary>Bounds runaway output (a hallucinating model); five minutes of speech is about 1,500 tokens.</summary>
    public static readonly int MaxOutputTokens = ProvidersData.Value.ProviderLimits.MaxOutputTokens;
    /// <summary>The deadline in seconds for key validation and model-list requests.</summary>
    public static readonly double ValidationDeadline = ProvidersData.Value.ProviderLimits.ValidationDeadline;
}

/// <summary>shared/providers.json, exported by windows/shared/export_providers.swift.</summary>
internal sealed record ProvidersData(ProvidersData.AudioData AudioFormat, ProvidersData.LimitsData ProviderLimits, ProvidersData.OpenAIData OpenAI)
{
    internal sealed record AudioData(int SampleRate, float SilencePeak, double MinimumSpeechSeconds);
    internal sealed record LimitsData(int MaxOutputTokens, double ValidationDeadline);
    internal sealed record OpenAIData(double NoSpeechThreshold, double LogprobThreshold, int MinLoudWindows, int UploadLimitBytes, string OllamaDefault, double DefaultTimeout, double MaximumTimeout);
    public static readonly ProvidersData Value = SharedData.Load<ProvidersData>("providers");
}

/// <summary>How shared cleanup code must shape a request for one provider. A provider declares it; callers never test for a concrete type.</summary>
public sealed record CleanupShape
{
    /// <summary>False: a fixed prompt that takes no screen context or style notes (the managed local model).</summary>
    public bool TakesContext { get; init; } = true;
    /// <summary>Character caps on screen context, for a small context window; null sends it whole.</summary>
    public ContextCaps? ContextCaps { get; init; }
    /// <summary>Words per section when long text is cleaned in parallel, each guarded on its own; null sends the text whole.</summary>
    public int? SectionTarget { get; init; }
    /// <summary>With sections: a sentence opening with a correction cue stays with the one it repairs.</summary>
    public bool KeepsCorrectionsAttached { get; init; }
    /// <summary>False: rewrite presets are refused, and their instructions are not sent as style notes on the ordinary cleanup that follows.</summary>
    public bool TakesPresets { get; init; } = true;
}

public sealed record ContextCaps(int Selected, int Clipboard, int Window);

public interface ITranscriptionProvider
{
    ProviderID Id { get; }
    DataDisclosure Disclosure { get; }
    /// <summary>The model's identifier for History; null where there is none.</summary>
    string? ModelID => null;
    /// <summary>One request, no retry. Throws <see cref="ProviderException"/>; <see cref="ProviderError.Cancelled"/> if the token is cancelled.</summary>
    Task<string> TranscribeAsync(AudioPayload audio, CancellationToken cancellationToken = default);
    /// <summary>Minimal authenticated call that consumes no tokens. Throws <see cref="ProviderException"/>.</summary>
    Task ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>A transcriber that returns segment detail too; <see cref="ITranscriptionProvider.TranscribeAsync"/> stays its text.</summary>
public interface IDetailedTranscriber : ITranscriptionProvider
{
    Task<TranscriptionResult> TranscribeDetailedAsync(AudioPayload audio, CancellationToken cancellationToken = default);
}

public interface ICleanupProvider
{
    ProviderID Id { get; }
    DataDisclosure Disclosure { get; }
    /// <summary>The model's identifier for History, read after the call; null where there is none.</summary>
    string? ModelID => null;
    CleanupShape Shape => new();
    /// <summary>This provider with the longest wait it permits, for a whole meeting's notes.</summary>
    ICleanupProvider ForMeetingNotes() => this;
    /// <summary>One request, no retry. Returns the model text unchanged; the caller runs the bound/guard check.</summary>
    Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default);
    Task ValidateAsync(CancellationToken cancellationToken = default);
}
