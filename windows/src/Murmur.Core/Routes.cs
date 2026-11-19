// Port of app/Routes.swift, without its migrations (spec/settings.md: Windows starts fresh).
using System.Globalization;
using System.Text.Json;

namespace Murmur.Core;

// Processing routes: which transcriber and cleaner a dictation uses, what each one sends off this computer,
// and the async glue between the providers and the controller. The controller keeps the UI calls and only
// acts on these decisions.
//
// Rules enforced here:
// - No fallback. A cloud route that fails or is not configured never runs the local model or another
//   provider; the session ends in the Error phase with its audio or raw text kept.
// - Each stage that sends data off this computer is disclosed inline in Settings. A local model server on a
//   loopback address sends nothing off this computer and needs none.
// - Messages are built from fixed strings and ProviderError.UserMessage; they never contain a key, a
//   response body or transcript text.

public enum TranscriptionRoute
{
    /// <summary>Parakeet through the local speech helper. The default.</summary>
    Local,
    /// <summary>A custom OpenAI-compatible endpoint.</summary>
    Cloud,
    /// <summary>Murmur Cloud, with the Murmur Cloud key.</summary>
    MurmurCloud,
}

public enum CleanupChoice
{
    /// <summary>The built-in RuleCleaner. The default.</summary>
    LocalRules,
    /// <summary>The managed local model: downloaded on request, runs on this computer.</summary>
    LocalModel,
    CustomAPI,
    /// <summary>Murmur Cloud: the server builds the prompt; the guard stays here.</summary>
    MurmurCloud,
}

public static class RouteRawValues
{
    static readonly string[] Transcription = ["local", "cloud", "murmur-cloud"];
    static readonly string[] Cleanup = ["local-rules", "local-model", "custom-api", "murmur-cloud"];

    public static string RawValue(this TranscriptionRoute route) => Transcription[(int)route];
    public static string RawValue(this CleanupChoice choice) => Cleanup[(int)choice];
    public static string RawValue(this CleanupRoute route) => route == CleanupRoute.Local ? "local" : "cloud";
    public static TranscriptionRoute? TranscriptionRouteFrom(string? raw) => Array.IndexOf(Transcription, raw) is var i and >= 0 ? (TranscriptionRoute)i : null;
    public static CleanupChoice? CleanupChoiceFrom(string? raw) => Array.IndexOf(Cleanup, raw) is var i and >= 0 ? (CleanupChoice)i : null;
}

public static class CleanupChoiceExtensions
{
    public static string Id(this CleanupChoice choice) => choice.RawValue();

    /// <summary>Professional is cloud-only (ProviderID.RunsProfessional): Murmur Cloud and Custom Provider.</summary>
    public static bool CloudOnlyOK(this CleanupChoice choice) => choice is CleanupChoice.MurmurCloud or CleanupChoice.CustomAPI;

    public static string DisplayName(this CleanupChoice choice) => choice switch
    {
        CleanupChoice.LocalRules => "Local rules (this Mac)",
        CleanupChoice.LocalModel => "Local model (this Mac)",
        CleanupChoice.CustomAPI => "Custom Provider",
        _ => "Murmur Cloud",
    };

    /// <summary>The processing route: anything but the rule cleaner goes through a provider.</summary>
    public static CleanupRoute ProcessingRoute(this CleanupChoice choice) => choice == CleanupChoice.LocalRules ? CleanupRoute.Local : CleanupRoute.Cloud;

    public static ProviderID? ProviderID(this CleanupChoice choice) => choice switch
    {
        CleanupChoice.LocalRules => null,
        CleanupChoice.LocalModel => Core.ProviderID.LocalModel,
        CleanupChoice.CustomAPI => Core.ProviderID.CustomAPI,
        _ => Core.ProviderID.MurmurCloud,
    };

    public static ProviderID? CredentialID(this CleanupChoice choice) => choice == CleanupChoice.CustomAPI ? Core.ProviderID.CustomCleanup : choice.ProviderID();
}

/// <summary>Swift's top-level provider factories in Routes.swift.</summary>
public static class Routes
{
    /// <summary>The one place a cloud transcriber is constructed: a user-configured OpenAI-compatible audio transcription endpoint.
    /// Custom servers may not require a key. Null for a missing model or an invalid address.</summary>
    public static ITranscriptionProvider? MakeTranscriptionProvider(APIKey? key, string apiBaseURL, string model)
    {
        var name = SwiftText.TrimWhitespaceAndNewlines(model);
        if (name.Length == 0) return null;
        try { return new OpenAICompatibleTranscriptionProvider(apiBaseURL, name, key); }
        catch (ProviderException) { return null; }
    }

    /// <summary>The one place a cleanup provider is constructed. Null for local rules or invalid custom settings.</summary>
    public static ICleanupProvider? MakeCleanupProvider(CleanupChoice choice, APIKey? key, string apiBaseURL, string model, string reasoningEffort = "")
    {
        switch (choice)
        {
            case CleanupChoice.LocalRules: return null;
            case CleanupChoice.LocalModel: return new LocalModelCleanupProvider();
            case CleanupChoice.CustomAPI:
                var name = SwiftText.TrimWhitespaceAndNewlines(model);
                if (name.Length == 0) return null;
                var effort = SwiftText.TrimWhitespaceAndNewlines(reasoningEffort);
                try { return new OpenAICompatibleCleanupProvider(apiBaseURL, name, key, Core.ProviderID.CustomAPI) { ReasoningEffort = effort.Length == 0 ? null : effort }; }
                catch (ProviderException) { return null; }
            default: return key is null ? null : new MurmurCloudCleanupProvider(key);
        }
    }
}

/// <summary>shared/routes.json, exported by windows/shared/export_routes.swift.</summary>
internal sealed record RoutesData(RoutesData.KeysData Keys, string PlaceholderAPIURL, string PlaceholderModel, string DefaultReasoningEffort,
                                  string KeyBindingPreference, string UnreadableAudio, string UnknownFailure, RoutesData.KeyTextData KeyText)
{
    internal sealed record KeysData(string Transcription, string LegacyCloudTranscriber, string Cleanup, string CustomAPIBaseURL, string CustomTranscriptionAPIBaseURL,
                                    string CustomAPIModel, string CustomTranscriptionModel, string CustomAPIReasoningEffort, string LegacyServerURL, string LegacyServerModel);
    internal sealed record KeyTextData(string Saved, string None, string Invalid, string StoreFailed, string RemoveFailed, string Removed, string Checking,
                                       string Missing, string Unreadable, IReadOnlyList<string> Providers);
    public static readonly RoutesData Value = SharedData.Load<RoutesData>("routes");
}

/// <summary>Route settings persisted in the settings store. Keys are never stored here (they live in the credential store).</summary>
public sealed record RouteSettings
{
    /// <summary>Shown in the empty address and model fields. A custom provider has no default: nothing is sent until the user enters their own.</summary>
    public static readonly string PlaceholderAPIURL = RoutesData.Value.PlaceholderAPIURL;
    public static readonly string PlaceholderModel = RoutesData.Value.PlaceholderModel;

    public TranscriptionRoute Transcription { get; set; } = TranscriptionRoute.Local;
    public CleanupChoice Cleanup { get; set; } = CleanupChoice.LocalRules;
    public string CustomAPIBaseURL { get; set; } = "";
    public string CustomTranscriptionAPIBaseURL { get; set; } = "";
    public string CustomAPIModel { get; set; } = "";
    public string CustomTranscriptionModel { get; set; } = "";
    /// <summary>Sent to the custom cleanup provider as <c>reasoning_effort</c>; empty sends none.</summary>
    public string CustomAPIReasoningEffort { get; set; } = RoutesData.Value.DefaultReasoningEffort;

    /// <summary>The endpoint setting a custom provider's key is bound to (customTranscription follows its own address, customCleanup the
    /// cleanup one); null for providers whose key has no destination. Changing it requires saving the key again (<see cref="CustomAPIKeyBinding"/>).</summary>
    public string? ApiBaseURL(ProviderID provider) => provider switch
    {
        ProviderID.CustomTranscription => CustomTranscriptionAPIBaseURL,
        ProviderID.CustomCleanup => CustomAPIBaseURL,
        _ => null,
    };

    /// <summary>Settings key names. The legacy names are never read on Windows, but stay so the backup's destination-settings list
    /// matches the Mac's (DataBackup.destinationSettings).</summary>
    public static class Key
    {
        static readonly RoutesData.KeysData K = RoutesData.Value.Keys;
        public static readonly string Transcription = K.Transcription, LegacyCloudTranscriber = K.LegacyCloudTranscriber, Cleanup = K.Cleanup,
            CustomAPIBaseURL = K.CustomAPIBaseURL, CustomTranscriptionAPIBaseURL = K.CustomTranscriptionAPIBaseURL, CustomAPIModel = K.CustomAPIModel,
            CustomTranscriptionModel = K.CustomTranscriptionModel, CustomAPIReasoningEffort = K.CustomAPIReasoningEffort,
            LegacyServerURL = K.LegacyServerURL, LegacyServerModel = K.LegacyServerModel;
    }

    /// <summary>Missing, wrongly typed or unknown values fall back to the approved local defaults. No legacy values are migrated.</summary>
    public static RouteSettings Load(ISettingsStore defaults)
    {
        string? S(string key) => defaults.Get(key) as string;
        var settings = new RouteSettings
        {
            Transcription = RouteRawValues.TranscriptionRouteFrom(S(Key.Transcription)) ?? TranscriptionRoute.Local,
            CustomTranscriptionModel = S(Key.CustomTranscriptionModel) ?? "",
            Cleanup = RouteRawValues.CleanupChoiceFrom(S(Key.Cleanup)) ?? CleanupChoice.LocalRules,
            CustomAPIBaseURL = S(Key.CustomAPIBaseURL) ?? "",
            CustomAPIModel = S(Key.CustomAPIModel) ?? "",
            CustomAPIReasoningEffort = S(Key.CustomAPIReasoningEffort) ?? RoutesData.Value.DefaultReasoningEffort,
        };
        settings.CustomTranscriptionAPIBaseURL = S(Key.CustomTranscriptionAPIBaseURL) ?? settings.CustomAPIBaseURL;
        return settings;
    }

    public void Save(ISettingsStore defaults)
    {
        defaults.Set(Key.Transcription, Transcription.RawValue());
        defaults.Set(Key.Cleanup, Cleanup.RawValue());
        defaults.Set(Key.CustomAPIBaseURL, CustomAPIBaseURL);
        defaults.Set(Key.CustomTranscriptionAPIBaseURL, CustomTranscriptionAPIBaseURL);
        defaults.Set(Key.CustomAPIModel, CustomAPIModel);
        defaults.Set(Key.CustomTranscriptionModel, CustomTranscriptionModel);
        defaults.Set(Key.CustomAPIReasoningEffort, CustomAPIReasoningEffort);
    }
}

/// <summary>Only the endpoint identifier lives in preferences; the secret stays in the credential store.</summary>
public static class CustomAPIKeyBinding
{
    public static readonly string Preference = RoutesData.Value.KeyBindingPreference;
    public static string PreferenceFor(ProviderID provider) => provider == ProviderID.CustomAPI ? Preference : $"{Preference}.{provider.RawValue()}";

    /// <summary>The normalized destination a key is bound to: the base URL without the scheme's default port. Throws <see cref="ProviderException"/>.</summary>
    public static string Endpoint(string raw)
    {
        var url = LocalEndpoint.Normalize(raw);
        return (url.Port == (url.Scheme == "https" ? 443 : 80) ? url with { Port = null } : url).ToString();
    }

    public static void Save(APIKey key, string address, ISettingsStore defaults, ICredentialStore credentials, ProviderID provider = ProviderID.CustomAPI)
    {
        var destination = Endpoint(address);
        // Fail closed if replacing a key fails partway through: never keep the old binding.
        defaults.Remove(PreferenceFor(provider));
        credentials.Save(key, provider);
        defaults.Set(PreferenceFor(provider), destination);
    }

    /// <summary>The key, only if it was saved for this address. Throws <see cref="CredentialException"/> (destinationMismatch) otherwise.</summary>
    public static APIKey? Read(string address, ISettingsStore defaults, ICredentialStore credentials, ProviderID provider = ProviderID.CustomAPI)
    {
        if (!credentials.Exists(provider)) return null;
        if (defaults.Get(PreferenceFor(provider)) as string != Endpoint(address)) throw new CredentialException(CredentialError.DestinationMismatch);
        return credentials.Read(provider);
    }

    /// <summary>The key for any provider: a custom provider's only through its bound endpoint, the others straight from the store.</summary>
    public static APIKey? ReadKey(ProviderID provider, RouteSettings routes, ISettingsStore defaults, ICredentialStore credentials) =>
        routes.ApiBaseURL(provider) is string address ? Read(address, defaults, credentials, provider) : credentials.Read(provider);

    /// <summary>True when the saved key is bound to a different endpoint than <paramref name="address"/>, so it must be saved again.</summary>
    public static bool NeedsConfirmation(string address, ISettingsStore defaults, ProviderID provider = ProviderID.CustomAPI)
    {
        string destination;
        try { destination = Endpoint(address); } catch (ProviderException) { return false; }
        return defaults.Get(PreferenceFor(provider)) as string != destination;
    }

    /// <summary>Forgets the endpoint a key was bound to (the key itself is deleted through the store).</summary>
    public static void Clear(ISettingsStore defaults, ProviderID provider = ProviderID.CustomAPI) => defaults.Remove(PreferenceFor(provider));
}

/// <summary>What a dictation would use with these settings and keys, decided before recording starts.</summary>
public sealed record RoutePlan(ITranscriptionProvider? Transcriber, ICleanupProvider? Cleaner, string? Refusal, IReadOnlyList<DataDisclosure> Stages)
{
    /// <summary>Null transcriber means local transcription (Parakeet).</summary>
    public bool UsesLocalModel => Transcriber is null && Refusal is null;

    /// <summary>Reads only the disclosure of a provider; this placeholder is never sent anywhere.</summary>
    static readonly APIKey PlaceholderKey = APIKey.Create("disclosure-only")!;

    /// <summary><paramref name="cleanupSendsText"/> is false when every cleanup option is off: the provider is not called
    /// then, so nothing is sent and no key is needed.</summary>
    public static RoutePlan Make(RouteSettings settings, bool cleanupSendsText, Func<ProviderID, APIKey?> key)
    {
        var stages = new List<DataDisclosure>();
        string? refusal = null;
        ITranscriptionProvider? transcriber = null;
        switch (settings.Transcription)
        {
            case TranscriptionRoute.Cloud:
                transcriber = Routes.MakeTranscriptionProvider(key(ProviderID.CustomTranscription), settings.CustomTranscriptionAPIBaseURL, settings.CustomTranscriptionModel);
                if (transcriber is null) refusal = ProviderError.InvalidConfiguration(ProviderID.CustomAPI).UserMessage;
                if (transcriber?.Disclosure is { StaysOnThisMac: false } disclosure) stages.Add(disclosure);
                break;
            case TranscriptionRoute.MurmurCloud:
                transcriber = key(ProviderID.MurmurCloud) is APIKey cloudKey ? new MurmurCloudTranscriptionProvider(cloudKey) : null;
                if (transcriber is null) refusal = ProviderError.InvalidConfiguration(ProviderID.MurmurCloud).UserMessage;
                // Shown before a key is saved too, so Settings discloses the route while it is set up.
                stages.Add(new MurmurCloudTranscriptionProvider(PlaceholderKey).Disclosure);
                break;
        }
        ICleanupProvider? cleaner = null;
        if (settings.Cleanup != CleanupChoice.LocalRules && cleanupSendsText && settings.Cleanup.ProviderID() is ProviderID provider)
        {
            var storedKey = settings.Cleanup == CleanupChoice.LocalModel ? null : key(settings.Cleanup.CredentialID() ?? provider);
            cleaner = Routes.MakeCleanupProvider(settings.Cleanup, storedKey, settings.CustomAPIBaseURL, settings.CustomAPIModel, settings.CustomAPIReasoningEffort);
            if (cleaner is null && refusal is null) refusal = ProviderError.InvalidConfiguration(provider).UserMessage;
            var preview = cleaner ?? Routes.MakeCleanupProvider(settings.Cleanup, storedKey ?? PlaceholderKey, settings.CustomAPIBaseURL, settings.CustomAPIModel);
            if (preview?.Disclosure is { StaysOnThisMac: false } disclosure) stages.Add(disclosure);
        }
        return new RoutePlan(transcriber, cleaner, refusal, stages);
    }
}

public static class DisclosureText
{
    /// <summary>One-line description for Settings, shown before any cloud use.</summary>
    public static string? Summary(RoutePlan plan, ProcessingStage stage) => plan.Stages.FirstOrDefault(s => s.Stage == stage)?.Summary;
}

/// <summary>A transcriber that can tell from the recording's level that it should not be called.</summary>
public interface ISilencePrecheckedTranscriber : ITranscriptionProvider
{
    bool ShouldSkip(float peakLevel, double? durationSeconds);
}

public abstract record CloudTranscriptionOutcome
{
    CloudTranscriptionOutcome() { }
    public sealed record Transcript(string Text) : CloudTranscriptionOutcome;
    /// <summary>Silence was skipped before sending (<c>Skipped</c>), or the provider heard nothing.</summary>
    public sealed record NoSpeech(bool Skipped) : CloudTranscriptionOutcome;
    /// <summary><c>Message</c> is safe to show; <c>KeepAudio</c> follows the provider error's disposition.</summary>
    public sealed record Failed(string Message, bool KeepAudio) : CloudTranscriptionOutcome;
    public sealed record Cancelled : CloudTranscriptionOutcome;
}

/// <summary>What the pre-check and transcriber saw, kept in the History entry (never logged).</summary>
public sealed record CloudTranscriptionDetail
{
    public double? ClipSeconds { get; set; }
    public float? PeakP50 { get; set; }
    public float? PeakP90 { get; set; }
    /// <summary>The pre-check's level (second-highest 100 ms peak); null when the audio was not decoded.</summary>
    public float? SilencePeak { get; set; }
    /// <summary><c>Unchecked</c> when the provider has no pre-check or no level.</summary>
    public Precheck Precheck { get; set; } = Precheck.Unchecked;
    public IReadOnlyList<TranscriptSegment>? Segments { get; set; }
    /// <summary>Why the outcome was no speech.</summary>
    public NoSpeechReason? NoSpeechReason { get; set; }

    /// <summary>Content-free line for the log: reason, clip loudness and pre-check level, segment scores. Never text.</summary>
    public string NoSpeechLogLine
    {
        get
        {
            static string F(float? value) => value is float v ? ((double)v).ToString("F4", CultureInfo.InvariantCulture) : "-";
            var scores = (Segments ?? []).Select(s => s.AvgLogprob).OfType<double>().ToList();
            var range = scores.Count == 0 ? "-" : $"{scores.Min().ToString("F2", CultureInfo.InvariantCulture)}/{scores.Max().ToString("F2", CultureInfo.InvariantCulture)}";
            var reason = NoSpeechReason is { } r ? JsonNamingPolicy.CamelCase.ConvertName(r.ToString()) : "unknown";
            return $"no-speech reason={reason} clip={(ClipSeconds is double c ? c.ToString("F2", CultureInfo.InvariantCulture) : "-")} p50={F(PeakP50)} p90={F(PeakP90)} "
                + $"silence={F(SilencePeak)}/{F(SilencePeak is null ? null : AudioFormat.SilencePeak)} segments={Segments?.Count ?? 0} logprob-min-max={range}";
        }
    }
}

public static class CloudTranscription
{
    public static readonly string UnreadableAudio = RoutesData.Value.UnreadableAudio;
    public static readonly string UnknownFailure = RoutesData.Value.UnknownFailure;

    /// <summary>Reads the recording and runs <see cref="RunAsync(ITranscriptionProvider, byte[], CancellationToken)"/>.</summary>
    public static async Task<CloudTranscriptionOutcome> RunAsync(ITranscriptionProvider provider, string audioFile, CancellationToken cancellationToken = default) =>
        (await RunDetailedAsync(provider, audioFile, cancellationToken).ConfigureAwait(false)).Outcome;

    public static async Task<CloudTranscriptionOutcome> RunAsync(ITranscriptionProvider provider, byte[] audioData, CancellationToken cancellationToken = default) =>
        (await RunDetailedAsync(provider, audioData, cancellationToken).ConfigureAwait(false)).Outcome;

    public static async Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> RunDetailedAsync(ITranscriptionProvider provider, string audioFile, CancellationToken cancellationToken = default)
    {
        byte[] data;
        try { data = await File.ReadAllBytesAsync(audioFile, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return (new CloudTranscriptionOutcome.Failed(UnreadableAudio, true), new CloudTranscriptionDetail());
        }
        return await RunDetailedAsync(provider, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One provider call at most: silence pre-check, then one request, no retry, no fallback.
    /// No dictionary-derived prompt is ever attached (VOCABULARY.md).
    /// The detail (clip loudness, pre-check, segments) is for History; it never changes the outcome.</summary>
    public static async Task<(CloudTranscriptionOutcome Outcome, CloudTranscriptionDetail Detail)> RunDetailedAsync(ITranscriptionProvider provider, byte[] audioData, CancellationToken cancellationToken = default)
    {
        var prepared = CloudAudio.Prepare(audioData);
        var detail = new CloudTranscriptionDetail { ClipSeconds = prepared.Duration, SilencePeak = prepared.Peak };
        if (prepared.Payload.Levels is { } levels && OpenAITranscriptionFormat.PeakPercentiles(levels) is var (p50, p90)) { detail.PeakP50 = p50; detail.PeakP90 = p90; }
        if (provider is ISilencePrecheckedTranscriber checker && prepared.Peak is float peak)
        {
            if (checker.ShouldSkip(peak, prepared.Duration))
            {
                detail.Precheck = Precheck.Skipped; detail.NoSpeechReason = NoSpeechReason.Precheck;
                return (new CloudTranscriptionOutcome.NoSpeech(true), detail);
            }
            detail.Precheck = Precheck.Passed;
        }
        if (cancellationToken.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
        try
        {
            string text;
            if (provider is IDetailedTranscriber detailed)
            {
                var result = await detailed.TranscribeDetailedAsync(prepared.Payload, cancellationToken).ConfigureAwait(false);
                text = result.Text; detail.Segments = result.Segments; detail.NoSpeechReason = result.NoSpeechReason;
            }
            else text = await provider.TranscribeAsync(prepared.Payload, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
            // A transcript of only punctuation (" .") is silence too: nothing is pasted.
            if (!SwiftText.Characters(text).Any(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)))
            {
                detail.NoSpeechReason ??= text.Length > 0 ? Core.NoSpeechReason.PunctuationOnly : Core.NoSpeechReason.Empty;
                return (new CloudTranscriptionOutcome.NoSpeech(false), detail);
            }
            return (new CloudTranscriptionOutcome.Transcript(text), detail);
        }
        catch (ProviderException error)
        {
            if (error.Error == ProviderError.Cancelled || cancellationToken.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
            return (new CloudTranscriptionOutcome.Failed(error.Error.UserMessage, error.Error.Disposition != SessionDisposition.Discard), detail);
        }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested) return (new CloudTranscriptionOutcome.Cancelled(), detail);
            return (new CloudTranscriptionOutcome.Failed(UnknownFailure, true), detail);
        }
    }

    /// <summary>Text for the Error phase.</summary>
    public static string? Message(CloudTranscriptionOutcome outcome) => outcome switch
    {
        CloudTranscriptionOutcome.Transcript => null,
        CloudTranscriptionOutcome.NoSpeech => "No speech was detected. Check your microphone and try again.",
        CloudTranscriptionOutcome.Failed f => f.KeepAudio ? f.Message + $" Audio is saved for retry for up to {RetryWindow.Text}." : f.Message,
        _ => "Canceled. The recording was deleted.",
    };
}

/// <summary>Runs one async request at a time for the controller. <see cref="Cancel"/> cancels the token (which cancels the HTTP request)
/// and guarantees <c>deliver</c> is never called for it afterwards. Delivery is posted to the SynchronizationContext current at
/// <see cref="Start"/> (the UI thread); call Start and Cancel from that thread, as Swift's main actor requires.</summary>
public sealed class CloudRequest
{
    readonly Lock gate = new();
    CancellationTokenSource? source;
    object token = new();

    public bool IsActive { get { lock (gate) return source is not null; } }

    public void Start<T>(Func<CancellationToken, Task<T>> work, Action<T> deliver)
    {
        CancellationTokenSource current;
        object ticket;
        lock (gate)
        {
            CancelLocked();
            current = source = new CancellationTokenSource();
            ticket = token = new object();
        }
        var context = SynchronizationContext.Current;
        void Deliver(T value)
        {
            lock (gate)
            {
                if (!ReferenceEquals(token, ticket) || current.IsCancellationRequested) return;
                source = null;
            }
            deliver(value);
        }
        async Task Run()
        {
            T value;
            try { value = await work(current.Token); }
            catch (OperationCanceledException) { return; }
            if (context is null) Deliver(value); else context.Post(_ => Deliver(value), null);
        }
        // Never runs the work inline: like a Swift Task it starts after Start returns, so on the UI thread a Cancel right after Start always wins.
        if (context is null) _ = Task.Run(Run); else context.Post(_ => _ = Run(), null);
    }

    public void Cancel() { lock (gate) CancelLocked(); }

    void CancelLocked()
    {
        token = new object();
        source?.Cancel();
        source = null;
    }
}
