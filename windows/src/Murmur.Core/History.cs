// Port of app/History.swift.
using System.Text.Json.Serialization;

namespace Murmur.Core;

// History entries and their private store. The entry JSON uses the Swift field names and Swift's date form (seconds since 2001-01-01),
// so a file written by either app reads in the other.

/// <summary>The stored <c>insertionStatus</c> strings. The stored property stays a string so an entry with a status this version does not know
/// still decodes and re-saves unchanged; read it through <see cref="HistoryEntry.Status"/>.</summary>
public enum InsertionStatus { SavedNotInserted, InsertionPending, HeldCleanup, HeldTranscript, Copied, Failed, Verified, Unverified, Edited }

public static class InsertionStatuses
{
    public static string Raw(this InsertionStatus status) => status switch
    {
        InsertionStatus.SavedNotInserted => "saved-not-inserted",
        InsertionStatus.InsertionPending => "insertion-pending",
        InsertionStatus.HeldCleanup => "held-cleanup",
        InsertionStatus.HeldTranscript => "held-transcript",
        InsertionStatus.Copied => "copied",
        InsertionStatus.Failed => "failed",
        InsertionStatus.Verified => "verified",
        InsertionStatus.Unverified => "unverified",
        _ => "edited",
    };

    public static InsertionStatus? Parse(string raw) => Enum.GetValues<InsertionStatus>().Where(s => s.Raw() == raw).Select(s => (InsertionStatus?)s).FirstOrDefault();
}

public sealed record HistoryEntry : IRetainedHistoryItem
{
    readonly DateTime recordedAt;

    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("recordedAt"), JsonConverter(typeof(SwiftDateConverter))]
    public required DateTime RecordedAt { get => recordedAt; init => recordedAt = SwiftDate.Normalize(value); }
    [JsonPropertyName("rawText")] public required string RawText { get; init; }
    /// <summary>Editable in History; <see cref="RawText"/> never changes.</summary>
    [JsonPropertyName("finalText")] public required string FinalText { get; init; }
    /// <summary>The stored status string (Swift <c>insertionStatus</c>).</summary>
    [JsonPropertyName("insertionStatus")] public required string InsertionStatusRaw { get; init; }
    [JsonIgnore] public InsertionStatus? Status => InsertionStatuses.Parse(InsertionStatusRaw);
    /// <summary>Why the full cleanup was not used (a content-free <see cref="CleanupFailureReason"/>), or null.</summary>
    [JsonPropertyName("cleanupFallback")] public string? CleanupFallback { get; init; }
    /// <summary>The cleanup model's output the guard rejected, kept for debugging. It is dictation text, so it lives and expires with the rest of the entry.</summary>
    [JsonPropertyName("cleanupRejectedOutput")] public string? CleanupRejectedOutput { get; init; }
    /// <summary>Recording length in seconds, for Insights' pace and time saved.</summary>
    [JsonPropertyName("seconds")] public double? Seconds { get; init; }
    /// <summary>The app the text was pasted into (a lowercased process name on Windows), for Insights; null when unknown or not pasted.</summary>
    [JsonPropertyName("app")] public string? App { get; init; }
    // Provenance: identifiers and settings only, never text, context or keys.
    /// <summary>Cleanup options in effect, after any writing mode's override.</summary>
    [JsonPropertyName("cleanupOptions")] public CleanupOptions? CleanupOptions { get; init; }
    [JsonPropertyName("writingModeID")] public string? WritingModeID { get; init; }
    [JsonPropertyName("writingModeName")] public string? WritingModeName { get; init; }
    /// <summary><c>ProviderID</c> raw value, or "local" for this PC's model.</summary>
    [JsonPropertyName("transcriptionProvider")] public string? TranscriptionProvider { get; init; }
    [JsonPropertyName("transcriptionModel")] public string? TranscriptionModel { get; init; }
    /// <summary><c>ProviderID</c> raw value, or "local-rules".</summary>
    [JsonPropertyName("cleanupProvider")] public string? CleanupProvider { get; init; }
    [JsonPropertyName("cleanupModel")] public string? CleanupModel { get; init; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; init; }
    /// <summary>Whether screen context was sent to the cleanup model.</summary>
    [JsonPropertyName("contextUsed")] public bool? ContextUsed { get; init; }
    [JsonPropertyName("cleanupOutcome")] public CleanupPath? CleanupOutcome { get; init; }
    /// <summary>True once the user edits <see cref="FinalText"/> in History.</summary>
    [JsonPropertyName("edited")] public bool? Edited { get; init; }
    /// <summary>How many voice cues were expanded into <see cref="FinalText"/>; never which ones.</summary>
    [JsonPropertyName("cues")] public int? Cues { get; init; }
    // Diagnostics: what a developer needs to see why a dictation came out as it did. Dictation text lives only here, never in a log.
    [JsonPropertyName("transcription")] public TranscriptionDetail? Transcription { get; init; }
    [JsonPropertyName("cleanup")] public CleanupDetail? Cleanup { get; init; }
    [JsonPropertyName("afterPaste")] public AfterPaste? AfterPaste { get; init; }
    [JsonPropertyName("timing")] public StageTiming? Timing { get; init; }
    /// <summary>File name in <c>history/recordings</c> when "Keep recordings for diagnostics" was on; purged after 7 days.</summary>
    [JsonPropertyName("recordingFile")] public string? RecordingFile { get; init; }
    /// <summary>A voice edit: the selection before the edit (<see cref="RawText"/> is the spoken instruction, <see cref="FinalText"/> the result).</summary>
    [JsonPropertyName("editOriginal")] public string? EditOriginal { get; init; }

    /// <summary>The same dictation with new text and status; the recording facts carry over.</summary>
    public HistoryEntry With(string finalText, InsertionStatus? status = null, string? fallback = null, string? rejected = null) =>
        this with { FinalText = finalText, InsertionStatusRaw = status?.Raw() ?? InsertionStatusRaw, CleanupFallback = fallback, CleanupRejectedOutput = rejected };

    // History's wording for an entry, kept beside the status type.
    [JsonIgnore]
    public bool NeedsAttention => Status is InsertionStatus.InsertionPending or InsertionStatus.Failed or InsertionStatus.HeldCleanup or InsertionStatus.HeldTranscript;

    /// <summary>The entry History opens expanded: the last dictation's, while it still needs attention, was saved without being pasted, or was a voice edit.</summary>
    public static string? RecoveryEntryToOpen(string? id, IEnumerable<HistoryEntry> history) =>
        history.FirstOrDefault(e => e.Id == id && (e.NeedsAttention || e.Status is InsertionStatus.SavedNotInserted or InsertionStatus.Edited))?.Id;

    [JsonIgnore]
    public string? StatusLabel => Status switch
    {
        InsertionStatus.Verified or InsertionStatus.Unverified => null,
        InsertionStatus.InsertionPending => "Saved",
        InsertionStatus.Failed => "Not inserted",
        InsertionStatus.Copied => "Copied",
        InsertionStatus.Edited => "Edited",
        InsertionStatus.HeldCleanup => "Cleanup held",
        InsertionStatus.HeldTranscript => "Held for review",
        _ => "Saved",
    };

    [JsonIgnore]
    public string? StatusNote => Status switch
    {
        InsertionStatus.InsertionPending => "Murmur stopped before saving the delivery result. Check the destination before copying this text to avoid a duplicate.",
        InsertionStatus.Failed => "Murmur could not paste this text. It is saved here so you can copy it.",
        InsertionStatus.HeldCleanup => "Cleanup changed too much, so only the raw transcript was saved and nothing was inserted.",
        InsertionStatus.HeldTranscript => "The transcript was held for review, so nothing was inserted.",
        _ => null,
    };

    /// <summary>Which path wrote the final text, in one line; null for entries saved before it was recorded.</summary>
    [JsonIgnore]
    public string? OutcomeNote
    {
        get
        {
            if (CleanupOutcome is not { } outcome) return null;
            var kind = CleanupOptions?.Professional == true ? "Professional rewrite" : CleanupOptions?.Polish == true ? "Polish" : "AI cleanup";
            var failed = CleanupFallback is null || CleanupFallback == CleanupFailureReason.CleanerFailed.ToString().ToLowerFirst();
            var why = failed ? "" : " (" + string.Concat(SwiftText.Characters(CleanupFallback ?? "").Select(c => SwiftText.IsUppercase(c) ? " " + SwiftText.Lowercased(c) : c)) + ")";
            var edited = Edited == true ? " Edited since." : "";
            return outcome switch
            {
                CleanupPath.Off => "Cleanup off; dictionary only." + edited,
                CleanupPath.ModelAccepted => $"{kind} applied." + edited,
                CleanupPath.ModelPunctuationOnLocal => $"{kind} rejected{why}; AI punctuation applied to local cleanup." + edited,
                CleanupPath.LocalRules => $"{kind} {(failed ? "failed" : "rejected")}{why}; local cleanup used." + edited,
                _ => "Local cleanup on this PC." + edited,
            };
        }
    }
}

static class StringCase
{
    /// <summary>"CleanerFailed" to "cleanerFailed": the Swift raw value of a failure reason.</summary>
    public static string ToLowerFirst(this string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>One Whisper segment as parsed, with the loud-window count under it and whether the parser dropped it.</summary>
public sealed record SegmentDetail
{
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("start")] public double? Start { get; init; }
    [JsonPropertyName("end")] public double? End { get; init; }
    [JsonPropertyName("avgLogprob")] public double? AvgLogprob { get; init; }
    [JsonPropertyName("noSpeechProb")] public double? NoSpeechProb { get; init; }
    [JsonPropertyName("compressionRatio")] public double? CompressionRatio { get; init; }
    [JsonPropertyName("loudWindows")] public int? LoudWindows { get; init; }
    [JsonPropertyName("dropped")] public bool Dropped { get; init; }
}

/// <summary>What the transcriber heard and how loud the clip was. Segments only for Whisper verbose_json.</summary>
public sealed record TranscriptionDetail
{
    [JsonPropertyName("segments")] public List<SegmentDetail>? Segments { get; init; }
    [JsonPropertyName("clipSeconds")] public double? ClipSeconds { get; init; }
    /// <summary>Median and 90th-percentile peak of the clip's 100 ms windows.</summary>
    [JsonPropertyName("peakP50")] public double? PeakP50 { get; init; }
    [JsonPropertyName("peakP90")] public double? PeakP90 { get; init; }
    /// <summary>The silence pre-check's level (second-highest window peak) and the threshold it was held against.</summary>
    [JsonPropertyName("silencePeak")] public double? SilencePeak { get; init; }
    [JsonPropertyName("silenceThreshold")] public double? SilenceThreshold { get; init; }
    /// <summary>"passed", or "unchecked" when the provider has no pre-check or the audio could not be decoded.</summary>
    [JsonPropertyName("precheck")] public string? Precheck { get; init; }
}

/// <summary>The cleanup model's exchange, without any screen text.</summary>
public sealed record CleanupDetail
{
    /// <summary>The model's exact output where the guard accepted it. A rejected output is <see cref="HistoryEntry.CleanupRejectedOutput"/>.</summary>
    [JsonPropertyName("modelOutput")] public string? ModelOutput { get; init; }
    /// <summary>"cleaned", "unchanged", "rejected" or "provider-error", one per section, comma-separated.</summary>
    [JsonPropertyName("guardVerdict")] public string? GuardVerdict { get; init; }
    [JsonPropertyName("providerError")] public string? ProviderError { get; init; }
    /// <summary>Short hash of the system prompt, to correlate prompt changes with results.</summary>
    [JsonPropertyName("promptHash")] public string? PromptHash { get; init; }
    [JsonPropertyName("dictionaryTerms")] public List<string>? DictionaryTerms { get; init; }
    /// <summary>The text sent for cleanup when the dictionary changed it from <c>rawText</c>.</summary>
    [JsonPropertyName("input")] public string? Input { get; init; }
    [JsonPropertyName("contextUsed")] public bool? ContextUsed { get; init; }
    /// <summary>The app dictated into, and the characters of each context part sent. Never the context.</summary>
    [JsonPropertyName("contextApp")] public string? ContextApp { get; init; }
    [JsonPropertyName("contextChars")] public Dictionary<string, int>? ContextChars { get; init; }
}

/// <summary>The pasted span as last observed when the correction watch closed. Text and <see cref="Changed"/> are null when the watch lost the span
/// (the reason is in <see cref="EndReason"/>), so "unchanged" is never claimed without evidence.</summary>
public sealed record AfterPaste
{
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("changed")] public bool? Changed { get; init; }
    /// <summary>"ax" (read back from the field) or "keys" (followed by keystrokes). On Windows "ax" is UI Automation.</summary>
    [JsonPropertyName("route")] public string? Route { get; init; }
    [JsonPropertyName("endReason")] public string? EndReason { get; init; }

    /// <summary>The span as the user left it, minus words typed after the dictation. Those are not an edit of it and can be anything, such as a password
    /// typed into the same composer, so History never keeps them. Replaced words stay, one for one; <c>changed</c> is by words, so whitespace alone is not an edit.
    /// Word alignment only; a dictation replaced wholesale is kept as typed.</summary>
    public static (string Text, bool Changed) TrimmingAppended(string initial, string edited)
    {
        var said = Words(initial);
        var seen = Words(edited);
        var (removed, inserted) = WordDifference.Of(said, seen);
        int lastKeptSeen = -1, lastKeptSaid = -1;
        for (int i = seen.Length - 1; i >= 0; i--) if (!inserted.Contains(i)) { lastKeptSeen = i; break; }
        for (int i = said.Length - 1; i >= 0; i--) if (!removed.Contains(i)) { lastKeptSaid = i; break; }
        // Dictated words after the last kept one were replaced; as many typed words stand in for them.
        int replacedTail = said.Length - 1 - lastKeptSaid;
        var kept = seen.Take(lastKeptSeen + 1 + Math.Min(replacedTail, seen.Length - lastKeptSeen - 1)).ToArray();
        return (string.Join(" ", kept), !kept.SequenceEqual(said));
    }

    static string[] Words(string text)
    {
        var words = new List<string>();
        var current = new List<string>();
        foreach (var c in SwiftText.Characters(text))
        {
            if (SwiftText.IsWhitespace(c)) { if (current.Count > 0) words.Add(string.Concat(current)); current.Clear(); }
            else current.Add(c);
        }
        if (current.Count > 0) words.Add(string.Concat(current));
        return [.. words];
    }
}

/// <summary>Swift's <c>Collection.difference(from:)</c>: which words of <c>said</c> were removed and which of <c>seen</c> were inserted.
/// A line-for-line port of the linear-space Myers algorithm in swift/stdlib/public/core/Diffing.swift (Apache License 2.0 with Runtime
/// Library Exception), so that when several shortest edit scripts exist the same words are kept as on the Mac. It matches the toolchain recorded in
/// windows/golden/toolchain.txt; a Swift release that changes the algorithm shows up as a failing golden case.</summary>
static class WordDifference
{
    record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int Size => Width + Height;
        public int Delta => Width - Height;
        public bool IsOdd => Delta % 2 != 0;
        public bool IsEven => !IsOdd;
        public int Max => (Size + 1) / 2;
        public Rect CroppedToTopLeftOf(Rect limit) => new(Left, Top, limit.Left, limit.Top);
        public Rect CroppedToBottomRightOf(Rect limit) => new(limit.Right, limit.Bottom, Right, Bottom);

        public Rect Shrunk(string[] old, string[] @new)
        {
            var r = this;
            while (r.Left < r.Right && r.Top < r.Bottom && old[r.Left] == @new[r.Top]) { r.Left++; r.Top++; }
            while (r.Right > r.Left && r.Bottom > r.Top && old[r.Right - 1] == @new[r.Bottom - 1]) { r.Right--; r.Bottom--; }
            return r;
        }
    }

    /// <summary>An edit: a removal at an offset of <c>said</c>, or an insertion at an offset of <c>seen</c>.</summary>
    readonly record struct Edit(bool IsInsertion, int Offset);

    sealed class KVector(int size)
    {
        // Two k-vectors in one buffer, zero-filled once and reused across boxes, as in Swift (stale entries are part of the behavior).
        readonly int[] buffer = new int[(size * 2 + 1) * 2];
        readonly int forwardOffset = size, backwardOffset = (size * 2 + 1) + size;
        public int Forward(int i) => buffer[forwardOffset + i];
        public void SetForward(int i, int v) => buffer[forwardOffset + i] = v;
        public int Backward(int i) => buffer[backwardOffset + i];
        public void SetBackward(int i, int v) => buffer[backwardOffset + i] = v;
    }

    public static (HashSet<int> Removed, HashSet<int> Inserted) Of(string[] said, string[] seen)
    {
        var box = new Rect(0, 0, said.Length, seen.Length).Shrunk(said, seen);
        var k = new KVector(box.Size);
        var result = new List<Edit>();
        var stack = new List<Rect>();
        var current = box;
        while (true)
        {
            var (edit, snake) = MiddleSnake(k, current, said, seen);
            if (edit is { } e) result.Add(e);
            if (snake is not { } s)
            {
                if (stack.Count == 0) break;
                current = stack[^1]; stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var head = current.CroppedToTopLeftOf(s).Shrunk(said, seen);
            var tail = current.CroppedToBottomRightOf(s).Shrunk(said, seen);
            current = head;
            stack.Add(tail);
        }
        return (result.Where(e => !e.IsInsertion).Select(e => e.Offset).ToHashSet(), result.Where(e => e.IsInsertion).Select(e => e.Offset).ToHashSet());
    }

    static (Edit? Edit, Rect? Snake) MiddleSnake(KVector k, Rect box, string[] old, string[] @new)
    {
        if (box.Size <= 1)
        {
            if (box.Size == 0) return (null, null);
            return box.Width == 1 ? (new Edit(false, box.Left), null) : (new Edit(true, box.Top), null);
        }
        k.SetForward(1, box.Left);
        k.SetBackward(1, box.Bottom);
        for (int depth = 0; depth <= box.Max; depth++)
        {
            if (box.IsOdd)
            {
                if (ForwardSearch(k, box, depth, old, @new) is { } r) return r;
                BackwardSearch(k, box, depth, old, @new);
            }
            else
            {
                ForwardSearch(k, box, depth, old, @new);
                if (BackwardSearch(k, box, depth, old, @new) is { } r) return r;
            }
        }
        throw new InvalidOperationException("Unreachable");
    }

    static (Edit, Rect)? ForwardSearch(KVector kv, Rect box, int depth, string[] old, string[] @new)
    {
        for (int k = depth; k >= -depth; k -= 2)
        {
            int newRight, newBottom, newLeft, newTop, editIndex;
            bool isInsertion;
            if (k == -depth || (k != depth && kv.Forward(k - 1) < kv.Forward(k + 1)))
            {
                newRight = kv.Forward(k + 1);
                newLeft = newRight;
                newBottom = box.Top + (newRight - box.Left) - k;
                isInsertion = true;
                editIndex = newBottom - 1;
            }
            else
            {
                newLeft = kv.Forward(k - 1);
                newRight = newLeft + 1;
                newBottom = box.Top + (newRight - box.Left) - k;
                isInsertion = false;
                editIndex = newLeft;
            }
            newTop = depth == 0 || newRight != newLeft ? newBottom : newBottom - 1;
            while (newRight < box.Right && newBottom < box.Bottom && old[newRight] == @new[newBottom]) { newRight++; newBottom++; }
            kv.SetForward(k, newRight);
            if (!box.IsOdd) continue;
            int c = k - box.Delta;
            if (c >= -(depth - 1) && c <= depth - 1 && newBottom >= kv.Backward(c))
                return (new Edit(isInsertion, editIndex), new Rect(newLeft, newTop, newRight, newBottom));
        }
        return null;
    }

    static (Edit, Rect)? BackwardSearch(KVector kv, Rect box, int depth, string[] old, string[] @new)
    {
        for (int c = depth; c >= -depth; c -= 2)
        {
            int k = c + box.Delta;
            int newLeft, newTop, newRight, newBottom, editIndex;
            bool isInsertion;
            if (c == -depth || (c != depth && kv.Backward(c - 1) > kv.Backward(c + 1)))
            {
                newTop = kv.Backward(c + 1);
                newBottom = newTop;
                newLeft = box.Left + (newTop - box.Top) + k;
                isInsertion = false;
                editIndex = newLeft;
            }
            else
            {
                newBottom = kv.Backward(c - 1);
                newTop = newBottom - 1;
                newLeft = box.Left + (newTop - box.Top) + k;
                isInsertion = true;
                editIndex = newTop;
            }
            newRight = depth == 0 || newTop != newBottom ? newLeft : newLeft + 1;
            while (newLeft > box.Left && newTop > box.Top && old[newLeft - 1] == @new[newTop - 1]) { newLeft--; newTop--; }
            kv.SetBackward(c, newTop);
            if (!box.IsEven) continue;
            if (k >= -depth && k <= depth && newLeft <= kv.Forward(k))
                return (new Edit(isInsertion, editIndex), new Rect(newLeft, newTop, newRight, newBottom));
        }
        return null;
    }
}

/// <summary>Seconds per stage, in pipeline order, from the key release: capture (release until transcription starts), transcription, cleanup,
/// handoff (cleanup done until insertion starts), insertion, then total. Insertion ends when the text is visible; <c>insertionDone</c> is the insertion call's whole duration.</summary>
public sealed record StageTiming
{
    [JsonPropertyName("transcription")] public double? Transcription { get; init; }
    [JsonPropertyName("cleanup")] public double? Cleanup { get; init; }
    [JsonPropertyName("insertion")] public double? Insertion { get; init; }
    [JsonPropertyName("total")] public double? Total { get; init; }
    [JsonPropertyName("capture")] public double? Capture { get; init; }
    [JsonPropertyName("handoff")] public double? Handoff { get; init; }
    [JsonPropertyName("insertionDone")] public double? InsertionDone { get; init; }
}

/// <summary>History entries as private files in one folder: <c>&lt;uuid&gt;.json</c>, written atomically; damaged files are moved to <c>unreadable/</c>;
/// writers and cleanup share a lock file in the folder, so cleanup cannot remove a live save. Links are never followed.</summary>
public sealed class HistoryStore
{
    public const string LockFileName = ".history.lock";
    readonly Action<int> onQuarantine;
    public string Directory { get; }

    /// <param name="onQuarantine">Told how many unreadable files <see cref="Entries"/> just moved aside; may run on any thread.</param>
    public HistoryStore(string directory, Action<int>? onQuarantine = null)
    {
        Directory = directory;
        this.onQuarantine = onQuarantine ?? (_ => { });
        if (File.Exists(directory) || (System.IO.Directory.Exists(directory) && FileLinks.IsLink(directory))) throw new RetentionError("History requires a real directory");
        PrivateFiles.PrepareDirectory(directory);
        RemovePendingFiles();
    }

    public static bool IsUuid(string text) => Guid.TryParseExact(text, "D", out _);

    /// <summary>Held while a writer or cleanup runs, across threads and processes. Waits for another holder.</summary>
    FileStream AcquireLock()
    {
        var path = Path.Combine(Directory, LockFileName);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, options); }
            catch (IOException) when (File.Exists(path) && DateTime.UtcNow < deadline) { Thread.Sleep(5); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new RetentionError("Cannot lock history directory"); }
        }
    }

    T Locked<T>(Func<T> operation)
    {
        using var held = AcquireLock();
        return operation();
    }

    void RemovePendingFiles() => Locked(() =>
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory).ToList())
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(".pending-", StringComparison.Ordinal) && IsUuid(name[9..])) File.Delete(file);
        }
        return 0;
    });

    public void Save(HistoryEntry entry) => Locked(() => { SaveLocked(entry); return 0; });

    void SaveLocked(HistoryEntry entry)
    {
        if (!IsUuid(entry.Id)) throw new RetentionError("Invalid history identifier");
        var target = Path.Combine(Directory, entry.Id + ".json");
        var temporary = Path.Combine(Directory, ".pending-" + Guid.NewGuid().ToString().ToUpperInvariant());
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) { stream.Write(StoreJson.Encode(entry)); stream.Flush(true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    /// <summary>The entry files: <c>.json</c>, not links.</summary>
    List<string> EntryFiles() => System.IO.Directory.EnumerateFiles(Directory).Where(f => Path.GetExtension(f) == ".json" && !FileLinks.IsLink(f)).ToList();

    /// <summary>The one validity rule: the file decodes and its name is its entry's id. <see cref="Entries"/> and <see cref="QuarantineUnreadable"/> move a file that fails it aside.</summary>
    HistoryEntry ReadEntry(string file)
    {
        var entry = StoreJson.Decode<HistoryEntry>(File.ReadAllBytes(file));
        if (!IsUuid(entry.Id) || Path.GetFileName(file) != entry.Id + ".json") throw new RetentionError("History identifier does not match its file");
        return entry;
    }

    HistoryEntry? TryReadEntry(string file)
    {
        try { return ReadEntry(file); }
        catch (Exception e) when (e is RetentionError or StoreError or System.Text.Json.JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Newest first. A file that cannot be read is quarantined, so one damaged file never hides the others.</summary>
    public List<HistoryEntry> Entries()
    {
        var readable = new List<HistoryEntry>();
        int moved = 0;
        foreach (var file in EntryFiles())
        {
            if (TryReadEntry(file) is { } entry) readable.Add(entry);
            else if (Quarantine(file)) moved++;
        }
        if (moved > 0) onQuarantine(moved);
        return readable.OrderByDescending(e => e.RecordedAt).ToList();
    }

    public void Delete(HistoryEntry entry)
    {
        if (!IsUuid(entry.Id)) throw new RetentionError("Invalid history identifier");
        var path = Path.Combine(Directory, entry.Id + ".json");
        if (!FileLinks.Exists(path)) throw new FileNotFoundException("The history entry is already gone.");
        FileLinks.DeleteTree(path);
    }

    public List<HistoryEntry> Expired(HistoryRetention retention, DateTime? now = null, TimeZoneInfo? zone = null) =>
        retention.Cutoff(now ?? DateTime.UtcNow, zone) is { } cutoff ? Entries().Where(e => e.RecordedAt <= cutoff).ToList() : [];

    /// <summary>Files moved aside by <see cref="QuarantineUnreadable"/>. Inside the history folder, so Delete All removes them.</summary>
    public string UnreadableDirectory => Path.Combine(Directory, "unreadable");

    public static string QuarantineNotice(int moved) =>
        $"{moved} damaged history {(moved == 1 ? "file was" : "files were")} moved to the history/unreadable folder. {(moved == 1 ? "It" : "They")} still expire with your retention setting.";

    /// <summary>Moves history files that cannot be decoded (or whose name does not match their id) into <c>unreadable/</c>, so one damaged file cannot
    /// stop Murmur from starting. Returns how many moved.</summary>
    public int QuarantineUnreadable()
    {
        int moved = 0;
        foreach (var file in EntryFiles()) if (TryReadEntry(file) is null && Quarantine(file)) moved++;
        return moved;
    }

    /// <summary>False when another reader already moved the file.</summary>
    bool Quarantine(string file) => Locked(() =>
    {
        if (!File.Exists(file)) return false;
        PrivateFiles.PrepareDirectory(UnreadableDirectory);
        File.Move(file, Path.Combine(UnreadableDirectory, Guid.NewGuid().ToString().ToUpperInvariant() + ".json"));
        return true;
    });

    /// <summary>Expires entries, and unreadable files by their modification date: their text still counts as history. Skips a linked
    /// <c>unreadable</c> folder and any linked file in it.</summary>
    public void Purge(HistoryRetention retention, DateTime? now = null, TimeZoneInfo? zone = null)
    {
        var current = now ?? DateTime.UtcNow;
        foreach (var entry in Expired(retention, current, zone)) Delete(entry);
        RemovePendingFiles();
        if (retention.Cutoff(current, zone) is not { } cutoff) return;
        if (FileLinks.IsLink(UnreadableDirectory) || !System.IO.Directory.Exists(UnreadableDirectory)) return;
        foreach (var file in System.IO.Directory.EnumerateFileSystemEntries(UnreadableDirectory).ToList())
        {
            if (FileLinks.IsLink(file)) continue;
            // A file whose date cannot be read never expires.
            DateTime modified;
            try { modified = File.GetLastWriteTimeUtc(file); } catch (IOException) { continue; }
            if (modified <= cutoff) FileLinks.DeleteTree(file);
        }
    }
}
