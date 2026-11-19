// Compares Murmur.Core with golden/model_text.json, which windows/golden/golden_model_text.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ModelTextGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("model_text");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Bytes => GoldenFiles.Cases(Root, "bytes", c => c.GetProperty("bytes").ToString());
    public static IEnumerable<object[]> Statuses => GoldenFiles.Cases(Root, "statuses", c => $"{c.GetProperty("kind").GetString()} {c.GetProperty("statusText").GetString()}");
    public static IEnumerable<object[]> Failures => GoldenFiles.Cases(Root, "failures", c => c.GetProperty("error").GetString()!);
    public static IEnumerable<object[]> Gates => GoldenFiles.Cases(Root, "gates", c => $"{c.GetProperty("gate").GetString()} {c.GetProperty("a")} {c.GetProperty("b")} {c.GetProperty("c")}");
    public static IEnumerable<object[]> Splits => GoldenFiles.Cases(Root, "splits", c => c.GetProperty("text").GetString()!);
    public static IEnumerable<object[]> Starts => GoldenFiles.Cases(Root, "starts", c => c.ToString());
    public static IEnumerable<object[]> Layouts => GoldenFiles.Cases(Root, "layouts", c => c.GetProperty("label").GetString()!);

    [Theory, MemberData(nameof(Bytes))]
    public void FormatBytesMatches(int i, string label) { Assert.NotNull(label); var c = Case("bytes", i); Assert.Equal(c.GetProperty("text").GetString(), ModelFacts.FormatBytes(c.GetProperty("bytes").GetInt64())); }

    static ModelDownloadError ErrorOf(string name, int? status) =>
        status is { } code ? ModelDownloadError.HttpStatus(code) : new ModelDownloadError(Enum.Parse<ModelDownloadErrorKind>(name, ignoreCase: true));

    [Theory, MemberData(nameof(Statuses))]
    public void RowPresentationMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("statuses", i);
        long Long(string n) => c.GetProperty(n).GetInt64();
        ModelStatus? status = c.GetProperty("kind").GetString() switch
        {
            "missing" => null,
            "notDownloaded" => ModelStatus.NotDownloaded,
            "verifying" => ModelStatus.Verifying,
            "ready" => ModelStatus.Ready,
            "downloading" => ModelStatus.Downloading(new(Long("done"), Long("total"))),
            "corrupt" => ModelStatus.Corrupt(c.GetProperty("reason").GetString()!),
            _ => ModelStatus.Failed(ErrorOf(c.GetProperty("error").GetString()!, c.TryGetProperty("errorStatus", out var code) ? code.GetInt32() : null)),
        };
        var row = ModelRowPresentation.Make(status);
        Assert.Equal(c.GetProperty("statusText").GetString(), row.StatusText);
        Assert.Equal(c.GetProperty("actions").EnumerateArray().Select(a => a.GetString()), row.Actions.Select(a => a.ToString().ToLowerInvariant()));
        Assert.Equal(c.TryGetProperty("progress", out var progress) ? progress.GetDouble() : (double?)null, row.Progress);
        Assert.Equal(c.GetProperty("isWorking").GetBoolean(), row.IsWorking);
        Assert.Equal(c.GetProperty("isReady").GetBoolean(), row.IsReady);
    }

    [Theory, MemberData(nameof(Failures))]
    public void FailureMessageMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("failures", i);
        var name = c.GetProperty("error").GetString()!;
        Assert.Equal(c.GetProperty("message").GetString(), ModelFacts.FailureMessage(ErrorOf(name, name == "httpStatus" ? 404 : null)));
    }

    [Theory, MemberData(nameof(Gates))]
    public void GatesMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("gates", i);
        bool a = c.GetProperty("a").GetBoolean(), b = c.GetProperty("b").GetBoolean(), d = c.GetProperty("c").GetBoolean();
        var actual = c.GetProperty("gate").GetString() switch
        {
            "start" => ModelGate.StartRefusal(modelAvailable: a, runtimeAvailable: b, downloadingActiveModel: d),
            "download" => ModelGate.DownloadRefusal(isBusy: a, preview: b, downloading: d),
            _ => ModelGate.RemoveRefusal(isBusy: a, preview: b, downloading: d),
        };
        Assert.Equal(GoldenFiles.Optional(c, "refusal"), actual);
    }

    [Theory, MemberData(nameof(Splits))]
    public void LiveWordsSplitMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("splits", i);
        var (settled, guess) = LiveWordsFacts.Split(c.GetProperty("text").GetString()!, c.GetProperty("limit").GetInt32());
        Assert.Equal(c.GetProperty("settled").GetString(), settled);
        Assert.Equal(c.GetProperty("guess").GetString(), guess);
    }

    [Theory, MemberData(nameof(Starts))]
    public void LiveWordsStartRuleMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("starts", i);
        Assert.Equal(c.GetProperty("starts").GetBoolean(), LiveWordsFacts.Starts(c.GetProperty("enabled").GetBoolean(), c.GetProperty("ready").GetBoolean(), c.GetProperty("cloudTranscription").GetBoolean(), c.GetProperty("onCloud").GetBoolean()));
    }

    /// <summary>Windows says "this PC" where the Mac says "this Mac"; nothing else differs.</summary>
    static string Pc(string s) => s.Replace("this Mac", "this PC");

    static ModelFacts Facts(string? lead, string[] tail, string? notice, string? closing) => new()
    {
        DisplayName = "Example", Repository = "Example/model", ConfirmName = "the example", SummaryLead = lead, SummaryTail = tail,
        Placement = folder => $"Saved at {folder}.", LicenseNotice = notice, Closing = closing, RemoveTitle = "Remove?",
        RemoveBody = (folder, size) => $"Deletes {folder} ({ModelFacts.FormatBytes(size)}).", Completed = "Done.", Removed = "Gone.",
    };

    [Theory, MemberData(nameof(Layouts))]
    public void SummaryAndConfirmationLayoutsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("layouts", i);
        var pinned = new ModelLicenseInfo(true, [new("Pinned model manifest", "Example License", new Uri("https://example.com/license"))], "This license is recorded in Murmur's pinned model manifest.");
        var unpinned = new ModelLicenseInfo(false, [new("Card A", "License A", new Uri("https://a.example/l")), new("Card B", "License B", new Uri("https://b.example/l"))], "Not recorded.");
        var none = new ModelLicenseInfo(false, [], "The license for this model is not recorded in Murmur's pinned model manifest.");
        var (facts, license) = c.GetProperty("label").GetString() switch
        {
            "lead and tail, pinned" => (Facts("Example", ["English"], null, null), pinned),
            "no lead, unpinned" => (Facts(null, [], null, null), unpinned),
            "notice and closing, none" => (Facts("X", ["a", "b"], "Credits: Example.", "Closing paragraph."), none),
            _ => (Facts(null, [], null, null), pinned),
        };
        var size = c.GetProperty("downloadSize").GetInt64();
        var folder = c.GetProperty("folder").GetString()!;
        var (title, body) = facts.Confirmation(size, license, folder);
        Assert.Equal(Pc(c.GetProperty("summary").GetString()!), facts.Summary(size));
        Assert.Equal(c.GetProperty("title").GetString(), title);
        Assert.Equal(c.GetProperty("body").GetString(), body);
        Assert.Equal(c.GetProperty("completed").GetString(), facts.CompletionMessage(null));
        Assert.Equal(c.GetProperty("failed").GetString(), facts.CompletionMessage(ModelDownloadError.Cancelled));
        Assert.Equal(c.GetProperty("removeBody").GetString(), facts.RemoveBody(folder, size));
    }

    [Fact]
    public void LicenseTextMatches()
    {
        var expected = Root.GetProperty("licenseText").EnumerateArray().Select(e => e.GetString()).ToArray();
        var pinned = new ModelLicenseInfo(true, [new("Pinned model manifest", "Example License", new Uri("https://example.com/license"))], "This license is recorded in Murmur's pinned model manifest.");
        var unpinned = new ModelLicenseInfo(false, [new("Card A", "License A", new Uri("https://a.example/l")), new("Card B", "License B", new Uri("https://b.example/l"))], "Not recorded.");
        var none = new ModelLicenseInfo(false, [], "The license for this model is not recorded in Murmur's pinned model manifest.");
        Assert.Equal(expected, new[] { ModelFacts.LicenseText(pinned), ModelFacts.LicenseText(unpinned), ModelFacts.LicenseText(none) });
    }
}
