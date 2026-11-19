// Compares Murmur.Core with golden/retention.json, which windows/golden/golden_retention.swift writes from the Mac's Swift.
using System.Globalization;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class RetentionGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("retention");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Cutoffs => GoldenFiles.Cases(Root, "cutoffs", c => $"{c.GetProperty("label").GetString()} {c.GetProperty("retention").GetString()}");
    public static IEnumerable<object[]> Plans => GoldenFiles.Cases(Root, "plans", c => $"{c.GetProperty("retention").GetString()} {c.GetProperty("sizes")}");
    public static IEnumerable<object[]> Merges => GoldenFiles.Cases(Root, "merges", c => c.GetProperty("backup").ToString());
    public static IEnumerable<object[]> Machine => GoldenFiles.Cases(Root, "machine", c => c.GetProperty("key").GetString()!);
    public static IEnumerable<object[]> Summaries => GoldenFiles.Cases(Root, "summaries", c => c.GetProperty("text").GetString()!);
    public static IEnumerable<object[]> Wipes => GoldenFiles.Cases(Root, "wipes", c => c.GetProperty("text").GetString()!);

    static DateTime FromUnix(double seconds) => DateTime.UnixEpoch.AddSeconds(seconds);
    static double ToUnix(DateTime value) => (value - DateTime.UnixEpoch).TotalSeconds;
    static HistoryRetention Retention(string raw) => Enum.GetValues<HistoryRetention>().Single(r => r.Raw() == raw);

    [Theory, MemberData(nameof(Cutoffs))]
    public void CutoffMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("cutoffs", i);
        var actual = Retention(c.GetProperty("retention").GetString()!).Cutoff(FromUnix(c.GetProperty("now").GetDouble()), TimeZoneInfo.Utc);
        // Swift leaves out a nil optional.
        if (!c.TryGetProperty("cutoff", out var expected)) Assert.Null(actual); else Assert.Equal(expected.GetDouble(), ToUnix(actual!.Value));
    }

    sealed record Item(string Id, DateTime RecordedAt) : IRetainedHistoryItem;

    [Theory, MemberData(nameof(Plans))]
    public void CapPlanMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("plans", i);
        var baseTime = FromUnix(1_790_000_000);
        var ages = c.GetProperty("ages").EnumerateArray().Select(a => a.GetDouble()).ToList();
        var sizes = c.GetProperty("sizes").EnumerateArray().Select(a => a.GetInt64()).ToList();
        var entries = ages.Select((age, n) => new Item($"00000000-0000-0000-0000-{n:D12}", baseTime.AddSeconds(-age))).ToList();
        var map = entries.Select((e, n) => (e.Id, Size: sizes[n])).ToDictionary(x => x.Id, x => (long)x.Size);
        var result = HistoryCap.Plan(entries, map, c.GetProperty("other").GetInt64(), Retention(c.GetProperty("retention").GetString()!), c.GetProperty("cap").GetInt64());
        Assert.Equal(c.GetProperty("dropped").EnumerateArray().Select(d => d.GetInt32()).ToArray(), result.Dropped.Select(d => entries.IndexOf(d)).ToArray());
        Assert.Equal(c.GetProperty("status").GetString(), result.Status switch { HistoryCapStatus.WithinCap => "within", HistoryCapStatus.ApproachingCap => "approaching", _ => "over" });
        Assert.Equal(c.GetProperty("used").GetInt64(), result.Status.UsedBytes);
        Assert.Equal(c.TryGetProperty("warning", out var warning) ? warning.GetString() : null, result.Status.Warning);
    }

    [Theory, MemberData(nameof(Merges))]
    public void MergedMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("merges", i);
        Dictionary<string, object?> Read(string name) => c.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
        Assert.Equal(Read("merged").OrderBy(p => p.Key), DataBackup.Merged(Read("backup"), Read("current")).OrderBy(p => p.Key));
    }

    [Theory, MemberData(nameof(Machine))]
    public void MachineSettingMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("machine", i);
        Assert.Equal(c.GetProperty("machine").GetBoolean(), DataBackup.IsMachineSetting(c.GetProperty("key").GetString()!));
    }

    [Theory, MemberData(nameof(Summaries))]
    public void SummaryMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("summaries", i);
        var mode = c.GetProperty("mode").GetString() == "on" ? HistoryMode.On : HistoryMode.Off;
        Assert.Equal(c.GetProperty("text").GetString(), HistoryText.Summary(mode, c.GetProperty("count").GetInt32(), c.GetProperty("sessionOnly").GetInt32()));
    }

    [Theory, MemberData(nameof(Wipes))]
    public void WipeMessageMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("wipes", i);
        var report = new DeleteAllReport();
        report.Failures.AddRange(GoldenFiles.Strings(c.GetProperty("failures")));
        var steps = GoldenFiles.Strings(c.GetProperty("steps")).Select(s => Enum.GetValues<PostWipeStep>().Single(x => x.Raw() == s)).ToList();
        var message = PostWipeSteps.Message(report, steps);
        Assert.Equal(c.GetProperty("succeeded").GetBoolean(), message.Succeeded);
        Assert.Equal(c.GetProperty("text").GetString(), message.Text);
    }

    [Fact]
    public void MegabytesMatch()
    {
        var expected = GoldenFiles.Strings(Root.GetProperty("megabytes"));
        long[] bytes = [0, 1, 1_048_575, 1_048_576, 1_572_864, 52_428_800, 47_185_920, 123_456_789];
        Assert.Equal(expected, bytes.Select(HistoryCap.Megabytes).ToArray());
    }

    [Fact]
    public void FixedWordingAndConstantsMatch()
    {
        var f = Root.GetProperty("fixed");
        string S(string name) => f.GetProperty(name).GetString()!;
        Assert.Equal(S("offDisclosure"), HistoryModes.OffDisclosure);
        Assert.Equal(S("retryWindow"), RetryWindow.Text);
        Assert.Equal(S("savedOn"), HistoryText.Saved(true));
        Assert.Equal(S("savedOff"), HistoryText.Saved(false));
        Assert.Equal(HistoryText.SessionOnlyNote, S("sessionOnlyNote"));
        var all = Enum.GetValues<HistoryRetention>();
        Assert.Equal(S("labels"), string.Join("|", all.Select(r => r.Label())));
        Assert.Equal(S("raws"), string.Join("|", all.Select(r => r.Raw())));
        Assert.Equal(S("destination"), string.Join("|", DataBackup.DestinationSettings.OrderBy(k => k, StringComparer.Ordinal)));
        Assert.Equal(HistoryCap.WarningFraction, double.Parse(S("warningFraction"), CultureInfo.InvariantCulture));
        Assert.Equal(HistoryCap.DefaultCapBytes, long.Parse(S("cap")));
        Assert.Equal(DiagnosticRecordings.LifetimeSeconds, double.Parse(S("lifetime"), CultureInfo.InvariantCulture));
        Assert.Equal(RetryWindow.LifetimeSeconds, double.Parse(S("retryLifetime"), CultureInfo.InvariantCulture));
        Assert.Equal(DiagnosticRecordings.FolderName, S("recordings"));
        Assert.Equal(S("steps"), string.Join("|", Enum.GetValues<PostWipeStep>().Select(s => s.Raw())));
        Assert.Equal(S("stepLabels"), string.Join("|", Enum.GetValues<PostWipeStep>().Select(s => s.Label())));
    }
}
