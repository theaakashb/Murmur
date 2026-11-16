import Foundation

// Spike-only: the one-letter correction candidate the production observer no longer computes
// (the app feeds initial -> edited to the learning ledger instead). Compiled with app/Observation.swift.

struct TermCorrection: Hashable {
    let source: String
    let target: String
}

extension DictationObservation {
    var candidate: TermCorrection? { edited.flatMap { Self.replacement(initial, $0) } }

    @discardableResult
    mutating func observeCandidate(value: String, selection: NSRange, now: TimeInterval,
                                   sameFocusedField: Bool, readableNonsecure: Bool) -> TermCorrection? {
        observe(value: value, selection: selection, now: now, sameFocusedField: sameFocusedField, readableNonsecure: readableNonsecure)
        return candidate
    }

    static func oneLetterEdit(_ source: String, _ target: String) -> Bool {
        let a = Array(source), b = Array(target)
        guard abs(a.count - b.count) <= 1 else { return false }
        var i = 0, j = 0, edits = 0
        while i < a.count && j < b.count {
            if a[i] == b[j] { i += 1; j += 1; continue }
            edits += 1
            if edits > 1 { return false }
            if a.count >= b.count { i += 1 }
            if b.count >= a.count { j += 1 }
        }
        return edits + (a.count - i) + (b.count - j) == 1
    }

    static func replacement(_ original: String, _ current: String) -> TermCorrection? {
        let before = original.components(separatedBy: " ")
        let after = current.components(separatedBy: " ")
        guard before.count == after.count else { return nil }
        let changes = before.indices.filter { before[$0] != after[$0] }
        guard changes.count == 1, let index = changes.first else { return nil }
        let source = before[index].lowercased()
        let target = after[index].lowercased()
        guard source != target, source.count >= 3, target.count >= 3,
              source.count <= 40, target.count <= 40,
              source.unicodeScalars.allSatisfy(CharacterSet.letters.contains),
              target.unicodeScalars.allSatisfy(CharacterSet.letters.contains),
              Self.oneLetterEdit(source, target) else { return nil }
        // Structural candidate only. Even one letter can change meaning; ledger needs an eligibility gate.
        return TermCorrection(source: source, target: target)
    }
}

struct CorrectionEvidence {
    private var sessions: [UUID: TermCorrection] = [:]

    /// Supply final settled evidence only. Updating with nil removes undo/reversion evidence.
    mutating func update(sessionID: UUID, correction: TermCorrection?) {
        sessions[sessionID] = correction
    }

    /// Default denies all candidates. Caller must establish a spelling/known-term relationship;
    /// capitalization, edit size and repetition alone cannot establish preserved meaning.
    func promoted(eligible: (TermCorrection) -> Bool = { _ in false }) -> [TermCorrection] {
        let bySource = Dictionary(grouping: sessions.values, by: \.source)
        return bySource.values.compactMap { corrections in
            guard corrections.count >= 2, Set(corrections.map(\.target)).count == 1,
                  let correction = corrections.first, eligible(correction) else { return nil }
            return correction
        }.sorted { $0.source < $1.source }
    }
}

func runObservationChecks() {
    func tracker(_ value: String = "🙂 Acme done") -> DictationObservation {
        DictationObservation(value: value, insertedRange: NSRange(location: 3, length: 9), now: 0)!
    }
    let corrected = TermCorrection(source: "acme", target: "akme")
    var observation = tracker()
    assert(observation.observeCandidate(value: "🙂 Akme done", selection: NSRange(location: 7, length: 0), now: 1,
                               sameFocusedField: true, readableNonsecure: true) == corrected)
    assert(observation.range == NSRange(location: 3, length: 9))
    assert(observation.observeCandidate(value: "🙂 Acme done", selection: NSRange(location: 7, length: 0), now: 2,
                               sameFocusedField: true, readableNonsecure: true) == nil)
    var ledger = CorrectionEvidence()
    let first = UUID(), second = UUID(), third = UUID()
    ledger.update(sessionID: first, correction: corrected)
    assert(ledger.promoted(eligible: { $0 == corrected }).isEmpty)
    ledger.update(sessionID: first, correction: corrected)
    assert(ledger.promoted(eligible: { $0 == corrected }).isEmpty)
    ledger.update(sessionID: second, correction: corrected)
    assert(ledger.promoted().isEmpty)
    assert(ledger.promoted(eligible: { $0 == corrected }) == [corrected])
    ledger.update(sessionID: third, correction: TermCorrection(source: "acme", target: "ackme"))
    assert(ledger.promoted(eligible: { _ in true }).isEmpty)
    ledger.update(sessionID: third, correction: nil)
    ledger.update(sessionID: second, correction: nil)
    assert(ledger.promoted(eligible: { _ in true }).isEmpty)
    var outside = tracker()
    _ = outside.observeCandidate(value: "x🙂 Acme done", selection: NSRange(location: 7, length: 0), now: 1,
                        sameFocusedField: true, readableNonsecure: true)
    assert(outside.stopped && outside.candidate == nil)
    var appended = tracker()
    _ = appended.observeCandidate(value: "🙂 Akme done more", selection: NSRange(location: 12, length: 0), now: 1,
                         sameFocusedField: true, readableNonsecure: true)
    assert(!appended.stopped && appended.edited == "Akme done more")  // a fix, then more typing, stays attributed
    var sent = tracker()
    _ = sent.observeCandidate(value: "🙂 Akme done", selection: NSRange(location: 7, length: 0), now: 1, sameFocusedField: true, readableNonsecure: true)
    _ = sent.observeCandidate(value: "🙂 ", selection: NSRange(location: 3, length: 0), now: 2, sameFocusedField: true, readableNonsecure: true)
    assert(sent.stopped && sent.edited == "Akme done")  // Return sent the message: the fix stands
    for reset in ["", "\n", " \n\t ", "\r\n", "\u{00A0}"] {
        for prior in [nil, "Acme done", "Akme done"] as [String?] {
            var item = tracker()
            if let prior {
                _ = item.observeCandidate(value: "🙂 " + prior, selection: NSRange(location: 3, length: 0), now: 1,
                                 sameFocusedField: true, readableNonsecure: true)
            }
            _ = item.observeCandidate(value: "🙂 " + reset, selection: NSRange(location: 3, length: 0), now: 2,
                             sameFocusedField: true, readableNonsecure: true)
            assert(item.stopped && item.stopReason == .spanCleared)
            assert(item.edited == prior && item.candidate == (prior == "Akme done" ? corrected : nil))
            _ = item.observeCandidate(value: "", selection: NSRange(location: 0, length: 0), now: 3,
                             sameFocusedField: false, readableNonsecure: true)
            assert(item.edited == prior)
        }
    }
    var selection = tracker()
    _ = selection.observeCandidate(value: "🙂 Acme done", selection: NSRange(location: 0, length: 0), now: 1,
                          sameFocusedField: true, readableNonsecure: true)
    assert(selection.stopped)
    for reason in ["expiry", "focus", "secure"] {
        var item = tracker()
        _ = item.observeCandidate(value: "🙂 Acme done", selection: NSRange(location: 7, length: 0),
                         now: reason == "expiry" ? 60 : 1,
                         sameFocusedField: reason != "focus", readableNonsecure: reason != "secure")
        assert(item.stopped)
    }
    for value in ["🙂 ACME done", "🙂 Acme! done", "🙂 done", "🙂 Acme undone now", "🙂 I decline"] {
        var item = tracker()
        assert(item.observeCandidate(value: value, selection: NSRange(location: 3, length: 0), now: 1,
                            sameFocusedField: true, readableNonsecure: true) == nil)
    }
    assert(DictationObservation(value: "🙂", insertedRange: NSRange(location: 1, length: 1), now: 0) == nil)
    for (focused, readable, time, retained) in [(true, false, 2.0, false),
                                                 (false, false, 60.0, false),
                                                 (false, true, 2.0, true),
                                                 (true, true, 60.0, true),
                                                 (true, true, .nan, false)] {
        var item = tracker()
        _ = item.observeCandidate(value: "🙂 Akme done", selection: NSRange(location: 7, length: 0), now: 1,
                         sameFocusedField: true, readableNonsecure: true)
        _ = item.observeCandidate(value: "", selection: NSRange(location: 0, length: 0), now: time,
                         sameFocusedField: focused, readableNonsecure: readable)
        assert(item.stopped && (item.candidate != nil) == retained)
    }
    for fragment in ["e\u{301}", "👩‍💻", "🇺🇸"] {
        assert(DictationObservation(value: fragment, insertedRange: NSRange(location: 0, length: 1), now: 0) == nil)
    }
    assert(DictationObservation(value: "Acmes", insertedRange: NSRange(location: 0, length: 4), now: 0) == nil)
    var combined = DictationObservation(value: "e\u{301} Acme", insertedRange: NSRange(location: 3, length: 4), now: 0)!
    assert(combined.observeCandidate(value: "e\u{301} Akme", selection: NSRange(location: 7, length: 0), now: 1,
                            sameFocusedField: true, readableNonsecure: true) == corrected)
    var splitSelection = DictationObservation(value: "Acme e\u{301}", insertedRange: NSRange(location: 0, length: 7), now: 0)!
    assert(splitSelection.observeCandidate(value: "Akme e\u{301}", selection: NSRange(location: 6, length: 0), now: 1,
                                 sameFocusedField: true, readableNonsecure: true) == nil)
    assert(splitSelection.stopped)
    print("Observation checks passed")
}
