// Port of WarmRunner in app/ModelHelper.swift.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Murmur.Core;

/// <summary>
/// Keeps <c>helper MODELS --serve</c> running with the model loaded, so a dictation skips process launch and model load.
/// One request at a time. Any failure (not ready, died, timed out, canceled) answers null so the caller falls back to the one-shot helper;
/// the warm process restarts on the next request. A changed launch key restarts it.
/// </summary>
public sealed class WarmRunner : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly int readySeconds, readyWithVocabularySeconds, readyRunnerSeconds, requestSeconds;
    Process? process;
    string? key;
    bool requestedGpu, usingGpu;
    readonly List<byte> pending = [];

    /// <summary>Called after each start with vocabulary arguments: true when the helper could not load dictionary support (it then transcribes without it).</summary>
    public Action<bool>? OnDictionaryStatus { get; set; }

    public WarmRunner(int readySeconds = 60, int readyWithVocabularySeconds = 120, int requestSeconds = ModelHelper.RequestTimeoutSeconds, int readyRunnerSeconds = 180)
    {
        this.readySeconds = readySeconds; this.readyWithVocabularySeconds = readyWithVocabularySeconds; this.readyRunnerSeconds = readyRunnerSeconds; this.requestSeconds = requestSeconds;
    }

    /// <summary>Starts (or confirms) the warm helper without a request.</summary>
    public async Task PrepareAsync(string helper, HelperLaunch launch, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await StartAsync(helper, launch, ct).ConfigureAwait(false); } finally { gate.Release(); }
    }

    /// <summary>An audio file (16 kHz mono WAV); the answer line is {"text":…}.</summary>
    public Task<string?> TranscribeAsync(string audio, string helper, HelperLaunch launch, CancellationToken ct = default) => SendAsync(audio, helper, launch, ct);

    /// <summary>16 kHz mono samples held in memory (an undone cancel); the answer is like an audio path's.</summary>
    public Task<string?> TranscribeAsync(float[] samples, string helper, HelperLaunch launch, CancellationToken ct = default)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return SendAsync(new JsonObject { ["samples"] = Convert.ToBase64String(bytes) }.ToJsonString(), helper, launch, ct);
    }

    /// <summary>A meeting's Them chunk with speaker separation on: the answer adds "words".</summary>
    public Task<string?> TranscribeTimedAsync(string audio, string helper, HelperLaunch launch, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["timed"] = audio }.ToJsonString(), helper, launch, ct);

    /// <summary>A clip spanning a chunk cut; the answer lists dictionary replacements, not text.</summary>
    public Task<string?> BoundaryAsync(string audio, string helper, HelperLaunch launch, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["boundary"] = audio }.ToJsonString(), helper, launch, ct);

    async Task<string?> SendAsync(string line, string helper, HelperLaunch launch, CancellationToken ct)
    {
        if (line.Contains('\n') || line.Contains('\r')) return null;
        try { await gate.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return null; }
        try
        {
            while (await StartAsync(helper, launch, ct).ConfigureAwait(false) && process is { } p)
            {
                try
                {
                    await p.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                    await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                    if (await ReadLineAsync(p, requestSeconds, ct).ConfigureAwait(false) is { } answer &&
                        (!usingGpu || ModelHelper.StringField(Parse(answer), "error") != "transcription failed")) return answer;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
                var retry = usingGpu && !ct.IsCancellationRequested;
                StopLocked();
                if (!retry || !await StartAttempt(helper, launch, gpu: false, ct).ConfigureAwait(false)) break;
                Trace.TraceInformation("Murmur speech: GPU request failed. Using CPU.");
            }
            return null;
        }
        finally { gate.Release(); }
    }

    // Called with the gate held.
    async Task<bool> StartAsync(string helper, HelperLaunch launch, CancellationToken ct)
    {
        if (process is { HasExited: false } && key == launch.Key && requestedGpu == launch.PreferGpu) return true;
        StopLocked();
        if (launch.PreferGpu)
        {
            if (await StartAttempt(helper, launch, gpu: true, ct).ConfigureAwait(false)) return true;
            if (ct.IsCancellationRequested) return false;
            Trace.TraceInformation("Murmur speech: GPU initialization failed. Using CPU.");
        }
        return await StartAttempt(helper, launch, gpu: false, ct).ConfigureAwait(false);
    }

    async Task<bool> StartAttempt(string helper, HelperLaunch launch, bool gpu, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        var args = new List<string> { launch.Models, "--serve" };
        args.AddRange(launch.Arguments); // the dictionary arguments, then the runner flag
        if (gpu) args.Add("--gpu");
        var next = new Process { StartInfo = ModelHelper.StartInfo(helper, args, redirectInput: true) };
        try { next.Start(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { next.Dispose(); return false; }
        next.ErrorDataReceived += static (_, _) => { }; // dropped: stderr can hold dictionary terms
        next.BeginErrorReadLine();
        next.StandardInput.NewLine = "\n"; next.StandardInput.AutoFlush = true;
        process = next; key = launch.Key; requestedGpu = launch.PreferGpu; usingGpu = gpu; pending.Clear();
        try
        {
            // A multilingual model (runner flag) may take longer to load than the English one.
            var timeout = launch.RunnerFlag is not null ? readyRunnerSeconds : launch.HasVocabulary ? readyWithVocabularySeconds : readySeconds;
            var ready = Parse(await ReadLineAsync(next, gpu ? Math.Min(timeout, 15) : timeout, ct).ConfigureAwait(false));
            if (ready?["ready"]?.GetValueKind() != JsonValueKind.True) { StopLocked(); return false; }
            if (launch.HasVocabulary) OnDictionaryStatus?.Invoke(ready["vocabulary_error"]?.GetValueKind() == JsonValueKind.True);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { StopLocked(); return false; }
    }

    static JsonObject? Parse(string? line) => ModelHelper.Answer(line);

    /// <summary>Reads one line; a watchdog kills the helper on timeout or cancellation, which ends the read.</summary>
    async Task<string?> ReadLineAsync(Process p, int timeoutSeconds, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { return await ModelHelper.ReadLineAsync(p.StandardOutput.BaseStream, pending, watchdog.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { ModelHelper.Kill(p); throw; }
    }

    void StopLocked()
    {
        if (process is { } p)
        {
            try { p.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
            ModelHelper.Kill(p);
            p.Dispose();
        }
        process = null; key = null; pending.Clear();
    }

    /// <summary>Ends the helper (waits for an in-flight request to finish or time out).</summary>
    public void Stop() { gate.Wait(); try { StopLocked(); } finally { gate.Release(); } }

    public void Dispose() { Stop(); gate.Dispose(); }
}
