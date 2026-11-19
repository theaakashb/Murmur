// Port of app/AIEngine.swift.
// The engine behind Murmur's optional AI features that are not dictation cleanup: writing modes (and voice edit). No setting: the engine
// follows the cleanup route. Murmur Cloud and Custom Provider keep their own. The Mac's local routes use Apple Intelligence where it is
// available; Windows has no Apple Intelligence, so a local route has no engine and a writing mode then gets only its non-AI settings.
namespace Murmur.Core;

/// <summary>Raw values "apple-intelligence", "custom-provider", "murmur-cloud".</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(KebabCaseEnumConverter<AIEngine>))]
public enum AIEngine { AppleIntelligence, CustomProvider, MurmurCloud }

public static class AIEngines
{
    public static string RawValue(this AIEngine engine) => engine switch
    {
        AIEngine.AppleIntelligence => "apple-intelligence",
        AIEngine.CustomProvider => "custom-provider",
        _ => "murmur-cloud",
    };

    /// <summary>Why this engine cannot run now, in one line, or null when it can. <paramref name="hasKey"/> only asks whether a key is stored.</summary>
    public static string? UnavailableReason(this AIEngine engine, RouteSettings routes, Func<ProviderID, bool> hasKey) => engine switch
    {
        AIEngine.AppleIntelligence => "Apple Intelligence is not available on Windows.",
        AIEngine.CustomProvider => Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, routes.CustomAPIBaseURL, routes.CustomAPIModel) is null
            ? "Set up a Custom Provider in Settings → Providers." : null,
        _ => hasKey(ProviderID.MurmurCloud) ? null : "Add your Murmur Cloud key in Settings → Providers.",
    };

    /// <summary>The provider for this engine, or null when it cannot run. Reuses the existing providers; the caller keeps the guard.</summary>
    public static ICleanupProvider? Provider(this AIEngine engine, RouteSettings routes, Func<ProviderID, APIKey?> key)
    {
        if (engine.UnavailableReason(routes, id => key(id) is not null) is not null) return null;
        return engine switch
        {
            AIEngine.CustomProvider => Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, key(ProviderID.CustomCleanup), routes.CustomAPIBaseURL, routes.CustomAPIModel, routes.CustomAPIReasoningEffort),
            AIEngine.MurmurCloud => key(ProviderID.MurmurCloud) is { } k ? new MurmurCloudCleanupProvider(k) : null,
            _ => null,
        };
    }

    /// <summary>The engine for a cleanup route; null when the route has none (the local routes, on Windows).</summary>
    public static AIEngine? Derive(CleanupChoice cleanup) => cleanup switch
    {
        CleanupChoice.MurmurCloud => AIEngine.MurmurCloud,
        CleanupChoice.CustomAPI => AIEngine.CustomProvider,
        _ => null,
    };
}
