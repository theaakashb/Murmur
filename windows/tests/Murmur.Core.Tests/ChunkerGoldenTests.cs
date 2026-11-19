// Cases from app/check_controller.swift, evaluated by the Mac implementation.
using System.Text.Json.Nodes;
using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class ChunkerGoldenTests
{
    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "chunker.json");
        var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var (kind, cases) in data)
            foreach (var test in cases!.AsArray()) yield return [kind, test!.ToJsonString()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesMac(string kind, string json)
    {
        var test = JsonNode.Parse(json)!;
        T[] Values<T>(string name) => test[name]!.AsArray().Select(n => n!.GetValue<T>()).ToArray();
        switch (kind)
        {
            case "joins":
                Assert.Equal(test["expected"]!.GetValue<string>(), Chunker.Join(Values<string>("texts"), Values<bool>("sentenceEnds")));
                break;
            case "repairs":
                var words = Values<string>("words");
                var repair = Chunker.Repair(words[0], words[1], words[2], words[3]);
                if (test["expected"] is null) Assert.Null(repair);
                else Assert.Equal(Values<string>("expected"), new[] { repair!.Value.Tail, repair.Value.Head });
                break;
            case "cuts":
                int frames = test["frames"]!.GetValue<int>();
                Assert.Equal(test["expected"]!.GetValue<int>(), Chunker.QuietestCut(Values<double>("energies").Select(e => (frames, e)).ToArray(), 10));
                break;
            case "pauses":
                Assert.Equal(test["expected"]!.GetValue<double>(), Chunker.PauseLength(Values<double>("energies").Select(e => (10, e)).ToArray(), test["cut"]!.GetValue<int>(), 10));
                break;
            default: throw new InvalidOperationException(kind);
        }
    }
}
