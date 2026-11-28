import Foundation

// When Professional would visibly help a dictation, decided on this Mac from the dictation's own words:
// no network, no model. The History row offers it; nothing is sent.

enum ProfessionalSignal {
    enum Reason: String, Equatable { case runOn, correction, list, fillers }

    /// The longest stretch of words with no sentence end that counts as a run-on. Unmeasured; tune on real dictations.
    static let runOnWords = 35, longRunOnWords = 70
    static let fillerShare = 0.08

    private static let correctionCues = ["no i mean", "i mean", "scratch that", "or rather", "wait no", "no wait", "actually no", "sorry i meant"]
    private static let listWords: Set<String> = ["first", "second", "third", "firstly", "secondly", "thirdly", "finally", "lastly"]
    private static let fillers: Set<String> = ["um", "uh", "er", "erm", "ah", "hmm", "umm", "uhh"]

    static func words(_ text: String) -> [String] {
        text.lowercased().split { !$0.isLetter && !$0.isNumber && $0 != "'" }.map(String.init)
    }

    static func longestRun(_ text: String) -> Int {
        var best = 0, run = 0
        for token in text.split(whereSeparator: \.isWhitespace) {
            run += 1; best = max(best, run)
            if let last = token.last, ".?!".contains(last) { run = 0 }
        }
        return best
    }

    /// The reasons Professional would change this dictation, empty when it would not.
    static func reasons(_ raw: String) -> [Reason] {
        let w = words(raw), joined = " " + w.joined(separator: " ") + " "
        var found: [Reason] = []
        let run = longestRun(raw)
        if run >= runOnWords { found.append(.runOn) }
        if correctionCues.contains(where: { joined.contains(" \($0) ") }) { found.append(.correction) }
        if w.filter({ listWords.contains($0) }).count >= 2 { found.append(.list) }
        let filler = w.filter { fillers.contains($0) }.count
        if filler >= 3, Double(filler) / Double(max(w.count, 1)) >= fillerShare { found.append(.fillers) }
        return found.count >= 2 || run >= longRunOnWords ? found : []
    }

    /// One line on what Professional would do here, built only from the reasons found.
    static func sentence(_ reasons: [Reason]) -> String {
        let parts = reasons.map { reason -> String in
            switch reason {
            case .runOn: return "break this long run-on into clear sentences"
            case .correction: return "keep only what you meant after your correction"
            case .list: return "set out the points you listed"
            case .fillers: return "tidy the filler words"
            }
        }
        guard let first = parts.first else { return "" }
        return "Professional would " + (parts.count == 1 ? first : parts.dropLast().joined(separator: ", ") + " and " + parts.last!) + "."
    }
}

/// How often the hint may show. Counters and dates only: never dictation text.
struct ProfessionalHintState: Codable, Equatable {
    var dictations = 0, dismissals = 0
    var firstSeen: Date?, lastOffered: Date?
    /// The History entry carrying the offer, if one is open.
    var offerID: String?
    /// The user acted on an offer (turned Professional on): the hint is done.
    var acted = false

    static let minDictations = 10, minDays = 3.0, minGapDays = 7.0, maxDismissals = 2

    /// Counts a finished dictation and, when the gate allows and `raw` shows signals, opens an offer on `entryID`.
    mutating func consider(entryID: String, raw: String, now: Date, enabled: Bool, professionalActive: Bool) {
        dictations += 1
        if firstSeen == nil { firstSeen = now }
        guard enabled, !professionalActive, !acted, dismissals < Self.maxDismissals, offerID == nil,
              dictations >= Self.minDictations, now.timeIntervalSince(firstSeen ?? now) >= Self.minDays * 86400,
              lastOffered.map({ now.timeIntervalSince($0) >= Self.minGapDays * 86400 }) ?? true,
              !ProfessionalSignal.reasons(raw).isEmpty else { return }
        offerID = entryID; lastOffered = now
    }
}
