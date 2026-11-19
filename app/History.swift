import Foundation
import Darwin

/// The stored `insertionStatus` strings. The stored property stays a String so an entry with a status
/// this version does not know still decodes and re-saves unchanged; read it through `HistoryEntry.status`.
enum InsertionStatus: String {
    case savedNotInserted = "saved-not-inserted", insertionPending = "insertion-pending", heldCleanup = "held-cleanup"
    case heldTranscript = "held-transcript", copied, failed, verified, unverified, edited
}

struct HistoryEntry: Codable, Identifiable, RetainedHistoryItem {
    let id: String
    let recordedAt: Date
    let rawText: String
    /// Editable in History; `rawText` never changes.
    var finalText: String
    var insertionStatus: String
    var status: InsertionStatus? { InsertionStatus(rawValue: insertionStatus) }
    /// Why the full cleanup was not used (a content-free `CleanupFailureReason`), or nil.
    var cleanupFallback: String? = nil
    /// The cleanup model's output the guard rejected, kept for debugging (user request, October 6,
    /// 2026). It is dictation text, so it lives and expires with the rest of the entry.
    var cleanupRejectedOutput: String? = nil
    /// Recording length in seconds, for Insights' pace and time saved; nil before October 15, 2026.
    var seconds: Double? = nil
    /// Bundle ID of the app the text was pasted into, for Insights; nil when unknown or not pasted.
    var app: String? = nil
    // Provenance (October 21, 2026): identifiers and settings only, never text, context or keys. Nil before then.
    /// Cleanup options in effect, after any writing mode's override.
    var cleanupOptions: CleanupOptions? = nil
    var writingModeID: String? = nil
    var writingModeName: String? = nil
    /// `ProviderID` raw value, or "local" for this Mac's model.
    var transcriptionProvider: String? = nil
    var transcriptionModel: String? = nil
    /// `ProviderID` raw value, or "local-rules".
    var cleanupProvider: String? = nil
    var cleanupModel: String? = nil
    var appVersion: String? = nil
    /// Whether screen context was sent to the cleanup model.
    var contextUsed: Bool? = nil
    var cleanupOutcome: CleanupPath? = nil
    /// True once the user edits `finalText` in History.
    var edited: Bool? = nil
    /// How many voice cues were expanded into `finalText`; never which ones. Nil when none, and before cues existed.
    var cues: Int? = nil
    // Diagnostics (October 22, 2026): what a developer needs to see why a dictation came out as it did.
    // Dictation text lives only here, with the entry's permissions and retention, never in the unified log. Nil before then.
    var transcription: TranscriptionDetail? = nil
    var cleanup: CleanupDetail? = nil
    var afterPaste: AfterPaste? = nil
    var timing: StageTiming? = nil
    /// File name in `history/recordings` when "Keep recordings for diagnostics" was on; purged after 7 days.
    var recordingFile: String? = nil
    /// A voice edit: the selection before the edit (`rawText` is the spoken instruction, `finalText` the result). Local only; expires with the entry.
    var editOriginal: String? = nil

    /// The same dictation with new text and status; the recording facts carry over.
    func with(finalText: String, status: InsertionStatus? = nil, fallback: String? = nil, rejected: String? = nil) -> HistoryEntry {
        var next = self
        next.finalText = finalText; if let status { next.insertionStatus = status.rawValue }; next.cleanupFallback = fallback; next.cleanupRejectedOutput = rejected
        return next
    }
}

/// One Whisper segment as parsed, with the loud-window count under it and whether the parser dropped it.
struct SegmentDetail: Codable, Equatable {
    var text: String
    var start: Double? = nil, end: Double? = nil
    var avgLogprob: Double? = nil, noSpeechProb: Double? = nil, compressionRatio: Double? = nil
    var loudWindows: Int? = nil
    var dropped = false
}

/// What the transcriber heard and how loud the clip was. Segments only for Whisper verbose_json.
struct TranscriptionDetail: Codable, Equatable {
    var segments: [SegmentDetail]? = nil
    var clipSeconds: Double? = nil
    /// Median and 90th-percentile peak of the clip's 100 ms windows.
    var peakP50: Double? = nil, peakP90: Double? = nil
    /// The silence pre-check's level (second-highest window peak) and the threshold it was held against.
    var silencePeak: Double? = nil, silenceThreshold: Double? = nil
    /// "passed", or "unchecked" when the provider has no pre-check or the audio could not be decoded.
    var precheck: String? = nil
}

/// The cleanup model's exchange, without any screen text.
struct CleanupDetail: Codable, Equatable {
    /// The model's exact output where the guard accepted it. A rejected output is `cleanupRejectedOutput`.
    var modelOutput: String? = nil
    /// "cleaned", "unchanged", "rejected" or "provider-error", one per section, comma-separated.
    var guardVerdict: String? = nil
    var providerError: String? = nil
    /// Short hash of the system prompt, to correlate prompt changes with results.
    var promptHash: String? = nil
    var dictionaryTerms: [String]? = nil
    /// The text sent for cleanup when the dictionary changed it from `rawText`.
    var input: String? = nil
    var contextUsed: Bool? = nil
    /// Bundle ID of the app dictated into, and the characters of each context part sent. Never the context.
    var contextApp: String? = nil
    var contextChars: [String: Int]? = nil
}

/// The pasted span as last observed when the correction watch closed. Text and `changed` are nil when
/// the watch lost the span (the reason is in `endReason`), so "unchanged" is never claimed without evidence.
struct AfterPaste: Codable, Equatable {
    var text: String? = nil
    var changed: Bool? = nil
    /// "ax" (read back from the field) or "keys" (followed by keystrokes).
    var route: String? = nil
    var endReason: String? = nil

    /// The span as the user left it, minus words typed after the dictation. Those are not an edit of it
    /// and can be anything, such as a password typed into the same composer, so History never keeps them.
    /// Replaced words stay, one for one; `changed` is by words, so whitespace alone is not an edit.
    /// ponytail: word alignment only; a dictation replaced wholesale is kept as typed.
    static func trimmingAppended(initial: String, edited: String) -> (text: String, changed: Bool) {
        let said = initial.split(whereSeparator: \.isWhitespace).map(String.init)
        let seen = edited.split(whereSeparator: \.isWhitespace).map(String.init)
        var inserted = Set<Int>(), removed = Set<Int>()
        for change in seen.difference(from: said) {
            switch change {
            case .insert(let offset, _, _): inserted.insert(offset)
            case .remove(let offset, _, _): removed.insert(offset)
            }
        }
        let lastKeptSeen = seen.indices.last { !inserted.contains($0) } ?? -1
        let lastKeptSaid = said.indices.last { !removed.contains($0) } ?? -1
        // Dictated words after the last kept one were replaced; as many typed words stand in for them.
        let replacedTail = said.count - 1 - lastKeptSaid
        let kept = Array(seen.prefix(lastKeptSeen + 1 + min(replacedTail, seen.count - lastKeptSeen - 1)))
        return (kept.joined(separator: " "), kept != said)
    }
}

/// Seconds per stage, in pipeline order, from the key release: `capture` (release until transcription
/// starts: the release tail plus stopping the recording), `transcription`, `cleanup`, `handoff` (cleanup
/// done until insertion starts: saving the entry, reading the focused field), `insertion`, then `total`.
/// `insertion` ends when the text is visible (⌘V posted, or the field first read back as matching) and
/// `total` is release to that moment. `insertionDone` is the insertion call's whole duration, which
/// also covers the read-back's wait; the clipboard restore runs after both and is in neither.
/// Entries from 0.2.3 and earlier lack `capture`, `handoff` and `insertionDone`, and their `insertion`
/// and `total` run to the end of a fixed 0.5 s clipboard wait, so they read about 0.5 s higher.
struct StageTiming: Codable, Equatable {
    var transcription: Double? = nil, cleanup: Double? = nil, insertion: Double? = nil, total: Double? = nil
    var capture: Double? = nil, handoff: Double? = nil, insertionDone: Double? = nil
}

struct HistoryStore {
    let directory: URL
    /// Told how many unreadable files `entries()` just moved aside; may run on any thread.
    private let onQuarantine: (Int) -> Void
    init(directory: URL, onQuarantine: @escaping (Int) -> Void = { _ in }) throws {
        self.directory = directory
        self.onQuarantine = onQuarantine
        if FileManager.default.fileExists(atPath: directory.path) {
            let attrs = try FileManager.default.attributesOfItem(atPath: directory.path)
            guard attrs[.type] as? FileAttributeType == .typeDirectory else { throw RetentionError("History requires a real directory") }
        } else {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        try removePendingFiles()
    }
    /// Writers and cleanup share a directory lock across processes, so cleanup cannot unlink a live save.
    private func locked<T>(_ operation: () throws -> T) throws -> T {
        let descriptor = open(directory.path, O_RDONLY | O_NOFOLLOW)
        guard descriptor >= 0 else { throw RetentionError("Cannot open history directory") }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX) == 0 else { throw RetentionError("Cannot lock history directory") }
        defer { flock(descriptor, LOCK_UN) }
        return try operation()
    }
    private func removePendingFiles() throws {
        try locked {
            for file in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
                let name = file.lastPathComponent
                guard name.hasPrefix(".pending-"), UUID(uuidString: String(name.dropFirst(9))) != nil else { continue }
                try FileManager.default.removeItem(at: file)
            }
        }
    }
    func save(_ entry: HistoryEntry) throws {
        try locked { try saveLocked(entry) }
    }
    private func saveLocked(_ entry: HistoryEntry) throws {
        guard UUID(uuidString: entry.id) != nil else { throw RetentionError("Invalid history identifier") }
        let target = directory.appendingPathComponent(entry.id + ".json")
        let temporary = directory.appendingPathComponent(".pending-" + UUID().uuidString)
        let descriptor = open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600)
        guard descriptor >= 0 else { throw RetentionError("Cannot create private history") }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        defer { try? handle.close(); try? FileManager.default.removeItem(at: temporary) }
        try handle.write(contentsOf: JSONEncoder().encode(entry))
        try handle.synchronize()
        guard rename(temporary.path, target.path) == 0 else { throw RetentionError("Cannot commit history") }
        let folder = open(directory.path, O_RDONLY)
        guard folder >= 0 else { throw RetentionError("Cannot flush history directory") }
        defer { close(folder) }
        guard fsync(folder) == 0 else { throw RetentionError("Cannot flush history directory") }
    }
    /// The entry files: `.json`, not symbolic links.
    private func entryFiles() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isSymbolicLinkKey])
            .filter { $0.pathExtension == "json" && ((try? $0.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true) }
    }
    /// The one validity rule: the file decodes and its name is its entry's id. `entries()` and `quarantineUnreadable` move a file that fails it aside.
    private func readEntry(_ file: URL) throws -> HistoryEntry {
        let entry = try JSONDecoder().decode(HistoryEntry.self, from: Data(contentsOf: file))
        guard UUID(uuidString: entry.id) != nil, file.lastPathComponent == entry.id + ".json" else {
            throw RetentionError("History identifier does not match its file")
        }
        return entry
    }
    /// Newest first. A file that cannot be read is quarantined, so one damaged file never hides the others.
    func entries() throws -> [HistoryEntry] {
        var readable: [HistoryEntry] = [], moved = 0
        for file in try entryFiles() {
            if let entry = try? readEntry(file) { readable.append(entry) } else if try quarantine(file) { moved += 1 }
        }
        if moved > 0 { onQuarantine(moved) }
        return readable.sorted { $0.recordedAt > $1.recordedAt }
    }
    func delete(_ entry: HistoryEntry) throws {
        guard UUID(uuidString: entry.id) != nil else { throw RetentionError("Invalid history identifier") }
        try FileManager.default.removeItem(at: directory.appendingPathComponent(entry.id + ".json"))
    }
    func expired(retention: HistoryRetention, now: Date = Date(), calendar: Calendar = .current) throws -> [HistoryEntry] {
        guard let cutoff = retention.cutoff(now: now, calendar: calendar) else { return [] }
        return try entries().filter { $0.recordedAt <= cutoff }
    }
    /// Files moved aside by `quarantineUnreadable`. Inside the history folder, so Delete All Data removes them.
    var unreadableDirectory: URL { directory.appendingPathComponent("unreadable", isDirectory: true) }
    static func quarantineNotice(_ moved: Int) -> String {
        "\(moved) damaged history \(moved == 1 ? "file was" : "files were") moved to the history/unreadable folder. \(moved == 1 ? "It" : "They") still expire with your retention setting."
    }
    /// Moves history files that cannot be decoded (or whose name does not match their id) into
    /// `unreadable/`, so one damaged file cannot stop Murmur from starting. Returns how many moved.
    func quarantineUnreadable() throws -> Int {
        var moved = 0
        for file in try entryFiles() where (try? readEntry(file)) == nil { if try quarantine(file) { moved += 1 } }
        return moved
    }
    /// False when another reader already moved the file.
    private func quarantine(_ file: URL) throws -> Bool {
        try locked {
            guard FileManager.default.fileExists(atPath: file.path) else { return false }
            try FileManager.default.createDirectory(at: unreadableDirectory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.moveItem(at: file, to: unreadableDirectory.appendingPathComponent(UUID().uuidString + ".json"))
            return true
        }
    }
    /// Expires entries, and unreadable files by their modification date: their text still counts as history.
    func purge(retention: HistoryRetention, now: Date = Date()) throws {
        for entry in try expired(retention: retention, now: now) { try delete(entry) }
        try removePendingFiles()
        guard let cutoff = retention.cutoff(now: now) else { return }
        let files = FileLinks.isLink(unreadableDirectory) ? [] : (try? FileManager.default.contentsOfDirectory(at: unreadableDirectory, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        for file in files where !FileLinks.isLink(file) && ((try? file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantFuture) <= cutoff {
            try FileManager.default.removeItem(at: file)
        }
    }
}

/// History's wording for an entry, kept beside the status type so Foundation-only checks cover it.
extension HistoryEntry {
    var needsAttention: Bool { [.insertionPending, .failed, .heldCleanup, .heldTranscript].contains(status) }

    /// The entry History opens expanded: the last dictation's, while it still needs attention, was saved without being pasted, or was a voice edit.
    static func recoveryEntryToOpen(_ id: String?, in history: [HistoryEntry]) -> String? {
        history.first { $0.id == id && ($0.needsAttention || $0.status == .savedNotInserted || $0.status == .edited) }?.id
    }

    var statusLabel: String? {
        switch status {
        case .verified, .unverified: return nil
        case .insertionPending: return "Saved"
        case .failed: return "Not inserted"
        case .copied: return "Copied"
        case .edited: return "Edited"
        case .heldCleanup: return "Cleanup held"
        case .heldTranscript: return "Held for review"
        default: return "Saved"
        }
    }

    var statusNote: String? {
        switch status {
        case .insertionPending: return "Murmur stopped before saving the delivery result. Check the destination before copying this text to avoid a duplicate."
        case .failed: return "Murmur could not paste this text. It is saved here so you can copy it."
        case .heldCleanup: return "Cleanup changed too much, so only the raw transcript was saved and nothing was inserted."
        case .heldTranscript: return "The transcript was held for review, so nothing was inserted."
        default: return nil
        }
    }

    /// Which path wrote the final text, in one line; nil for entries saved before it was recorded.
    var outcomeNote: String? {
        guard let outcome = cleanupOutcome else { return nil }
        let kind = cleanupOptions?.professional == true ? "Professional rewrite" : cleanupOptions?.polish == true ? "Polish" : "AI cleanup"
        let failed = cleanupFallback == nil || cleanupFallback == CleanupFailureReason.cleanerFailed.rawValue
        let why = failed ? "" : " (" + (cleanupFallback ?? "").map { $0.isUppercase ? " " + $0.lowercased() : String($0) }.joined() + ")"
        let edited = self.edited == true ? " Edited since." : ""
        switch outcome {
        case .off: return "Cleanup off; dictionary only." + edited
        case .modelAccepted: return "\(kind) applied." + edited
        case .modelPunctuationOnLocal: return "\(kind) rejected\(why); AI punctuation applied to local cleanup." + edited
        case .localRules: return "\(kind) \(failed ? "failed" : "rejected")\(why); local cleanup used." + edited
        case .localRoute: return "Local cleanup on this Mac." + edited
        }
    }
}
