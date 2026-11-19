// Tests for ModelHelper.cs and WarmRunner.cs (port of the process side of app/ModelHelper.swift). The warm-process tests drive a shell-script stand-in for the helper, so they run on macOS and Linux only; Windows runs the real helper in windows/bench.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class ModelHelperTests
{
    static string Dir(string name) => Directory.CreateDirectory(TempDirs.New(name)).FullName;

    static string ModelFolder(bool complete = true)
    {
        var dir = Dir("model");
        foreach (var f in ModelHelper.RequiredFiles.Take(complete ? 4 : 3)) File.WriteAllText(Path.Combine(dir, f), f);
        return dir;
    }

    static void Executable(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

    /// <summary>A stand-in helper: ready line, then each request line is answered by <paramref name="onRequest"/> (shell).</summary>
    static string FakeHelper(string onRequest)
    {
        var path = Path.Combine(Dir("helper"), "helper.sh");
        File.WriteAllText(path, "#!/bin/sh\necho '{\"ready\":true,\"vocabulary_error\":false}'\nwhile read line; do\n" + onRequest + "\ndone\n");
        Executable(path);
        return path;
    }

    [Fact]
    public void LaunchNeedsTheFourModelFiles()
    {
        Assert.Null(HelperLaunch.Make(ModelFolder(complete: false)));
        Assert.Null(HelperLaunch.Make(Path.Combine(Dir("missing"), "none")));
        Assert.NotNull(HelperLaunch.Make(ModelFolder()));
    }

    [Fact]
    public void LaunchKeyFollowsTheModelFolderAndTheVocabularyContents()
    {
        var models = ModelFolder();
        var vocabulary = Path.Combine(Dir("vocab"), "vocabulary.json");
        var bare = HelperLaunch.Make(models, vocabulary)!;
        Assert.Empty(bare.Arguments); // no file: no dictionary arguments
        File.WriteAllText(vocabulary, "");
        Assert.Empty(HelperLaunch.Make(models, vocabulary)!.Arguments); // an empty file is no vocabulary
        File.WriteAllText(vocabulary, "{\"terms\":[{\"text\":\"Acme\"}]}");
        var one = HelperLaunch.Make(models, vocabulary)!;
        Assert.Equal(["--vocabulary", vocabulary], one.Arguments);
        Assert.NotEqual(bare.Key, one.Key);
        File.WriteAllText(vocabulary, "{\"terms\":[{\"text\":\"Acme\"},{\"text\":\"Zed\"}]}");
        Assert.NotEqual(one.Key, HelperLaunch.Make(models, vocabulary)!.Key);
        Assert.Equal(HelperLaunch.Make(models, vocabulary)!.Key, HelperLaunch.Make(models, vocabulary)!.Key);
    }

    [Fact]
    public void AnswersParseOnlyJsonObjects()
    {
        Assert.Equal("hi", ModelHelper.Answer("{\"text\":\"hi\"}")!["text"]!.GetValue<string>());
        Assert.Null(ModelHelper.Answer("[1]"));
        Assert.Null(ModelHelper.Answer("nonsense"));
        Assert.Null(ModelHelper.Answer(null));
    }

    [Fact]
    public async Task ReadsLinesUpToTheOutputLimit()
    {
        var pending = new List<byte>();
        using var ok = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\n"));
        Assert.Equal("one", await ModelHelper.ReadLineAsync(ok, pending, default));
        Assert.Equal("two", await ModelHelper.ReadLineAsync(ok, pending, default));
        Assert.Null(await ModelHelper.ReadLineAsync(ok, pending, default));
        using var huge = new MemoryStream(new byte[ModelHelper.OutputLimit + 200_000]);
        Assert.Null(await ModelHelper.ReadLineAsync(huge, new List<byte>(), default));
    }

    [Fact]
    public async Task WarmRunnerAnswersRestartsOnAKeyChangeAndRecoversFromATimeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var helper = FakeHelper("case \"$line\" in *slow*) sleep 30;; *) echo \"{\\\"text\\\":\\\"pid $$ $line\\\"}\";; esac");
        var models = ModelFolder();
        using var runner = new WarmRunner(readySeconds: 10, readyWithVocabularySeconds: 10, requestSeconds: 2);
        var launch = HelperLaunch.Make(models)!;
        var first = await runner.TranscribeAsync("a.wav", helper, launch);
        var second = await runner.TranscribeAsync("b.wav", helper, launch);
        Assert.NotNull(first);
        Assert.Equal("pid", first!.Split('"')[3].Split(' ')[0]);
        Assert.Equal(first.Split(' ')[1], second!.Split(' ')[1]); // the same warm process answered both

        File.WriteAllText(Path.Combine(models, "tokens.txt"), "changed"); // a different file size changes the key
        var restarted = await runner.TranscribeAsync("c.wav", helper, HelperLaunch.Make(models)!);
        Assert.NotEqual(first.Split(' ')[1], restarted!.Split(' ')[1]);

        Assert.Null(await runner.TranscribeAsync("slow.wav", helper, HelperLaunch.Make(models)!)); // timed out: helper killed
        Assert.NotNull(await runner.TranscribeAsync("d.wav", helper, HelperLaunch.Make(models)!)); // and restarted on the next request
        Assert.Null(await runner.TranscribeAsync("bad\nline", helper, HelperLaunch.Make(models)!)); // a newline would split the protocol
    }

    [Fact]
    public async Task WarmRunnerRejectsAnOversizedAnswerAndAHelperThatIsNeverReady()
    {
        if (OperatingSystem.IsWindows()) return;
        using var runner = new WarmRunner(readySeconds: 3, readyWithVocabularySeconds: 3, requestSeconds: 10);
        var launch = HelperLaunch.Make(ModelFolder())!;
        Assert.Null(await runner.TranscribeAsync("x.wav", FakeHelper("head -c 5000000 /dev/zero | tr '\\0' 'a'; echo"), launch));
        var silent = Path.Combine(Dir("silent"), "h.sh");
        File.WriteAllText(silent, "#!/bin/sh\nsleep 30\n");
        Executable(silent);
        Assert.Null(await runner.TranscribeAsync("x.wav", silent, launch));
        Assert.Null(await runner.TranscribeAsync("x.wav", Path.Combine(Dir("none"), "nothing"), launch)); // cannot start
    }

    [Fact]
    public async Task OneShotReturnsTheLastLineOrNull()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Dir("once");
        var ok = Path.Combine(dir, "ok.sh");
        File.WriteAllText(ok, "#!/bin/sh\necho '{\"x\":1}'\necho '{\"text\":\"done\"}'\n");
        var bad = Path.Combine(dir, "bad.sh");
        File.WriteAllText(bad, "#!/bin/sh\necho '{\"text\":\"partial\"}'\nexit 1\n");
        foreach (var f in new[] { ok, bad }) Executable(f);
        var launch = HelperLaunch.Make(ModelFolder())!;
        Assert.Equal("done", ModelHelper.Answer(await ModelHelper.RunOnceAsync(ok, launch, "a.wav"))!["text"]!.GetValue<string>());
        Assert.Null(await ModelHelper.RunOnceAsync(bad, launch, "a.wav")); // a failing exit status is a failure whatever it printed
    }
}
