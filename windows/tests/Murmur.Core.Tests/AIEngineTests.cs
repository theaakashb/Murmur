// Ports the AI engine block of app/check_routes.swift (factoryChecks, 169-179). Windows has no Apple Intelligence, so the local routes have no engine.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class AIEngineTests
{
    [Fact]
    public void AvailabilityAndDefaults()
    {
        var r = new RouteSettings();
        Assert.True(AIEngine.MurmurCloud.UnavailableReason(r, _ => false) is not null && AIEngine.MurmurCloud.UnavailableReason(r, id => id == ProviderID.MurmurCloud) is null, "Murmur Cloud engine needs a key");
        Assert.NotNull(AIEngine.CustomProvider.UnavailableReason(r, _ => false));
        r.CustomAPIBaseURL = "https://api.example.com/v1"; r.CustomAPIModel = "m";
        Assert.True(AIEngine.CustomProvider.UnavailableReason(r, _ => false) is null && AIEngine.CustomProvider.Provider(r, _ => null) is OpenAICompatibleCleanupProvider, "Custom engine builds the existing provider");
        Assert.Null(AIEngine.MurmurCloud.Provider(r, _ => null));
        Assert.IsType<MurmurCloudCleanupProvider>(AIEngine.MurmurCloud.Provider(r, id => id == ProviderID.MurmurCloud ? APIKey.Create("engine-key") : null));
        Assert.True(AIEngines.Derive(CleanupChoice.MurmurCloud) == AIEngine.MurmurCloud && AIEngines.Derive(CleanupChoice.CustomAPI) == AIEngine.CustomProvider, "cloud and custom routes keep their own engine");
        Assert.True(AIEngines.Derive(CleanupChoice.LocalRules) is null && AIEngines.Derive(CleanupChoice.LocalModel) is null, "local routes have no engine on Windows");
        Assert.True(AIEngine.AppleIntelligence.UnavailableReason(r, _ => true) is not null && AIEngine.AppleIntelligence.Provider(r, _ => null) is null, "Apple Intelligence never runs on Windows");
        Assert.Equal(["apple-intelligence", "custom-provider", "murmur-cloud"], Enum.GetValues<AIEngine>().Select(e => e.RawValue()));
    }
}
