// Ports the retention, cap, diagnostic recording and Delete All assertions of check_dictionary.swift (retentionChecks), check_processing.swift (recordingChecks)
// and check_app_services.swift (checkExpiryAndText, checkWipe).
using Murmur.Core;

namespace Murmur.Core.Tests;

public class RetentionTests
{
    sealed record Item(string Id, DateTime RecordedAt) : IRetainedHistoryItem;

    static string Fresh() { var root = TempDirs.New("retention"); Directory.CreateDirectory(root); return root; }

    [Fact]
    public void ExpiryAndWording()
    {
        var c = new Checker();
        var now = DateTime.UnixEpoch.AddSeconds(1_790_000_000);
        c.Check(HistoryRetention.Forever.Cutoff(now, TimeZoneInfo.Utc) is null, "forever has no cutoff");
        c.Check(HistoryRetention.ThirtyDays.Cutoff(now, TimeZoneInfo.Utc) == now.AddSeconds(-30 * 86400), "30-day cutoff");
        c.Check(HistoryRetention.SixMonths.Cutoff(now, TimeZoneInfo.Utc) == DateTime.UnixEpoch.AddSeconds(1_774_102_400), "six months uses calendar months");
        // A month-end boundary clamps to the shorter month, as the calendar does.
        var aug31 = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        c.Check(HistoryRetention.SixMonths.Cutoff(aug31, TimeZoneInfo.Utc) == new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), "six months before August 31 is February 28");
        c.Check(HistoryText.Summary(HistoryMode.On, 2, 0) == "Saved locally · 2 transcripts", "history on summary keeps the existing wording");
        c.Check(HistoryText.Summary(HistoryMode.On, 1, 1).Contains("1 kept only until Murmur quits"), "history on with memory entries says so");
        c.Check(HistoryText.Summary(HistoryMode.Off, 0, 0).StartsWith("History is off"), "history off summary");
        c.Check(HistoryText.Saved(true) == "Saved in History." && HistoryText.Saved(false).Contains("until Murmur quits"), "saved wording follows mode");
        c.Check(HistoryModes.OffDisclosure.Contains("cannot be recovered"), "off disclosure available");
        c.Check(!HistoryMode.Off.PersistsTranscripts() && HistoryMode.On.PersistsTranscripts(), "history mode flag");
        c.Check(Enum.GetValues<HistoryRetention>().ToDictionary(r => r.Raw()).Keys.SequenceEqual(["30-days", "six-months", "forever"]), "retention strings match the Mac's");
        c.Done();
    }

    [Fact]
    public void CapPlanAndEnforce()
    {
        var c = new Checker();
        var root = Fresh();
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        var baseTime = DateTime.UnixEpoch.AddSeconds(1_790_000_000);
        var items = Enumerable.Range(0, 5).Select(i => new Item(Guid.NewGuid().ToString().ToUpperInvariant(), baseTime.AddSeconds(i * 60))).ToList();
        foreach (var item in items) File.WriteAllBytes(Path.Combine(history, item.Id + ".json"), new byte[1000]);
        c.Check(HistoryCap.Usage(history) == 5000, "usage sums files");
        c.Check(HistoryCap.DefaultCapBytes == 50 * 1024 * 1024, "50 MB default cap");

        var sizes = items.ToDictionary(i => i.Id, _ => 1000L);
        var plan = HistoryCap.Plan(items.OrderBy(_ => Guid.NewGuid()).ToList(), sizes, 0, HistoryRetention.ThirtyDays, 2500);
        c.Check(plan.Dropped.Select(d => d.Id).SequenceEqual(items.Take(3).Select(i => i.Id)), "drops the three oldest");
        c.Check(plan.Status == new HistoryCapStatus.WithinCap(2000, 2500), "within cap after drop");
        var single = HistoryCap.Plan([items[4]], sizes, 0, HistoryRetention.SixMonths, 500);
        c.Check(single.Dropped.Count == 0 && single.Status == new HistoryCapStatus.OverCap(1000, 500), "newest entry kept");
        var forever = HistoryCap.Plan(items, sizes, 0, HistoryRetention.Forever, 2500);
        c.Check(forever.Dropped.Count == 0 && forever.Status == new HistoryCapStatus.OverCap(5000, 2500) && forever.Status.Warning is not null, "forever over cap warns");
        var near = HistoryCap.Plan(items, sizes, 0, HistoryRetention.Forever, 5400);
        c.Check(near.Status == new HistoryCapStatus.ApproachingCap(5000, 5400) && near.Status.Warning is not null, "forever approaching warns");
        c.Check(HistoryCap.Plan(items, sizes, 0, HistoryRetention.Forever, 50_000).Status.Warning is null, "no warning well under cap");

        var deleted = new List<string>();
        var result = HistoryCap.Enforce(items, history, HistoryRetention.ThirtyDays, item => { deleted.Add(item.Id); File.Delete(Path.Combine(history, item.Id + ".json")); }, 3000);
        c.Check(deleted.SequenceEqual(items.Take(2).Select(i => i.Id)) && result.Status.UsedBytes == 3000, "enforce deletes oldest");
        c.Check(HistoryCap.Usage(history) == 3000, "files removed");
        var noDrop = HistoryCap.Enforce(items.TakeLast(3).ToList(), history, HistoryRetention.Forever, _ => c.Check(false, "forever must not delete"), 1000);
        c.Check(noDrop.Dropped.Count == 0, "forever enforce drops nothing");

        var quarantine = Path.Combine(history, "unreadable");
        Directory.CreateDirectory(quarantine);
        File.WriteAllBytes(Path.Combine(quarantine, "damaged.json"), new byte[700]);
        // Recordings never count, whatever is in them.
        Directory.CreateDirectory(DiagnosticRecordings.DirectoryIn(history));
        File.WriteAllBytes(Path.Combine(DiagnosticRecordings.DirectoryIn(history), "a.wav"), new byte[900]);
        if (Links.TryCreate(Path.Combine(history, "outside-link"), root, false)) c.Check(HistoryCap.Usage(history) == 3700, "quarantined bytes count without following links");
        else c.Check(HistoryCap.Usage(history) == 3700, "quarantined bytes count");
        Assert.Throws<RetentionError>(() => HistoryCap.Enforce([new Item("../x", baseTime)], history, HistoryRetention.ThirtyDays, _ => { }));
        c.Done();
    }

    [Fact]
    public void DiagnosticRecordingsKeepAndPurge()
    {
        var c = new Checker();
        var root = Fresh();
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        var audio = Path.Combine(root, "retry.wav");
        File.WriteAllText(audio, "RIFF");
        var recordings = new DiagnosticRecordings();
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        c.Check(recordings.Keep(audio, id) is null && !Directory.Exists(DiagnosticRecordings.DirectoryIn(history)), "off keeps nothing and creates nothing");
        recordings.Directory = DiagnosticRecordings.DirectoryIn(history);
        var name = recordings.Keep(audio, id);
        var kept = Path.Combine(DiagnosticRecordings.DirectoryIn(history), id + ".wav");
        c.Check(name == id + ".wav" && File.Exists(audio) && File.Exists(kept), "on copies the audio and leaves the original for the caller");
        if (!OperatingSystem.IsWindows()) c.Check(File.GetUnixFileMode(kept) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "the copy is owner-only");
        c.Check(recordings.Keep(audio, "../x") is null, "only entry ids name recordings");
        c.Check(HistoryCap.Usage(history) == 0, "recordings do not count toward the History size cap");
        DiagnosticRecordings.Purge(history, DateTime.UtcNow.AddDays(6));
        c.Check(File.Exists(kept), "kept for 7 days");
        DiagnosticRecordings.Purge(history, DateTime.UtcNow.AddDays(8));
        c.Check(!File.Exists(kept), "purged after 7 days");
        // A file dated in the future (clock moved back) is purged too; a file that is not a WAV is left alone.
        File.WriteAllText(kept, "x"); File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(3));
        var other = Path.Combine(DiagnosticRecordings.DirectoryIn(history), "notes.txt"); File.WriteAllText(other, "x"); File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-30));
        DiagnosticRecordings.Purge(history);
        c.Check(!File.Exists(kept) && File.Exists(other), "future-dated WAVs expire, other files stay");
        c.Done();
    }

    [Fact]
    public void DeleteAllRemovesDataAndReportsCredentials()
    {
        var c = new Checker();
        var root = Fresh();
        var locations = MurmurDataLocations.Standard(Path.Combine(root, "Murmur"));
        foreach (var dir in new[] { locations.History, locations.Dictionary, locations.RetryAudio, locations.Chunks })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f.json"), "x");
        }
        File.WriteAllText(Path.Combine(locations.Dictionary, "cues.json"), "x");
        Directory.CreateDirectory(locations.Meetings); File.WriteAllText(Path.Combine(locations.Meetings, "m.json"), "x");
        // The settings file lives beside the data folders: Delete All keeps it, apart from the derived keys.
        var settings = new InMemorySettingsStore(new Dictionary<string, object?>
        {
            ["liveWords"] = true, ["dictionarySuggestionsDismissed"] = new[] { "x" }, ["tips.enabled"] = true, ["tips.done"] = new[] { "a" }, ["tipsy"] = 1L,
        });
        var called = 0;
        var report = DataWipe.DeleteAllData(locations, () => { called++; return ["customCleanup"]; }, settings);
        c.Check(report.Succeeded && called == 1 && report.CredentialItems.SequenceEqual(["customCleanup"]), "deleteAll reports credential removal");
        c.Check(report.Removed.Count == 4, "history, dictionary+ledger, retry audio and abandoned chunks removed");
        foreach (var dir in new[] { locations.History, locations.Dictionary, locations.RetryAudio, locations.Chunks }) c.Check(!Directory.Exists(dir), $"{Path.GetFileName(dir)} gone");
        c.Check(settings.Snapshot().Keys.Order().SequenceEqual(["liveWords", "tipsy"]), "derived settings go, other settings stay");

        // The reload plan recreates empty directories (and runs every step past a failure).
        var order = new List<PostWipeStep>();
        var failed = PostWipeSteps.Run(step => { order.Add(step); if (step == PostWipeStep.RecreateRetryAudio) throw new InvalidOperationException("no"); });
        c.Check(order.SequenceEqual(Enum.GetValues<PostWipeStep>()), "every reload step runs, in order, past a failure");
        c.Check(order[0] == PostWipeStep.DiscardInMemoryState && order[^1] == PostWipeStep.RefreshViews, "discard first, refresh last");
        c.Check(failed.SequenceEqual([PostWipeStep.RecreateRetryAudio]), "failed steps are reported");

        Directory.CreateDirectory(locations.Dictionary);
        var second = DataWipe.DeleteAllData(locations, () => throw new InvalidOperationException("denied"));
        c.Check(second.Absent.Count == 3 && second.Removed.Count == 1 && !second.Succeeded && second.Failures.Count == 1, "absent data and a credential failure are reported");
        var clean = PostWipeSteps.Message(new DeleteAllReport(), []);
        c.Check(clean.Succeeded && clean.Text.Contains("All Murmur data was deleted"), "success message");
        var partial = new DeleteAllReport(); partial.Failures.Add("Could not remove saved credentials: denied");
        var bad = PostWipeSteps.Message(partial, [PostWipeStep.RecreateHistory]);
        c.Check(!bad.Succeeded && bad.Text.Contains("credentials") && bad.Text.Contains("history"), "failure message lists problems");
        c.Check(DeleteAllDataText.Body.Contains("Credential Manager") && DeleteAllDataText.Body.Contains("cannot be undone") && DeleteAllDataText.Body.Contains("Backups"), "wipe confirmation is explicit");
        c.Check(DeleteAllDataText.Body.Contains("cues") && DeleteAllDataText.Explanation.Contains("cues"), "wipe confirmation names cues");
        c.Done();
    }

    [Fact]
    public void FileLinksRemoveOnlyTheLink()
    {
        var root = Fresh();
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var link = Path.Combine(root, "link");
        if (!Links.TryCreate(link, target, false)) return;
        Assert.True(FileLinks.IsLink(link) && !FileLinks.IsLink(target));
        FileLinks.DeleteTree(link);
        Assert.False(FileLinks.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        FileLinks.DeleteTree(Path.Combine(root, "absent")); // missing is fine
    }
}
