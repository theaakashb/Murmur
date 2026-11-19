// Ports checkBackup from app/check_app_services.swift (backup, restore, links), plus the Windows junction cases and the purge/Delete All link rules.
using System.Diagnostics;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Creates directory links for the tests: a symbolic link everywhere, a junction on Windows only. Returns false when the link cannot be made
/// (Windows without the symlink privilege), in which case the caller has nothing to check.</summary>
internal static class Links
{
    public static bool TryCreate(string link, string target, bool junction)
    {
        try
        {
            if (!junction) { Directory.CreateSymbolicLink(link, target); return true; }
            if (!OperatingSystem.IsWindows()) return false;
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        // Only Windows without the symlink privilege may skip; anywhere else a link that cannot be made is a failed test, not a pass.
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>The two variants: symlink on every platform, junction on Windows only (Windows-only cases run nowhere else).</summary>
    public static IEnumerable<object[]> Kinds => OperatingSystem.IsWindows() ? [[false], [true]] : [[false]];
}

public class BackupTests
{
    static string Fresh() { var root = TempDirs.New("backup"); Directory.CreateDirectory(root); return root; }

    static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    [Fact]
    public void ExportReadMergeRestore()
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        var folder = Path.Combine(root, "backup");
        Directory.CreateDirectory(live.History); Directory.CreateDirectory(live.Dictionary);
        Write(Path.Combine(live.Dictionary, "cues.json"), "old");
        Write(Path.Combine(live.History, "h.json"), "h");
        Directory.CreateDirectory(folder); // an existing backup is replaced
        Write(Path.Combine(folder, "stale.txt"), "stale");
        DataBackup.Export(live, new Dictionary<string, object?> { ["liveWords"] = true, ["customAPIKeyEndpoint"] = "https://a.example", ["modelDirectory"] = "C:\\x" }, folder);
        Assert.False(File.Exists(Path.Combine(folder, "stale.txt")));
        Write(Path.Combine(live.Dictionary, "cues.json"), "new");
        Directory.Delete(live.History, true);

        var backup = DataBackup.ReadSettings(folder);
        Assert.True(backup["liveWords"] is true && !backup.ContainsKey("customAPIKeyEndpoint") && !backup.ContainsKey("modelDirectory"), "backup keeps user settings and drops machine ones");
        var merged = DataBackup.Merged(new Dictionary<string, object?> { ["liveWords"] = true, ["customAPIKeyEndpoint.x"] = "https://a.example" },
                                       new Dictionary<string, object?> { ["liveWords"] = false, ["customAPIKeyEndpoint.x"] = "https://b.example" });
        Assert.True(merged["liveWords"] is true && merged["customAPIKeyEndpoint.x"] is "https://b.example", "restore keeps this PC's key bindings");

        DataBackup.Restore(folder, live);
        Assert.Equal("old", File.ReadAllText(Path.Combine(live.Dictionary, "cues.json")));
        Assert.True(File.Exists(Path.Combine(live.History, "h.json")), "restore recreates missing history");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(Path.GetDirectoryName(live.History)!), p => Path.GetFileName(p).StartsWith(".restore-"));
        Assert.Throws<RetentionError>(() => DataBackup.ReadSettings(root));
    }

    [Fact]
    public void BackupHoldsNoKeysAndNoAudio()
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        Write(Path.Combine(live.History, "h.json"), "h");
        Write(Path.Combine(live.RetryAudio, "a.wav"), "audio");
        Write(Path.Combine(live.Chunks, "c.wav"), "audio");
        var folder = Path.Combine(root, "backup");
        DataBackup.Export(live, new Dictionary<string, object?>(), folder);
        Assert.Equal(["history", DataBackup.SettingsFile], Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void RestoreNeverChangesWhereDictationsAreSent()
    {
        var routed = DataBackup.Merged(new Dictionary<string, object?> { ["cleanupChoice"] = "custom-api", ["customAPIBaseURL"] = "https://other.example/v1", ["transcriptionRoute"] = "custom" },
                                       new Dictionary<string, object?> { ["cleanupChoice"] = "local-rules" });
        Assert.True(routed["cleanupChoice"] is "local-rules" && !routed.ContainsKey("customAPIBaseURL") && !routed.ContainsKey("transcriptionRoute"), "restore never changes where dictations are sent");
    }

    [Fact]
    public void BackupsFromAnotherMachineNeverRedirect()
    {
        // Every key in the destination set, on its own, loses to this PC's value and never arrives from the backup.
        foreach (var key in DataBackup.DestinationSettings)
        {
            var merged = DataBackup.Merged(new Dictionary<string, object?> { [key] = "elsewhere" }, new Dictionary<string, object?> { [key] = "mine" });
            Assert.Equal("mine", merged[key]);
            Assert.DoesNotContain(key, DataBackup.Merged(new Dictionary<string, object?> { [key] = "elsewhere" }, new Dictionary<string, object?>()).Keys);
        }
    }

    [Fact]
    public void ExportWritesMachineSettingsNowhere()
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        var folder = Path.Combine(root, "backup");
        var settings = DataBackup.DestinationSettings.ToDictionary(k => k, k => (object?)"x");
        settings["liveWords"] = true; settings["inputDeviceUID"] = "{0.0.1}"; settings["NSWindow Frame A"] = "1"; settings["SUFoo"] = "1";
        DataBackup.Export(live, settings, folder);
        Assert.Equal(["liveWords"], DataBackup.ReadSettings(folder).Keys.ToArray());
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void RestoreDropsLinksInsideTheBackup(bool junction)
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        var folder = Path.Combine(root, "backup");
        Write(Path.Combine(live.Dictionary, "cues.json"), "old");
        Write(Path.Combine(live.History, "h.json"), "h");
        DataBackup.Export(live, new Dictionary<string, object?>(), folder);
        var outside = Path.Combine(root, "outside");
        var victim = Path.Combine(outside, "keep.wav");
        Write(victim, "x"); File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-400));
        if (!Links.TryCreate(Path.Combine(folder, "history", "unreadable"), outside, junction)) return;
        DataBackup.Restore(folder, live);
        Assert.False(FileLinks.Exists(Path.Combine(live.History, "unreadable")), "restore drops links inside the backup");
        Assert.True(File.Exists(victim), "what the link pointed to is untouched");
        Assert.True(File.Exists(Path.Combine(live.History, "h.json")));
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void RestoreTreatsALinkedBackupFolderAsEmpty(bool junction)
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        Write(Path.Combine(live.History, "mine.json"), "mine");
        var outside = Path.Combine(root, "outside");
        Write(Path.Combine(outside, "secret.json"), "elsewhere");
        var folder = Path.Combine(root, "backup");
        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, DataBackup.SettingsFile), "{}");
        if (!Links.TryCreate(Path.Combine(folder, "history"), outside, junction)) return;
        DataBackup.Restore(folder, live);
        Assert.True(Directory.Exists(live.History) && !FileLinks.IsLink(live.History), "history is a real folder");
        Assert.Empty(Directory.GetFileSystemEntries(live.History));
        Assert.True(File.Exists(Path.Combine(outside, "secret.json")));
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void ExportSkipsLinksInLiveData(bool junction)
    {
        var root = Fresh();
        var live = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        Write(Path.Combine(live.History, "h.json"), "h");
        var outside = Path.Combine(root, "outside");
        Write(Path.Combine(outside, "secret.json"), "elsewhere");
        if (!Links.TryCreate(Path.Combine(live.History, "linked"), outside, junction)) return;
        var folder = Path.Combine(root, "backup");
        DataBackup.Export(live, new Dictionary<string, object?>(), folder);
        Assert.True(File.Exists(Path.Combine(folder, "history", "h.json")));
        Assert.False(FileLinks.Exists(Path.Combine(folder, "history", "linked")), "a link is never followed into the backup");
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void RecordingPurgeDoesNotFollowALinkedFolder(bool junction)
    {
        var root = Fresh();
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        var outside = Path.Combine(root, "outside");
        var victim = Path.Combine(outside, "keep.wav");
        Write(victim, "x"); File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-400));
        if (!Links.TryCreate(DiagnosticRecordings.DirectoryIn(history), outside, junction)) return;
        DiagnosticRecordings.Purge(history);
        Assert.True(File.Exists(victim), "recording purge does not follow a linked folder");
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void UnreadablePurgeDoesNotFollowALinkedFolder(bool junction)
    {
        var root = Fresh();
        var history = Path.Combine(root, "history");
        var store = new HistoryStore(history);
        var outside = Path.Combine(root, "outside");
        var victim = Path.Combine(outside, "keep.json");
        Write(victim, "x"); File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-400));
        if (!Links.TryCreate(store.UnreadableDirectory, outside, junction)) return;
        store.Purge(HistoryRetention.ThirtyDays);
        Assert.True(File.Exists(victim), "the unreadable purge does not follow a linked folder");
    }

    [Theory, MemberData(nameof(Links.Kinds), MemberType = typeof(Links))]
    public void DeleteAllRemovesALinkNotItsTarget(bool junction)
    {
        var root = Fresh();
        var locations = MurmurDataLocations.Standard(Path.Combine(root, "live"));
        Write(Path.Combine(locations.History, "h.json"), "h");
        var outside = Path.Combine(root, "outside");
        var victim = Path.Combine(outside, "keep.txt");
        Write(victim, "x");
        // A linked folder inside a data folder, and a data folder that is itself a link.
        if (!Links.TryCreate(Path.Combine(locations.History, "linked"), outside, junction)) return;
        if (!Links.TryCreate(locations.Dictionary, outside, junction)) return;
        var report = DataWipe.DeleteAllData(locations, () => []);
        Assert.True(report.Succeeded, string.Join("; ", report.Failures));
        Assert.True(File.Exists(victim), "Delete All never deletes what a link points to");
        Assert.False(FileLinks.Exists(locations.History) || FileLinks.Exists(locations.Dictionary));
    }
}
