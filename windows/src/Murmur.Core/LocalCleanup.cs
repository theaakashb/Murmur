// Port of app/LocalCleanup.swift and app/LocalCleanupFacts.swift.
// Managed local cleanup: the Vedu Scribe 0.8B model (Apache-2.0) run by the bundled Murmur.Cleanup helper over stdin/stdout. No network is used for
// inference and no transcript is logged. The model has one fixed prompt, so this provider reads the typed CleanupInput and ignores the built prompt text:
// screen context, custom instructions and rewrite presets are not supported on this route (ProcessingCoordinator never sends them).
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Murmur.Core;

/// <summary>What one managed local cleanup model is and what it can do. The Mac replaced Vedu Scribe 0.8B with Murmur Cleanup 2B (which also takes tone, rewrite presets and screen
/// context as header lines, and is trusted like Murmur Cloud); the Windows GGUF of it is not published yet. Replacing the model is: add a profile, set
/// <see cref="LocalCleanupModels.Current"/>, pin the GGUF in windows/shared/windows-models.json and list the old folder in <see cref="ModelFacts.Retired"/> (windows/CLEANUP-MODEL-SWAP.md).</summary>
public sealed record LocalCleanupModel
{
    public required string Name { get; init; }
    /// <summary>Recorded in History: the pinned repository and the first 7 characters of its revision.</summary>
    public required string ModelId { get; init; }
    /// <summary>The Hugging Face repository the pinned files come from (windows/shared/windows-models.json).</summary>
    public required string Repository { get; init; }
    /// <summary>The folder's name before <c>-&lt;rev7&gt;</c> under <c>&lt;data root&gt;\Models</c>.</summary>
    public required string FolderName { get; init; }
    /// <summary>Folder names of the models this one replaced; the next download deletes them.</summary>
    public IReadOnlyList<string> RetiredFolderNames { get; init; } = [];
    /// <summary>Polish, Professional and Natural run as model passes on the Local model route (Mac: runsModelLevels). Without it those need Murmur Cloud or a Custom Provider.</summary>
    public bool RunsModelLevels { get; init; }
    /// <summary>The model has a formal tone header (<c>[tone: formal]</c>), so Professional runs on it.</summary>
    public bool RunsProfessional { get; init; }
    /// <summary>The model takes screen text (<c>[screen: ...]</c>) and rewrite presets (<c>[mode: ...]</c>); without it both fall back as before.</summary>
    public bool TakesContextAndPresets { get; init; }
    /// <summary>Polish and Professional are held only to malfunctions, as on Murmur Cloud; otherwise the full guard (numbers, names, negations) applies.</summary>
    public bool TrustedLikeCloud { get; init; }
    /// <summary>The longest dictation (in characters) one request takes.</summary>
    public int MaximumCharacters { get; init; } = 4_000;
}

public static class LocalCleanupModels
{
    /// <summary>The model Windows ships: one fixed prompt, Natural only, held to the full guard. The GGUF is the same weights as the Mac's earlier model, so the id is the Mac's.</summary>
    public static readonly LocalCleanupModel VeduScribe08B = new()
    {
        Name = "Vedu Scribe 0.8B", ModelId = "heyvedu/vedu-scribe-0.8b@7dced58",
        Repository = "clouddevaakash/vedu-scribe-0.8b-gguf", FolderName = "vedu-scribe-0.8b",
    };

    /// <summary>The model this build installs and runs. To replace it, change this line (windows/CLEANUP-MODEL-SWAP.md).</summary>
    public static readonly LocalCleanupModel Installed = VeduScribe08B;

    static readonly AsyncLocal<LocalCleanupModel?> scoped = new();
    /// <summary>The model in effect: <see cref="Installed"/>, or the one a test put in scope with <see cref="Use"/>.</summary>
    public static LocalCleanupModel Current => scoped.Value ?? TestDefault ?? Installed;
    /// <summary>Set once by the test assembly: the Mac's model, so the tests ported from the Mac and its goldens compare like for like. Never set by the app.</summary>
    internal static LocalCleanupModel? TestDefault { get; set; }

    /// <summary>Test seam: runs code as if <paramref name="model"/> were installed, for this async flow only (tests run in parallel).</summary>
    public static IDisposable Use(LocalCleanupModel model)
    {
        var previous = scoped.Value; scoped.Value = model;
        return new Scope(() => scoped.Value = previous);
    }
    sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}

public static class LocalCleanupIdentity
{
    public static string ModelName => LocalCleanupModels.Current.Name;
    /// <summary>Murmur.Cleanup.exe, in Cleanup\ beside Murmur.exe (the Mac's helper is LocalCleanupRunner).</summary>
    public const string HelperName = "Murmur.Cleanup.exe";
    /// <summary>Recorded in History: the pinned repository and the first 7 characters of its revision (artifact-pins.json).</summary>
    public static string ModelID => LocalCleanupModels.Current.ModelId;
    /// <summary>The folder's name before <c>-&lt;rev7&gt;</c> under <c>&lt;data root&gt;\Models</c>.</summary>
    public static string FolderName => LocalCleanupModels.Current.FolderName;
}

public sealed record LocalCleanupRequest(string Text, string Tone, IReadOnlyList<string> Terms)
{
    /// <summary>"email", "notes" or "prompt" for a rewrite preset; empty for cleanup.</summary>
    public string Mode { get; init; } = "";
    /// <summary>Screen context as one line (<see cref="LocalCleanup.ScreenLine"/>); empty for none or for a preset.</summary>
    public string Screen { get; init; } = "";
    public bool Equals(LocalCleanupRequest? other) => other is not null && Text == other.Text && Tone == other.Tone && Terms.SequenceEqual(other.Terms) && Mode == other.Mode && Screen == other.Screen;
    public override int GetHashCode() => HashCode.Combine(Text, Tone, Terms.Count, Mode, Screen);
}

public static class LocalCleanup
{
    /// <summary>Selected, clipboard and window text in that order, whitespace collapsed, "[" "]" written "(" ")", at most 400 characters: the <c>[screen:]</c> line the model was trained on (MurmurModel <c>common.screen_text</c>).</summary>
    public static string ScreenLine(ScreenContext? context)
    {
        if (context is null) return "";
        var parts = new[] { context.Selected, context.Clipboard, context.Window }.Select(t => string.Join(" ", (t ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(t => t.Length > 0);
        var line = string.Join(" | ", parts).Replace('[', '(').Replace(']', ')');
        return SwiftText.Prefix(line, 400);
    }
}

/// <summary>The managed local cleanup model's download facts (the provider and helper client are below).</summary>
public sealed partial record ModelFacts
{
    /// <summary>The current local cleanup model's GGUF, pinned in windows/shared/windows-models.json.</summary>
    public static readonly ModelFacts LocalCleanup = new()
    {
        DisplayName = LocalCleanupIdentity.ModelName,
        Repository = LocalCleanupModels.Current.Repository,
        Source = ModelPinsSource.WindowsModels,
        FolderName = LocalCleanupIdentity.FolderName,
        Retired = LocalCleanupModels.Current.RetiredFolderNames,
        ConfirmName = LocalCleanupIdentity.ModelName,
        SummaryLead = LocalCleanupIdentity.ModelName,
        SummaryTail = ["English", "Apache-2.0"],
        Placement = _ => "Cleanup then runs on this PC without the network.",
        RemoveTitle = "Remove the local cleanup model?",
        RemoveBody = (_, _) => "Cleanup falls back to Local rules until you download it again.",
        Completed = "The model is downloaded and verified.",
        Removed = "The local cleanup model was removed.",
    };
}

public sealed record LocalModelCleanupProvider : ICleanupProvider
{
    public ProviderID Id => ProviderID.LocalModel;
    /// <summary>Null when the model and helper are installed.</summary>
    public Func<OnDeviceModelUnavailable?> StatusSource { get; init; } = LocalCleanupRunner.InstalledStatus;
    public Func<LocalCleanupRequest, CancellationToken, Task<string>> Generate { get; init; } = (request, token) => LocalCleanupRunner.Shared.CleanAsync(request, token);
    public OnDeviceModelUnavailable? Status() => StatusSource();
    public string? ModelID => LocalCleanupIdentity.ModelID;
    /// <summary>One fixed prompt: a model that takes screen context and presets (<see cref="LocalCleanupModel.TakesContextAndPresets"/>) gets them as header lines; style notes are never followed. Otherwise rewrite presets fall back to local rules.</summary>
    public CleanupShape Shape => new() { TakesContext = LocalCleanupModels.Current.TakesContextAndPresets, SectionTarget = 150, KeepsCorrectionsAttached = true, TakesPresets = LocalCleanupModels.Current.TakesContextAndPresets };
    public ICleanupProvider ForMeetingNotes() => this;
    // The wording is the Mac's: windows/golden/data/routes.json holds this text and the route tests compare it.
    public DataDisclosure Disclosure => new(Id, ProcessingStage.Cleanup, $"{LocalCleanupIdentity.ModelName}, downloaded to this Mac", [], ["the transcript"], "");

    public async Task<string> CleanupAsync(CleanupMessages messages, CancellationToken cancellationToken = default)
    {
        var model = LocalCleanupModels.Current;
        if (messages.Preset is { } preset && model.TakesContextAndPresets)
        {
            if (Status() is { } unavailable) throw new ProviderException(ProviderError.ModelUnavailable(Id, unavailable));
            if (SwiftText.Count(preset.Transcript) > model.MaximumCharacters) throw new ProviderException(ProviderError.PayloadTooLarge(Id, null));
            var mode = preset.Kind switch { ModeTransform.AiPrompt => "prompt", ModeTransform.Email => "email", _ => "notes" };
            return await Generate(new LocalCleanupRequest(preset.Transcript, "casual", preset.Terms.Take(12).ToArray()) { Mode = mode }, cancellationToken).ConfigureAwait(false);
        }
        // Meeting notes and voice edits (and presets, for a model that cannot take them) carry no typed input: this model cannot follow them, so they are refused, not approximated.
        var input = messages.Input ?? throw new ProviderException(ProviderError.InvalidConfiguration(Id));
        if (Status() is { } reason) throw new ProviderException(ProviderError.ModelUnavailable(Id, reason));
        if (input.Raw.Length > model.MaximumCharacters && SwiftText.Count(input.Raw) > model.MaximumCharacters)
            throw new ProviderException(ProviderError.PayloadTooLarge(Id, null));
        var tone = input.Options.Professional && model.RunsProfessional ? "formal" : "casual";
        var screen = model.TakesContextAndPresets ? LocalCleanup.ScreenLine(input.Context) : "";
        return await Generate(new LocalCleanupRequest(input.Raw, tone, input.Terms.Take(12).ToArray()) { Screen = screen }, cancellationToken).ConfigureAwait(false);
    }

    public Task ValidateAsync(CancellationToken cancellationToken = default) =>
        Status() is { } reason ? Task.FromException(new ProviderException(ProviderError.ModelUnavailable(Id, reason))) : Task.CompletedTask;

    /// <summary>Loads the model ahead of the first request (route selected, dictation started), so that cost is not inside a dictation.</summary>
    public void Prewarm() { if (Status() is null) _ = Task.Run(() => LocalCleanupRunner.Shared.WarmAsync()); }
}

/// <summary>One helper process, one request at a time, a fresh chat per request inside it. Cancelling or timing out ends only this child; the next request starts
/// a new one. It also exits when idle, on <see cref="MemoryPressure"/> and at <see cref="Shutdown"/>.
/// The protocol is Murmur.Cleanup's, one JSON object per line: the helper prints <c>{"ready":true}</c> first; <c>{"id","text","tone","terms","mode","screen"}</c> is answered by
/// <c>{"id","text"}</c> or <c>{"id","error":"too-long"|"failed"}</c>. The helper is started as <c>Murmur.Cleanup.exe &lt;model folder&gt;</c>.</summary>
public sealed class LocalCleanupRunner
{
    public static LocalCleanupRunner Shared { get; } = new();

    /// <summary>The cleanup helper; Cleanup\Murmur.Cleanup.exe beside Murmur.exe unless the app sets it.</summary>
    public static string HelperPath { get; set; } = BundledModels.CleanupHelperPath;

    static string? modelDirectory;
    /// <summary>The model folder, <c>&lt;data root&gt;\Models\vedu-scribe-0.8b-&lt;rev7&gt;</c> unless the app sets it.</summary>
    public static string ModelDirectory
    {
        get => modelDirectory ??= PinnedManifest is { } manifest ? ModelFacts.LocalCleanup.Directory(MurmurDataLocations.Root, manifest) : Path.Combine(ModelFacts.ModelsRoot(MurmurDataLocations.Root), LocalCleanupIdentity.FolderName);
        set => modelDirectory = value;
    }

    /// <summary>Full pinned-checksum check of the installed files, set by the app (for example <c>() =&gt; ModelVerificationCache.Shared.Verify(...)</c> wrapped to return false on <see cref="RetentionError"/>); null skips it.</summary>
    public static Func<bool>? Verifier { get; set; }

    /// <summary>Set by the app so new helper processes use the current settings.</summary>
    public Func<bool>? PreferGpu { get; set; }

    static ModelManifest? PinnedManifest
    {
        get
        {
            try { return BundledModels.Pins(ModelFacts.LocalCleanup.Source) is { } pins ? ModelFacts.LocalCleanup.Manifest(pins) : null; }
            catch (ModelDownloadException) { return null; }
        }
    }

    /// <summary>Null when the helper and every pinned model file (by size) are in place; the checksums are verified once per launch, on the first request (<see cref="Verifier"/>).</summary>
    public static OnDeviceModelUnavailable? InstalledStatus()
    {
        if (!File.Exists(HelperPath) || PinnedManifest is not { } manifest) return OnDeviceModelUnavailable.ModelNotDownloaded;
        foreach (var file in manifest.Files)
            if (Digests.FileSize(Path.Combine(ModelDirectory, file.Rfilename.Replace('/', Path.DirectorySeparatorChar))) != file.Size) return OnDeviceModelUnavailable.ModelNotDownloaded;
        return null;
    }

    readonly string? executable, directory;
    readonly TimeSpan startupTimeout, requestTimeout, idleTimeout;
    string Executable => executable ?? HelperPath;
    string Directory => directory ?? ModelDirectory;

    readonly object sync = new();
    Process? process;
    StreamWriter? input;
    int generation;
    readonly Queue<string> lines = new();
    bool exited = true;
    TaskCompletionSource<string?>? waiter;
    bool busy;
    Guid? owner;
    readonly List<(Guid Ticket, TaskCompletionSource<bool> Source)> waiting = [];
    bool verified;
    bool evictWhenFree;
    int nextId = 1;
    CancellationTokenSource? idle;

    /// <summary>Null paths use <see cref="HelperPath"/> and <see cref="ModelDirectory"/> when a request runs, so the app can set them after startup.</summary>
    public LocalCleanupRunner(string? executable = null, string? directory = null, double startupSeconds = 60, double requestSeconds = 30, double idleSeconds = 300)
    {
        this.executable = executable; this.directory = directory;
        startupTimeout = TimeSpan.FromSeconds(startupSeconds); requestTimeout = TimeSpan.FromSeconds(requestSeconds); idleTimeout = TimeSpan.FromSeconds(idleSeconds);
    }

    /// <summary>Throws <see cref="ProviderException"/>: cancelled, <c>ModelUnavailable(ModelDamaged)</c> when the verifier fails, payload too large, timeout, or malformed response (a helper that cannot start or reports "failed").</summary>
    public async Task<string> CleanAsync(LocalCleanupRequest request, CancellationToken cancellationToken = default)
    {
        var id = ProviderID.LocalModel;
        var ticket = Guid.NewGuid();
        // Cancelling a queued request removes only that request; cancelling the active one ends only its own child.
        var acquired = await Acquire(ticket, cancellationToken).ConfigureAwait(false);
        if (!acquired) throw new ProviderException(ProviderError.Cancelled);
        try
        {
            using var registration = cancellationToken.Register(() => Cancel(ticket));
            if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
            CancelIdle();
            if (!verified)
            {
                // Verified once per launch of the app, off the request queue; Shutdown resets it.
                var check = Verifier;
                if (!await Task.Run(() => check?.Invoke() ?? true).ConfigureAwait(false)) throw new ProviderException(ProviderError.ModelUnavailable(id, OnDeviceModelUnavailable.ModelDamaged));
                verified = true;
                if (cancellationToken.IsCancellationRequested) throw new ProviderException(ProviderError.Cancelled);
            }
            var (kind, text) = await RoundTrip(request, cancellationToken).ConfigureAwait(false);
            ScheduleIdle();
            if (cancellationToken.IsCancellationRequested) { Terminate(); throw new ProviderException(ProviderError.Cancelled); }
            return kind switch
            {
                Reply.Text => text!,
                Reply.TooLong => throw new ProviderException(ProviderError.PayloadTooLarge(id, null)),
                Reply.Timeout => throw new ProviderException(ProviderError.Timeout(id)),
                _ => throw new ProviderException(ProviderError.MalformedResponse(id)),
            };
        }
        finally { Release(); }
    }

    /// <summary>Verifies the files and loads the model ahead of the first request, so that cost is not inside a dictation. Does nothing while a request runs or when the model is not installed.</summary>
    public async Task WarmAsync()
    {
        lock (sync) { if (busy) return; }
        if (InstalledStatus() is not null) return;
        var ticket = Guid.NewGuid();
        if (!await Acquire(ticket, CancellationToken.None).ConfigureAwait(false)) return;
        try
        {
            CancelIdle();
            if (!verified)
            {
                var check = Verifier;
                if (!await Task.Run(() => check?.Invoke() ?? true).ConfigureAwait(false)) return;
                verified = true;
            }
            await EnsureReady(CancellationToken.None).ConfigureAwait(false);
            ScheduleIdle();
        }
        finally { Release(); }
    }

    /// <summary>Ends the helper (idle, memory pressure, quit). Safe at any time; an in-flight request fails and falls back.</summary>
    public void Shutdown() { CancelIdle(); verified = false; Terminate(); }

    /// <summary>Memory pressure frees the model now if idle, or as soon as the request in flight finishes. The Mac gets this from a dispatch source; the app calls it when Windows reports low memory.</summary>
    public void MemoryPressure()
    {
        bool idleNow;
        lock (sync) { if (busy) evictWhenFree = true; idleNow = !busy; }
        if (idleNow) Terminate();
    }

    enum Reply { Text, TooLong, Timeout, Failed }

    Task<bool> Acquire(Guid ticket, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromResult(false);
        TaskCompletionSource<bool> source;
        lock (sync)
        {
            if (!busy) { busy = true; owner = ticket; return Task.FromResult(true); }
            source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Add((ticket, source));
        }
        // Registered after queueing: a token that is already cancelled runs Cancel at once and finds the ticket.
        var registration = token.Register(() => Cancel(ticket));
        source.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return source.Task;
    }

    void Release()
    {
        bool evict = false;
        lock (sync)
        {
            if (waiting.Count == 0)
            {
                busy = false; owner = null;
                if (evictWhenFree) { evictWhenFree = false; evict = true; }
            }
            else
            {
                var next = waiting[0]; waiting.RemoveAt(0);
                owner = next.Ticket;
                next.Source.TrySetResult(true);
            }
        }
        if (evict) Terminate();
    }

    void Cancel(Guid ticket)
    {
        TaskCompletionSource<bool>? queued = null;
        var active = false;
        lock (sync)
        {
            if (owner == ticket) active = true;
            else
            {
                var index = waiting.FindIndex(w => w.Ticket == ticket);
                if (index >= 0) { queued = waiting[index].Source; waiting.RemoveAt(index); }
            }
        }
        if (active) Terminate();
        queued?.TrySetResult(false);
    }

    async Task<Reply?> EnsureReady(CancellationToken cancellationToken)
    {
        lock (sync) { if (process is not null) return null; }
        var gpu = PreferGpu?.Invoke() == true;
        var failure = Reply.Failed;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Launch(gpu);
            if (started && !cancellationToken.IsCancellationRequested &&
                ModelHelper.Answer(await NextLine(startupTimeout).ConfigureAwait(false))?["ready"]?.GetValueKind() == JsonValueKind.True)
                return null;
            failure = started ? Reply.Timeout : Reply.Failed;
            Terminate();
            if (!gpu || cancellationToken.IsCancellationRequested) break;
            Trace.TraceInformation("Murmur cleanup: GPU initialization failed. Using CPU.");
            gpu = false;
        }
        return failure;
    }

    async Task<(Reply, string?)> RoundTrip(LocalCleanupRequest request, CancellationToken cancellationToken)
    {
        if (await EnsureReady(cancellationToken).ConfigureAwait(false) is { } startupError) return (startupError, null);
        int id;
        StreamWriter? writer;
        lock (sync) { id = nextId++; writer = input; }
        if (writer is null) { Terminate(); return (Reply.Failed, null); }
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = id, ["text"] = request.Text, ["tone"] = request.Tone, ["terms"] = request.Terms, ["mode"] = request.Mode, ["screen"] = request.Screen });
        try { await writer.WriteLineAsync(body).ConfigureAwait(false); await writer.FlushAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Terminate(); return (Reply.Failed, null); }
        if (await NextLine(requestTimeout).ConfigureAwait(false) is not { } line) { Terminate(); return (Reply.Timeout, null); }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var replied) || replied != id) { Terminate(); return (Reply.Failed, null); }
            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) return (Reply.Text, text.GetString());
            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "too-long" ? (Reply.TooLong, null) : (Reply.Failed, null);
        }
        catch (JsonException) { Terminate(); return (Reply.Failed, null); }
    }

    bool Launch(bool gpu)
    {
        var path = Executable;
        if (!File.Exists(path)) return false;
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(Directory);
        if (gpu) start.ArgumentList.Add("--gpu");
        start.Environment["HF_HUB_OFFLINE"] = "1";
        Process task;
        try { task = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
        task.ErrorDataReceived += (_, _) => { };   // The helper's diagnostics are dropped, never logged; draining keeps it from blocking on a full pipe.
        task.BeginErrorReadLine();
        int mine;
        lock (sync)
        {
            mine = ++generation;
            process = task; input = task.StandardInput; lines.Clear(); exited = false;
        }
        // shortcut: ReadLineAsync buffers a whole line before Deliver can reject an oversized one; a helper this misbehaved is ours, so we accept it.
        _ = Task.Run(async () =>
        {
            try { while (await task.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) Deliver(line, mine); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            Ended(mine);
        });
        return true;
    }

    void Deliver(string line, int mine)
    {
        TaskCompletionSource<string?>? toResume = null;
        var tooLarge = false;
        lock (sync)
        {
            if (mine != generation) return;
            // A reply this large is not ours; stop rather than buffer without bound.
            if (Encoding.UTF8.GetByteCount(line) > 200_000) tooLarge = true;
            else if (waiter is not null) { toResume = waiter; waiter = null; }
            else lines.Enqueue(line);
        }
        if (tooLarge) Terminate();
        toResume?.TrySetResult(line);
    }

    void Ended(int mine)
    {
        TaskCompletionSource<string?>? toResume;
        lock (sync)
        {
            if (mine != generation) return;
            exited = true; process = null; input = null;
            toResume = waiter; waiter = null;
        }
        toResume?.TrySetResult(null);
    }

    async Task<string?> NextLine(TimeSpan timeout)
    {
        TaskCompletionSource<string?> source;
        int mine;
        lock (sync)
        {
            if (lines.Count > 0) return lines.Dequeue();
            if (exited) return null;
            source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = source; mine = generation;
        }
        using var stop = new CancellationTokenSource();
        _ = Task.Delay(timeout, stop.Token).ContinueWith(t => { if (!t.IsCanceled) TimedOut(mine); }, TaskScheduler.Default);
        try { return await source.Task.ConfigureAwait(false); }
        finally { stop.Cancel(); }
    }

    void TimedOut(int mine) { bool current; lock (sync) current = mine == generation; if (current) Terminate(); }

    void Terminate()
    {
        Process? dying;
        TaskCompletionSource<string?>? toResume;
        lock (sync)
        {
            dying = process;
            process = null; input = null; exited = true; generation++; lines.Clear();
            toResume = waiter; waiter = null;
        }
        if (dying is not null)
        {
            try { dying.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _ = Task.Run(() => { try { dying.WaitForExit(2000); } finally { dying.Dispose(); } });
        }
        toResume?.TrySetResult(null);
    }

    void CancelIdle() { lock (sync) { idle?.Cancel(); idle = null; } }

    void ScheduleIdle()
    {
        CancellationTokenSource mine;
        lock (sync) { idle?.Cancel(); idle = mine = new CancellationTokenSource(); }
        _ = Task.Delay(idleTimeout, mine.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            bool free; lock (sync) free = !busy;
            if (free) Terminate();
        }, TaskScheduler.Default);
    }
}
