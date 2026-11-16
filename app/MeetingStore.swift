import Foundation

// Meeting persistence: the store, its place under the History cap, and chunk-audio recovery.

// MARK: - Store

/// One JSON file per meeting in `history/meetings/`, owner-only, written atomically. Inside the
/// History folder, so Delete All Data removes it with History.
struct MeetingStore {
    let directory: URL
    init(directory: URL) throws {
        self.directory = directory
        let fm = FileManager.default
        if let attrs = try? fm.attributesOfItem(atPath: directory.path) {
            guard attrs[.type] as? FileAttributeType == .typeDirectory else { throw RetentionError("Meetings require a real directory") }
        } else {
            try fm.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    }
    private func file(_ id: String) throws -> URL {
        guard UUID(uuidString: id) != nil else { throw RetentionError("Invalid meeting identifier") }
        return directory.appendingPathComponent(id + ".json")
    }
    func save(_ meeting: Meeting) throws {
        let url = try file(meeting.id)
        if let attrs = try? FileManager.default.attributesOfItem(atPath: url.path), attrs[.type] as? FileAttributeType != .typeRegular {
            throw RetentionError("Meeting file is not a regular file")
        }
        try JSONEncoder().encode(meeting).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    /// Newest first. A file that cannot be read moves to `unreadable/` (still expiring by date) so one
    /// damaged meeting cannot hide the rest.
    func meetings() throws -> [Meeting] {
        var found: [Meeting] = []
        for url in try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isSymbolicLinkKey]) where url.pathExtension == "json" {
            guard (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { continue }
            if let meeting = try? JSONDecoder().decode(Meeting.self, from: Data(contentsOf: url)),
               UUID(uuidString: meeting.id) != nil, url.lastPathComponent == meeting.id + ".json" {
                found.append(meeting); continue
            }
            try FileManager.default.createDirectory(at: unreadable, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.moveItem(at: url, to: unreadable.appendingPathComponent(UUID().uuidString + ".json"))
        }
        return found.sorted { $0.recordedAt > $1.recordedAt }
    }
    var unreadable: URL { directory.appendingPathComponent("unreadable", isDirectory: true) }
    func delete(_ id: String) throws { try FileManager.default.removeItem(at: try file(id)) }
    func size(_ id: String) -> Int64 {
        ((try? file(id)).flatMap { try? FileManager.default.attributesOfItem(atPath: $0.path)[.size] as? NSNumber })?.int64Value ?? 0
    }
    /// Deletes meetings that started at or before `cutoff` (from `HistoryRetention.cutoff`; nil is Forever).
    @discardableResult
    func purge(cutoff: Date?) throws -> [String] {
        guard let cutoff else { return [] }
        let expired = try meetings().filter { $0.recordedAt <= cutoff }.map(\.id)
        for id in expired { try delete(id) }
        try purgeUnreadable(cutoff: cutoff)
        return expired
    }
    /// Damaged files still hold meeting text, so they expire by their modification date.
    func purgeUnreadable(cutoff: Date) throws {
        let files = (try? FileManager.default.contentsOfDirectory(at: unreadable, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        for url in files where ((try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantFuture) <= cutoff {
            try FileManager.default.removeItem(at: url)
        }
    }
}

/// The 50 MB History cap counts meetings (they live inside the History folder), so they join the
/// same oldest-first list as dictations. Otherwise meetings over the cap would drop every dictation.
struct CappedHistoryItem: RetainedHistoryItem {
    let id: String
    let recordedAt: Date
    let isMeeting: Bool
}

enum MeetingCap {
    @discardableResult
    static func enforce(entries: [(id: String, recordedAt: Date)], meetings: [Meeting], historyDirectory: URL, meetingStore: MeetingStore,
                        retention: HistoryRetention, capBytes: Int64 = HistoryCap.defaultCapBytes,
                        deleteEntry: (String) throws -> Void) throws -> HistoryCapResult<CappedHistoryItem> {
        var sizes: [String: Int64] = [:]
        for entry in entries {
            sizes[entry.id] = ((try? FileManager.default.attributesOfItem(atPath: historyDirectory.appendingPathComponent(entry.id + ".json").path))?[.size] as? NSNumber)?.int64Value ?? 0
        }
        for meeting in meetings { sizes[meeting.id] = meetingStore.size(meeting.id) }
        let items = entries.map { CappedHistoryItem(id: $0.id, recordedAt: $0.recordedAt, isMeeting: false) }
            + meetings.map { CappedHistoryItem(id: $0.id, recordedAt: $0.recordedAt, isMeeting: true) }
        let other = max(0, try HistoryCap.usage(directory: historyDirectory) - sizes.values.reduce(0, +))
        let result = HistoryCap.plan(entries: items, sizes: sizes, otherBytes: other, retention: retention, capBytes: capBytes)
        for item in result.dropped { if item.isMeeting { try meetingStore.delete(item.id) } else { try deleteEntry(item.id) } }
        return result
    }
}

// MARK: - Chunk audio and recovery

/// Chunk audio waiting for transcription, in `retry-audio/meetings/<meeting id>/`, named
/// `<stream>-<start ms>.wav`. A chunk is deleted once its line is saved. What a crash or a failed
/// transcription leaves is retry audio: offered as "Finish transcript", gone after 24 hours.
enum MeetingAudio {
    static func folder(_ root: URL, meeting id: String) -> URL { root.appendingPathComponent(id, isDirectory: true) }
    static func name(_ stream: MeetingStream, start: Double) -> String { "\(stream.rawValue)-\(Int((start * 1000).rounded())).wav" }
    static func parse(_ url: URL) -> (stream: MeetingStream, start: Double)? {
        guard url.pathExtension == "wav" else { return nil }
        let parts = url.deletingPathExtension().lastPathComponent.split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2, let stream = MeetingStream(rawValue: String(parts[0])), let ms = Int(parts[1]), ms >= 0 else { return nil }
        return (stream, Double(ms) / 1000)
    }
    /// Leftover chunks of one meeting, oldest first.
    static func leftovers(_ root: URL, meeting id: String) -> [URL] {
        let urls = (try? FileManager.default.contentsOfDirectory(at: folder(root, meeting: id), includingPropertiesForKeys: nil)) ?? []
        return urls.filter { parse($0) != nil }.sorted { parse($0)!.start < parse($1)!.start }
    }
    /// Removes folders of meetings that no longer exist (or are not to be kept), chunk files older
    /// than 24 hours, and anything that is not a chunk. `keep` holds the meeting ids still on record.
    /// `active` (the meeting recording now, and any still transcribing) is never touched: its folder
    /// holds the chunker's file being written, and deleting it stopped both streams in the first live tests.
    static func purge(_ root: URL, keep: Set<String>, active: Set<String> = [], now: Date = Date()) {
        let fm = FileManager.default
        for folder in (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? [] {
            if active.contains(folder.lastPathComponent) { continue }
            guard keep.contains(folder.lastPathComponent) else { try? fm.removeItem(at: folder); continue }
            for file in (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.creationDateKey])) ?? [] {
                let created = (try? file.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
                if parse(file) == nil || now.timeIntervalSince(created) >= RetryWindow.lifetime || created > now { try? fm.removeItem(at: file) }
            }
            if ((try? fm.contentsOfDirectory(atPath: folder.path)) ?? []).isEmpty { try? fm.removeItem(at: folder) }
        }
    }
}
