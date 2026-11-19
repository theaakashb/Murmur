// Compares Murmur.Core with golden/history.json, which windows/golden/golden_history.swift writes from the Mac's Swift.
using System.Text.Json;
using System.Text.Json.Nodes;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class HistoryGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("history");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Entries => GoldenFiles.Cases(Root, "entries", c => c.GetProperty("label").GetString()!);
    public static IEnumerable<object[]> Trims => GoldenFiles.Cases(Root, "trims", c => $"{c.GetProperty("initial").GetString()} -> {c.GetProperty("edited").GetString()}");
    public static IEnumerable<object[]> Recovery => GoldenFiles.Cases(Root, "recovery", c => c.TryGetProperty("opens", out var o) ? o.ToString() : "none");

    static string? Optional(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    /// <summary>Swift writes whole-number doubles as 2 and .NET as 2 too, but "2.0" and "2" are the same JSON number: compare numbers by value.</summary>
    internal static bool SameJson(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob) return oa.Count == ob.Count && oa.All(p => ob.ContainsKey(p.Key) && SameJson(p.Value, ob[p.Key]));
        if (a is JsonArray aa && b is JsonArray ab) return aa.Count == ab.Count && aa.Zip(ab).All(p => SameJson(p.First, p.Second));
        var va = a.AsValue().GetValueKind(); var vb = b.AsValue().GetValueKind();
        if (va == JsonValueKind.Number && vb == JsonValueKind.Number) return a.GetValue<double>() == b.GetValue<double>();
        return va == vb && a.ToJsonString() == b.ToJsonString();
    }

    [Theory, MemberData(nameof(Entries))]
    public void EntryJsonAndWordingMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("entries", i);
        var swiftJson = c.GetProperty("json").GetString()!;
        var entry = StoreJson.Decode<HistoryEntry>(System.Text.Encoding.UTF8.GetBytes(swiftJson));
        // What Swift wrote reads back and re-encodes to the same JSON tree: the field names and value forms are interchangeable.
        var ours = JsonNode.Parse(StoreJson.Encode(entry));
        Assert.True(SameJson(JsonNode.Parse(swiftJson), ours), $"re-encoded entry differs:\nSwift:  {swiftJson}\n.NET:   {ours!.ToJsonString()}");
        Assert.Equal(c.GetProperty("needsAttention").GetBoolean(), entry.NeedsAttention);
        Assert.Equal(Optional(c, "statusLabel"), entry.StatusLabel);
        Assert.Equal(Optional(c, "statusNote"), entry.StatusNote);
        // The Windows wording says "this PC" where the Mac says "this Mac"; nothing else differs.
        Assert.Equal(Optional(c, "outcomeNote")?.Replace("this Mac", "this PC"), entry.OutcomeNote);
        Assert.Equal(Optional(c, "status"), entry.Status?.Raw());
    }

    [Theory, MemberData(nameof(Trims))]
    public void TrimmingAppendedMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("trims", i);
        var (text, changed) = AfterPaste.TrimmingAppended(c.GetProperty("initial").GetString()!, c.GetProperty("edited").GetString()!);
        Assert.Equal(c.GetProperty("text").GetString(), text);
        Assert.Equal(c.GetProperty("changed").GetBoolean(), changed);
    }

    [Theory, MemberData(nameof(Recovery))]
    public void RecoveryEntryMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("recovery", i);
        var id = Optional(c, "id");
        var entries = c.GetProperty("entries").EnumerateArray().Select(s => new HistoryEntry { Id = "6D1A0001-5E5E-4A4A-9A9A-0000000000A1", RecordedAt = DateTime.UnixEpoch, RawText = "", FinalText = "x", InsertionStatusRaw = s.GetString()! }).ToList();
        Assert.Equal(Optional(c, "opens"), HistoryEntry.RecoveryEntryToOpen(id, entries));
    }

    [Fact]
    public void QuarantineNoticesMatch() =>
        Assert.Equal(GoldenFiles.Strings(Root.GetProperty("notices")), new[] { 1, 2, 5 }.Select(HistoryStore.QuarantineNotice).ToArray());
}
