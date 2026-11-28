// Port of app/Credentials.swift (without KeychainCredentialStore: the Windows Credential Manager store lives in Murmur.Platform).
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace Murmur.Core;

/// <summary>Providers that can hold a user-supplied API key. The raw value (<see cref="ProviderIDExtensions.RawValue"/>) is the credential account name.
/// <c>CustomAPI</c> uses the OpenAI-compatible chat completions format. <c>Openrouter</c> and <c>Cerebras</c> remain only because the Mac
/// migrates their keys; no client for them exists. <c>LocalServer</c> names a user-run local endpoint's errors and checks.
/// Declared in Swift's order (allCases).</summary>
public enum ProviderID
{
    Openrouter,
    Cerebras,
    CustomAPI,
    CustomTranscription,
    CustomCleanup,
    LocalServer,
    /// <summary>Apple's on-device system model. It has no key; listed so it can name errors.</summary>
    AppleIntelligence,
    /// <summary>The managed local cleanup model. No key; nothing leaves the computer.</summary>
    LocalModel,
}

public static class ProviderIDExtensions
{
    static readonly string[] Raw = ["openrouter", "cerebras", "customAPI", "customTranscription", "customCleanup", "localServer", "appleIntelligence", "local-model"];

    public static string RawValue(this ProviderID id) => Raw[(int)id];

    public static ProviderID? FromRaw(string? raw) => Array.IndexOf(Raw, raw) is var i and >= 0 ? (ProviderID)i : null;

    public static string DisplayName(this ProviderID id) => id switch
    {
        ProviderID.Openrouter => "OpenRouter",
        ProviderID.Cerebras => "Cerebras",
        ProviderID.CustomAPI => "Custom Provider",
        ProviderID.CustomTranscription => "Custom transcription",
        ProviderID.CustomCleanup => "Custom cleanup",
        ProviderID.LocalServer => "Local model server",
        ProviderID.AppleIntelligence => "Apple Intelligence",
        _ => "Local model",
    };

    /// <summary>Professional is cloud-only: neither the Local model nor Apple Intelligence runs it.</summary>
    public static bool RunsProfessional(this ProviderID id) => id != ProviderID.AppleIntelligence && (id != ProviderID.LocalModel || LocalCleanupModels.Current.RunsProfessional);
}

/// <summary>An API key. Every textual representation is redacted, so printing, interpolating or serializing a key
/// (or a record holding one) never reveals it.</summary>
[DebuggerDisplay("APIKey(redacted)")]
public sealed class APIKey : IEquatable<APIKey>
{
    /// <summary>The secret. Read it only to build an authentication header.</summary>
    [JsonIgnore] public string Secret { get; }

    APIKey(string secret) { Secret = secret; }

    /// <summary>Trims surrounding whitespace. Returns null for empty keys, keys over 4096 bytes, or keys containing whitespace,
    /// control or non-ASCII characters, which cannot be valid in an HTTP header.</summary>
    public static APIKey? Create(string raw)
    {
        var trimmed = SwiftText.TrimWhitespaceAndNewlines(raw);
        if (trimmed.Length == 0 || Encoding.UTF8.GetByteCount(trimmed) > 4096) return null;
        // ASCII without whitespace or controls is exactly 0x21...0x7E.
        foreach (var c in trimmed) if (c < 0x21 || c > 0x7E) return null;
        return new APIKey(trimmed);
    }

    public bool Equals(APIKey? other) => other is not null && Secret == other.Secret;
    public override bool Equals(object? obj) => obj is APIKey k && Equals(k);
    public override int GetHashCode() => Secret.GetHashCode();
    public override string ToString() => "APIKey(redacted)";
}

public enum CredentialErrorKind { Keychain, UnreadableItem, DestinationMismatch }

/// <summary>Errors carry only status codes, never key material. <c>Keychain</c> is the platform store's failure (Credential Manager on Windows).</summary>
public sealed record CredentialError(CredentialErrorKind Kind, int Status = 0)
{
    public static CredentialError Keychain(int status) => new(CredentialErrorKind.Keychain, status);
    public static readonly CredentialError UnreadableItem = new(CredentialErrorKind.UnreadableItem);
    public static readonly CredentialError DestinationMismatch = new(CredentialErrorKind.DestinationMismatch);

    public string Description => Kind switch
    {
        CredentialErrorKind.Keychain => $"Keychain operation failed (status {Status}).",
        CredentialErrorKind.UnreadableItem => "The stored API key could not be read.",
        _ => "Save the API key again for this server address. The existing key is for another or unconfirmed destination.",
    };

    public override string ToString() => Description;
}

public sealed class CredentialException(CredentialError error) : Exception(error.Description)
{
    public CredentialError Error { get; } = error;
}

/// <summary>Storage for provider API keys. Implementations must never print or log keys. Failures throw <see cref="CredentialException"/>.</summary>
public interface ICredentialStore
{
    APIKey? Read(ProviderID provider);
    /// <summary>Adds the key, or replaces an existing key for the same provider.</summary>
    void Save(APIKey key, ProviderID provider);
    /// <summary>Deleting a missing key succeeds.</summary>
    void Delete(ProviderID provider);
    /// <summary>Removes every key this app created (used by "Delete all data").</summary>
    void DeleteAll();
    /// <summary>Whether a key is stored, without reading the secret where the store allows it.</summary>
    bool Exists(ProviderID provider)
    {
        try { return Read(provider) is not null; } catch (CredentialException) { return false; }
    }
}

/// <summary>Test and preview store. Holds keys only in process memory.</summary>
public sealed class InMemoryCredentialStore(IReadOnlyDictionary<ProviderID, APIKey>? initial = null) : ICredentialStore
{
    readonly Dictionary<ProviderID, APIKey> keys = initial is null ? [] : new(initial);
    readonly Lock gate = new();

    public APIKey? Read(ProviderID provider) { lock (gate) return keys.GetValueOrDefault(provider); }
    public void Save(APIKey key, ProviderID provider) { lock (gate) keys[provider] = key; }
    public void Delete(ProviderID provider) { lock (gate) keys.Remove(provider); }
    public void DeleteAll() { lock (gate) keys.Clear(); }
}
