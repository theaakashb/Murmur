using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Murmur.Core;

/// <summary>
/// The settings JSON form shared by <see cref="JsonSettingsStore"/> and the backup's settings.json: one object, sorted keys.
/// Values are string, bool, long, double, a list of strings, a list of numbers, or (kept as is, never dropped) any other JSON.
/// </summary>
static class SettingsJson
{
    static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Null when <paramref name="data"/> is not a JSON object.</summary>
    public static Dictionary<string, object?>? TryParse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var values = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = FromJson(property.Value);
                if (value is not null) values[property.Name] = value;
            }
            return values;
        }
        catch (JsonException) { return null; }
    }

    static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.Null => null,
        JsonValueKind.Array when e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) => e.EnumerateArray().Select(x => x.GetString()!).ToArray(),
        JsonValueKind.Array when e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray(),
        _ => e.Clone(),
    };

    /// <summary>The stored form of a value; throws for a type the file cannot hold.</summary>
    public static object Normalize(object value) => value switch
    {
        string or bool or long or JsonElement => value,
        int i => (long)i,
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => (double)f,
        IEnumerable<string> strings => strings.ToArray(),
        IEnumerable<double> numbers when numbers.All(double.IsFinite) => numbers.ToArray(),
        _ => throw new ArgumentException($"A settings value of type {value.GetType().Name} cannot be stored."),
    };

    public static byte[] Serialize(IEnumerable<KeyValuePair<string, object?>> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values.Where(v => v.Value is not null).OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                switch (Normalize(value!))
                {
                    case string s: writer.WriteStringValue(s); break;
                    case bool b: writer.WriteBooleanValue(b); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case string[] list: writer.WriteStartArray(); foreach (var item in list) writer.WriteStringValue(item); writer.WriteEndArray(); break;
                    case double[] list: writer.WriteStartArray(); foreach (var item in list) writer.WriteNumberValue(item); writer.WriteEndArray(); break;
                    case JsonElement element: element.WriteTo(writer); break;
                }
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

/// <summary>
/// <see cref="ISettingsStore"/> persisted as <c>settings.json</c> through <see cref="PrivateFiles"/> (private, replaced atomically on every write).
/// A missing file is an empty store. A file that is not a JSON object is moved aside to <c>settings.json.corrupt-*</c>, never deleted or overwritten silently.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";
    readonly string path;
    readonly Lock gate = new();
    Dictionary<string, object?> values;

    /// <summary>Where a corrupt file was moved by the constructor, or null.</summary>
    public string? MovedAsideTo { get; }

    public JsonSettingsStore(string path)
    {
        this.path = Path.GetFullPath(path);
        PrivateFiles.PrepareDirectory(Path.GetDirectoryName(this.path)!);
        var data = PrivateFiles.Read(this.path);
        var parsed = data is null ? null : SettingsJson.TryParse(data);
        values = parsed ?? [];
        if (data is not null && parsed is null)
        {
            MovedAsideTo = $"{this.path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            File.Move(this.path, MovedAsideTo);
        }
    }

    public object? Get(string key) { lock (gate) return values.GetValueOrDefault(key); }

    public void Set(string key, object? value)
    {
        if (value is null) { Remove(key); return; }
        var stored = SettingsJson.Normalize(value);
        lock (gate)
        {
            var next = new Dictionary<string, object?>(values) { [key] = stored };
            Save(next);
        }
    }

    public void Remove(string key)
    {
        lock (gate)
        {
            if (!values.ContainsKey(key)) return;
            var next = new Dictionary<string, object?>(values);
            next.Remove(key);
            Save(next);
        }
    }

    public IReadOnlyDictionary<string, object?> Snapshot() { lock (gate) return new Dictionary<string, object?>(values); }

    /// <summary>Writes first, then adopts: a failed write leaves memory and disk as they were.</summary>
    void Save(Dictionary<string, object?> next)
    {
        PrivateFiles.Write(SettingsJson.Serialize(next), path);
        values = next;
    }
}
