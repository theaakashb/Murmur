// Compares EditCheck.Verdict with golden/edit_check.json, which windows/golden/golden_edit_check.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class VoiceEditGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("edit_check");
    public static IEnumerable<object[]> Verdicts => GoldenFiles.Cases(Root, "verdicts", c => c.GetProperty("output").GetString()!);

    [Theory, MemberData(nameof(Verdicts))]
    public void VerdictMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("verdicts")[i];
        var v = EditCheck.Verdict(c.GetProperty("selection").GetString()!, c.GetProperty("instruction").GetString()!, c.GetProperty("output").GetString()!);
        var text = GoldenFiles.Optional(c, "text");
        switch (c.GetProperty("kind").GetString())
        {
            case "apply": Assert.Equal(text, Assert.IsType<EditCheck.EditVerdict.Apply>(v).Text); break;
            case "refused": Assert.Equal(text, Assert.IsType<EditCheck.EditVerdict.Refused>(v).Reason); break;
            default: Assert.IsType<EditCheck.EditVerdict.Unchanged>(v); break;
        }
    }
}
