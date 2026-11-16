// Routing history between disk and memory (history off, session-only entries) and its summary text. Foundation only.
import Foundation

/// The disk side of history, as closures over HistoryStore (which is not Foundation-only).
struct HistoryDisk<Entry> {
    let save: (Entry) throws -> Void
    let entries: () throws -> [Entry]
    let delete: (Entry) throws -> Void
}

/// Every history write goes through here. With history on, entries go to disk. With history off,
/// they stay in memory for this app session only, so a held cleanup or a failed insertion can still
/// be copied from History until Murmur quits; nothing is written to disk. Thread-safe: the first save
/// of a dictation happens off the main thread.
final class HistoryRouter<Entry: RetainedHistoryItem>: @unchecked Sendable {
    /// Memory entries above this count are dropped oldest first.
    let memoryLimit: Int
    private let disk: HistoryDisk<Entry>
    private let lock = NSLock()
    private var memory: [String: Entry] = [:]
    private var persists: Bool

    init(disk: HistoryDisk<Entry>, persists: Bool, memoryLimit: Int = 100) {
        self.disk = disk; self.persists = persists; self.memoryLimit = max(1, memoryLimit)
    }

    /// Changing this does not move existing entries: memory entries stay in memory, disk entries stay on disk.
    var persistsTranscripts: Bool {
        get { lock.lock(); defer { lock.unlock() }; return persists }
        set { lock.lock(); persists = newValue; lock.unlock() }
    }

    /// An entry already held in memory stays in memory, so one dictation never splits across both.
    func save(_ entry: Entry) throws {
        lock.lock()
        if !persists || memory[entry.id] != nil {
            memory[entry.id] = entry
            if memory.count > memoryLimit {
                let excess = memory.values.sorted { $0.recordedAt < $1.recordedAt }.prefix(memory.count - memoryLimit)
                for old in excess { memory[old.id] = nil }
            }
            lock.unlock()
            return
        }
        lock.unlock()
        try disk.save(entry)
    }

    /// Replaces an existing entry where it already lives (memory or disk), whatever `persistsTranscripts` says now.
    func update(_ entry: Entry) throws {
        lock.lock()
        if memory[entry.id] != nil { memory[entry.id] = entry; lock.unlock(); return }
        lock.unlock()
        try disk.save(entry)
    }

    /// Disk and memory entries, newest first. A memory entry wins over a disk entry with the same id.
    func entries() throws -> [Entry] {
        let stored = try disk.entries()
        lock.lock(); let held = memory; lock.unlock()
        return (stored.filter { held[$0.id] == nil } + Array(held.values)).sorted { $0.recordedAt > $1.recordedAt }
    }

    func delete(_ entry: Entry) throws {
        lock.lock(); let wasHeld = memory.removeValue(forKey: entry.id) != nil; lock.unlock()
        if !wasHeld { try disk.delete(entry) }
    }

    var sessionOnlyIDs: Set<String> { lock.lock(); defer { lock.unlock() }; return Set(memory.keys) }

    /// Applies time-based retention to memory entries (disk entries use HistoryStore.purge).
    func purgeSessionOnly(recordedAtOrBefore cutoff: Date) {
        lock.lock(); memory = memory.filter { $0.value.recordedAt > cutoff }; lock.unlock()
    }

    func clearSessionOnly() { lock.lock(); memory.removeAll(); lock.unlock() }
}

enum HistoryText {
    static func summary(mode: HistoryMode, count: Int, sessionOnly: Int) -> String {
        let noun = count == 1 ? "transcript" : "transcripts"
        switch mode {
        case .on:
            return sessionOnly == 0 ? "Saved locally · \(count) \(noun)" : "Saved locally · \(count) \(noun), \(sessionOnly) kept only until Murmur quits"
        case .off:
            return "History is off · \(count) \(noun) · new transcripts are kept only until Murmur quits"
        }
    }

    static let sessionOnlyNote = "Not saved to disk. This text is lost when Murmur quits."

    /// The first sentence of a completion message.
    static func saved(persists: Bool) -> String {
        persists ? "Saved in History." : "Kept in History until Murmur quits."
    }
}
