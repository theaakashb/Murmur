// Port of app/MurmurCloudEdit.swift.
using System.Text.Json;

namespace Murmur.Core;

public static class MurmurCloudEditFormat
{
    public static ProviderRequest MakeRequest(string text, string instruction, APIKey key)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, OpenAIChatFormat.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("instruction", instruction);
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }
        return new ProviderRequest { Method = "POST", Url = MurmurCloud.BaseURL.Appending("/v1/edit"), Body = buffer.ToArray() }
            .WithHeader("Authorization", $"Bearer {key.Secret}").WithHeader("Content-Type", "application/json").WithHeader("Accept", "application/json");
    }
}

public sealed record MurmurCloudEditProvider(APIKey ApiKey)
{
    public IHttpTransport Transport { get; init; } = HttpClientTransport.Default;

    public DataDisclosure Disclosure => new(ProviderID.MurmurCloud, ProcessingStage.Cleanup, MurmurCloud.Host,
        ["the selected text", "your spoken instruction"], ["audio", "history", "the dictionary"], "");

    public async Task<string> EditAsync(string text, string instruction, CancellationToken cancellationToken = default)
    {
        var request = MurmurCloudEditFormat.MakeRequest(text, instruction, ApiKey);
        var response = await new ProviderHTTP(ProviderID.MurmurCloud, Transport, MurmurCloud.CleanupDeadline)
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return MurmurCloudCleanupFormat.Parse(response);
    }
}
