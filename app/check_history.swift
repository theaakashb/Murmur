import Foundation
import CryptoKit
import Darwin

@main struct HistoryChecks {
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("dictation-history-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try HistoryStore(directory: root)
        let now = Date(timeIntervalSince1970: 1791021600)
        let entry = HistoryEntry(id: UUID().uuidString, recordedAt: now.addingTimeInterval(-31 * 86400), rawText: "Synthetic original", finalText: "Synthetic final", insertionStatus: "saved-not-inserted")
        try store.save(entry)
        let loaded = try store.entries()
        precondition(loaded.count == 1 && loaded[0].rawText == entry.rawText)
        // The cleanup fallback reason round-trips; files written before it existed still load.
        let fellBack = HistoryEntry(id: UUID().uuidString, recordedAt: now, rawText: "a", finalText: "A.", insertionStatus: InsertionStatus.unverified.rawValue, cleanupFallback: "unexplainedDeletion", cleanupRejectedOutput: "Sure!")
        let roundTrip = try JSONDecoder().decode(HistoryEntry.self, from: JSONEncoder().encode(fellBack))
        precondition(roundTrip.cleanupFallback == "unexplainedDeletion" && roundTrip.cleanupRejectedOutput == "Sure!", "fallback reason and rejected output round-trip")
        let older = #"{"id":"\#(UUID().uuidString)","recordedAt":0,"rawText":"a","finalText":"a","insertionStatus":"unverified"}"#
        let olderEntry = try JSONDecoder().decode(HistoryEntry.self, from: Data(older.utf8))
        precondition(olderEntry.cleanupFallback == nil, "older history decodes without a fallback reason")
        // History's wording for each status, and which entry it opens on.
        func with(_ status: InsertionStatus) -> HistoryEntry { entry.with(finalText: "ok", status: status) }
        precondition([.verified, .unverified].allSatisfy { with($0).statusLabel == nil && with($0).statusNote == nil && !with($0).needsAttention }, "delivered text carries no label or note")
        precondition(with(.failed).needsAttention && with(.failed).statusLabel == "Not inserted")
        precondition(with(.insertionPending).needsAttention && with(.insertionPending).statusLabel == "Saved")
        precondition(with(.insertionPending).statusNote?.contains("avoid a duplicate") == true)
        precondition(!entry.needsAttention && entry.statusLabel == "Saved" && with(.copied).statusLabel == "Copied")
        var unknown = entry; unknown.insertionStatus = "from-a-newer-version"
        precondition(unknown.statusLabel == "Saved" && unknown.statusNote == nil && !unknown.needsAttention, "a status this version does not know reads as saved")
        precondition(HistoryEntry.recoveryEntryToOpen(entry.id, in: [with(.failed)]) == entry.id, "History opens on the entry that still needs attention")
        precondition(HistoryEntry.recoveryEntryToOpen(entry.id, in: [with(.verified)]) == nil && HistoryEntry.recoveryEntryToOpen(nil, in: [with(.failed)]) == nil && HistoryEntry.recoveryEntryToOpen(entry.id, in: []) == nil, "no entry opens once delivered, without a recovery entry or when it is gone")
        precondition(fellBack.outcomeNote == nil, "no outcome line before the outcome was recorded")
        var noted = fellBack; noted.cleanupOutcome = .localRules; noted.cleanupOptions = CleanupOptions(polish: true); noted.edited = true
        precondition(noted.outcomeNote == "Polish rejected (unexplained deletion); local cleanup used. Edited since.", "the outcome line names the cleanup, the reason and the edit")
        noted.cleanupFallback = CleanupFailureReason.cleanerFailed.rawValue; noted.edited = nil
        precondition(noted.outcomeNote == "Polish failed; local cleanup used.")
        // Provenance round-trips, older files decode without it, and new text keeps it.
        var traced = fellBack
        traced.cleanupOptions = CleanupOptions(professional: true); traced.writingModeID = UUID().uuidString; traced.writingModeName = "Email"
        traced.transcriptionProvider = "local"; traced.transcriptionModel = ModelFacts.batch.repository
        traced.cleanupProvider = "gemini"; traced.cleanupModel = "gemma-4-26b-a4b-it"; traced.appVersion = "1.0 (7)"
        traced.contextUsed = true; traced.cleanupOutcome = .modelPunctuationOnLocal; traced.edited = true
        let tracedTrip = try JSONDecoder().decode(HistoryEntry.self, from: JSONEncoder().encode(traced))
        func provenance(_ e: HistoryEntry) -> [String?] {
            [e.cleanupOptions.map { "\($0)" }, e.writingModeID, e.writingModeName, e.transcriptionProvider, e.transcriptionModel, e.cleanupProvider,
             e.cleanupModel, e.appVersion, e.contextUsed.map { "\($0)" }, e.cleanupOutcome?.rawValue, e.edited.map { "\($0)" }]
        }
        precondition(tracedTrip.cleanupOptions == CleanupOptions(professional: true) && provenance(tracedTrip) == provenance(traced), "provenance round-trips")
        precondition(provenance(olderEntry).allSatisfy { $0 == nil }, "older history decodes without provenance")
        let retexted = traced.with(finalText: "B.", status: .verified)
        precondition(retexted.finalText == "B." && provenance(retexted) == provenance(traced), "with(finalText:) keeps provenance")
        // Diagnostics round-trip, survive with(finalText:), and older files decode without them.
        var diagnosed = traced
        diagnosed.transcription = TranscriptionDetail(segments: [SegmentDetail(text: " Thanks.", start: 1.5, end: 2, avgLogprob: -0.4, noSpeechProb: 0.1, compressionRatio: 0.9, loudWindows: 2, dropped: true)],
                                                      clipSeconds: 2, peakP50: 0.02, peakP90: 0.3, silencePeak: 0.25, silenceThreshold: 0.01, precheck: "passed")
        diagnosed.cleanup = CleanupDetail(modelOutput: "Hi.", guardVerdict: "cleaned", promptHash: "0123abcd", dictionaryTerms: ["Akme"], input: "hi", contextUsed: true,
                                          contextApp: "com.apple.Notes", contextChars: ["selected": 0, "clipboard": 12, "window": 40])
        diagnosed.afterPaste = AfterPaste(text: "Hi there.", changed: true, route: "ax", endReason: "focus departed")
        // Words typed after the dictation are never kept; edits of it and wholesale replacements are.
        let said = "Send it to Akme today."
        precondition(AfterPaste.trimmingAppended(initial: said, edited: said + "\nuser@host secret: x1") == (said, false), "append-only is no edit and keeps nothing typed")
        precondition(AfterPaste.trimmingAppended(initial: said, edited: "Send it to Acme today. secret: x1") == ("Send it to Acme today.", true), "an edit keeps the fix, drops the append")
        precondition(AfterPaste.trimmingAppended(initial: said, edited: "Send it to Akme tomorrow. secret: x1") == ("Send it to Akme tomorrow.", true), "a fixed last word stays, the append goes")
        precondition(AfterPaste.trimmingAppended(initial: "TrueNASDOTCloud doesn't get that.", edited: "truenas.clouddev.dad") == ("truenas.clouddev.dad", true), "a wholesale replacement is kept")
        precondition(AfterPaste.trimmingAppended(initial: said, edited: said + "\n") == (said, false), "whitespace alone is no edit")
        diagnosed.timing = StageTiming(transcription: 0.4, cleanup: 0.9, insertion: 0.1, total: 1.6, capture: 0.44, handoff: 0.01, insertionDone: 0.2)
        let olderTiming = try JSONDecoder().decode(StageTiming.self, from: Data(#"{"transcription":0.4,"cleanup":0.4,"insertion":0.51,"total":1.8}"#.utf8))
        precondition(olderTiming.capture == nil && olderTiming.handoff == nil && olderTiming.insertionDone == nil && olderTiming.total == 1.8, "timing from 0.2.3 decodes")
        diagnosed.recordingFile = diagnosed.id + ".wav"
        let diagnosedTrip = try JSONDecoder().decode(HistoryEntry.self, from: JSONEncoder().encode(diagnosed.with(finalText: "Hi.", status: .verified)))
        precondition(diagnosedTrip.transcription == diagnosed.transcription && diagnosedTrip.cleanup == diagnosed.cleanup && diagnosedTrip.afterPaste == diagnosed.afterPaste
                     && diagnosedTrip.timing == diagnosed.timing && diagnosedTrip.recordingFile == diagnosed.recordingFile, "diagnostics round-trip and survive with(finalText:)")
        precondition(olderEntry.transcription == nil && olderEntry.cleanup == nil && olderEntry.afterPaste == nil && olderEntry.timing == nil && olderEntry.recordingFile == nil, "older history decodes without diagnostics")
        let path = root.appendingPathComponent(entry.id + ".json")
        let abandoned = root.appendingPathComponent(".pending-" + UUID().uuidString)
        try JSONEncoder().encode(entry).write(to: abandoned)
        _ = try HistoryStore(directory: root)
        precondition(!FileManager.default.fileExists(atPath: abandoned.path) && FileManager.default.fileExists(atPath: path.path), "startup removes orphan writes and keeps committed history")
        // A second opener must wait for an active writer rather than deleting its temporary output.
        let lockFD = open(root.path, O_RDONLY)
        precondition(lockFD >= 0 && flock(lockFD, LOCK_EX) == 0)
        try JSONEncoder().encode(entry).write(to: abandoned)
        let entered = DispatchSemaphore(value: 0), finished = DispatchSemaphore(value: 0)
        DispatchQueue.global().async {
            entered.signal(); _ = try! HistoryStore(directory: root); finished.signal()
        }
        entered.wait()
        precondition(finished.wait(timeout: .now() + 0.1) == .timedOut && FileManager.default.fileExists(atPath: abandoned.path), "cleanup waits for the writer's lock")
        precondition(flock(lockFD, LOCK_UN) == 0); close(lockFD)
        precondition(finished.wait(timeout: .now() + 2) == .success && !FileManager.default.fileExists(atPath: abandoned.path))
        let permissions = try FileManager.default.attributesOfItem(atPath: path.path)[.posixPermissions] as! NSNumber
        precondition(permissions.intValue == 0o600)
        let thirty = try store.expired(retention: .thirtyDays, now: now); precondition(thirty.count == 1)
        let forever = try store.expired(retention: .forever, now: now); precondition(forever.isEmpty)
        let mismatched = root.appendingPathComponent(UUID().uuidString + ".json")
        try JSONEncoder().encode(entry).write(to: mismatched)
        let beforeMismatch = try store.entries().count
        let mismatchedMoved = try FileManager.default.contentsOfDirectory(at: store.unreadableDirectory, includingPropertiesForKeys: nil)
        precondition(beforeMismatch == 1 && mismatchedMoved.count == 1 && !FileManager.default.fileExists(atPath: mismatched.path), "a file named for another id is quarantined, not accepted")
        try FileManager.default.removeItem(at: mismatchedMoved[0])
        let corrupt = root.appendingPathComponent(UUID().uuidString + ".json")
        try Data("broken JSON".utf8).write(to: corrupt)
        // Startup moves it aside instead of refusing to start; it still expires by modification date.
        let movedCount = try store.quarantineUnreadable(), remaining = try store.entries().count
        precondition(movedCount == 1 && remaining == 1)
        let moved = try FileManager.default.contentsOfDirectory(at: store.unreadableDirectory, includingPropertiesForKeys: nil)
        precondition(moved.count == 1 && !FileManager.default.fileExists(atPath: corrupt.path))
        try store.purge(retention: .sixMonths, now: now); precondition(FileManager.default.fileExists(atPath: moved[0].path))
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-200 * 86400)], ofItemAtPath: moved[0].path)
        try store.purge(retention: .forever, now: now); precondition(FileManager.default.fileExists(atPath: moved[0].path))
        try store.purge(retention: .sixMonths, now: now); precondition(!FileManager.default.fileExists(atPath: moved[0].path))
        // A file damaged while the app runs: entries() returns the others, moves it aside, says so once per move, and saving still works.
        var told: [Int] = []
        let live = try HistoryStore(directory: root) { told.append($0) }
        let others = (0..<3).map { HistoryEntry(id: UUID().uuidString, recordedAt: now.addingTimeInterval(Double($0)), rawText: "Mid-session \($0)", finalText: "Mid-session \($0)", insertionStatus: "verified") }
        for other in others { try live.save(other) }
        try Data("broken JSON".utf8).write(to: root.appendingPathComponent(others[1].id + ".json"))
        let survivors = try live.entries()
        let quarantined = try FileManager.default.contentsOfDirectory(at: live.unreadableDirectory, includingPropertiesForKeys: nil)
        precondition(Set(survivors.map(\.id)).isSuperset(of: [others[0].id, others[2].id]) && !survivors.contains { $0.id == others[1].id }, "a damaged file does not hide the others")
        precondition(quarantined.count == 1 && !FileManager.default.fileExists(atPath: root.appendingPathComponent(others[1].id + ".json").path), "the damaged file is moved aside, not deleted")
        let again = try live.entries().count
        precondition(told == [1] && again == survivors.count, "told once, not on every read")
        try live.save(others[1])
        let afterSave = try live.entries().count
        precondition(afterSave == survivors.count + 1, "saving still works")
        for other in others { try live.delete(other) }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let sixMonths = calendar.date(from: DateComponents(year: 2026, month: 8, day: 31))!
        let cutoff = calendar.date(byAdding: .month, value: -6, to: sixMonths)!
        let boundary = HistoryEntry(id: UUID().uuidString, recordedAt: cutoff, rawText: "Calendar boundary", finalText: "Calendar boundary", insertionStatus: "pending")
        try store.save(boundary)
        let six = try store.expired(retention: .sixMonths, now: sixMonths, calendar: calendar); precondition(six.contains { $0.id == boundary.id })
        try store.delete(entry); try store.delete(boundary)
        let empty = try store.entries(); precondition(empty.isEmpty)
        // Native manifest verifier must fail closed on absent and corrupted artifacts.
        let modelRoot = root.appendingPathComponent("model"); try FileManager.default.createDirectory(at: modelRoot, withIntermediateDirectories: true)
        let bytes = Data("synthetic weight".utf8)
        let checksum = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        let manifest = root.appendingPathComponent("manifest.json")
        let fixture: [String: Any] = ["models": ["FluidInference/parakeet-unified-en-0.6b-coreml": ["revision": String(repeating: "a", count: 40), "files": [["rfilename": "weights", "size": bytes.count, "lfs": ["sha256": checksum]]], "batch_files": ["weights"]]]]
        try JSONSerialization.data(withJSONObject: fixture).write(to: manifest)
        do { try verifyAppModels(modelRoot, manifest: manifest); fatalError("Missing weights accepted") } catch {}
        let weight = modelRoot.appendingPathComponent("weights"); try bytes.write(to: weight)
        try verifyAppModels(modelRoot, manifest: manifest)
        try Data(repeating: 0, count: bytes.count).write(to: weight)
        do { try verifyAppModels(modelRoot, manifest: manifest); fatalError("Corrupt weights accepted") } catch {}
        // The cache skips re-hashing only while size, mtime and inode are unchanged.
        let cache = ModelVerificationCache()
        do { try cache.verify(modelRoot, manifest: manifest); fatalError("Cache accepted corrupt weights") } catch {}
        try bytes.write(to: weight); try cache.verify(modelRoot, manifest: manifest)
        try cache.verify(modelRoot, manifest: manifest)   // hit
        try Data(repeating: 0, count: bytes.count).write(to: weight)
        do { try cache.verify(modelRoot, manifest: manifest); fatalError("Cache accepted rewritten weights") } catch {}
        print("Native history and verifier checks passed (atomic roundtrip, provenance, permissions, calendar expiry, invalid policy, mismatched/corrupt history, quarantine and expiry of unreadable files, deletion, absent/corrupt weights).")
    }
}
