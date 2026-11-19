// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention History
import Foundation

// Golden outputs of History.swift's entry model over synthetic entries: the JSON Swift writes (so Windows files stay interchangeable),
// the wording of each status, the outcome line, AfterPaste.trimmingAppended (including a seeded run of random word lists, which
// pins how ties in the word difference are broken), and the quarantine notice. One case per line. Run from the repository root.
struct EntryCase: Encodable {
    var label: String, json: String
    var needsAttention: Bool, statusLabel: String?, statusNote: String?, outcomeNote: String?
    var status: String?
}
struct TrimCase: Encodable { var initial: String, edited: String, text: String, changed: Bool }
struct RecoveryCase: Encodable { var id: String?; var entries: [String]; var opens: String? }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

@main struct Gen {
    static func main() throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let at = Date(timeIntervalSinceReferenceDate: 800_000_000.25)
        let id = "6D1A0001-5E5E-4A4A-9A9A-0000000000A1"
        var entries: [(String, HistoryEntry)] = []
        let base = HistoryEntry(id: id, recordedAt: at, rawText: "Synthetic original", finalText: "Synthetic final", insertionStatus: "saved-not-inserted")
        entries.append(("minimal", base))
        for status in ["saved-not-inserted", "insertion-pending", "held-cleanup", "held-transcript", "copied", "failed", "verified", "unverified", "edited", "from-a-newer-version"] {
            var e = base; e.insertionStatus = status; entries.append(("status \(status)", e))
        }
        var fell = base; fell.insertionStatus = "unverified"; fell.cleanupFallback = "unexplainedDeletion"; fell.cleanupRejectedOutput = "Sure! <b> & \"quoted\" / slash"
        entries.append(("fallback", fell))
        var traced = fell
        traced.seconds = 12.5; traced.app = "notepad.exe"; traced.cleanupOptions = CleanupOptions(polish: true)
        traced.writingModeID = "6D1A0001-5E5E-4A4A-9A9A-000000000002"; traced.writingModeName = "Email"
        traced.transcriptionProvider = "local"; traced.transcriptionModel = "synthetic-model"; traced.cleanupProvider = "customAPI"; traced.cleanupModel = "synthetic-cleanup"
        traced.appVersion = "1.0 (7)"; traced.contextUsed = true; traced.cleanupOutcome = .localRules; traced.edited = true; traced.cues = 2; traced.editOriginal = "Synthetic selection"
        entries.append(("provenance polish rejected", traced))
        var failed = traced; failed.cleanupFallback = CleanupFailureReason.cleanerFailed.rawValue; failed.edited = nil; entries.append(("provenance polish failed", failed))
        for (name, outcome) in [("off", CleanupPath.off), ("accepted", .modelAccepted), ("punctuation", .modelPunctuationOnLocal), ("rules", .localRules), ("route", .localRoute)] {
            var e = traced; e.cleanupOutcome = outcome; e.cleanupOptions = CleanupOptions(professional: true); e.cleanupFallback = nil; entries.append(("outcome \(name) professional", e))
            var plain = e; plain.cleanupOptions = nil; plain.edited = nil; entries.append(("outcome \(name) plain", plain))
        }
        var diagnosed = traced
        diagnosed.transcription = TranscriptionDetail(segments: [SegmentDetail(text: " Thanks.", start: 1.5, end: 2, avgLogprob: -0.4, noSpeechProb: 0.1, compressionRatio: 0.9, loudWindows: 2, dropped: true),
                                                                 SegmentDetail(text: "Plain")],
                                                      clipSeconds: 2, peakP50: 0.02, peakP90: 0.3, silencePeak: 0.25, silenceThreshold: 0.01, precheck: "passed")
        diagnosed.cleanup = CleanupDetail(modelOutput: "Hi.", guardVerdict: "cleaned", providerError: "timeout", promptHash: "0123abcd", dictionaryTerms: ["Akme"], input: "hi", contextUsed: true,
                                          contextApp: "notepad.exe", contextChars: ["selected": 0, "clipboard": 12, "window": 40])
        diagnosed.afterPaste = AfterPaste(text: "Hi there.", changed: true, route: "ax", endReason: "focus departed")
        diagnosed.timing = StageTiming(transcription: 0.4, cleanup: 0.9, insertion: 0.1, total: 1.6, capture: 0.44, handoff: 0.01, insertionDone: 0.2)
        diagnosed.recordingFile = id + ".wav"
        entries.append(("diagnostics", diagnosed))
        var empties = diagnosed; empties.transcription = TranscriptionDetail(); empties.cleanup = CleanupDetail(); empties.afterPaste = AfterPaste(); empties.timing = StageTiming()
        entries.append(("empty details", empties))
        entries.append(("unicode", base.with(finalText: "Caf\u{e9} \u{1F600} \u{2028} line\nbreak \"q\" \\ back")))
        let cases = entries.map { name, e in
            EntryCase(label: name, json: String(decoding: try! encoder.encode(e), as: UTF8.self), needsAttention: e.needsAttention, statusLabel: e.statusLabel,
                      statusNote: e.statusNote, outcomeNote: e.outcomeNote, status: e.status?.rawValue)
        }

        var trims: [TrimCase] = []
        func trim(_ initial: String, _ edited: String) { let r = AfterPaste.trimmingAppended(initial: initial, edited: edited); trims.append(TrimCase(initial: initial, edited: edited, text: r.text, changed: r.changed)) }
        let said = "Send it to Akme today."
        trim(said, said + "\nuser@host secret: x1"); trim(said, "Send it to Acme today. secret: x1"); trim(said, "Send it to Akme tomorrow. secret: x1")
        trim("TrueNASDOTCloud doesn't get that.", "truenas.clouddev.dad"); trim(said, said + "\n"); trim("", "typed only"); trim("a b c", ""); trim("a b c", "a b c"); trim("a  b\tc", "a b   c d")
        trim("the the the", "the the"); trim("one two three four", "one three four five"); trim("a b a b", "b a b a"); trim("x y", "y x")
        var seed: UInt64 = 20_260_101
        func next(_ bound: Int) -> Int { seed = seed &* 6364136223846793005 &+ 1442695040888963407; return Int((seed >> 33) % UInt64(bound)) }
        let alphabet = ["a", "b", "c", "d"]
        // Which of several equally short edit scripts Swift's word difference reports is a detail of the standard library that the C# port does not
        // reproduce; so the random cases keep only the pairs with exactly one shortest edit script, where every correct algorithm must agree.
        func scripts(_ a: [String], _ b: [String]) -> Set<String> {
            var lcs = Array(repeating: Array(repeating: 0, count: b.count + 1), count: a.count + 1)
            for i in stride(from: a.count - 1, through: 0, by: -1) { for j in stride(from: b.count - 1, through: 0, by: -1) {
                lcs[i][j] = a[i] == b[j] ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
            } }
            var found = Set<String>()
            func walk(_ i: Int, _ j: Int, _ removed: [Int], _ inserted: [Int]) {
                if i == a.count && j == b.count { found.insert("\(removed)|\(inserted)"); return }
                if i < a.count && j < b.count && a[i] == b[j] && lcs[i][j] == lcs[i + 1][j + 1] + 1 { walk(i + 1, j + 1, removed, inserted) }
                if i < a.count && lcs[i][j] == lcs[i + 1][j] { walk(i + 1, j, removed + [i], inserted) }
                if j < b.count && lcs[i][j] == lcs[i][j + 1] { walk(i, j + 1, removed, inserted + [j]) }
            }
            walk(0, 0, [], [])
            return found
        }
        for _ in 0..<600 {
            let saidWords = (0..<next(8)).map { _ in alphabet[next(alphabet.count)] }
            let typedWords = (0..<next(9)).map { _ in alphabet[next(alphabet.count)] }
            if scripts(saidWords, typedWords).count > 1 { continue }
            let said = saidWords.joined(separator: " "), typed = typedWords.joined(separator: " ")
            trim(said, typed)
        }

        let one = base.with(finalText: "x", status: .failed), delivered = base.with(finalText: "x", status: .verified), saved = base, edited = base.with(finalText: "x", status: .edited)
        var recovery: [RecoveryCase] = []
        for (name, list) in [("failed", [one]), ("saved", [saved]), ("edited", [edited]), ("verified", [delivered]), ("none", [])] {
            recovery.append(RecoveryCase(id: id, entries: list.map { $0.status?.rawValue ?? "" }, opens: HistoryEntry.recoveryEntryToOpen(id, in: list)))
            _ = name
        }
        recovery.append(RecoveryCase(id: nil, entries: ["failed"], opens: HistoryEntry.recoveryEntryToOpen(nil, in: [one])))
        let notices = [1, 2, 5].map { HistoryStore.quarantineNotice($0) }
        let noticeLine = String(decoding: try encoder.encode(notices), as: UTF8.self)

        print("{\n" + [lines("entries", cases), lines("trims", trims), lines("recovery", recovery), "\"notices\": \(noticeLine)"].joined(separator: ",\n") + "\n}")
    }
}
