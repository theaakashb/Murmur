// Compares Murmur.Core with golden/writing_modes.json, which windows/golden/golden_writing_modes.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class WritingModesGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("writing_modes");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Sites => GoldenFiles.Cases(Root, "sites", c => $"{c.GetProperty("pattern").GetString()} | {c.GetProperty("url").GetString()}");
    public static IEnumerable<object[]> Strips => GoldenFiles.Cases(Root, "strips", c => $"{c.GetProperty("phrase").GetString()} | {c.GetProperty("text").GetString()} | {c.GetProperty("startOnly")}");
    public static IEnumerable<object[]> Matches => GoldenFiles.Cases(Root, "matches", c => $"{Optional(c, "bundleID")} {Optional(c, "url")} -> {Optional(c, "matched")}");
    public static IEnumerable<object[]> Triggers => GoldenFiles.Cases(Root, "triggers", c => c.GetProperty("transcript").GetString()!);
    public static IEnumerable<object[]> Modes => GoldenFiles.Cases(Root, "modes", c => c.GetProperty("label").GetString()!);
    public static IEnumerable<object[]> Styles => GoldenFiles.Cases(Root, "styles", c => c.GetProperty("label").GetString()!);
    public static IEnumerable<object[]> Needs => GoldenFiles.Cases(Root, "needs", c => c.GetProperty("label").GetString()!);

    static string? Optional(JsonElement c, string name) => c.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    static WritingMode ModeOf(string json) => StoreJson.Decode<WritingMode>(Encoding.UTF8.GetBytes(json));
    static List<WritingMode> ModesOf(JsonElement list) => list.EnumerateArray().Select(m => ModeOf(m.GetString()!)).ToList();
    static CleanupOptions OptionsOf(string json) => StoreJson.Decode<CleanupOptions>(Encoding.UTF8.GetBytes(json));

    [Theory, MemberData(nameof(Sites))]
    public void WebsiteMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("sites", i);
        Assert.Equal(c.GetProperty("matches").GetBoolean(), WritingModes.WebsiteMatches(c.GetProperty("pattern").GetString()!, new Uri(c.GetProperty("url").GetString()!)));
    }

    [Theory, MemberData(nameof(Strips))]
    public void StripTriggerMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("strips", i);
        Assert.Equal(Optional(c, "result"), WritingModes.StripTrigger(c.GetProperty("phrase").GetString()!, c.GetProperty("text").GetString()!, c.GetProperty("startOnly").GetBoolean()));
    }

    [Theory, MemberData(nameof(Matches))]
    public void ModeMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("matches", i);
        var url = Optional(c, "url");
        Assert.Equal(Optional(c, "matched"), WritingModes.Match(ModesOf(c.GetProperty("modes")), Optional(c, "bundleID"), url is null ? null : new Uri(url))?.Name);
    }

    [Theory, MemberData(nameof(Triggers))]
    public void TriggerMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("triggers", i);
        var result = WritingModes.Trigger(ModesOf(c.GetProperty("modes")), c.GetProperty("transcript").GetString()!);
        Assert.Equal(Optional(c, "matched"), result?.Mode.Name);
        Assert.Equal(Optional(c, "text"), result?.Text);
    }

    [Theory, MemberData(nameof(Modes))]
    public void ModeJsonMatches(int i, string label)
    {
        Assert.NotNull(label);
        var swiftJson = Case("modes", i).GetProperty("json").GetString()!;
        var mode = ModeOf(swiftJson);
        var ours = JsonNode.Parse(StoreJson.Encode(mode));
        Assert.True(HistoryGoldenTests.SameJson(JsonNode.Parse(swiftJson), ours), $"Swift:  {swiftJson}\n.NET:   {ours!.ToJsonString()}");
    }

    [Theory, MemberData(nameof(Styles))]
    public void StyleMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("styles", i);
        var mode = Optional(c, "mode") is { } json ? ModeOf(json) : null;
        var context = c.GetProperty("context").GetBoolean() ? new ScreenContext { Selected = "Synthetic selection" } : null;
        var style = DictationStyle.ForMode(mode, OptionsOf(c.GetProperty("globalOptions").GetString()!), c.GetProperty("globalScreenContext").GetBoolean(), context);
        Assert.True(HistoryGoldenTests.SameJson(JsonNode.Parse(c.GetProperty("options").GetString()!), JsonNode.Parse(StoreJson.Encode(style.Options))), "options");
        Assert.Equal(c.GetProperty("tone").GetString(), JsonNamingPolicy.KebabCaseLower.ConvertName(style.Tone.ToString()));
        Assert.Equal(c.GetProperty("instructions").GetString(), style.Instructions);
        Assert.Equal(Optional(c, "transform"), style.Transform is { } t ? JsonNamingPolicy.CamelCase.ConvertName(t.ToString()) : null);
        Assert.Equal(c.GetProperty("usesEngine").GetBoolean(), style.UsesEngine);
        Assert.Equal(c.GetProperty("usesModel").GetBoolean(), style.UsesModel);
        Assert.Equal(c.GetProperty("hasContext").GetBoolean(), style.Context is not null);
    }

    [Theory, MemberData(nameof(Needs))]
    public void NeedsScreenContextMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("needs", i);
        var mode = Optional(c, "mode") is { } json ? ModeOf(json) : null;
        Assert.Equal(c.GetProperty("needs").GetBoolean(),
            DictationStyle.NeedsScreenContext(mode, c.GetProperty("modes").EnumerateArray().Select(m => ModeOf(m.GetString()!)).ToList(),
                OptionsOf(c.GetProperty("globalOptions").GetString()!), c.GetProperty("globalScreenContext").GetBoolean()));
    }

    [Fact]
    public void PresetsMatch()
    {
        var rows = Root.GetProperty("presets").EnumerateArray().Select(r => GoldenFiles.Strings(r)).ToList();
        var ours = WritingMode.Presets.Select(p => new[] { p.Id.ToString().ToUpperInvariant(), p.Name, string.Join("|", p.Triggers), p.Transform is { } t ? JsonNamingPolicy.CamelCase.ConvertName(t.ToString()) : "", p.Instructions }).ToList();
        Assert.Equal(rows.Count, ours.Count);
        for (int i = 0; i < rows.Count; i++) Assert.Equal(rows[i], ours[i]);
    }
}
