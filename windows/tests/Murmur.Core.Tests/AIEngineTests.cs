// Ports the AI engine block of app/check_routes.swift (factoryChecks, 169-179). Windows has no Apple Intelligence, so the local routes have no engine.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class AIEngineTests
{
    [Fact]
    public void AvailabilityAndDefaults()
    {
        var r = new RouteSettings();
        Assert.NotNull(AIEngine.CustomProvider.UnavailableReason(r));
        r.CustomAPIBaseURL = "https://api.example.com/v1"; r.CustomAPIModel = "m";
        Assert.True(AIEngine.CustomProvider.UnavailableReason(r) is null && AIEngine.CustomProvider.Provider(r, _ => null) is OpenAICompatibleCleanupProvider, "Custom engine builds the existing provider");
        Assert.True(AIEngines.Derive(CleanupChoice.CustomAPI) == AIEngine.CustomProvider, "the custom route keeps its own engine");
        Assert.True(AIEngines.Derive(CleanupChoice.LocalRules) is null && AIEngines.Derive(CleanupChoice.LocalModel) is null, "local routes have no engine on Windows");
        Assert.True(AIEngine.AppleIntelligence.UnavailableReason(r) is not null && AIEngine.AppleIntelligence.Provider(r, _ => null) is null, "Apple Intelligence never runs on Windows");
        Assert.Equal(["apple-intelligence", "custom-provider"], Enum.GetValues<AIEngine>().Select(e => e.RawValue()));
    }
}
