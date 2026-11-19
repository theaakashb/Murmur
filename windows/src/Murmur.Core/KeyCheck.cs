// Port of app/KeyCheck.swift.
namespace Murmur.Core;

// API key UI strings and key validation.

/// <summary>Key UI strings (shared/routes.json, exported from KeyText).</summary>
public static class KeyText
{
    static readonly RoutesData.KeyTextData T = RoutesData.Value.KeyText;
    public static readonly string Saved = T.Saved, None = T.None, Invalid = T.Invalid, StoreFailed = T.StoreFailed, RemoveFailed = T.RemoveFailed,
                                  Removed = T.Removed, Checking = T.Checking, Missing = T.Missing, Unreadable = T.Unreadable;

    /// <summary>Only active provider credentials appear in Settings.</summary>
    public static readonly IReadOnlyList<ProviderID> Providers = [.. T.Providers.Select(raw => ProviderIDExtensions.FromRaw(raw) ?? throw new InvalidDataException("Unknown provider id in shared/routes.json."))];

    public static string Validated(ProviderID provider) =>
        provider == ProviderID.LocalServer ? "The local model server answered."
        : provider is ProviderID.CustomTranscription or ProviderID.CustomCleanup or ProviderID.CustomAPI ? "The custom API endpoint answered."
        : $"{provider.DisplayName()} accepted the key.";
}

/// <summary>Swift's <c>Result&lt;[String], ProviderError&gt;</c> for model discovery: exactly one of the two is set.</summary>
public sealed record DiscoveryResult(IReadOnlyList<string>? Models, ProviderError? Error);

public static class KeyCheck
{
    /// <summary>One validation request (no tokens used). Returns a message with no key or response body.</summary>
    public static async Task<string> RunAsync(ProviderID provider, APIKey? key, string apiBaseURL, IHttpTransport? transport = null, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (provider)
            {
                case ProviderID.CustomAPI or ProviderID.CustomTranscription or ProviderID.CustomCleanup:
                    await (new OpenAICompatibleCleanupProvider(apiBaseURL, "validation", key, ProviderID.CustomAPI) with { Transport = transport ?? HttpClientTransport.Default })
                        .ValidateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ProviderID.LocalServer:
                    await (new OpenAICompatibleCleanupProvider(apiBaseURL, "validation", key) with { Transport = transport ?? HttpClientTransport.Default })
                        .ValidateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ProviderID.AppleIntelligence:
                    // Windows has no Apple Intelligence (AppleCleanup.swift is not ported).
                    return OnDeviceModelUnavailable.DeviceNotEligible.UserMessage();
                case ProviderID.LocalModel:
                    return new LocalModelCleanupProvider().Status()?.UserMessage() ?? "The local cleanup model is ready.";
                case ProviderID.MurmurCloud:
                    if (key is null) return ProviderError.InvalidConfiguration(provider).UserMessage;
                    await MurmurCloud.ValidateAsync(key, transport ?? HttpClientTransport.Default, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    // Legacy key accounts only (Cerebras, OpenRouter): no client exists for them.
                    return ProviderError.InvalidConfiguration(provider).UserMessage;
            }
            return KeyText.Validated(provider);
        }
        catch (ProviderException error) { return error.Error.UserMessage; }
        catch (Exception) { return ProviderError.Network(provider).UserMessage; }
    }

    /// <summary>Lists a local server's models. Sends no transcript.</summary>
    public static async Task<DiscoveryResult> DiscoverModelsAsync(string serverURL, APIKey? key, IHttpTransport? transport = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var p = new OpenAICompatibleCleanupProvider(serverURL, "discovery", key) with { Transport = transport ?? HttpClientTransport.Default };
            return new DiscoveryResult(await p.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (ProviderException error) { return new DiscoveryResult(null, error.Error); }
        catch (Exception) { return new DiscoveryResult(null, ProviderError.Network(ProviderID.LocalServer)); }
    }

    public static string DiscoveryMessage(DiscoveryResult result) => result switch
    {
        { Models: { Count: 0 } } => "The server answered but lists no models. Load or pull a model, then try again.",
        { Models: { } models } => $"Found {models.Count} model{(models.Count == 1 ? "" : "s")}.",
        _ => result.Error!.UserMessage,
    };
}
