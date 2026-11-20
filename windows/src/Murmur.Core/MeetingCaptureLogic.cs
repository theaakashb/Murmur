// Windows meeting capture logic with no Windows APIs: the silence fill of SystemAudioTap.receive (app/MeetingCapture.swift),
// and the process-tree resolution that replaces AudioProcessList / TapTarget.
namespace Murmur.Core;

/// <summary>
/// Process loopback delivers no packets while the target is silent, so that time is filled with zeros and the Them stream keeps the meeting's sample clock
/// (and so its line times) in step with Me. Times are seconds on one monotonic clock (QPC): packet times come from the device's QPC position.
/// </summary>
public sealed class SilenceFill
{
    public const double Tolerance = 0.2, PieceSeconds = 0.1;
    public static readonly int PieceFrames = (int)(AudioFormat.SampleRate * PieceSeconds);
    double? next;

    public void Begin(double now) => next = now;

    /// <summary>Silence to emit before audio arriving at <paramref name="now"/> (or while nothing arrives), in 0.1 s pieces so the chunker can still cut at its usual length.</summary>
    public int Pieces(double now)
    {
        if (next is not { } expected) return 0;
        int count = 0;
        while (now - expected >= Tolerance) { count++; expected += PieceSeconds; }
        next = expected;
        return count;
    }

    /// <summary>A real packet of <paramref name="frames"/> 16 kHz frames starts at <paramref name="now"/>.</summary>
    public void Audio(double now, int frames) => next = Math.Max(next ?? now, now) + frames / (double)AudioFormat.SampleRate;
}

public sealed record ProcessInfo(int Pid, int ParentPid, string Name);

/// <summary>An audio session: a process with an active capture (input) or render (output) session on some endpoint.</summary>
public sealed record AudioClient(int Pid, bool Input, bool Output);

public abstract record CallTarget
{
    /// <summary>One call app's process tree, matched again at every refresh so a window or helper that starts later is picked up.</summary>
    public sealed record Only(CallApp App) : CallTarget;
    /// <summary>Everything except Murmur's own process tree.</summary>
    public sealed record AllExcept(int OwnPid) : CallTarget;
}

public static class ProcessTargets
{
    static bool Is(ProcessInfo process, CallApp app) => CallApp.Normalize(process.Name) == app.ProcessName;

    /// <summary>The app's top-level processes: those whose parent is not also the app. A tree capture of the root covers its helpers (new Teams plays from msedgewebview2 children of ms-teams).</summary>
    public static IReadOnlyList<int> Roots(IReadOnlyList<ProcessInfo> all, CallApp app)
    {
        var matching = all.Where(process => Is(process, app)).ToArray();
        var pids = matching.Select(process => process.Pid).ToHashSet();
        return matching.Where(process => !pids.Contains(process.ParentPid)).Select(process => process.Pid).Order().ToArray();
    }

    /// <summary>The roots and everything below them.</summary>
    public static IReadOnlySet<int> Tree(IReadOnlyList<ProcessInfo> all, IEnumerable<int> roots)
    {
        var tree = roots.ToHashSet();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var process in all)
                if (!tree.Contains(process.Pid) && tree.Contains(process.ParentPid) && process.Pid != process.ParentPid) { tree.Add(process.Pid); grew = true; }
        }
        return tree;
    }

    /// <summary>One loopback client captures one tree. With several roots (two instances), the one playing now wins, else the lowest pid; a refresh switches when that changes.</summary>
    public static int? Pick(IReadOnlyList<int> roots, IReadOnlyList<ProcessInfo> all, IEnumerable<AudioClient> clients)
    {
        var playing = clients.Where(client => client.Output).Select(client => client.Pid).ToHashSet();
        foreach (var root in roots) if (Tree(all, [root]).Overlaps(playing)) return root;
        return roots.Count == 0 ? null : roots[0];
    }

    public static bool IsPlaying(IReadOnlyList<ProcessInfo> all, CallTarget target, IEnumerable<AudioClient> clients)
    {
        var outputs = clients.Where(client => client.Output).Select(client => client.Pid);
        return target switch
        {
            CallTarget.Only only => Tree(all, Roots(all, only.App)).Overlaps(outputs),
            CallTarget.AllExcept own => outputs.Any(pid => !Tree(all, [own.OwnPid]).Contains(pid)),
            _ => false,
        };
    }

    /// <summary>The catalog app each microphone-using process belongs to, walking up parents, so a webview helper counts for its app. Murmur's own processes are never reported.</summary>
    public static IReadOnlyList<string> InputApps(IReadOnlyList<ProcessInfo> all, IEnumerable<AudioClient> clients, int ownPid)
    {
        var byPid = all.GroupBy(process => process.Pid).ToDictionary(group => group.Key, group => group.First());
        var own = Tree(all, [ownPid]);
        var names = new HashSet<string>();
        foreach (var client in clients.Where(client => client.Input && !own.Contains(client.Pid)))
        {
            var seen = new HashSet<int>();
            for (var current = byPid.GetValueOrDefault(client.Pid); current is not null && seen.Add(current.Pid); current = byPid.GetValueOrDefault(current.ParentPid))
                if (CallApp.Find(current.Name) is { } app) { names.Add(app.ProcessName); break; }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Whether the app (or a helper below it) has a microphone session now.</summary>
    public static bool UsesMicrophone(IReadOnlyList<ProcessInfo> all, IEnumerable<AudioClient> clients, string processName, int ownPid) =>
        InputApps(all, clients, ownPid).Contains(CallApp.Normalize(processName));
}
