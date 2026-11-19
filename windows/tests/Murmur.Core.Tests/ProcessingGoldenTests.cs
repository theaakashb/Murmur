// Compares ProcessingCoordinator with golden/processing.json, which windows/golden/golden_processing.swift writes from the Mac's Swift:
// every cleanup-corpus entry on the local route and through each deterministic mock provider, for every option set.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>The generator's GoldenMock; keep the two identical. ASCII-only transforms of the transcript between its tags.</summary>
internal sealed class GoldenMock(string kind) : ICleanupProvider
{
    public ProviderID Id => ProviderID.CustomAPI;
    public DataDisclosure Disclosure => new(Id, ProcessingStage.Cleanup, "example.invalid", ["transcript"], ["audio"], "golden");
    public Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default)
    {
        var body = MockProvider.Body(messages.User);
        switch (kind)
        {
            case "echo": return Task.FromResult(body);
            case "period":
                // Punctuation only: an ASCII lowercase first letter capitalized, a final period added when none of . ? ! ends it.
                var o = body.Length > 0 && body[0] is >= 'a' and <= 'z' ? char.ToUpperInvariant(body[0]) + body[1..] : body;
                return Task.FromResult(o.Length > 0 && o[^1] is '.' or '?' or '!' ? o : o + ".");
            case "answer": return Task.FromResult("Sure! " + body);
            case "number": return Task.FromResult(body + " 17");
            case "empty": return Task.FromResult("");
            case "copy": return Task.FromResult(body + " Ayesha Khan");
            default: return Task.FromException<string>(new ProviderException(ProviderError.Network(ProviderID.CustomAPI)));
        }
    }
    public Task ValidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class ProcessingGoldenTests
{
    static readonly JsonElement Root = CleanupGolden.Load("processing");
    public static IEnumerable<object[]> Corpus => CleanupGolden.Cases(Root, "corpus", "id");
    public static IEnumerable<object[]> Context => CleanupGolden.Cases(Root, "context");
    static readonly IReadOnlyList<(string Name, CleanupOptions Options)> Sets = [.. CleanupGolden.OptionSets, .. CleanupGolden.TrustedSets];
    static readonly string[] Mocks = ["echo", "period", "answer", "number", "empty", "fail"];

    static string Encode(ProcessingResult r, string input) => r.Outcome is ProcessingOutcome.Insert i
        ? $"{(r.CleanupPath is { } p ? GoldenFiles.Camel(p) : "-")}|{(r.Fallback is { } f ? GoldenFiles.Camel(f) : "-")}|{r.GuardVerdict ?? "-"}|{(i.Text == input ? "=" : i.Text)}"
        : "held";

    static ProcessingCoordinator Make(CleanupRoute route)
    {
        var p = new ProcessingCoordinator(TempDirs.New("processing-golden"), new InMemorySettingsStore());
        p.Update(p.Settings with { Route = route }, persist: false);
        return p;
    }

    static List<string> Want(JsonElement c, string key) => c.GetProperty(key).EnumerateArray().Select(x => x.GetString()!).ToList();

    [Theory, MemberData(nameof(Corpus))]
    public async Task CorpusEntries(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("corpus")[i];
        var input = c.GetProperty("input").GetString()!;
        var local = Make(CleanupRoute.Local);
        Assert.Equal(c.GetProperty("ownLocal").GetString(), Encode(local.Process(input, new DictationStyle(CleanupGolden.Decode(c.GetProperty("own").GetString()!))), input));
        var failures = new List<string>();
        void Compare(string kind, List<string> want, IReadOnlyList<string> got)
        {
            for (int k = 0; k < Sets.Count; k++) if (want[k] != got[k]) failures.Add($"{kind} {Sets[k].Name}: want {want[k]}, got {got[k]}");
        }
        Compare("local", Want(c, "local"), Sets.Select(s => Encode(local.Process(input, new DictationStyle(s.Options)), input)).ToList());
        var cloud = Make(CleanupRoute.Cloud);
        foreach (var kind in Mocks)
        {
            cloud.CloudCleaner = new GoldenMock(kind);
            var got = new List<string>();
            foreach (var (_, options) in Sets) got.Add(Encode(await cloud.ProcessAsync(input, new DictationStyle(options)), input));
            Compare(kind, Want(c, kind), got);
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // spec/pipeline.md §9: on trusted polish a word copied from screen context is pasted; on strict polish the guard rejects it.
    [Theory, MemberData(nameof(Context))]
    public async Task ScreenContextOnTrustedAndStrictRoutes(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("context")[i];
        var input = c.GetProperty("input").GetString()!;
        var p = Make(CleanupRoute.Cloud);
        p.CloudCleaner = new GoldenMock("copy");
        var polish = new CleanupOptions(punctuation: true, polish: true, spoken: true);
        var screen = new ScreenContext { Clipboard = "Ayesha Khan" };
        async Task<string> Run(CleanupOptions options)
        {
            var r = await p.ProcessAsync(input, new DictationStyle(options, context: screen));
            return Encode(r, input) + $"|{(r.ContextUsed ? "true" : "false")}|{(r.RejectedOutput is not null ? "true" : "false")}";
        }
        Assert.Equal(c.GetProperty("trusted").GetString(), await Run(polish with { TrustModel = true }));
        Assert.Equal(c.GetProperty("strict").GetString(), await Run(polish));
    }

    [Fact]
    public void PromptHashes() =>
        Assert.Equal(Want(Root, "promptHashes"), Sets.Select(s => ProcessingCoordinator.PromptHash(CleanupPrompt.Build("x", s.Options).System)).ToList());
}
