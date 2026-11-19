// The C# side of UserDefaults: what Settings, Routes and the app read and write. Values are JSON-shaped:
// string, bool, long, double, IReadOnlyList<string>, or null for missing. Typed access with defaults is in
// Settings.cs (port of app/Settings.swift); the persisted store is JsonSettingsStore.
namespace Murmur.Core;

public interface ISettingsStore
{
    object? Get(string key);
    void Set(string key, object? value);
    void Remove(string key);
    IReadOnlyDictionary<string, object?> Snapshot();
}

/// <summary>Test and preview store; process memory only.</summary>
public sealed class InMemorySettingsStore(IDictionary<string, object?>? initial = null) : ISettingsStore
{
    readonly Dictionary<string, object?> values = new(initial ?? new Dictionary<string, object?>());
    readonly Lock gate = new();
    public object? Get(string key) { lock (gate) return values.GetValueOrDefault(key); }
    public void Set(string key, object? value) { lock (gate) { if (value is null) values.Remove(key); else values[key] = value; } }
    public void Remove(string key) { lock (gate) values.Remove(key); }
    public IReadOnlyDictionary<string, object?> Snapshot() { lock (gate) return new Dictionary<string, object?>(values); }
}
