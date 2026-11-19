// Port of app/Retention.swift.
using System.Globalization;

namespace Murmur.Core;

// History features beyond time-based expiry (which stays in HistoryStore): a size cap, a history-off mode, diagnostic recordings,
// the retry window, Delete All and Backup & Restore. On Windows a "link" is any reparse point (symbolic link, junction, mount point):
// purges, Delete All and restore never follow one, and removing one removes only the link.

public sealed class RetentionError(string message) : Exception(message);

/// <summary>The stored retention values and the one date rule for them; an unknown stored value reads as 30 days (<c>Prefs.Retention</c>).</summary>
public enum HistoryRetention { ThirtyDays, SixMonths, Forever }

public static class HistoryRetentions
{
    public static string Raw(this HistoryRetention value) => value switch { HistoryRetention.ThirtyDays => "30-days", HistoryRetention.SixMonths => "six-months", _ => "forever" };

    public static string Label(this HistoryRetention value) => value switch { HistoryRetention.ThirtyDays => "30 days", HistoryRetention.SixMonths => "Six months", _ => "Forever" };

    /// <summary>Entries recorded at or before this date expire; null for Forever. Six months is calendar months in <paramref name="zone"/> (default: the local zone).</summary>
    public static DateTime? Cutoff(this HistoryRetention value, DateTime now, TimeZoneInfo? zone = null)
    {
        now = SwiftDate.Normalize(now);
        switch (value)
        {
            case HistoryRetention.Forever: return null;
            case HistoryRetention.ThirtyDays: return now.AddTicks(-30L * 86400 * TimeSpan.TicksPerSecond);
            default:
                zone ??= TimeZoneInfo.Local;
                var earlier = TimeZoneInfo.ConvertTimeFromUtc(now, zone).AddMonths(-6);
                if (zone.IsInvalidTime(earlier)) earlier = earlier.AddHours(1);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(earlier, DateTimeKind.Unspecified), zone);
        }
    }
}

/// <summary>The history fields the cap needs. <see cref="HistoryEntry"/> has both.</summary>
public interface IRetainedHistoryItem
{
    string Id { get; }
    DateTime RecordedAt { get; }
}

public abstract record HistoryCapStatus(long UsedBytes, long CapBytes)
{
    public sealed record WithinCap(long UsedBytes, long CapBytes) : HistoryCapStatus(UsedBytes, CapBytes);
    /// <summary>Forever retention only: at or above <see cref="HistoryCap.WarningFraction"/> of the cap.</summary>
    public sealed record ApproachingCap(long UsedBytes, long CapBytes) : HistoryCapStatus(UsedBytes, CapBytes);
    /// <summary>Forever retention (never dropped), or a finite policy whose newest entry alone exceeds the cap.</summary>
    public sealed record OverCap(long UsedBytes, long CapBytes) : HistoryCapStatus(UsedBytes, CapBytes);

    public string? Warning => this switch
    {
        ApproachingCap => $"History uses {HistoryCap.Megabytes(UsedBytes)} of {HistoryCap.Megabytes(CapBytes)}. With Forever retention, Murmur does not remove old entries. Delete entries you no longer need.",
        OverCap => $"History uses {HistoryCap.Megabytes(UsedBytes)}, above the {HistoryCap.Megabytes(CapBytes)} limit. With Forever retention, Murmur does not remove old entries. Delete entries or choose a shorter retention period.",
        _ => null,
    };
}

/// <param name="Dropped">Entries dropped, oldest first.</param>
public sealed record HistoryCapResult<TItem>(HistoryCapStatus Status, IReadOnlyList<TItem> Dropped);

public static class HistoryCap
{
    /// <summary>50 MB, counted as 50 x 1024 x 1024 bytes.</summary>
    public const long DefaultCapBytes = 50L * 1024 * 1024;
    public const double WarningFraction = 0.9;

    public static string Megabytes(long bytes) => (bytes / 1_048_576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Bytes of history files, including quarantined data; links are not followed.</summary>
    public static long Usage(string directory)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            // Diagnostic recordings are not transcripts: they expire after 7 days on their own, and counting them would make
            // the cap delete transcript History to make room for audio it cannot remove.
            if (Path.GetFileName(path) == DiagnosticRecordings.FolderName) continue;
            if (FileLinks.IsLink(path)) continue;
            if (Directory.Exists(path)) total += Usage(path);
            else if (File.Exists(path)) total += new FileInfo(path).Length;
        }
        return total;
    }

    /// <summary>Pure plan. Finite retention drops oldest entries until usage fits, always keeping the newest entry (it may be the only
    /// copy of a just-failed insertion). Forever never drops; it warns.</summary>
    public static HistoryCapResult<TItem> Plan<TItem>(IReadOnlyList<TItem> entries, IReadOnlyDictionary<string, long> sizes, long otherBytes,
        HistoryRetention retention, long capBytes = DefaultCapBytes) where TItem : IRetainedHistoryItem
    {
        long Size(TItem item) => sizes.GetValueOrDefault(item.Id);
        long used = otherBytes + entries.Sum(Size);
        if (retention == HistoryRetention.Forever)
        {
            HistoryCapStatus forever = used > capBytes ? new HistoryCapStatus.OverCap(used, capBytes)
                : used >= capBytes * WarningFraction ? new HistoryCapStatus.ApproachingCap(used, capBytes)
                : new HistoryCapStatus.WithinCap(used, capBytes);
            return new(forever, []);
        }
        var oldestFirst = entries.OrderBy(e => e.RecordedAt).ToList();
        var dropped = new List<TItem>();
        foreach (var item in oldestFirst.Take(Math.Max(0, oldestFirst.Count - 1)))
        {
            if (used <= capBytes) continue;
            used -= Size(item);
            dropped.Add(item);
        }
        return new(used > capBytes ? new HistoryCapStatus.OverCap(used, capBytes) : new HistoryCapStatus.WithinCap(used, capBytes), dropped);
    }

    /// <summary>Measures <c>&lt;id&gt;.json</c> files in <paramref name="directory"/>, then deletes the planned entries through <paramref name="delete"/>
    /// (pass <c>HistoryStore.Delete</c>). Stops at the first failed deletion and rethrows.</summary>
    public static HistoryCapResult<TItem> Enforce<TItem>(IReadOnlyList<TItem> entries, string directory, HistoryRetention retention,
        Action<TItem> delete, long capBytes = DefaultCapBytes) where TItem : IRetainedHistoryItem
    {
        var sizes = EntrySizes(entries, directory);
        var other = Math.Max(0, Usage(directory) - sizes.Values.Sum());
        var result = Plan(entries, sizes, other, retention, capBytes);
        foreach (var item in result.Dropped) delete(item);
        return result;
    }

    static Dictionary<string, long> EntrySizes<TItem>(IReadOnlyList<TItem> entries, string directory) where TItem : IRetainedHistoryItem
    {
        var sizes = new Dictionary<string, long>();
        foreach (var item in entries)
        {
            if (!HistoryStore.IsUuid(item.Id)) throw new RetentionError("Invalid history identifier");
            var path = Path.Combine(directory, item.Id + ".json");
            if (File.Exists(path) && !FileLinks.IsLink(path)) sizes[item.Id] = new FileInfo(path).Length;
        }
        return sizes;
    }
}

/// <summary>
/// Optional recordings kept for diagnostics (off by default): the WAV the app would delete after a successful transcription is copied into
/// <c>history/recordings</c>, linked from its History entry by file name, and purged after 7 days. <see cref="Directory"/> is null while the
/// setting is off, so nothing is kept. Safe to use from any thread.
/// </summary>
public sealed class DiagnosticRecordings
{
    public const double LifetimeSeconds = 7 * 24 * 60 * 60;
    public const string FolderName = "recordings";
    public static string DirectoryIn(string history) => Path.Combine(history, FolderName);

    readonly Lock gate = new();
    string? target;
    public string? Directory { get { lock (gate) return target; } set { lock (gate) target = value; } }

    /// <summary>Copies <paramref name="audio"/> to <c>&lt;entry id&gt;.wav</c> (owner-only) and returns its file name, or null when off or on any failure.
    /// A copy, not a move: if saving the entry fails, the retry audio is still there.</summary>
    public string? Keep(string audio, string entryID, DateTime? now = null)
    {
        var directory = Directory;
        if (directory is null || !HistoryStore.IsUuid(entryID)) return null;
        var name = entryID + ".wav";
        var destination = Path.Combine(directory, name);
        try
        {
            PrivateFiles.PrepareDirectory(directory);
            File.Copy(audio, destination, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(destination, now ?? DateTime.UtcNow);
            return name;
        }
        catch (Exception) { try { File.Delete(destination); } catch (IOException) { } return null; }
    }

    /// <summary>Removes recordings older than the lifetime. Runs whether or not the setting is still on. Never follows a linked folder or file.</summary>
    public static void Purge(string history, DateTime? now = null)
    {
        var current = SwiftDate.Normalize(now ?? DateTime.UtcNow);
        var folder = DirectoryIn(history);
        if (FileLinks.IsLink(folder) || !System.IO.Directory.Exists(folder)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(folder).ToList())
        {
            if (Path.GetExtension(file) != ".wav" || FileLinks.IsLink(file)) continue;
            var modified = File.GetLastWriteTimeUtc(file);
            if ((current - modified).TotalSeconds >= LifetimeSeconds || modified > current) File.Delete(file);
        }
    }
}

/// <summary>Purge and restore never follow links: a restored backup could aim one outside Murmur's storage.
/// A link is any reparse point on Windows (symlink, junction, mount point) and a symbolic link elsewhere.</summary>
public static class FileLinks
{
    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes the link itself, never what it points to.</summary>
    public static void RemoveLink(string path)
    {
        // A directory link on Windows (symlink or junction) goes with Directory.Delete(non-recursive), which removes only the link.
        if (OperatingSystem.IsWindows() && (File.GetAttributes(path) & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    /// <summary>Deletes a file, folder or link. Links inside are removed, not followed. Missing is fine.</summary>
    public static void DeleteTree(string path)
    {
        if (IsLink(path)) { RemoveLink(path); return; }
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToList()) DeleteTree(entry);
            Directory.Delete(path, recursive: false);
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    internal static bool Exists(string path) => IsLink(path) || Directory.Exists(path) || File.Exists(path);
}

/// <summary>The 24-hour retry-audio window: how long audio left for retry or speaker separation stays. User text that names the window is built from <see cref="Text"/>.</summary>
public static class RetryWindow
{
    public const double LifetimeSeconds = 24 * 60 * 60;
    public static readonly string Text = $"{(int)(LifetimeSeconds / 3600)} hours";
}

/// <summary>History-off keeps results only in memory for the running session. Raw values "on", "off".</summary>
public enum HistoryMode { On, Off }

public static class HistoryModes
{
    public static string Raw(this HistoryMode value) => value == HistoryMode.On ? "on" : "off";
    public static bool PersistsTranscripts(this HistoryMode value) => value == HistoryMode.On;

    public static readonly string OffDisclosure = $"With history off, Murmur does not save transcript history to disk. If insertion fails and Murmur quits or stops unexpectedly, that text cannot be recovered. Retry audio can still remain for up to {RetryWindow.Text}, and learning can still save spellings in your dictionary. Turning history off does not delete existing history; delete it separately.";
}

/// <summary>Where Murmur keeps user data. The dictionary, ledger and cues share the <c>dictionary</c> directory.</summary>
public sealed record MurmurDataLocations(string History, string Dictionary, string RetryAudio, string Chunks)
{
    /// <summary>Meetings live inside the history and retry-audio folders, so retention and Delete All cover them.</summary>
    public string Meetings => Path.Combine(History, "meetings");
    public string MeetingAudio => Path.Combine(RetryAudio, "meetings");

    /// <summary>%LOCALAPPDATA%\Murmur, the base of every location below.</summary>
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Murmur");

    /// <summary>The layout under <paramref name="baseDirectory"/>; the app and Delete All both use it.</summary>
    public static MurmurDataLocations Standard(string baseDirectory) =>
        new(Path.Combine(baseDirectory, "history"), Path.Combine(baseDirectory, "dictionary"), Path.Combine(baseDirectory, "retry-audio"), Path.Combine(baseDirectory, "chunks"));
}

public sealed class DeleteAllReport
{
    public List<string> Removed { get; } = [];
    /// <summary>Locations that did not exist (nothing to delete).</summary>
    public List<string> Absent { get; } = [];
    /// <summary>Names of the credentials removed (Swift <c>keychainItems</c>).</summary>
    public IReadOnlyList<string> CredentialItems { get; set; } = [];
    public List<string> Failures { get; } = [];
    public bool Succeeded => Failures.Count == 0;
}

public static class DataWipe
{
    /// <summary>Settings Delete All removes besides the data folders: derived from dictations or usage (spec/settings.md).</summary>
    public static bool IsDerivedSetting(string key) => key == Prefs.DismissedSuggestions.Key || key.StartsWith("tips.", StringComparison.Ordinal);

    /// <summary>
    /// Removes history, dictionary, ledger, cues, retry audio and chunk directories, then calls <paramref name="removeCredentials"/> (which returns the names
    /// it removed) and clears the derived settings. Continues past failures and reports them. In-memory stores must be reloaded or discarded afterwards.
    /// A link in a data location is removed, never followed: nothing outside the data folders is deleted. Deletion is a filesystem unlink;
    /// backups or snapshots may still hold copies.
    /// </summary>
    public static DeleteAllReport DeleteAllData(MurmurDataLocations locations, Func<IReadOnlyList<string>> removeCredentials, ISettingsStore? settings = null)
    {
        var report = new DeleteAllReport();
        var seen = new HashSet<string>();
        foreach (var location in new[] { locations.History, locations.Dictionary, locations.RetryAudio, locations.Chunks })
        {
            var path = Path.GetFullPath(location);
            if (!seen.Add(path)) continue;
            if (!FileLinks.Exists(path)) { report.Absent.Add(location); continue; }
            try { FileLinks.DeleteTree(path); report.Removed.Add(location); }
            catch (Exception e) { report.Failures.Add($"Could not delete {Path.GetFileName(path)}: {e.Message}"); }
        }
        try { report.CredentialItems = removeCredentials(); }
        catch (Exception e) { report.Failures.Add($"Could not remove saved credentials: {e.Message}"); }
        if (settings is not null)
        {
            try { foreach (var key in settings.Snapshot().Keys.Where(IsDerivedSetting).ToList()) settings.Remove(key); }
            catch (Exception e) { report.Failures.Add($"Could not clear derived settings: {e.Message}"); }
        }
        return report;
    }
}

public static class DeleteAllDataText
{
    public const string Title = "Delete all Murmur data?";
    public const string Body = "This permanently deletes transcript history and meeting notes, your dictionary, learned entries and cues, retry audio, and API keys Murmur saved in Credential Manager. A meeting being recorded is stopped and discarded. Settings and the downloaded model are kept. Backups or snapshots may still hold copies. This cannot be undone.";
    public const string Explanation = "Deletes history and meetings, dictionary, learned entries, cues, retry audio and saved API keys. Settings and the downloaded model stay.";
}

/// <summary>What happens after DataWipe: every in-memory store pointing at a deleted directory is discarded and recreated empty, in this order.
/// Steps run even when an earlier one fails.</summary>
public enum PostWipeStep
{
    /// <summary>Drop history router (including memory-only entries), recovery selection and retry-audio pointer.</summary>
    DiscardInMemoryState,
    RecreateHistory,
    RecreateRetryAudio,
    /// <summary>A new processing coordinator re-reads the (now absent) dictionary, ledger and cues as empty.</summary>
    ReloadDictionary,
    RefreshViews,
}

public static class PostWipeSteps
{
    public static string Raw(this PostWipeStep step) => step switch
    {
        PostWipeStep.DiscardInMemoryState => "discardInMemoryState",
        PostWipeStep.RecreateHistory => "recreateHistory",
        PostWipeStep.RecreateRetryAudio => "recreateRetryAudio",
        PostWipeStep.ReloadDictionary => "reloadDictionary",
        _ => "refreshViews",
    };

    public static string Label(this PostWipeStep step) => step switch
    {
        PostWipeStep.DiscardInMemoryState => "in-memory data",
        PostWipeStep.RecreateHistory => "history",
        PostWipeStep.RecreateRetryAudio => "retry audio",
        PostWipeStep.ReloadDictionary => "dictionary",
        _ => "display",
    };

    /// <summary>Returns the steps that failed.</summary>
    public static List<PostWipeStep> Run(Action<PostWipeStep> perform)
    {
        var failed = new List<PostWipeStep>();
        foreach (var step in Enum.GetValues<PostWipeStep>())
        {
            try { perform(step); } catch (Exception) { failed.Add(step); }
        }
        return failed;
    }

    public static (bool Succeeded, string Text) Message(DeleteAllReport report, IReadOnlyList<PostWipeStep> failedSteps)
    {
        var problems = report.Failures.ToList();
        if (failedSteps.Count > 0) problems.Add("Could not set up empty storage for: " + string.Join(", ", failedSteps.Select(s => s.Label())) + ".");
        if (problems.Count == 0) return (true, "All Murmur data was deleted. Settings and the downloaded model were kept.");
        return (false, "Some Murmur data could not be deleted. " + string.Join(" ", problems));
    }
}

/// <summary>Backup &amp; Restore: settings, history (with meetings), dictionary, learned entries and cues, copied into one folder.
/// API keys and audio stay out: keys belong in Credential Manager, audio is short-lived. Writes <c>settings.json</c> (the Mac writes settings.plist,
/// so a Mac backup does not restore here in v1).</summary>
public static class DataBackup
{
    public const string SettingsFile = "settings.json";

    /// <summary>Settings that describe this PC, not the user: paths, devices, window frames, updater state, which host each saved key was approved for
    /// (restoring that could send a key elsewhere), and where dictations are sent: a backup from someone else must never change the destination.</summary>
    public static bool IsMachineSetting(string key) =>
        key is "modelDirectory" or "inputDeviceUID" || DestinationSettings.Contains(key)
            || key.StartsWith("customAPIKeyEndpoint", StringComparison.Ordinal) || key.StartsWith("SU", StringComparison.Ordinal) || key.StartsWith("NSWindow Frame", StringComparison.Ordinal);

    /// <summary>Literal keys, as in Retention.swift; the Routes port's check ties them to the route keys and <c>Prefs</c>.</summary>
    public static readonly IReadOnlySet<string> DestinationSettings = new HashSet<string>
    {
        "transcriptionRoute", "cloudTranscriber", "cleanupChoice", "cleanupRoute",
        "customAPIBaseURL", "customTranscriptionAPIBaseURL", "localServerURL",
    };

    /// <summary>Names the copy leaves out: the history folder's lock file and in-flight writes.</summary>
    static bool Transient(string name) => name == HistoryStore.LockFileName || name.StartsWith(".pending-", StringComparison.Ordinal);

    /// <summary>Copies a folder without following links (a link is skipped) and without the transient files.</summary>
    static void CopyTree(string source, string destination)
    {
        PrivateFiles.PrepareDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (Transient(name) || FileLinks.IsLink(entry)) continue;
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry)) CopyTree(entry, target);
            else File.Copy(entry, target);
        }
    }

    /// <summary>Replaces <paramref name="live"/> with <paramref name="staged"/>; the old folder (or link) is deleted only after the swap.</summary>
    static void Swap(string live, string staged, string oldName)
    {
        if (!FileLinks.Exists(live)) { Directory.Move(staged, live); return; }
        var old = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(live))!, oldName);
        Directory.Move(live, old);
        try { Directory.Move(staged, live); }
        catch { Directory.Move(old, live); throw; }
        try { FileLinks.DeleteTree(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string FolderName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Writes to a sibling folder first, so a failed backup never removes the one it replaces.</summary>
    public static void Export(MurmurDataLocations locations, IReadOnlyDictionary<string, object?> settings, string folder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        var staged = Path.Combine(parent, ".murmur-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateFiles.PrepareDirectory(staged);
            foreach (var dir in new[] { locations.History, locations.Dictionary })
            {
                if (Directory.Exists(dir) && !FileLinks.IsLink(dir)) CopyTree(dir, Path.Combine(staged, FolderName(dir)));
            }
            PrivateFiles.Write(SettingsJson.Serialize(settings.Where(s => !IsMachineSetting(s.Key))), Path.Combine(staged, SettingsFile));
            Swap(folder, staged, ".murmur-backup-old-" + Guid.NewGuid().ToString("N"));
        }
        finally { if (Directory.Exists(staged)) FileLinks.DeleteTree(staged); }
    }

    /// <summary>The backup's settings; throws for a folder that is not a Murmur backup.</summary>
    public static Dictionary<string, object?> ReadSettings(string folder)
    {
        var file = Path.Combine(folder, SettingsFile);
        var data = File.Exists(file) && !FileLinks.IsLink(file) ? File.ReadAllBytes(file) : null;
        return (data is null ? null : SettingsJson.TryParse(data)) ?? throw new RetentionError("This folder is not a Murmur backup.");
    }

    /// <summary>The backup's settings, keeping this PC's machine settings (and dropping the backup's value for one this PC has not set).</summary>
    public static Dictionary<string, object?> Merged(IReadOnlyDictionary<string, object?> backup, IReadOnlyDictionary<string, object?> current)
    {
        var merged = backup.Where(s => !IsMachineSetting(s.Key)).ToDictionary(s => s.Key, s => s.Value);
        foreach (var (key, value) in current.Where(s => IsMachineSetting(s.Key))) merged[key] = value;
        return merged;
    }

    /// <summary>A backup is someone else's folder: a link inside it could point purge or writes outside Murmur's storage.</summary>
    public static void RemoveLinks(string folder)
    {
        if (FileLinks.IsLink(folder)) { FileLinks.RemoveLink(folder); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
        {
            if (FileLinks.IsLink(entry)) FileLinks.RemoveLink(entry);
            else if (Directory.Exists(entry)) RemoveLinks(entry);
        }
    }

    /// <summary>Copies both folders in beside the live ones, then swaps them, so a failed copy changes nothing.
    /// A folder missing from the backup (or itself a link) is restored as empty.</summary>
    public static void Restore(string folder, MurmurDataLocations locations)
    {
        var dirs = new[] { locations.History, locations.Dictionary };
        var staged = dirs.Select(d => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(d))!, ".restore-" + FolderName(d))).ToList();
        try
        {
            for (int i = 0; i < dirs.Length; i++)
            {
                FileLinks.DeleteTree(staged[i]);
                var source = Path.Combine(folder, FolderName(dirs[i]));
                if (Directory.Exists(source) && !FileLinks.IsLink(source)) { CopyTree(source, staged[i]); RemoveLinks(staged[i]); }
                else PrivateFiles.PrepareDirectory(staged[i]);
            }
            for (int i = 0; i < dirs.Length; i++) Swap(dirs[i], staged[i], ".restore-old-" + FolderName(dirs[i]) + "-" + Guid.NewGuid().ToString("N"));
        }
        finally { foreach (var stage in staged) if (FileLinks.Exists(stage)) FileLinks.DeleteTree(stage); }
    }
}
