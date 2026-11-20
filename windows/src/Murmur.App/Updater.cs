using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace Murmur.App;

/// <summary>Velopack updates from GitHub releases (PLAN.md "Updates and releases"). Requests carry nothing but the HTTP request for the public feed.</summary>
internal sealed class Updater : IDisposable
{
    public const string RepoUrl = "https://github.com/theaakashb/Murmur";
    /// <summary>AppUpdater.swift's UpdateText, with the transport changed.</summary>
    public const string Explanation = "Murmur checks GitHub for updates when it starts and every few hours. This uses the network even when transcription runs on this PC. Update requests contain no transcripts, audio, dictionary or API keys.";
    static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    readonly UpdateManager manager;
    readonly Func<bool> enabled, busy;
    readonly Timer timer;
    readonly SemaphoreSlim gate = new(1, 1);
    VelopackAsset? ready;

    /// <summary>Raised on a worker thread with the new version once it is downloaded.</summary>
    public event Action<string>? UpdateReady;
    public bool IsInstalled => manager.IsInstalled;
    public string? ReadyVersion => ready?.Version.ToString();

    /// <param name="source">GitHub in production; a local folder in tests (MURMUR_UPDATE_FEED).</param>
    public Updater(IUpdateSource source, Func<bool> enabled, Func<bool> busy)
    {
        manager = new UpdateManager(source);
        this.enabled = enabled; this.busy = busy;
        timer = new Timer(_ => _ = CheckAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static IUpdateSource ProductionSource() =>
        Environment.GetEnvironmentVariable("MURMUR_UPDATE_FEED") is { Length: > 0 } folder
            ? new SimpleFileSource(new DirectoryInfo(folder))
            : new GithubSource(RepoUrl, accessToken: null, prerelease: false);

    /// <summary>First check now, then every few hours. Does nothing for a build that isn't installed by Velopack.</summary>
    public void Start()
    {
        if (!IsInstalled) return;
        timer.Change(TimeSpan.Zero, Interval);
    }

    /// <summary>Returns the new version if one was downloaded, null otherwise. Never throws: a failed check just waits for the next one.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled || ready is not null || !enabled() || busy() || !await gate.WaitAsync(0)) return null;
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return null;
            await manager.DownloadUpdatesAsync(info);
            ready = info.TargetFullRelease;
            var version = ready.Version.ToString();
            UpdateReady?.Invoke(version);
            return version;
        }
        catch (Exception e) { CrashLog.Write(e); return null; }
        finally { gate.Release(); }
    }

    /// <summary>The "Restart to update" action. Returns a message when it can't run now.</summary>
    public string? RestartToUpdate()
    {
        if (ready is null) return "No update is ready.";
        if (busy()) return "Finish dictation or meeting recording before updating.";
        manager.ApplyUpdatesAndRestart(ready);
        return null;
    }

    /// <summary>On quit: install a downloaded update after this process exits, without restarting.</summary>
    public void ApplyOnExit()
    {
        if (ready is not null) manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
    }

    public void Dispose() => timer.Dispose();
}

/// <summary>Start at login: an HKCU Run entry for Velopack's stable launcher, which survives updates (the app folder changes every version).</summary>
internal static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Murmur";

    /// <summary>Update.exe sits beside current\, one level above the running exe; null when not installed by Velopack.</summary>
    static string? Launcher()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));
        return File.Exists(path) ? path : null;
    }

    public static bool IsOn { get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string; } }

    public static bool Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!on) { key.DeleteValue(ValueName, throwOnMissingValue: false); return true; }
        if (Launcher() is not { } launcher) return false;
        key.SetValue(ValueName, $"\"{launcher}\" --processStart Murmur.exe");
        return true;
    }
}
