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
    public async Task LiveWordsGpuStartupFailurePreservesQueuedAudioForCpu()
    {
        if (OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(Dir("live-fallback"), "helper.sh");
        File.WriteAllText(helper, """
            #!/bin/sh
            if [ "$4" = "--gpu" ]; then exit 1; fi
            echo '{"ready":true}'
            bytes=$(wc -c | tr -d ' ')
            printf '{"partial":"%s"}\n' "$bytes"
            """);
        Executable(helper);
        var samples = System.Threading.Channels.Channel.CreateUnbounded<float[]>();
        Assert.True(samples.Writer.TryWrite([1, 2, 3]));
        samples.Writer.Complete();
        var partials = new List<string>();
        await ModelHelper.StreamAsync(helper, ModelFolder(), samples.Reader, partials.Add, default, preferGpu: true);
        Assert.Equal(["12"], partials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmSpeechRetriesGpuFailureOnCpu(bool failAfterReady)
    {
        if (OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(Dir("warm-fallback"), "helper.sh");
        var gpu = failAfterReady ? "echo '{\"ready\":true}'; read line; exit 1" : "exit 1";
        File.WriteAllText(helper, $$"""
            #!/bin/sh
            if [ "$3" = "--gpu" ]; then {{gpu}}; fi
            echo '{"ready":true}'
            while read line; do
                echo '{"text":"cpu"}'
            done
            """);
        Executable(helper);
        var launch = HelperLaunch.Make(ModelFolder(), preferGpu: true)!;
        using var runner = new WarmRunner(readySeconds: 10, requestSeconds: 10);
        var answer = await runner.TranscribeAsync("synthetic.wav", helper, launch);
        Assert.Equal("cpu", ModelHelper.StringField(ModelHelper.Answer(answer), "text"));
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

    /// <summary>A helper that answers ready, reads one request and then never answers, on any OS.</summary>
    static string HungHelper()
    {
        if (!OperatingSystem.IsWindows()) return FakeHelper("sleep 60");
        var path = Path.Combine(Dir("helper"), "hung.cmd");
        File.WriteAllText(path, "@echo off\r\necho {\"ready\":true}\r\nset /p line=\r\nping -n 60 127.0.0.1 >nul\r\n");
        return path;
    }

    // Stop() runs on the UI thread (Release from a model row, Controller.Dispose at exit). It used to wait for the in-flight request's own timeout (300 s in the app).
    [Fact]
    public async Task StopEndsAnInFlightRequestAtOnceInsteadOfWaitingForItsTimeout()
    {
        var helper = HungHelper();
        using var runner = new WarmRunner(readySeconds: 20, readyWithVocabularySeconds: 20, requestSeconds: 45);
        var launch = HelperLaunch.Make(ModelFolder())!;
        await runner.PrepareAsync(helper, launch);
        var request = runner.TranscribeAsync(new float[16000], helper, launch);
        await Task.Delay(1000); // the helper has the request and is not answering
        var clock = System.Diagnostics.Stopwatch.StartNew();
        runner.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Stop took {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // The reported shape: 81.8 s of 48 kHz stereo float32, downmixed and resampled, cut into chunks, the helper stuck on the first chunk. The user then quits or releases the model: Stop runs on the UI thread.
    [Fact]
    public async Task StopReturnsAtOnceWhileAnEightyTwoSecondRecordingsFirstChunkIsStuckInTheHelper()
    {
        const int rate = 48000; const double seconds = 81.8;
        var stereo = new float[(int)(rate * seconds) * 2];
        for (int i = 0; i < stereo.Length / 2; i++) { var v = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / rate) * (i / 24000 % 2 == 0 ? 1 : 0.01)); stereo[2 * i] = v; stereo[2 * i + 1] = v * 0.9f; }
        var mono16 = new float[stereo.Length / 2 / 3]; // mean of the channels, one sample in three
        for (int i = 0; i < mono16.Length; i++) mono16[i] = (stereo[6 * i] + stereo[6 * i + 1]) / 2;

        var helper = HungHelper();
        using var runner = new WarmRunner(readySeconds: 20, readyWithVocabularySeconds: 20, requestSeconds: 45);
        var launch = HelperLaunch.Make(ModelFolder())!;
        await runner.PrepareAsync(helper, launch);
        var job = new ChunkJob((samples, boundary, ct) => boundary ? Task.FromResult<string?>(null) : runner.TranscribeAsync(samples, helper, launch, ct), CancellationToken.None);
        var chunker = new Chunker(job.Enqueue, job.EnqueueBoundary);
        for (int i = 0; i < mono16.Length; i += 320) chunker.Append(mono16[i..Math.Min(i + 320, mono16.Length)]);
        chunker.Finish(); job.Finish();
        await Task.Delay(1000); // the first 20 s chunk is with the helper, which never answers

        var clock = System.Diagnostics.Stopwatch.StartNew();
        runner.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Stop took {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Null(await job.Result.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
