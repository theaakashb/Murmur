using System.Text.Json;
using Murmur.Core;

namespace Murmur.App;

/// <summary>spec/test-hooks.md: with MURMUR_TEST_EVENTS=1, content-free events go to diagnostics\events.jsonl in the data folder.</summary>
internal static class TestEvents
{
    static string? path;
    static readonly object gate = new();

    public static void Init(string root)
    {
        if (Environment.GetEnvironmentVariable("MURMUR_TEST_EVENTS") != "1") return;
        var dir = Path.Combine(root, "diagnostics");
        PrivateFiles.PrepareDirectory(dir);
        path = Path.Combine(dir, "events.jsonl");
    }

    /// <summary>Never pass text, keys or response bodies: only fixed words, numbers and ids.</summary>
    public static void Emit(string name, params (string Key, object? Value)[] data)
    {
        if (path is null) return;
        var row = new Dictionary<string, object?> { ["t"] = DateTime.UtcNow.ToString("o"), ["event"] = name };
        foreach (var (k, v) in data) if (v is not null) row[k] = v;
        try
        {
            lock (gate)
            {
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(file);
                writer.WriteLine(JsonSerializer.Serialize(row));
            }
        }
        catch (IOException) { /* diagnostics must never break dictation */ }
    }
}
