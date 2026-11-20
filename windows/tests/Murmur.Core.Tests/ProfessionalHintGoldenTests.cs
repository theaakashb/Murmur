// Compares ProfessionalSignal with golden/professional_hint.json, which windows/golden/golden_professional_hint.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ProfessionalHintGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("professional_hint");
    public static IEnumerable<object[]> Signals => GoldenFiles.Cases(Root, "signals", c => c.GetProperty("text").GetString()!);
    public static IEnumerable<object[]> Sentences => GoldenFiles.Cases(Root, "sentences", c => string.Join("+", c.GetProperty("reasons").EnumerateArray()));

    static ProfessionalSignal.Reason[] Reasons(JsonElement a) => a.EnumerateArray().Select(x => Enum.Parse<ProfessionalSignal.Reason>(x.GetString()!, ignoreCase: true)).ToArray();

    [Theory, MemberData(nameof(Signals))]
    public void ReasonsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("signals")[i];
        var text = c.GetProperty("text").GetString()!;
        var reasons = ProfessionalSignal.Reasons(text);
        Assert.Equal(Reasons(c.GetProperty("reasons")), reasons);
        Assert.Equal(c.GetProperty("sentence").GetString(), ProfessionalSignal.Sentence(reasons));
        Assert.Equal(c.GetProperty("longestRun").GetInt32(), ProfessionalSignal.LongestRun(text));
    }

    [Theory, MemberData(nameof(Sentences))]
    public void SentencesMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("sentences")[i];
        Assert.Equal(c.GetProperty("sentence").GetString(), ProfessionalSignal.Sentence(Reasons(c.GetProperty("reasons"))));
    }
}
