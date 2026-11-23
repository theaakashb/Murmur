// Windows-only: meeting notes written on this PC by Gemma 3 4B (Q4_K_M GGUF) through the bundled Murmur.Cleanup helper's --notes mode (LLamaSharp: Vulkan when
// the GPU can offload, else CPU). 8,192 tokens of context, greedy decoding, 1,200 reply tokens: the settings of the notes benchmark
// (windows/bench/notes/NOTES_MODEL_BENCHMARK.md). No network is used for inference and no transcript is logged.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Murmur.Core;

public static class LocalNotesIdentity
{
    public const string ModelName = "Gemma 3 4B";
    /// <summary>Settings › Meetings › Meeting notes.</summary>
    public const string RouteLabel = "On this PC (Gemma 3 4B)";
    /// <summary>The folder's name before <c>-&lt;rev7&gt;</c> under <c>&lt;data root&gt;\Models</c>.</summary>
    public const string FolderName = "gemma-3-4b-it";
    public const string Repository = "ggml-org/gemma-3-4b-it-GGUF";
    public const string File = "gemma-3-4b-it-Q4_K_M.gguf";
    /// <summary>Notes as written by this route say where they came from: the route name stored with them.</summary>
    public const string NotesRouteName = "Gemma 3 4B (on this PC)";
    public const string GemmaTerms = "https://ai.google.dev/gemma/terms";
    public const string GemmaProhibitedUse = "https://ai.google.dev/gemma/prohibited_use_policy";
}

/// <summary>The model's download facts, shown in Settings › Meetings and confirmed before any byte is downloaded. The Gemma terms are not a standard open licence, so the
/// confirmation carries their notice and use restrictions in place of the pinned-licence line.</summary>
public sealed partial record ModelFacts
{
    public static readonly ModelFacts LocalNotes = new()
    {
        DisplayName = "Meeting notes model",
        RowTitle = "Gemma 3 4B (meeting notes)",
        Repository = LocalNotesIdentity.Repository,
        Source = ModelPinsSource.WindowsModels,
        FolderName = LocalNotesIdentity.FolderName,
        ConfirmName = "Gemma 3 4B",
        SummaryLead = "Gemma 3 4B",
        SummaryTail = ["English", "Gemma terms"],
        Placement = SavedIn,
        LicenseNotice = "Gemma is provided under and subject to the Gemma Terms of Use: " + LocalNotesIdentity.GemmaTerms + "\n"
            + "Use restrictions (Gemma Prohibited Use Policy): " + LocalNotesIdentity.GemmaProhibitedUse + "\n"
            + "Murmur does not change the weights. By downloading you agree to those terms; they also apply to anyone you share the model with.",
        Closing = null,
        ClosingSource = () => NotesGpu.ClosingNotice(),
        RemoveTitle = "Remove the meeting notes model?",
        RemoveBody = (folder, _) => $"Notes on this PC stop until you download it again. This deletes {folder}.",
        Completed = "The model is downloaded and verified.",
        Removed = "The meeting notes model was removed.",
        Explanation = "Writes meeting notes on this PC, so the transcript never leaves it. A long meeting takes a while without a compatible GPU.",
    };
}

/// <summary>Whether a GPU can run the notes model, and what to say when none can. The check is the one behind "Use GPU when available": the helper's Vulkan backend loads
/// and reports it can offload layers (<c>Murmur.Cleanup.exe --probe</c>). It runs once per launch, off the UI thread.</summary>
public static class NotesGpu
{
    /// <summary>The exact warning (the Mac's sentence case, ellipsis and punctuation). The measured figure: a 5900X CPU took about 8 to 9 minutes for a 30-minute meeting; slower laptops take longer.</summary>
    public const string NoGpuWarning = "No compatible GPU found. Local meeting notes may take 10 minutes or more for a 30-minute meeting. A Custom Provider, or a PC with a compatible GPU, is much faster.";

    static Task<bool>? probe;
    static readonly object sync = new();

    /// <summary>Replaces the check (the app starts the real one at launch; tests inject a result).</summary>
    public static Func<CancellationToken, Task<bool>> Probe { get; set; } = RunHelperProbe;

    /// <summary>Starts the check if it has not started, and returns it.</summary>
    public static Task<bool> Available() { lock (sync) return probe ??= Task.Run(() => Probe(CancellationToken.None)); }

    /// <summary>Forgets the result (a test, or the user installed a driver and pressed Check again).</summary>
    public static void Reset() { lock (sync) probe = null; }

    /// <summary>The result if the check has finished, else null. Never blocks.</summary>
    public static bool? Known { get { lock (sync) return probe is { IsCompletedSuccessfully: true } done ? done.Result : null; } }

    /// <summary>The warning to show, or null when a GPU can run the model. A check that failed or has not finished shows nothing: the warning is for a known absence.</summary>
    public static string? Warning(bool? gpuAvailable) => gpuAvailable == false ? NoGpuWarning : null;

    /// <summary>The paragraph the download confirmation ends with. Waits briefly for the check (it normally finished at launch).</summary>
    public static string? ClosingNotice()
    {
        var pending = Available();
        try { pending.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        return Warning(Known);
    }

    static async Task<bool> RunHelperProbe(CancellationToken cancellationToken)
    {
        var path = LocalCleanupRunner.HelperPath;
        if (!System.IO.File.Exists(path)) return false;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
        start.ArgumentList.Add("--probe");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return ModelHelper.Answer(line)?["gpu"]?.GetValueKind() == JsonValueKind.True;
            }
            catch (OperationCanceledException) { ModelHelperKill(process); return false; }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    static void ModelHelperKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}

/// <summary>Where a notes run stands: finished steps out of the planned ones (an extraction per chunk, then merges), and an estimate of what is left.</summary>
public sealed record NotesProgress(int Completed, int Planned, bool OnGpu, double ElapsedSeconds, double? InitialEstimateSeconds = null)
{
    /// <summary>Measured on the benchmark's Ryzen 9 5900X: about 8.5 minutes of CPU per 30 minutes of meeting (Gemma 3 4B, 12 threads). Slower laptops are slower.</summary>
    public const double CpuSecondsPerMeetingMinute = 17;
    /// <summary>Vulkan on the benchmark's RTX 3080: under a minute for 30 minutes.</summary>
    public const double GpuSecondsPerMeetingMinute = 2;

    public static double EstimateSeconds(double meetingMinutes, bool onGpu) => Math.Max(0, meetingMinutes) * (onGpu ? GpuSecondsPerMeetingMinute : CpuSecondsPerMeetingMinute);

    /// <summary>Seconds left: the pace of the finished steps, or the meeting-length estimate before the first one finishes.</summary>
    public double? RemainingSeconds =>
        Completed >= 1 && Planned >= Completed ? ElapsedSeconds / Completed * (Planned - Completed)
        : InitialEstimateSeconds is { } initial ? Math.Max(0, initial - ElapsedSeconds) : null;

    /// <summary>"about 6 minutes left", "about a minute left", "less than a minute left".</summary>
    public static string Remaining(double seconds) => seconds < 45 ? "less than a minute left"
        : Math.Round(seconds / 60, MidpointRounding.AwayFromZero) <= 1 ? "about a minute left"
        : string.Create(CultureInfo.InvariantCulture, $"about {(long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)} minutes left");

    /// <summary>The line under "Writing notes…". On CPU it adds the steps and the estimate; on a GPU the wait is short, so only the steps.</summary>
    public string Text
    {
        get
        {
            var steps = Completed == 0 ? "Starting…" : string.Create(CultureInfo.InvariantCulture, $"{Completed} of {Planned} steps done");
            if (OnGpu) return Completed == 0 ? "Writing notes…" : steps;
            return RemainingSeconds is { } left ? $"{steps} · {Remaining(left)}" : steps;
        }
    }
}

/// <summary>One helper process for one notes write: starts it, answers each request in turn and ends it. Failures are <see cref="ProviderException"/>s the writer already knows:
/// a prompt that leaves no room → PayloadTooLarge, a reply that stopped at the cap → OutputTruncated.</summary>
public sealed class LocalNotesEngine : IDisposable
{
    readonly string helper, folder;
    readonly bool preferGpu;
    readonly TimeSpan startupTimeout, requestTimeout;
    Process? process;
    int nextId = 1;
    public bool OnGpu { get; private set; }

    public LocalNotesEngine(string helper, string folder, bool preferGpu, double startupSeconds = 120, double requestSeconds = 1800)
    {
        this.helper = helper; this.folder = folder; this.preferGpu = preferGpu;
        startupTimeout = TimeSpan.FromSeconds(startupSeconds); requestTimeout = TimeSpan.FromSeconds(requestSeconds);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var id = ProviderID.LocalModel;
        if (!File.Exists(helper)) throw new ProviderException(ProviderError.ModelUnavailable(id, OnDeviceModelUnavailable.ModelNotDownloaded));
        for (var gpu = preferGpu; ; gpu = false)
        {
            if (await Launch(gpu, cancellationToken).ConfigureAwait(false)) { OnGpu = gpu; return; }
            if (!gpu) throw new ProviderException(ProviderError.ModelUnavailable(id, OnDeviceModelUnavailable.ModelDamaged));
            Trace.TraceInformation("Murmur meeting notes: GPU initialization failed. Using CPU.");
        }
    }

    async Task<bool> Launch(bool gpu, CancellationToken cancellationToken)
    {
        var start = ModelHelper.StartInfo(helper, gpu ? [folder, "--notes", "--gpu"] : [folder, "--notes"], redirectInput: true);
        start.Environment["HF_HUB_OFFLINE"] = "1";
        Process next;
        try { next = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
        next.ErrorDataReceived += static (_, _) => { };   // diagnostics are dropped, never logged; draining keeps the helper from blocking on a full pipe
        next.BeginErrorReadLine();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(startupTimeout);
            var line = await next.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (ModelHelper.Answer(line)?["ready"]?.GetValueKind() == JsonValueKind.True) { process = next; return true; }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException) { cancellationToken.ThrowIfCancellationRequested(); }
        Kill(next);
        next.Dispose();
        return false;
    }

    public async Task<string> GenerateAsync(CleanupMessages messages, CancellationToken cancellationToken)
    {
        var id = ProviderID.LocalModel;
        if (process is not { } running) throw new ProviderException(ProviderError.ModelUnavailable(id, OnDeviceModelUnavailable.ModelNotDownloaded));
        int requestId = nextId++;
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = requestId, ["system"] = messages.System, ["user"] = messages.User });
        using var kill = cancellationToken.Register(() => Kill(running));
        string? line;
        try
        {
            await running.StandardInput.WriteLineAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
            await running.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            line = await running.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Kill(running);
            if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
            throw new ProviderException(ProviderError.Timeout(id));
        }
        var answer = ModelHelper.Answer(line);
        if (answer is null || answer["id"]?.GetValue<int>() != requestId) { Kill(running); throw new ProviderException(ProviderError.MalformedResponse(id)); }
        if (ModelHelper.StringField(answer, "text") is { } text) return text;
        throw new ProviderException(ModelHelper.StringField(answer, "error") switch
        {
            "too-long" => ProviderError.PayloadTooLarge(id, null),
            "output-cap" => ProviderError.OutputTruncated(id),
            _ => ProviderError.MalformedResponse(id),
        });
    }

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }

    public void Dispose()
    {
        if (process is { } p) { try { p.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException) { } if (!p.WaitForExit(2000)) Kill(p); p.Dispose(); process = null; }
    }
}
