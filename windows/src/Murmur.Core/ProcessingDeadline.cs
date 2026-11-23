namespace Murmur.Core;

/// <summary>
/// Bounds on the waits between the end of a recording and its transcript. The Mac has none of these: it reads the focus synchronously at key down and its only
/// time limits are 300 s per helper request (app/ModelHelper.swift readLine(timeout: 300)) and 300 s for the one-shot (app/Controller.swift launchRunner).
/// On Windows the focus probe is UI Automation, which can hang, and the local route chains three of those 300 s limits, so both get a conservative bound here.
/// </summary>
public static class ProcessingDeadline
{
    /// <summary>Longest the pipeline waits for the focus probe and the mode that depends on it. A late answer is ignored (the password check still discards the dictation if it arrives while the session is live).</summary>
    public static readonly TimeSpan Focus = TimeSpan.FromSeconds(3);

    /// <summary>Longest the whole transcription (chunks, the whole-file retry and the one-shot fallback together) may take: 120 s plus twice the clip's length.
    /// At the measured 0.1 real-time factor that is more than ten times the expected time, and a 300 s recording gets 720 s.</summary>
    public static TimeSpan Transcription(double clipSeconds) => TimeSpan.FromSeconds(120 + 2 * Math.Max(0, clipSeconds));

    /// <summary>Waits for <paramref name="task"/> for at most <paramref name="limit"/>; the task's result, or <paramref name="fallback"/> when it is still running (the task itself is not cancelled).</summary>
    public static async Task<T> WaitOr<T>(Task<T> task, TimeSpan limit, T fallback)
    {
        try { return await task.WaitAsync(limit).ConfigureAwait(false); }
        catch (TimeoutException) { return fallback; }
    }

    /// <summary>Runs <paramref name="work"/> with a token that is also cancelled after <paramref name="limit"/>. TimedOut is true only when the limit, not <paramref name="ct"/>, fired; a work that gave up on its token then still returns its result, and one that ignored the token returns the default.</summary>
    public static async Task<(T? Result, bool TimedOut)> RunAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan limit, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        var task = work(deadline.Token);
        var finished = await Task.WhenAny(task, Task.Delay(limit + TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (finished == task) return (await task.ConfigureAwait(false), deadline.IsCancellationRequested && !ct.IsCancellationRequested);
        // The work ignored its token for five seconds past the limit (a blocked pipe write): stop waiting for it.
        return (default, !ct.IsCancellationRequested);
    }
}
