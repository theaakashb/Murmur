import Foundation

/// Pure snapshots only: the caller owns AX access, monotonic time, focus identity and polling.
/// Start only after verified insertion, in a nonsecure field with readable value/selection.
struct DictationObservation {
    /// Why observation ended; the raw values are logged and stored in History's `AfterPaste.endReason`.
    enum StopReason: String {
        case accessLost = "access or security lost", invalidClock = "invalid clock", focusDeparted = "focus departed", windowEnded = "window ended"
        case outsideAnchorsChanged = "outside anchors changed", selectionLeftSpan = "selection left dictated span", spanCleared = "span cleared", finished
        /// A valid prior edit survives these (the text it was read from is unchanged); any other reason removes it.
        var keepsEdit: Bool { [.windowEnded, .focusDeparted, .selectionLeftSpan, .spanCleared].contains(self) }
    }
    let sessionID: UUID
    /// The dictated span as inserted, original casing.
    let initial: String
    private let prefix: String
    private let suffix: String
    private let start: TimeInterval
    private(set) var range: NSRange
    private(set) var stopped = false
    private(set) var stopReason: StopReason?
    /// The span's current text, original casing; nil once attribution is lost. The app feeds
    /// `initial` -> `edited` to the ledger.
    private(set) var edited: String?

    init?(sessionID: UUID = UUID(), value: String, insertedRange: NSRange, now: TimeInterval) {
        let text = value as NSString
        guard now.isFinite, insertedRange.location >= 0, insertedRange.length > 0,
              insertedRange.location <= text.length,
              insertedRange.length <= text.length - insertedRange.location,
              Range(insertedRange, in: value) != nil,
              Self.validBoundaries(insertedRange, value) else { return nil }
        // A span cutting a word cannot establish a whole-word correction.
        let before = text.substring(to: insertedRange.location)
        let inserted = text.substring(with: insertedRange)
        let after = text.substring(from: NSMaxRange(insertedRange))
        guard !(before.last?.isLetter == true && inserted.first?.isLetter == true),
              !(inserted.last?.isLetter == true && after.first?.isLetter == true) else { return nil }
        self.sessionID = sessionID
        self.initial = text.substring(with: insertedRange)
        self.prefix = text.substring(to: insertedRange.location)
        self.suffix = text.substring(from: NSMaxRange(insertedRange))
        self.range = insertedRange
        self.start = now
    }

    /// Call even on unchanged snapshots so expiry/focus/permission changes stop observation.
    mutating func observe(value: String, selection: NSRange, now: TimeInterval,
                          sameFocusedField: Bool, readableNonsecure: Bool) {
        guard !stopped else { return }
        guard readableNonsecure else { return stop(.accessLost) }
        guard now.isFinite, now >= start else { return stop(.invalidClock) }
        guard sameFocusedField else { return stop(.focusDeparted) }
        guard now - start < 60 else { return stop(.windowEnded) }
        let text = value as NSString
        let prefixLength = (prefix as NSString).length
        let suffixLength = (suffix as NSString).length
        // ponytail: exact outside anchors reject even harmless outside edits; richer alignment needs live evidence.
        guard text.length >= prefixLength + suffixLength,
              text.substring(to: prefixLength) == prefix,
              text.substring(from: text.length - suffixLength) == suffix else {
            return stop(.outsideAnchorsChanged)
        }
        let updatedRange = NSRange(location: prefixLength, length: text.length - prefixLength - suffixLength)
        guard Range(updatedRange, in: value) != nil, Self.validBoundaries(updatedRange, value),
              selection.location >= updatedRange.location,
              selection.length >= 0, selection.location <= NSMaxRange(updatedRange),
              selection.length <= NSMaxRange(updatedRange) - selection.location,
              Range(selection, in: value) != nil, Self.validBoundaries(selection, value) else { return stop(.selectionLeftSpan) }
        // Text typed after the span stays in it; the ledger's word diff ignores added words.
        let current = text.substring(with: updatedRange)
        // A chat composer empties when Return sends it: keep the fix made before that.
        if current.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return stop(.spanCleared) }
        range = updatedRange
        edited = current
    }

    /// Finish after a final snapshot; evidence remains reversible by session ID in the ledger.
    mutating func finish() { stopped = true; stopReason = stopReason ?? .finished }

    private mutating func stop(_ reason: StopReason) {
        stopped = true
        stopReason = reason
        if !reason.keepsEdit { edited = nil }
    }

    private static func validBoundaries(_ range: NSRange, _ value: String) -> Bool {
        // AX offsets are UTF16; NSString accepts surrogate/combining/ZWJ fragments.
        // ponytail: linear boundary scan; indexed boundaries if large-field polling proves expensive.
        var offset = 0
        var boundaries: Set<Int> = [0]
        for character in value {
            offset += String(character).utf16.count
            boundaries.insert(offset)
        }
        return boundaries.contains(range.location) && boundaries.contains(NSMaxRange(range))
    }
}

/// Stand-in for a field with no readable text (VS Code's stub, web canvases): a virtual copy of
/// the pasted span, edited by the keys typed after it, starting with the caret at its end. Its
/// `value`/`selection` feed `DictationObservation` as snapshots with no text around the span.
/// `lost` (a ⌘/⌃ chord such as undo, or an edit outside the span) drops the evidence; `departed`
/// (Return, Tab, vertical caret keys, or the caret leaving the span) keeps it, like focus departure.
/// After a click the caret could be anywhere: every caret position and every selection of 1–3 whole words
/// is followed at once, and the span reads as the single edit `resolve` finds plausible.
/// ponytail: the target app's own autocorrect/autocomplete is invisible here; the ledger's filter
/// and two-session rule are the backstop. Read the field instead wherever AX exposes it.
struct KeystrokeSpan {
    enum Key: Equatable {
        case text(String), backspace(word: Bool), forwardDelete
        case left(word: Bool, extend: Bool), right(word: Bool, extend: Bool), other
    }
    enum State { case editing, departed, lost }
    private(set) var chars: [Character]
    private(set) var state = State.editing
    private var anchor: Int, caret: Int
    /// Where the caret might be after a click; empty while its position is known.
    private var guesses: [KeystrokeSpan] = []
    private var typedSinceClick = false
    private var unresolved = false
    /// Picks the plausible reading among the guesses (the ledger's filter in the app).
    var resolve: ([String]) -> String? = { _ in nil }

    init(_ text: String) { chars = Array(text); anchor = chars.count; caret = chars.count }
    private init(_ chars: [Character], anchor: Int, caret: Int) { self.chars = chars; self.anchor = anchor; self.caret = caret }

    var value: String { String(chars) }
    /// UTF16 range, as AX reports selections. Unknown after a click: the span start stands in.
    var selection: NSRange {
        if !guesses.isEmpty { return NSRange(location: 0, length: 0) }
        func utf16(_ index: Int) -> Int { String(chars[..<index]).utf16.count }
        let low = min(anchor, caret), high = max(anchor, caret)
        return NSRange(location: utf16(low), length: utf16(high) - utf16(low))
    }

    mutating func depart() { if state == .editing { state = .departed } }

    /// Takes the plausible reading of the guesses as `value`. Resolving runs the ledger's filter
    /// (tens of ms on a long span), so the app calls this on its poll, not per keystroke.
    mutating func refresh() {
        guard unresolved else { return }
        unresolved = false
        if let reading = resolve(guesses.map(\.value)) { chars = Array(reading) }
    }

    /// A click in the app (a double-click is two). A click after typing that still has no
    /// plausible reading ends the watch; otherwise the guesses restart from the current text.
    /// ponytail: one guess per caret position and word, O(span length) work per key; bound the
    /// span if very long dictations make keystrokes lag.
    mutating func click() {
        guard state == .editing else { return }
        if typedSinceClick {
            guard let reading = resolve(guesses.map(\.value)) else { state = .departed; return }
            chars = Array(reading)
        }
        typedSinceClick = false; unresolved = false
        let words = chars.indices.filter { isWord(chars[$0]) && ($0 == 0 || !isWord(chars[$0 - 1])) }
        // Selections of 1–3 whole words cover a double-click and a mouse drag across a spacing fix
        // (git hub → GitHub); the ledger learns at most 3 words per side.
        guesses = (0...chars.count).map { KeystrokeSpan(chars, anchor: $0, caret: $0) }
            + words.flatMap { start in
                var end = start
                return (0..<3).compactMap { _ -> KeystrokeSpan? in
                    end = wordEnd(after: end)
                    return end <= chars.count ? KeystrokeSpan(chars, anchor: start, caret: end) : nil
                }
            }
    }

    mutating func apply(_ key: Key) {
        guard state == .editing else { return }
        if !guesses.isEmpty {
            if key == .other { state = .lost; return }
            typedSinceClick = true
            var seen = Set<String>()
            let next = guesses.compactMap { guess -> KeystrokeSpan? in
                var next = guess
                next.apply(key)
                return next.state == .editing && seen.insert("\(next.anchor) \(next.caret) \(next.value)").inserted ? next : nil
            }
            // Return or a caret key ends the edit: settle what was typed before it.
            if next.isEmpty { refresh(); state = .departed; return }
            guesses = next
            unresolved = true
            return
        }
        let low = min(anchor, caret), high = max(anchor, caret)
        switch key {
        case .text(let text):
            // Return, Tab, Escape (control) and arrow/Home/End/Page keys (private-use U+F700...) end the
            // edit (a chat sends, the caret moves away) without undoing it.
            guard !text.isEmpty else { state = .lost; return }
            guard !text.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || CharacterSet.newlines.contains($0) || (0xF700...0xF8FF).contains($0.value) }) else { state = .departed; return }
            chars.replaceSubrange(low..<high, with: Array(text)); caret = low + text.count; anchor = caret
        case .backspace(let word):
            if low < high { remove(low..<high); return }
            let start = word ? wordStart(before: caret) : caret - 1
            guard start >= 0, caret > 0 else { state = .lost; return }
            remove(start..<caret)
        case .forwardDelete:
            if low < high { remove(low..<high); return }
            guard caret < chars.count else { state = .lost; return }
            remove(caret..<caret + 1)
        case .left(let word, let extend):
            var next = !extend && low < high ? low : caret
            if word || extend || low == high { next = word ? wordStart(before: next) : next - 1 }
            guard next >= 0 else { state = .departed; return }
            caret = next; if !extend { anchor = next }
        case .right(let word, let extend):
            var next = !extend && low < high ? high : caret
            if word || extend || low == high { next = word ? wordEnd(after: next) : next + 1 }
            guard next <= chars.count else { state = .departed; return }
            caret = next; if !extend { anchor = next }
        case .other:
            state = .lost
        }
    }

    private mutating func remove(_ range: Range<Int>) { chars.removeSubrange(range); caret = range.lowerBound; anchor = caret }
    private func isWord(_ c: Character) -> Bool { c.isLetter || c.isNumber || c == "'" || c == "\u{2019}" }
    /// macOS Option-Left: skip separators, then the word. -1 when that would leave the span.
    private func wordStart(before index: Int) -> Int {
        var i = index
        while i > 0, !isWord(chars[i - 1]) { i -= 1 }
        guard i > 0 else { return -1 }
        while i > 0, isWord(chars[i - 1]) { i -= 1 }
        return i
    }
    private func wordEnd(after index: Int) -> Int {
        var i = index
        while i < chars.count, !isWord(chars[i]) { i += 1 }
        guard i < chars.count else { return chars.count + 1 }
        while i < chars.count, isWord(chars[i]) { i += 1 }
        return i
    }
}
