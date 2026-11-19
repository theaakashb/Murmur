// Compares Murmur.Core with golden/shortcut.json, which windows/golden/golden_shortcut.swift writes from the Mac's Swift (the ShortcutMachine event sequences).
using System.Globalization;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ShortcutMachineGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("shortcut");
    public static IEnumerable<object[]> Cases => GoldenFiles.Cases(Root, "cases", c => c.GetProperty("label").GetString()!);

    static string Describe(ShortcutAction? action) => action is { } a ? a.ToString().ToLowerInvariant() : "-";

    [Theory, MemberData(nameof(Cases))]
    public void SequenceMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Root.GetProperty("cases")[i];
        var machine = new ShortcutMachine(c.GetProperty("hold").GetDouble(), c.GetProperty("window").GetDouble()) { PressToToggle = c.GetProperty("pressToToggle").GetBoolean() };
        var results = GoldenFiles.Strings(c.GetProperty("results"));
        var states = GoldenFiles.Strings(c.GetProperty("states"));
        int n = 0;
        foreach (var op in c.GetProperty("ops").EnumerateArray().Select(GoldenFiles.Strings))
        {
            double time = op.Length > 1 ? double.Parse(op[1], CultureInfo.InvariantCulture) : 0;
            string result = op[0] switch
            {
                "press" => Describe(machine.Press(time, op.Length > 2 && op[2] == "repeat")),
                "release" => Describe(machine.Release(time)),
                "expire" => Describe(machine.DoublePressExpired(time)),
                "cancel" => Describe(machine.Cancel()),
                "finish" => Describe(machine.Finish()),
                "startToggled" => Describe(machine.StartToggled()),
                "lost" => Describe(machine.EventStreamLost()),
                "processingFinished" => Do(machine.ProcessingFinished),
                "beginProcessing" => machine.BeginProcessing().ToString().ToLowerInvariant(),
                _ => throw new InvalidOperationException(op[0]),
            };
            Assert.True(results[n] == result, $"{label}: op {n} {string.Join(",", op)} gave {result}, Swift {results[n]}");
            var state = $"{machine.State.ToString().ToLowerInvariant()}/{machine.AwaitingSecondPress.ToString().ToLowerInvariant()}";
            Assert.True(states[n] == state, $"{label}: state after op {n} is {state}, Swift {states[n]}");
            n++;
        }
        Assert.Equal(results.Length, n);
    }

    static string Do(Action action) { action(); return "-"; }
}
