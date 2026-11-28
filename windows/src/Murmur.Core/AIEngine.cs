// Port of app/AIEngine.swift.
// The engine behind Murmur's optional AI features that are not dictation cleanup: writing modes (and voice edit). No setting: the engine
// follows the cleanup route. Custom Provider keeps its own. The Mac's local routes use Apple Intelligence where it is
// available; Windows has no Apple Intelligence, so a local route has no engine and a writing mode then gets only its non-AI settings.
namespace Murmur.Core;

/// <summary>Raw values "apple-intelligence", "custom-provider".</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(KebabCaseEnumConverter<AIEngine>))]
public enum AIEngine { AppleIntelligence, CustomProvider }

public static class AIEngines
{
    public static string RawValue(this AIEngine engine) => engine switch
    {
        AIEngine.AppleIntelligence => "apple-intelligence",
        _ => "custom-provider",
    };

    /// <summary>Why this engine cannot run now, in one line, or null when it can. <paramref name="hasKey"/> only asks whether a key is stored.</summary>
    public static string? UnavailableReason(this AIEngine engine, RouteSettings routes, Func<ProviderID, bool> hasKey) => engine switch
    {
        AIEngine.AppleIntelligence => "Apple Intelligence is not available on Windows.",
        _ => Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, null, routes.CustomAPIBaseURL, routes.CustomAPIModel) is null
            ? "Set up a Custom Provider in Settings → Providers." : null,
    };

    /// <summary>The provider for this engine, or null when it cannot run. Reuses the existing providers; the caller keeps the guard.</summary>
    public static ICleanupProvider? Provider(this AIEngine engine, RouteSettings routes, Func<ProviderID, APIKey?> key)
    {
        if (engine.UnavailableReason(routes, id => key(id) is not null) is not null) return null;
        return engine switch
        {
            AIEngine.CustomProvider => Routes.MakeCleanupProvider(CleanupChoice.CustomAPI, key(ProviderID.CustomCleanup), routes.CustomAPIBaseURL, routes.CustomAPIModel, routes.CustomAPIReasoningEffort),
            _ => null,
        };
    }

    /// <summary>The engine for a cleanup route; null when the route has none (the local routes, on Windows).</summary>
    public static AIEngine? Derive(CleanupChoice cleanup) => cleanup switch
    {
        CleanupChoice.CustomAPI => AIEngine.CustomProvider,
        _ => null,
    };
}
