// Compares Murmur.Core with golden/voice_cues.json, which windows/golden/golden_voice_cues.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class VoiceCuesGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("voice_cues");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Words => GoldenFiles.Cases(Root, "words", c => c.GetProperty("input").GetString()!);
    public static IEnumerable<object[]> Split => GoldenFiles.Cases(Root, "split", c => c.GetProperty("raw").GetString()!);
    public static IEnumerable<object[]> Store => GoldenFiles.Cases(Root, "store", c => c.GetProperty("label").GetString()!);

    [Theory, MemberData(nameof(Words))]
    public void WordsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("words", i);
        Assert.Equal(GoldenFiles.Strings(c.GetProperty("words")), VoiceCueText.Words(c.GetProperty("input").GetString()!));
    }

    static string[] Encode(VoiceCuePiece piece) => piece switch
    {
        VoiceCuePiece.Text t => ["t", t.Raw],
        VoiceCuePiece.Expansion e => ["e", e.Value, e.Closing],
        _ => throw new InvalidOperationException(),
    };

    [Theory, MemberData(nameof(Split))]
    public void SplitAndJoinMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("split", i);
        var cues = c.GetProperty("cues").EnumerateArray().Select(x => new VoiceCue { Id = Guid.NewGuid(), Phrase = x.GetProperty("phrase").GetString()!, Text = x.GetProperty("text").GetString()!, CreatedAt = DateTime.UnixEpoch }).ToList();
        var pieces = VoiceCueText.Split(c.GetProperty("raw").GetString()!, cues);
        if (!c.TryGetProperty("pieces", out var expected)) { Assert.Null(pieces); return; }
        Assert.NotNull(pieces);
        Assert.Equal(expected.EnumerateArray().Select(GoldenFiles.Strings), pieces.Select(Encode));
        var keeps = GoldenFiles.Strings(c.GetProperty("keeps")).ToHashSet();
        Assert.Equal(c.GetProperty("joined").GetString(), VoiceCueText.Join(pieces, GoldenFiles.Strings(c.GetProperty("processed")), keeps.Contains));
    }

    [Theory, MemberData(nameof(Store))]
    public void StoreSequence(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("store", i);
        var ops = c.GetProperty("ops").EnumerateArray().Select(GoldenFiles.Strings).ToList();
        var results = GoldenFiles.Strings(c.GetProperty("results"));
        var states = GoldenFiles.Strings(c.GetProperty("states"));
        var dir = TempDirs.New("golden-cues");
        var store = new VoiceCueStore(dir);
        for (int k = 0; k < ops.Count; k++)
        {
            var op = ops[k]; var a = op.Skip(1).ToArray();
            Guid Id(string s) => int.Parse(s) < store.Cues.Count ? store.Cues[int.Parse(s)].Id : Guid.NewGuid();
            string result;
            try
            {
                switch (op[0])
                {
                    case "add": store.Add(a[0], a[1]); break;
                    case "update": store.Update(Id(a[0]), a[1], a[2]); break;
                    case "delete": store.Delete(Id(a[0])); break;
                    case "reopen": store = new VoiceCueStore(dir); break;
                    case "damage": File.WriteAllBytes(Path.Combine(dir, VoiceCueStore.FileName), Encoding.UTF8.GetBytes("not json")); break;
                    case "version": File.WriteAllBytes(Path.Combine(dir, VoiceCueStore.FileName), Encoding.UTF8.GetBytes("""{"cues":[],"version":2}""")); break;
                    case "remove": Directory.Delete(dir, true); break;
                    default: throw new InvalidOperationException("unknown op " + op[0]);
                }
                result = "ok";
            }
            catch (StoreError e) { result = "error:" + e.Message; }
            Assert.Equal(results[k], result);
            Assert.Equal(states[k], string.Join("\u001E", store.Cues.Select(x => x.Phrase + "\u001F" + x.Text)) + (store.Unreadable ? " #unreadable" : ""));
        }
    }
}
