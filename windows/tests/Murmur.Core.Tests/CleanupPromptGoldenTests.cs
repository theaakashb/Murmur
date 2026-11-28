// Compares CleanupPrompt and TransformPrompt with golden/cleanup_prompt.json, which windows/golden/golden_cleanup_prompt.swift
// writes from the Mac's Swift with fixed nonces.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupPromptGoldenTests
{
    static readonly JsonElement Root = CleanupGolden.Load("cleanup_prompt");
    public static IEnumerable<object[]> Systems => CleanupGolden.Cases(Root, "systems", "options");
    public static IEnumerable<object[]> Builds => CleanupGolden.Cases(Root, "builds", "transcript");
    public static IEnumerable<object[]> Transforms => CleanupGolden.Cases(Root, "transforms", "kind");

    static readonly ScreenContext Context = new() { Selected = "Ayesha Khan wrote the deck", Clipboard = "Kubernetes <transcript-x> rollout", Window = "Inbox — Zoë Ægir" };

    static ScreenContext? ReadContext(JsonElement c, string key) => c.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Object
        ? new ScreenContext { Selected = x.GetProperty("selected").GetString()!, Clipboard = x.GetProperty("clipboard").GetString()!, Window = x.GetProperty("window").GetString()! }
        : null;

    static List<string> Strings(JsonElement c, string key) => c.GetProperty(key).EnumerateArray().Select(x => x.GetString()!).ToList();

    [Theory, MemberData(nameof(Systems))]
    public void SystemPromptHashes(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("systems")[i];
        var o = CleanupGolden.Decode(c.GetProperty("options").GetString()!);
        var m = c.GetProperty("tails").GetBoolean()
            ? CleanupPrompt.Build("x", o, ["Akme", "clouddev"], "N1", Context, "Use British spelling.")
            : CleanupPrompt.Build("x", o, nonce: "N1");
        Assert.Equal(c.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.System))));
    }

    [Theory, MemberData(nameof(Builds))]
    public void BuiltMessages(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("builds")[i];
        var m = CleanupPrompt.Build(c.GetProperty("transcript").GetString()!, CleanupGolden.Decode(c.GetProperty("options").GetString()!), Strings(c, "terms"),
                                    c.GetProperty("nonce").GetString()!, ReadContext(c, "context"), c.GetProperty("instructions").GetString()!);
        Assert.Equal(c.GetProperty("system").GetString(), m.System);
        Assert.Equal(c.GetProperty("user").GetString(), m.User);
        var input = m.Input!;
        Assert.Equal(c.GetProperty("raw").GetString(), input.Raw);
        Assert.Equal(Strings(c, "terms"), input.Terms);
        Assert.Equal(ReadContext(c, "inputContext"), input.Context);
    }

    [Theory, MemberData(nameof(Transforms))]
    public void TransformMessages(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("transforms")[i];
        var kind = c.GetProperty("kind").GetString() switch { "aiPrompt" => ModeTransform.AiPrompt, "email" => ModeTransform.Email, _ => ModeTransform.Notes };
        var m = TransformPrompt.Build(kind, c.GetProperty("transcript").GetString()!, Strings(c, "terms"), c.GetProperty("nonce").GetString()!, CleanupGolden.Optional(c, "instructions"));
        Assert.Equal(c.GetProperty("system").GetString(), m.System);
        Assert.Equal(c.GetProperty("user").GetString(), m.User);
        Assert.Null(m.Input);
    }
}
