using System.Reflection;
using System.Text.Json;

namespace Murmur.Core;

/// <summary>Reads the JSON files that windows/shared/export_*.swift writes from the Swift constants (embedded as <c>shared/NAME.json</c>).</summary>
public static class SharedData
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Deserializes <c>shared/{name}.json</c>; throws when the resource is missing.</summary>
    public static T Load<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"shared/{name}.json")
            ?? throw new FileNotFoundException($"Embedded shared data '{name}' not found.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException($"Shared data '{name}' is empty.");
    }
}
