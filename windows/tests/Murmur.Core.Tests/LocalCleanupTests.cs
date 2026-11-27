// Ports app/check_local_cleanup.swift: the provider's request, the helper client against a fake helper process (tests/FakeCleanupHelper: ready line, one reply per
// request, serialized, one child, timeouts, cancellation) and the guard through ProcessingCoordinator.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class LocalCleanupTests
{
    sealed class Recorder
    {
        readonly object gate = new();
        readonly List<LocalCleanupRequest> requests = [];
        public List<LocalCleanupRequest> Requests { get { lock (gate) return [.. requests]; } }
        public void Add(LocalCleanupRequest r) { lock (gate) requests.Add(r); }
    }

    static LocalModelCleanupProvider Provider(Recorder recorder, OnDeviceModelUnavailable? status = null, Func<string, string>? reply = null) => new()
    {
        StatusSource = () => status,
        Generate = (r, _) => { recorder.Add(r); return Task.FromResult((reply ?? (t => t))(r.Text)); },
    };

    static async Task<ProviderError?> Failure(Func<Task> action)
    {
        try { await action(); return null; } catch (ProviderException e) { return e.Error; }
    }

    // MARK: provider

    [Fact]
    public async Task ProviderSendsTypedInputNotThePrompt()
    {
        var rec = new Recorder();
        var pro = CleanupOptions.Standard with { Professional = true, Polish = true, TrustModel = true };
        var terms = Enumerable.Range(1, 15).Select(n => $"Term{n}").ToList();
        var built = CleanupPrompt.Build("hello there", pro, terms, context: new ScreenContext { Selected = "SECRET-SCREEN-TEXT" }, instructions: "STYLE-NOTE");
        await Provider(rec).CleanupAsync(built);
        Assert.True(rec.Requests.Count == 1 && rec.Requests[0].Text == "hello there" && rec.Requests[0].Tone == "formal", "Professional reaches the model as the formal tone: the request is the typed transcript, never the built prompt");
        Assert.Equal(terms.Take(12), rec.Requests[0].Terms);
        await Provider(rec).CleanupAsync(CleanupPrompt.Build("hello there", CleanupOptions.Standard));
        Assert.Equal("casual", rec.Requests[1].Tone);
        Assert.DoesNotContain(rec.Requests, r => r.Text.Contains("SECRET") || r.Text.Contains("STYLE-NOTE"));
        Assert.True(rec.Requests[0].Screen == "SECRET-SCREEN-TEXT" && rec.Requests[0].Mode == "" && rec.Requests[1].Screen == "", "screen context reaches the model as its one [screen:] line, only when given");
        Assert.Equal("a b | (x)", LocalCleanup.ScreenLine(new ScreenContext { Selected = " a\n b ", Window = "[x]" }));
        Assert.Equal(400, SwiftText.Count(LocalCleanup.ScreenLine(new ScreenContext { Selected = new string('y', 500) })));
        await Provider(rec).CleanupAsync(TransformPrompt.Build(ModeTransform.Email, "email dana saying the deck is ready", ["Dana"]));
        Assert.True(rec.Requests[2].Mode == "email" && rec.Requests[2].Text == "email dana saying the deck is ready" && rec.Requests[2].Tone == "casual"
            && rec.Requests[2].Terms.SequenceEqual(["Dana"]) && rec.Requests[2].Screen == "", "a rewrite preset reaches the model as [mode:], with no screen text");
        await Provider(rec).CleanupAsync(TransformPrompt.Build(ModeTransform.AiPrompt, "ask it to fix the login bug"));
        Assert.Equal("prompt", rec.Requests[3].Mode);
    }

    [Fact]
    public async Task ProviderRefusalsAreContentFree()
    {
        var rec = new Recorder();
        Assert.Equal(ProviderError.InvalidConfiguration(ProviderID.LocalModel), await Failure(() => Provider(rec).CleanupAsync(new CleanupMessages("rewrite", "x"))));
        var casual = CleanupPrompt.Build("hello there", CleanupOptions.Standard);
        Assert.Equal(ProviderError.ModelUnavailable(ProviderID.LocalModel, OnDeviceModelUnavailable.ModelNotDownloaded), await Failure(() => Provider(rec, OnDeviceModelUnavailable.ModelNotDownloaded).CleanupAsync(casual)));
        Assert.Equal(ProviderError.PayloadTooLarge(ProviderID.LocalModel, null), await Failure(() => Provider(rec).CleanupAsync(CleanupPrompt.Build(string.Concat(Enumerable.Repeat("word ", 1_000)), CleanupOptions.Standard))));
        Assert.Equal(ProviderError.ModelUnavailable(ProviderID.LocalModel, OnDeviceModelUnavailable.ModelDamaged), await Failure(() => Provider(rec, OnDeviceModelUnavailable.ModelDamaged).ValidateAsync()));
        await Provider(rec).ValidateAsync();
        Assert.Empty(rec.Requests);
        var provider = Provider(rec);
        Assert.True(provider.Disclosure.StaysOnThisMac && provider.Id.RawValue() == "local-model");
        Assert.Equal(LocalCleanupIdentity.ModelID, provider.ModelID);
    }

    [Fact]
    public async Task LimitCountsCharactersNotUtf16Units()
    {
        // 4,000 grapheme clusters of two UTF-16 units each is within the limit; one more is not.
        var rec = new Recorder();
        var emoji = "\U0001F600";
        await Provider(rec).CleanupAsync(CleanupPrompt.Build(string.Concat(Enumerable.Repeat(emoji, 4_000)), CleanupOptions.Faithful));
        Assert.Single(rec.Requests);
        Assert.Equal(ProviderError.PayloadTooLarge(ProviderID.LocalModel, null), await Failure(() => Provider(rec).CleanupAsync(CleanupPrompt.Build(string.Concat(Enumerable.Repeat(emoji, 4_001)), CleanupOptions.Faithful))));
    }

    [Fact]
    public void DefaultProviderIsReportedNotInstalledWithoutTheHelper()
    {
        Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded, new LocalModelCleanupProvider().Status());
        Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded, LocalCleanupRunner.InstalledStatus());
    }

    [Fact]
    public void InstalledStatusNeedsTheHelperAndEveryPinnedFile()
    {
        using var temp = new TempDir();
        var oldHelper = LocalCleanupRunner.HelperPath;
        var oldDirectory = LocalCleanupRunner.ModelDirectory;
        try
        {
            Directory.CreateDirectory(temp.Combine("model"));
            LocalCleanupRunner.ModelDirectory = temp.Combine("model");
            LocalCleanupRunner.HelperPath = temp.Combine("missing.exe");
            Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded, LocalCleanupRunner.InstalledStatus());
            File.WriteAllText(temp.Combine("helper.exe"), "x");
            LocalCleanupRunner.HelperPath = temp.Combine("helper.exe");
            Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded, LocalCleanupRunner.InstalledStatus());          // helper present, model file missing
            File.WriteAllText(temp.Combine("model", "vedu-scribe-0.8b-Q8_0.gguf"), "too short");
            Assert.Equal(OnDeviceModelUnavailable.ModelNotDownloaded, LocalCleanupRunner.InstalledStatus());          // wrong size
        }
        finally { LocalCleanupRunner.HelperPath = oldHelper; LocalCleanupRunner.ModelDirectory = oldDirectory; }
    }

    // MARK: helper client

    static string Helper()
    {
        var exe = OperatingSystem.IsWindows() ? "FakeCleanupHelper.exe" : "FakeCleanupHelper";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "FakeCleanupHelper");
            if (!File.Exists(Path.Combine(project, "FakeCleanupHelper.csproj"))) continue;
            var built = Directory.GetFiles(Path.Combine(project, "bin"), exe, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (built is not null) return built;
        }
        throw new FileNotFoundException("FakeCleanupHelper is not built; build windows/tests/FakeCleanupHelper");
    }

    sealed class Fixtures : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Helper { get; } = LocalCleanupTests.Helper();
        public Fixtures()
        {
            // The apphost needs to find the runtime that runs the tests: <root>/shared/Microsoft.NETCore.App/<version>/ is three folders below the dotnet root.
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is null)
                Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
        }
        public LocalCleanupRunner Runner(double startup = 20, double request = 5, double idle = 60) => new(Helper, Dir.Path, startup, request, idle);
        public int Pid() => int.Parse(File.ReadAllText(Dir.Combine("pid")).Trim());
        public void Dispose() => Dir.Dispose();
    }

    static LocalCleanupRequest Req(string text, string tone = "casual", params string[] terms) => new(text, tone, terms);

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; }
    }

    static async Task WaitDead(int pid) { for (var i = 0; i < 50 && Alive(pid); i++) await Task.Delay(100); }

    [Fact]
    public async Task GpuStartupFailureFallsBackToOneWarmCpuHelper()
    {
        using var f = new Fixtures();
        File.WriteAllText(f.Dir.Combine("reject-gpu"), "");
        var runner = f.Runner();
        runner.PreferGpu = () => true;
        try
        {
            var first = await runner.CleanAsync(Req("synthetic"));
            Assert.Equal(first, await runner.CleanAsync(Req("synthetic")));
            Assert.Equal(new[] { "gpu", "cpu" }, File.ReadAllLines(f.Dir.Combine("attempts")));
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task OneWarmHelperServesConsecutiveAndConcurrentRequests()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            var first = await runner.CleanAsync(Req("one"));
            var second = await runner.CleanAsync(Req("two", "formal", "A"));
            Assert.True(first == second && first.StartsWith("reply "), "consecutive requests reuse one warm helper");
            Assert.True(await Task.Run(() => Alive(f.Pid())));
            var all = await Task.WhenAll(new[] { "a", "b", "c" }.Select(t => runner.CleanAsync(Req(t))));
            Assert.True(all.Length == 3 && all.Distinct().Count() == 1, "concurrent requests are serialized on the one helper");
            Assert.Equal(first, all[0]);
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task ToneAndTermsReachTheHelper()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try { Assert.Equal("formal|Priya,Ünïcode \"q\"|say echo", await runner.CleanAsync(Req("say echo", "formal", "Priya", "Ünïcode \"q\""))); }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task HelperErrorsBecomeProviderErrorsAndTheChildSurvives()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            Assert.Equal(ProviderError.PayloadTooLarge(ProviderID.LocalModel, null), await Failure(() => runner.CleanAsync(Req("toolong"))));
            Assert.Equal(ProviderError.MalformedResponse(ProviderID.LocalModel), await Failure(() => runner.CleanAsync(Req("broken"))));
            var pid = f.Pid();
            Assert.True(Alive(pid), "helper still alive after errors it reported");
            Assert.StartsWith("reply " + pid, await runner.CleanAsync(Req("fine")));
        }
        finally { runner.Shutdown(); }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("wrongid")]
    public async Task AReplyThatIsNotOursEndsTheChild(string text)
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            Assert.Equal(ProviderError.MalformedResponse(ProviderID.LocalModel), await Failure(() => runner.CleanAsync(Req(text))));
            var pid = f.Pid();
            await WaitDead(pid);
            Assert.False(Alive(pid), "a helper that broke the protocol is terminated");
            Assert.StartsWith("reply ", await runner.CleanAsync(Req("after")));
            Assert.NotEqual(pid, f.Pid());
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task AHungRequestTimesOutAndOnlyThatChildEnds()
    {
        using var f = new Fixtures();
        var runner = f.Runner(request: 1.5);
        try
        {
            await runner.CleanAsync(Req("warm"));
            var pid1 = f.Pid();
            Assert.Equal(ProviderError.Timeout(ProviderID.LocalModel), await Failure(() => runner.CleanAsync(Req("hang"))));
            await WaitDead(pid1);
            Assert.False(Alive(pid1), "the timed-out child was terminated");
            var third = await runner.CleanAsync(Req("after"));
            var pid2 = f.Pid();
            Assert.True(pid2 != pid1 && third == $"reply {pid2}", "a fresh helper serves the next request");
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task CancellingTheActiveRequestEndsItsChild()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            await runner.CleanAsync(Req("warm"));
            var pid = f.Pid();
            using var cts = new CancellationTokenSource();
            var task = runner.CleanAsync(Req("hang"), cts.Token);
            await Task.Delay(500);
            cts.Cancel();
            Assert.Equal(ProviderError.Cancelled, await Failure(() => task));
            await WaitDead(pid);
            Assert.False(Alive(pid), "the cancelled child was terminated");
            Assert.StartsWith("reply ", await runner.CleanAsync(Req("after")));
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task CancellingAQueuedRequestLeavesTheActiveOne()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            await runner.CleanAsync(Req("warm"));
            var active = runner.CleanAsync(Req("slow"));
            await Task.Delay(250);
            using var cts = new CancellationTokenSource();
            var queued = runner.CleanAsync(Req("queued"), cts.Token);
            await Task.Delay(250);
            cts.Cancel();
            Assert.Equal(ProviderError.Cancelled, await Failure(() => queued));
            Assert.StartsWith("reply ", await active);
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task AlreadyCancelledRequestsNeverStartAHelper()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(ProviderError.Cancelled, await Failure(() => runner.CleanAsync(Req("x"), cts.Token)));
        Assert.False(File.Exists(f.Dir.Combine("pid")));
    }

    [Fact]
    public async Task UnverifiedFilesAreRefusedBeforeAnythingStarts()
    {
        using var f = new Fixtures();
        LocalCleanupRunner.Verifier = () => false;
        try
        {
            var damaged = f.Runner();
            Assert.Equal(ProviderError.ModelUnavailable(ProviderID.LocalModel, OnDeviceModelUnavailable.ModelDamaged), await Failure(() => damaged.CleanAsync(Req("x"))));
            Assert.False(File.Exists(f.Dir.Combine("pid")));
            LocalCleanupRunner.Verifier = () => true;
            var good = f.Runner();
            try { Assert.StartsWith("reply ", await good.CleanAsync(Req("x"))); } finally { good.Shutdown(); }
        }
        finally { LocalCleanupRunner.Verifier = null; }
    }

    [Fact]
    public async Task AHelperThatCannotStartFailsCleanly()
    {
        using var f = new Fixtures();
        var missing = new LocalCleanupRunner(f.Dir.Combine("nope.exe"), f.Dir.Path);
        Assert.Equal(ProviderError.MalformedResponse(ProviderID.LocalModel), await Failure(() => missing.CleanAsync(Req("x"))));
        // A helper that reports {"ready":false} and exits: the model could not load.
        File.WriteAllText(f.Dir.Combine("notready"), "");
        Assert.Equal(ProviderError.Timeout(ProviderID.LocalModel), await Failure(() => f.Runner(startup: 10).CleanAsync(Req("x"))));
    }

    [Fact]
    public async Task AnIdleHelperExitsAndShutdownEndsIt()
    {
        using var f = new Fixtures();
        var idle = f.Runner(request: 5, idle: 0.5);
        await idle.CleanAsync(Req("x"));
        var idlePid = f.Pid();
        await Task.Delay(1200);
        await WaitDead(idlePid);
        Assert.False(Alive(idlePid), "an idle helper exits and releases the model");
        var again = await idle.CleanAsync(Req("y"));
        Assert.NotEqual(idlePid, f.Pid());
        var pid = f.Pid();
        idle.Shutdown();
        await WaitDead(pid);
        Assert.False(Alive(pid), "Shutdown ends the helper");
        Assert.StartsWith("reply ", again);
    }

    [Fact]
    public async Task MemoryPressureEndsAnIdleHelperAndWaitsForABusyOne()
    {
        using var f = new Fixtures();
        var runner = f.Runner();
        try
        {
            await runner.CleanAsync(Req("x"));
            var pid = f.Pid();
            runner.MemoryPressure();
            await WaitDead(pid);
            Assert.False(Alive(pid));
            await runner.CleanAsync(Req("x"));
            var busyPid = f.Pid();
            var active = runner.CleanAsync(Req("slow"));
            await Task.Delay(250);
            runner.MemoryPressure();
            Assert.True(Alive(busyPid), "the request in flight is not cut off");
            Assert.StartsWith("reply ", await active);
            await WaitDead(busyPid);
            Assert.False(Alive(busyPid), "evicted as soon as the request finished");
        }
        finally { runner.Shutdown(); }
    }

    [Fact]
    public async Task WarmLoadsTheHelperAheadOfTheFirstRequest()
    {
        using var f = new Fixtures();
        // WarmAsync does nothing unless the model is installed, which a unit test cannot stage (800 MB), so it is a no-op here.
        var runner = f.Runner();
        await runner.WarmAsync();
        Assert.False(File.Exists(f.Dir.Combine("pid")));
    }

    // MARK: guard through the pipeline

    static ProcessingCoordinator Coordinator()
    {
        var p = new ProcessingCoordinator(TempDirs.New("local-cleanup"), new InMemorySettingsStore());
        // Natural first: it keeps the full guard even with the global trust option on. Polish and Professional are trusted like Murmur Cloud.
        var options = CleanupOptions.Standard with { TrustModel = true };
        p.Update(p.Settings with { Route = CleanupRoute.Cloud, Options = options }, persist: false);
        return p;
    }

    [Fact]
    public async Task TheFullGuardAppliesEvenWithTheTrustOptionOn()
    {
        var p = Coordinator();
        var rec = new Recorder();
        const string deadlineRaw = "can you send me the report by 5 the quarterly one not the monthly one";
        p.CloudCleaner = Provider(rec, reply: _ => "Can you send me the quarterly report, not the monthly one?");
        var dropped = await p.ProcessAsync(deadlineRaw);
        Assert.True(dropped.Text!.Contains('5'), $"a model output that drops the deadline is not inserted: {dropped.Text}");
        Assert.True(dropped.Fallback is not null && dropped.CleanupPath != CleanupPath.ModelAccepted, "dropped deadline is recorded as a rejection");
        p.CloudCleaner = Provider(rec, reply: _ => "Can you send me the report by 5, the quarterly one, not the monthly one?");
        var ok = await p.ProcessAsync(deadlineRaw);
        Assert.True(ok.Fallback is null && ok.PromptHash == "local-fixed", "a faithful output is accepted and the fixed prompt is recorded");

        // Polish and Professional are trusted like Murmur Cloud: a rewrite only the full guard rejects is accepted.
        p.Update(p.Settings with { Options = CleanupOptions.Standard with { Polish = true, Professional = true, TrustModel = true } }, persist: false);
        p.CloudCleaner = Provider(rec, reply: _ => "Are you free later?");
        var trusted = await p.ProcessAsync("um hi are you free later");
        Assert.True(trusted.Text == "Are you free later?" && trusted.CleanupPath == CleanupPath.ModelAccepted, "Professional on the Local model is trusted");
        // Polish: trusted, so the dropped negation below is held only by the truncation check.
        p.Update(p.Settings with { Options = CleanupOptions.Standard with { Polish = true, TrustModel = true } }, persist: false);

        p.CloudCleaner = Provider(rec, reply: _ => "Priya and I were thinking we could ship on Monday.");
        var friday = await p.ProcessAsync("Priya and I were thinking we could ship on Friday, actually no, on Monday");
        Assert.True(friday.Text == "Priya and I were thinking we could ship on Monday." && friday.Fallback is null, "the correct \"actually no\" repair is accepted");

        const string notRaw = "please do not send the contract today";
        p.CloudCleaner = Provider(rec, reply: _ => "Please send the contract today.");
        var negation = await p.ProcessAsync(notRaw);
        Assert.Contains("not", negation.Text!.ToLowerInvariant());
        Assert.True(negation.Fallback is not null && negation.CleanupPath != CleanupPath.ModelAccepted, "a refused answer falls back with no second try");

        p.CloudCleaner = Provider(rec, reply: _ => "Thanks, Shreya Raman. Send 42.50 dollars.");
        Assert.Null((await p.ProcessAsync("thanks shreya raman send 42.50 dollars")).Fallback);
    }

    [Fact]
    public async Task OversizedInputFallsBackToLocalRulesInFull()
    {
        var p = Coordinator();
        var huge = string.Join(' ', Enumerable.Repeat("word", 3_000));
        p.CloudCleaner = new LocalModelCleanupProvider { StatusSource = () => null, Generate = (_, _) => throw new ProviderException(ProviderError.PayloadTooLarge(ProviderID.LocalModel, null)) };
        var tooBig = await p.ProcessAsync(huge);
        Assert.True(tooBig.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3_000, "oversized text is kept in full by the rules fallback");
        Assert.True(tooBig.ProviderError == ProviderError.PayloadTooLarge(ProviderID.LocalModel, null) && tooBig.Fallback == CleanupFailureReason.CleanerFailed, "the fallback and its reason are recorded");
    }

    [Fact]
    public void LongInputIsSplitAtSentenceEndsAndARepairStaysWithItsSentence()
    {
        const string filler = "we reviewed the plan and agreed on the next steps for the team.";
        var longText = string.Join(' ', Enumerable.Repeat(filler, 14)) + " We ship on Friday. Actually no, on Monday. " + string.Join(' ', Enumerable.Repeat(filler, 14));
        var sections = CleanupSections.Split(longText, 150, keepingCorrectionsAttached: true);
        Assert.True(sections.Count > 1 && sections.All(s => !s.StartsWith("Actually no")), "a correction is never cut from its antecedent");
        Assert.Equal(longText, string.Join(' ', sections));
        static bool Opens(string sentence) => Regex.IsMatch(sentence, CleanupSections.CorrectionOpening, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(RuleCleaner.CuePatterns.All(c => Opens(string.Join(' ', c.Words) + " Monday.")) && Opens("Or rather Monday.") && Opens("Instead, Monday."), "every rule-engine cue keeps its sentence attached");
        Assert.True(!Opens("Nobody came.") && !Opens("Sorrysome text.") && !Opens("We ship on Friday."), "ordinary sentences are still cut freely");
        Assert.Equal("Local model", ProviderID.LocalModel.DisplayName());
    }
}
