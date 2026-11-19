// Ports app/check_history.swift (the history store and entry model; the model manifest verifier at its end is not part of History).
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class HistoryTests
{
    static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();
    static readonly DateTime Now = DateTime.UnixEpoch.AddSeconds(1_791_021_600);

    static HistoryEntry Entry(string? id = null, DateTime? at = null, string raw = "Synthetic original", string final = "Synthetic final", string status = "saved-not-inserted") =>
        new() { Id = id ?? NewId(), RecordedAt = at ?? Now, RawText = raw, FinalText = final, InsertionStatusRaw = status };

    static string Json(HistoryEntry e) => System.Text.Encoding.UTF8.GetString(StoreJson.Encode(e));
    static HistoryEntry RoundTrip(HistoryEntry e) => StoreJson.Decode<HistoryEntry>(StoreJson.Encode(e));

    [Fact]
    public void EntryModel()
    {
        var c = new Checker();
        var entry = Entry();
        var fellBack = Entry(status: InsertionStatus.Unverified.Raw(), raw: "a", final: "A.") with { CleanupFallback = "unexplainedDeletion", CleanupRejectedOutput = "Sure!" };
        var roundTrip = RoundTrip(fellBack);
        c.Check(roundTrip.CleanupFallback == "unexplainedDeletion" && roundTrip.CleanupRejectedOutput == "Sure!", "fallback reason and rejected output round-trip");
        // Files written before a field existed still load.
        var older = StoreJson.Decode<HistoryEntry>(System.Text.Encoding.UTF8.GetBytes($$"""{"id":"{{NewId()}}","recordedAt":0,"rawText":"a","finalText":"a","insertionStatus":"unverified"}"""));
        c.Check(older.CleanupFallback is null && older.RecordedAt == new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), "older history decodes without a fallback reason; dates count from 2001");

        var edited = entry.With("Synthetic result", InsertionStatus.Edited) with { EditOriginal = "Synthetic selection" };
        var editBack = RoundTrip(edited);
        c.Check(editBack.EditOriginal == "Synthetic selection" && editBack.Status == InsertionStatus.Edited && editBack.StatusLabel == "Edited" && !editBack.NeedsAttention && older.EditOriginal is null, "voice edit entry round-trips");

        HistoryEntry With(InsertionStatus s) => entry.With("ok", s);
        c.Check(new[] { InsertionStatus.Verified, InsertionStatus.Unverified }.All(s => With(s).StatusLabel is null && With(s).StatusNote is null && !With(s).NeedsAttention), "delivered text carries no label or note");
        c.Check(With(InsertionStatus.Failed).NeedsAttention && With(InsertionStatus.Failed).StatusLabel == "Not inserted", "failed");
        c.Check(With(InsertionStatus.InsertionPending).NeedsAttention && With(InsertionStatus.InsertionPending).StatusLabel == "Saved", "pending");
        c.Check(With(InsertionStatus.InsertionPending).StatusNote!.Contains("avoid a duplicate"), "pending note");
        c.Check(!entry.NeedsAttention && entry.StatusLabel == "Saved" && With(InsertionStatus.Copied).StatusLabel == "Copied", "saved and copied");
        var unknown = entry with { InsertionStatusRaw = "from-a-newer-version" };
        c.Check(unknown.StatusLabel == "Saved" && unknown.StatusNote is null && !unknown.NeedsAttention && unknown.Status is null, "a status this version does not know reads as saved");
        c.Check(HistoryEntry.RecoveryEntryToOpen(entry.Id, [With(InsertionStatus.Failed) ]) == entry.Id, "History opens on the entry that still needs attention");
        c.Check(HistoryEntry.RecoveryEntryToOpen(entry.Id, [entry]) == entry.Id && !entry.NeedsAttention, "History opens on text that was saved and not pasted, without flagging it");
        c.Check(HistoryEntry.RecoveryEntryToOpen(entry.Id, [With(InsertionStatus.Verified)]) is null && HistoryEntry.RecoveryEntryToOpen(null, [With(InsertionStatus.Failed)]) is null && HistoryEntry.RecoveryEntryToOpen(entry.Id, []) is null, "no entry opens once delivered, without a recovery entry or when it is gone");
        c.Check(fellBack.OutcomeNote is null, "no outcome line before the outcome was recorded");
        var noted = fellBack with { CleanupOutcome = CleanupPath.LocalRules, CleanupOptions = new CleanupOptions(polish: true), Edited = true };
        c.Check(noted.OutcomeNote == "Polish rejected (unexplained deletion); local cleanup used. Edited since.", "the outcome line names the cleanup, the reason and the edit");
        noted = noted with { CleanupFallback = "cleanerFailed", Edited = null };
        c.Check(noted.OutcomeNote == "Polish failed; local cleanup used.", "a cleaner failure reads as failed");

        // Provenance round-trips and survives With.
        var traced = fellBack with
        {
            CleanupOptions = new CleanupOptions(professional: true), WritingModeID = NewId(), WritingModeName = "Email", TranscriptionProvider = "local", TranscriptionModel = "synthetic-model",
            CleanupProvider = "customAPI", CleanupModel = "synthetic-cleanup", AppVersion = "1.0 (7)", ContextUsed = true, CleanupOutcome = CleanupPath.ModelPunctuationOnLocal, Edited = true,
        };
        var tracedTrip = RoundTrip(traced);
        c.Check(tracedTrip.CleanupOptions == new CleanupOptions(professional: true) && Json(tracedTrip) == Json(traced), "provenance round-trips");
        c.Check(older.CleanupOptions is null && older.WritingModeID is null && older.CleanupOutcome is null && older.Edited is null, "older history decodes without provenance");
        var retexted = traced.With("B.", InsertionStatus.Verified);
        c.Check(retexted.FinalText == "B." && retexted.WritingModeID == traced.WritingModeID && retexted.CleanupOptions == traced.CleanupOptions, "With keeps provenance");

        // Diagnostics round-trip, survive With, and older files decode without them.
        var diagnosed = traced with
        {
            Transcription = new TranscriptionDetail { Segments = [new SegmentDetail { Text = " Thanks.", Start = 1.5, End = 2, AvgLogprob = -0.4, NoSpeechProb = 0.1, CompressionRatio = 0.9, LoudWindows = 2, Dropped = true }], ClipSeconds = 2, PeakP50 = 0.02, PeakP90 = 0.3, SilencePeak = 0.25, SilenceThreshold = 0.01, Precheck = "passed" },
            Cleanup = new CleanupDetail { ModelOutput = "Hi.", GuardVerdict = "cleaned", PromptHash = "0123abcd", DictionaryTerms = ["Akme"], Input = "hi", ContextUsed = true, ContextApp = "notepad.exe", ContextChars = new() { ["selected"] = 0, ["clipboard"] = 12, ["window"] = 40 } },
            AfterPaste = new AfterPaste { Text = "Hi there.", Changed = true, Route = "ax", EndReason = "focus departed" },
            Timing = new StageTiming { Transcription = 0.4, Cleanup = 0.9, Insertion = 0.1, Total = 1.6, Capture = 0.44, Handoff = 0.01, InsertionDone = 0.2 },
        };
        diagnosed = diagnosed with { RecordingFile = diagnosed.Id + ".wav" };
        var diagnosedTrip = RoundTrip(diagnosed.With("Hi.", InsertionStatus.Verified));
        c.Check(Json(diagnosedTrip with { FinalText = diagnosed.FinalText, InsertionStatusRaw = diagnosed.InsertionStatusRaw, CleanupFallback = diagnosed.CleanupFallback, CleanupRejectedOutput = diagnosed.CleanupRejectedOutput }) == Json(diagnosed), "diagnostics round-trip and survive With");
        var olderTiming = JsonSerializer.Deserialize<StageTiming>("""{"transcription":0.4,"cleanup":0.4,"insertion":0.51,"total":1.8}""")!;
        c.Check(olderTiming.Capture is null && olderTiming.Handoff is null && olderTiming.InsertionDone is null && olderTiming.Total == 1.8, "timing from 0.2.3 decodes");
        c.Check(older.Transcription is null && older.Cleanup is null && older.AfterPaste is null && older.Timing is null && older.RecordingFile is null, "older history decodes without diagnostics");

        // Words typed after the dictation are never kept; edits of it and wholesale replacements are.
        var said = "Send it to Akme today.";
        c.Check(AfterPaste.TrimmingAppended(said, said + "\nuser@host secret: x1") == (said, false), "append-only is no edit and keeps nothing typed");
        c.Check(AfterPaste.TrimmingAppended(said, "Send it to Acme today. secret: x1") == ("Send it to Acme today.", true), "an edit keeps the fix, drops the append");
        c.Check(AfterPaste.TrimmingAppended(said, "Send it to Akme tomorrow. secret: x1") == ("Send it to Akme tomorrow.", true), "a fixed last word stays, the append goes");
        c.Check(AfterPaste.TrimmingAppended("TrueNASDOTCloud doesn't get that.", "truenas.clouddev.dad") == ("truenas.clouddev.dad", true), "a wholesale replacement is kept");
        c.Check(AfterPaste.TrimmingAppended(said, said + "\n") == (said, false), "whitespace alone is no edit");
        c.Done();
    }

    [Fact]
    public void StoreSaveLoadPermissionsAndExpiry()
    {
        var c = new Checker();
        var root = TempDirs.New("history");
        var store = new HistoryStore(root);
        var entry = Entry(at: Now.AddDays(-31));
        store.Save(entry);
        var loaded = store.Entries();
        c.Check(loaded.Count == 1 && loaded[0].RawText == entry.RawText, "atomic round trip");
        var path = Path.Combine(root, entry.Id + ".json");
        if (!OperatingSystem.IsWindows())
        {
            c.Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "entry file is 0600");
            c.Check(File.GetUnixFileMode(root) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "history folder is 0700");
        }
        c.Check(store.Expired(HistoryRetention.ThirtyDays, Now).Count == 1, "30-day expiry");
        c.Check(store.Expired(HistoryRetention.Forever, Now).Count == 0, "forever never expires");
        c.Check(!Directory.EnumerateFiles(root).Any(f => Path.GetFileName(f).StartsWith(".pending-")), "no temporary file is left behind");
        c.Check(HistoryStore.IsUuid(entry.Id) && !HistoryStore.IsUuid("../x") && !HistoryStore.IsUuid(Guid.NewGuid().ToString("N")), "only hyphenated UUIDs name entries");
        Assert.Throws<RetentionError>(() => store.Save(Entry(id: "../escape")));
        Assert.Throws<RetentionError>(() => store.Delete(Entry(id: "../escape")));

        // The six-month boundary follows calendar months.
        var sixMonths = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var boundary = Entry(at: HistoryRetention.SixMonths.Cutoff(sixMonths, TimeZoneInfo.Utc)!.Value, raw: "Calendar boundary");
        store.Save(boundary);
        c.Check(store.Expired(HistoryRetention.SixMonths, sixMonths, TimeZoneInfo.Utc).Any(e => e.Id == boundary.Id), "calendar boundary expires");
        store.Delete(entry); store.Delete(boundary);
        c.Check(store.Entries().Count == 0, "deletion");
        Assert.Throws<FileNotFoundException>(() => store.Delete(entry));
        c.Done();
    }

    [Fact]
    public async Task StartupRemovesOrphanWritesAndWaitsForTheWriter()
    {
        var c = new Checker();
        var root = TempDirs.New("history");
        var store = new HistoryStore(root);
        var entry = Entry();
        store.Save(entry);
        var abandoned = Path.Combine(root, ".pending-" + NewId());
        File.WriteAllBytes(abandoned, StoreJson.Encode(entry));
        _ = new HistoryStore(root);
        c.Check(!File.Exists(abandoned) && File.Exists(Path.Combine(root, entry.Id + ".json")), "startup removes orphan writes and keeps committed history");

        // A second opener must wait for an active writer rather than deleting its temporary output.
        var lockPath = Path.Combine(root, HistoryStore.LockFileName);
        var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllBytes(abandoned, StoreJson.Encode(entry));
        var opener = Task.Run(() => new HistoryStore(root));
        c.Check(await Task.WhenAny(opener, Task.Delay(150)) != opener && File.Exists(abandoned), "cleanup waits for the writer's lock");
        held.Dispose();
        await opener.WaitAsync(TimeSpan.FromSeconds(5));
        c.Check(!File.Exists(abandoned), "cleanup runs once the writer is done");
        c.Done();
    }

    [Fact]
    public void DamagedFilesAreMovedAsideNotDeleted()
    {
        var c = new Checker();
        var root = TempDirs.New("history");
        var store = new HistoryStore(root);
        var entry = Entry();
        store.Save(entry);

        var mismatched = Path.Combine(root, NewId() + ".json");
        File.WriteAllBytes(mismatched, StoreJson.Encode(entry));
        var before = store.Entries().Count;
        var movedMismatch = Directory.GetFiles(store.UnreadableDirectory);
        c.Check(before == 1 && movedMismatch.Length == 1 && !File.Exists(mismatched), "a file named for another id is quarantined, not accepted");
        File.Delete(movedMismatch[0]);

        var corrupt = Path.Combine(root, NewId() + ".json");
        File.WriteAllText(corrupt, "broken JSON");
        var movedCount = store.QuarantineUnreadable();
        c.Check(movedCount == 1 && store.Entries().Count == 1, "startup moves it aside instead of refusing to start");
        var moved = Directory.GetFiles(store.UnreadableDirectory);
        c.Check(moved.Length == 1 && !File.Exists(corrupt), "the damaged file is in unreadable/");

        // It still expires by modification date, and only then.
        store.Purge(HistoryRetention.SixMonths, Now);
        c.Check(File.Exists(moved[0]), "a fresh damaged file stays");
        File.SetLastWriteTimeUtc(moved[0], Now.AddDays(-200));
        store.Purge(HistoryRetention.Forever, Now);
        c.Check(File.Exists(moved[0]), "forever keeps it");
        store.Purge(HistoryRetention.SixMonths, Now);
        c.Check(!File.Exists(moved[0]), "an old damaged file expires with the retention");

        // A file damaged while the app runs: Entries returns the others, moves it aside, says so once per move, and saving still works.
        var told = new List<int>();
        var live = new HistoryStore(root, told.Add);
        var others = Enumerable.Range(0, 3).Select(i => Entry(at: Now.AddSeconds(i), raw: $"Mid-session {i}", final: $"Mid-session {i}", status: "verified")).ToList();
        foreach (var other in others) live.Save(other);
        File.WriteAllText(Path.Combine(root, others[1].Id + ".json"), "broken JSON");
        var survivors = live.Entries();
        c.Check(survivors.Select(e => e.Id).ToHashSet().IsSupersetOf([others[0].Id, others[2].Id]) && survivors.All(e => e.Id != others[1].Id), "a damaged file does not hide the others");
        c.Check(Directory.GetFiles(live.UnreadableDirectory).Length == 1 && !File.Exists(Path.Combine(root, others[1].Id + ".json")), "the damaged file is moved aside, not deleted");
        c.Check(told.SequenceEqual([1]) && live.Entries().Count == survivors.Count, "told once, not on every read");
        live.Save(others[1]);
        c.Check(live.Entries().Count == survivors.Count + 1, "saving still works");
        c.Check(HistoryStore.QuarantineNotice(1).StartsWith("1 damaged history file was moved") && HistoryStore.QuarantineNotice(2).Contains("files were"), "notice wording");
        c.Done();
    }

    [Fact]
    public void EntriesIgnoreLinkedFilesAndRejectALinkedFolder()
    {
        var root = TempDirs.New("history");
        var store = new HistoryStore(root);
        var outside = TempDirs.New("outside"); Directory.CreateDirectory(outside);
        var entry = Entry();
        File.WriteAllBytes(Path.Combine(outside, entry.Id + ".json"), StoreJson.Encode(entry));
        try { File.CreateSymbolicLink(Path.Combine(root, entry.Id + ".json"), Path.Combine(outside, entry.Id + ".json")); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        Assert.Empty(store.Entries());
        Assert.True(File.Exists(Path.Combine(outside, entry.Id + ".json")), "a linked entry is neither read nor moved");

        var linked = TempDirs.New("linked");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        if (!Links.TryCreate(linked, outside, false)) return;
        Assert.Throws<RetentionError>(() => new HistoryStore(linked));
    }
}
