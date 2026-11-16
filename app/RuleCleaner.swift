import Foundation

// The deterministic local cleaner: fillers, corrections, lists, spoken commands and email layout.

// MARK: - Cleaner protocols

/// A synchronous cleanup provider (e.g. the deterministic local baseline).
protocol Cleaner {
    func clean(_ raw: String, options: CleanupOptions) throws -> String
}


// MARK: - Rule-based baseline

/// Deterministic, conservative local baseline. When a rule is unsure it leaves text unchanged.
/// This is not the PRD's cleanup-model selection; it is a working local route until one is chosen.
struct RuleCleaner: Cleaner {
    init() {}

    func clean(_ raw: String, options: CleanupOptions) throws -> String {
        var text = options.spoken ? RuleCleaner.spokenCommands(raw) : raw
        if options.fillers { text = RuleCleaner.removeFillers(text) }
        if options.corrections {
            if options.polish { text = RuleCleaner.applyInsteadCorrections(text) }
            text = RuleCleaner.applyCorrections(text)
        }
        if options.lists { text = RuleCleaner.formatList(text) }
        if options.punctuation { text = RuleCleaner.capitalizeSentences(text) }
        return text
    }

    /// Local punctuation: capitals at sentence starts and for "I". Adding periods or question
    /// marks needs the cloud model; rules cannot tell where an unpunctuated sentence ends.
    static func capitalizeSentences(_ text: String) -> String {
        // A . ? or ! ends a sentence only before whitespace, so "gmail.com" keeps its case. A period after a
        // dotted abbreviation or a title does not: "the U.S. at 9 a.m. today", "Dr. Patel".
        // ponytail: "in the U.S. Next year" stays lowercase; the cloud model punctuates those.
        let titles: Set<String> = ["mr", "mrs", "ms", "dr", "st", "vs"]
        var out = "", start = true, ended = false, word = ""
        for c in text {
            if start, c.isLetter { out += c.uppercased(); word = String(c); start = false; ended = false; continue }
            if c.isLetter || c.isNumber { start = false; ended = false }
            else if c == "." { if word.last != "." { ended = !word.contains(".") && !titles.contains(word.lowercased()) } }
            else if "?!".contains(c) { ended = true }
            else if c == "\n" || (c.isWhitespace && ended) { start = true }
            word = c.isWhitespace ? "" : word + String(c)
            out.append(c)
        }
        return out.replacingOccurrences(of: #"\bi(?=\b|')"#, with: "I", options: .regularExpression)
    }

    // MARK: Fillers

    static let stutterWords: Set<String> = ["i", "the", "a", "an", "we", "you", "and", "to", "my", "he", "she", "they", "it's", "i'm"]
    /// Words after which a comma stays when a following hesitation is removed ("So, um, I think").
    static let commaKeepers: Set<String> = [
        "so", "well", "yes", "yeah", "yep", "no", "nope", "okay", "ok", "oh", "right", "sure", "alright",
        "hey", "hi", "hello", "now", "anyway", "honestly", "actually", "also", "basically", "first", "second",
        "third", "finally", "then", "again", "however", "though", "thanks", "look", "listen", "sorry",
    ]

    static func isHesitation(_ tok: CleanupToken) -> Bool {
        guard tok.kind == .word, CleanupText.hesitations.contains(tok.lower) else { return false }
        // All-caps forms such as "ER" or "MM" are abbreviations, not hesitations.
        return !(tok.text.count > 1 && tok.text == tok.text.uppercased())
    }

    static func removeFillers(_ text: String) -> String {
        var t = CleanupText.tokenize(text)
        var changed = false
        // Pass 1: hesitations and comma-delimited "like" / "you know".
        var i = 0
        while i < t.count {
            let mask = CleanupText.quotedMask(t)
            guard t[i].kind == .word, !mask[i] else { i += 1; continue }
            let w = t[i].lower
            let start = CleanupText.isSentenceStart(t, i)
            let p = CleanupText.prevNonSpace(t, i)
            let n = CleanupText.nextNonSpace(t, i)
            let pComma = p.map { t[$0].isComma } ?? false
            let nComma = n.map { t[$0].isComma } ?? false
            if isHesitation(t[i]) {
                var lo = i, hi = i, capitalize = start
                if pComma && nComma, let p, let n {
                    let wb = CleanupText.prevNonSpace(t, p).flatMap { t[$0].kind == .word ? $0 : nil }
                    let wa = CleanupText.nextNonSpace(t, n).flatMap { t[$0].kind == .word ? $0 : nil }
                    if let wb, let wa, t[wb].lower == t[wa].lower, !mask[wb] {
                        // Restart across a hesitation: "Let's, um, let's go" keeps the second copy.
                        lo = wb; hi = n; capitalize = CleanupText.isSentenceStart(t, wb)
                    } else if let wb, commaKeepers.contains(t[wb].lower)
                                || (t[wb].text.first?.isUppercase == true && t[wb].lower != "i" && CleanupText.isSentenceStart(t, wb)) {
                        hi = n
                    } else {
                        lo = p; hi = n
                    }
                } else if nComma, let n {
                    hi = n
                } else if start, let n, t[n].isTerminal, t[n].text != "\n" {
                    hi = n // A hesitation that is its own sentence: "Hmm. Okay."
                } else if pComma, let p, n.map({ t[$0].isTerminal || t[$0].text == ";" }) ?? true {
                    lo = p
                }
                remove(&t, lo, hi, capitalizeNext: capitalize)
                changed = true; i = max(0, lo - 1); continue
            }
            var spanEnd: Int? = nil
            if w == "like" { spanEnd = i }
            if w == "you", let k = n, k == i + 2, t[k].kind == .word, t[k].lower == "know" { spanEnd = k }
            if let e = spanEnd {
                let ne = CleanupText.nextNonSpace(t, e)
                let neComma = ne.map { t[$0].isComma } ?? false
                let neEnd = ne.map { t[$0].isTerminal } ?? true
                if pComma, neComma, let p, let ne {
                    remove(&t, p, ne, capitalizeNext: false); changed = true; i = max(0, p - 1); continue
                }
                if start, neComma, let ne {
                    remove(&t, i, ne, capitalizeNext: true); changed = true; i = max(0, i - 1); continue
                }
                if w == "you", pComma, neEnd, let p {
                    remove(&t, p, e, capitalizeNext: false); changed = true; i = max(0, p - 1); continue
                }
            }
            i += 1
        }
        // Pass 2: stutters ("I I think", "the, the plan").
        i = 0
        while i < t.count {
            let mask = CleanupText.quotedMask(t)
            guard t[i].kind == .word, !mask[i], stutterWords.contains(t[i].lower),
                  let k = CleanupText.nextWord(t, from: i + 1), t[k].lower == t[i].lower else { i += 1; continue }
            let between = t[(i + 1)..<k]
            guard between.allSatisfy({ $0.kind == .space || $0.isComma }), between.filter(\.isComma).count <= 1 else { i += 1; continue }
            let start = CleanupText.isSentenceStart(t, i)
            t.removeSubrange(i..<k)
            if start { t[i].text = CleanupText.capitalized(t[i].text) }
            changed = true
        }
        return changed ? CleanupText.render(CleanupText.normalize(t)) : text
    }

    /// Remove tokens lo...hi plus trailing spaces; optionally capitalize the following word.
    static func remove(_ t: inout [CleanupToken], _ lo: Int, _ hi: Int, capitalizeNext: Bool) {
        var end = hi + 1
        while end < t.count, t[end].kind == .space { end += 1 }
        t.removeSubrange(lo..<end)
        if lo > 0, lo < t.count, t[lo - 1].kind == .word, t[lo].kind == .word {
            t.insert(CleanupToken(kind: .space, text: " "), at: lo)
        }
        if capitalizeNext, let k = CleanupText.nextWord(t, from: lo), !t[lo..<k].contains(where: { $0.kind == .punct && !$0.isComma }) {
            t[k].text = CleanupText.capitalized(t[k].text)
        }
    }

    // MARK: Corrections

    enum Cue { case sorry, no, iMean, actually, orRather, noWait, scratch }

    static let cuePatterns: [([String], Cue)] = [
        (["scratch", "that"], .scratch), (["no", "wait"], .noWait), (["wait", "no"], .noWait),
        (["i", "mean"], .iMean), (["i", "meant"], .iMean), (["or", "rather"], .orRather),
        (["sorry"], .sorry), (["actually"], .actually), (["no"], .no),
    ]
    static let apologyLead: Set<String> = ["i'm", "im", "am", "so", "very", "really", "was", "were", "be", "been", "feel", "felt", "say", "said", "is", "are", "terribly", "truly", "i", "you're", "we're", "he's", "she's", "they're", "awfully", "sincerely"]
    static let anchors: Set<String> = ["at", "on", "in", "to", "for", "with", "from", "by", "the", "a", "an", "my", "your", "his", "her", "our", "their", "this", "into", "onto", "before", "after", "until"]
    static let weekdays: Set<String> = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]
    static let months: Set<String> = ["january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december"]
    /// "may" is a modal, so `months` omits it: `category` accepts only a capitalized "May", the guard's lowercase word lists accept it always.
    static let monthsAndMay: Set<String> = months.union(["may"])
    static let relativeTimes: Set<String> = ["today", "tomorrow", "tonight", "yesterday", "morning", "afternoon", "evening", "noon", "midnight"]
    static let colors: Set<String> = ["red", "blue", "green", "yellow", "black", "white", "orange", "purple", "pink", "gray", "grey", "brown", "silver", "gold"]

    static func category(_ t: [CleanupToken], _ i: Int) -> String? {
        let w = t[i].lower
        if weekdays.contains(w) { return "weekday" }
        if months.contains(w) || t[i].text == "May" { return "month" }
        if relativeTimes.contains(w) { return "time" }
        if colors.contains(w) { return "color" }
        if CleanupText.ordinals[w] != nil { return "ordinal" }
        if CleanupText.numberValue(w) != nil { return "number" }
        if t[i].text.first?.isUppercase == true, w != "i", !w.hasPrefix("i'"), !CleanupText.isSentenceStart(t, i) { return "name" }
        return nil
    }

    static func matchCue(_ t: [CleanupToken], at i: Int) -> (Cue, Int)? {
        for (words, cue) in cuePatterns {
            var idx = i, last = i, ok = true
            for (n, w) in words.enumerated() {
                if n > 0 {
                    guard let k = CleanupText.nextNonSpace(t, idx), k == idx + 2, t[k].kind == .word else { ok = false; break }
                    idx = k
                }
                guard t[idx].kind == .word, t[idx].lower == w else { ok = false; break }
                last = idx
            }
            if ok { return (cue, last) }
        }
        return nil
    }

    static func isNumberWord(_ t: [CleanupToken], _ i: Int) -> Bool {
        t[i].kind == .word && CleanupText.ordinals[t[i].lower] == nil && CleanupText.numberValue(t[i].lower) != nil
    }

    static let insteadFragment = try! NSRegularExpression(pattern: #"(?<![\p{L}\p{N}’'\-])([\p{L}]+)\. +([\p{L}]+) instead\.(?=\s|$)"#, options: .caseInsensitive)

    /// shortcut: only one known date/time/color slot, expand only with evidence for safe scope.
    static func applyInsteadCorrections(_ text: String) -> String {
        let ns = text as NSString
        var out = text
        let mask = CleanupText.quotedMask(CleanupText.tokenize(text))
        for match in insteadFragment.matches(in: text, range: NSRange(location: 0, length: ns.length)).reversed() {
            let prefix = CleanupText.tokenize(ns.substring(to: match.range(at: 1).location))
            let tokens = CleanupText.tokenize(ns.substring(with: match.range))
            let offset = prefix.count
            guard offset + 3 < mask.count, !mask[offset], !mask[offset + 3],
                  let category = Self.category(tokens, 0), ["weekday", "month", "time", "color"].contains(category),
                  category == Self.category(tokens, 3), tokens[0].lower != tokens[3].lower else { continue }
            let sentence = Array(prefix.reversed().prefix { !$0.isTerminal })
            // Multiple alternatives, negations and subordinate clauses leave replacement scope unclear.
            guard !sentence.contains(where: { $0.kind == .word &&
                (Self.category([$0], 0) == category || CleanupText.isNegation($0.lower) ||
                 ["and", "or", "but", "if", "unless", "because", "while", "instead"].contains($0.lower)) }) else { continue }
            let replacement = category == "color" || category == "time" ? tokens[3].lower : tokens[3].text
            out = (out as NSString).replacingCharacters(in: match.range, with: replacement + ".")
        }
        return out
    }

    /// Speech models often end a sentence around a cue ("Tuesday. No wait. Wednesday."). Rejoin it
    /// into the comma form the rules expect, and keep the rejoined text only if a correction applies.
    static func applyCorrections(_ text: String) -> String {
        let merged = mergeSplitCues(text)
        if merged != text {
            let corrected = applyCommaCorrections(merged)
            if corrected != merged { return corrected }
        }
        return applyCommaCorrections(text)
    }

    // "Actually" crosses a period only for a one-word repair fragment; a full sentence may
    // introduce a new fact. "Scratch that" already accepts a preceding period.
    // Every `cuePatterns` cue except "scratch that"; "wait no" may carry a comma, "actually" only before a one-word repair.
    static let splitCue = try! NSRegularExpression(pattern: #"(?<=[\p{L}\p{N}])\. (?:(Oh|Ah),? )?("# + cuePatterns.filter { $0.1 != .scratch }.map { cue -> String in
        switch cue.0 {
        case ["wait", "no"]: return "wait,? no"
        case ["actually"]: return #"actually(?=[.,] +[\p{L}\p{N}]+[.!?](?:\s|$))"#
        default: return cue.0.joined(separator: " ")
        }
    }.joined(separator: "|") + #")[.,] +(\p{L}+|\p{N}+)"#, options: .caseInsensitive)

    static func mergeSplitCues(_ text: String) -> String {
        let ns = text as NSString
        var out = text
        for m in splitCue.matches(in: text, range: NSRange(location: 0, length: ns.length)).reversed() {
            // A leading interjection stays as a word ("sentence. Oh, no, I mean" -> "sentence, oh, no, I mean").
            let lead = m.range(at: 1).location == NSNotFound ? "" : ns.substring(with: m.range(at: 1)).lowercased() + ", "
            let cue = ns.substring(with: m.range(at: 2)).lowercased().replacingOccurrences(of: ",", with: "")
            var word = ns.substring(with: m.range(at: 3))
            // Only words known to be lowercase mid-sentence; names, weekdays and months keep their capital.
            let lower = word.lowercased()
            if relativeTimes.contains(lower) || colors.contains(lower) || anchors.contains(lower)
                || CleanupText.ordinals[lower] != nil || CleanupText.numberValue(lower) != nil { word = lower }
            let replacement = ", \(lead)\(cue), \(word)"
            if cue == "actually" {
                let prefix = CleanupText.tokenize(ns.substring(to: m.range.location))
                guard let previous = prefix.last, CleanupText.quotedMask(prefix).last != true else { continue }
                let fragment = previous.text + replacement
                guard applyCommaCorrections(fragment) != fragment else { continue }
            }
            out = (out as NSString).replacingCharacters(in: m.range, with: replacement)
        }
        return out
    }

    static func applyCommaCorrections(_ text: String) -> String {
        var t = CleanupText.tokenize(text)
        var changed = false
        var edits = 0
        var i = 0
        scan: while i < t.count, edits < 20 {
            let mask = CleanupText.quotedMask(t)
            guard t[i].kind == .word, !mask[i], let (cue, cueEnd) = matchCue(t, at: i), let p = CleanupText.prevNonSpace(t, i) else { i += 1; continue }
            let before = t[p]
            let scratch = cue == .scratch
            guard before.isComma || before.isDash || (scratch && (before.text == "." || before.text == "!")) else { i += 1; continue }
            let q = CleanupText.nextNonSpace(t, cueEnd)
            let qIsSep = q.map { t[$0].isComma || t[$0].isDash || t[$0].text == ":" } ?? false
            let needsSep = cue == .sorry || cue == .no || cue == .noWait || cue == .scratch
            if needsSep && !qIsSep { i += 1; continue }
            // Interregnum: separators, hesitations, a secondary cue ("no, sorry,"), or a short
            // negated restatement of the reparandum ("no, not Tuesday, Thursday").
            var r = cueEnd + 1
            while r < t.count {
                if t[r].kind == .space || t[r].isComma || t[r].isDash || t[r].text == ":" { r += 1; continue }
                if isHesitation(t[r]) { r += 1; continue }
                if t[r].kind == .word, let (c2, e2) = matchCue(t, at: r), c2 != .actually, c2 != .scratch, c2 != .no { r = e2 + 1; continue }
                if t[r].kind == .word, t[r].lower == "not" {
                    var k = r + 1, words = 0
                    while k < t.count, !t[k].isBoundary { if t[k].kind == .word { words += 1 }; k += 1 }
                    if words >= 1, words <= 3, k < t.count, t[k].isComma || t[k].isDash,
                       let after = CleanupText.nextNonSpace(t, k), t[after].kind == .word { r = k + 1; continue }
                }
                break
            }
            guard r < t.count, t[r].kind == .word, !mask[r] else { i += 1; continue }
            var repair: [Int] = []
            var k = r
            while k < t.count, !t[k].isBoundary { if t[k].kind == .word { repair.append(k) }; k += 1 }
            guard !repair.isEmpty else { i += 1; continue }
            let repairBoundary = k < t.count ? k : nil

            // Reparandum window: words before the cue back to the previous boundary.
            var window: [Int] = []
            var w = p - 1
            if scratch {
                while w >= 0, !t[w].isTerminal { if t[w].kind == .word { window.insert(w, at: 0) }; w -= 1 }
                if before.isTerminal == false || !window.isEmpty {
                    guard !window.isEmpty, window.count <= 30, !window.contains(where: { mask[$0] }) else { i += 1; continue }
                    deleteRange(&t, from: window[0], to: r, capitalize: true)
                    changed = true; edits += 1; i = 0; continue scan
                }
                i += 1; continue
            }
            while w >= 0, !t[w].isBoundary { if t[w].kind == .word { window.insert(w, at: 0) }; w -= 1 }
            guard let lastW = window.last, !window.contains(where: { mask[$0] }) else { i += 1; continue }
            if cue == .sorry && apologyLead.contains(t[lastW].lower) { i += 1; continue }

            var from: Int? = nil
            // Number run + noun restated with a new number ("twelve chairs, I mean fifteen chairs").
            if window.count >= 2, !isNumberWord(t, lastW), isNumberWord(t, window[window.count - 2]), isNumberWord(t, r) {
                var rk = 0
                while rk < repair.count, isNumberWord(t, repair[rk]) { rk += 1 }
                if rk < repair.count, t[repair[rk]].lower == t[lastW].lower {
                    var s = window.count - 2
                    while s > 0, isNumberWord(t, window[s - 1]) { s -= 1 }
                    from = window[s]
                }
            }
            // Rule A: same-category swap ("Tuesday, sorry, Thursday"); number runs move together.
            if from == nil, let cat = category(t, lastW), cat == category(t, r), t[lastW].lower != t[r].lower {
                var s = window.count - 1
                if cat == "number" { while s > 0, isNumberWord(t, window[s - 1]) { s -= 1 } }
                if cat == "name", s > 0, CleanupText.titles.contains(t[window[s - 1]].lower), CleanupText.titles.contains(t[r].lower) { s -= 1 }
                from = window[s]
            }
            if from == nil, category(t, lastW) == "name", CleanupText.titles.contains(t[r].lower), repair.count >= 2,
               window.count >= 2, CleanupText.titles.contains(t[window[window.count - 2]].lower) {
                from = window[window.count - 2] // "Ms. Alvarez, no, Mr. Alvarez"
            }
            // Rule B: repair restarts a short phrase with the same anchor word ("at the office, I mean at home").
            if from == nil, anchors.contains(t[r].lower), repair.count <= 6, let j = window.lastIndex(where: { t[$0].lower == t[r].lower }) {
                let span = window.count - j
                if span >= 2 && span <= 6 { from = window[j] }
            }
            guard let start = from else { i += 1; continue } // Ambiguous: keep the original wording.
            // A comma closing a parenthetical repair goes too ("the design team, I mean the
            // product team, to the review"), unless it separates a clause or a list item.
            if let b = repairBoundary, t[b].isComma, before.isComma,
               let prevOfStart = CleanupText.prevNonSpace(t, start), t[prevOfStart].kind == .word,
               let nextWordAfter = CleanupText.nextNonSpace(t, b), t[nextWordAfter].kind == .word,
               !["and", "but", "or", "so", "then", "number", "step", "item"].contains(t[nextWordAfter].lower),
               CleanupText.ordinals[t[nextWordAfter].lower] == nil, t[nextWordAfter].text.first?.isLowercase == true {
                t.remove(at: b)
            }
            deleteRange(&t, from: start, to: r, capitalize: CleanupText.isSentenceStart(t, start))
            changed = true; edits += 1; i = 0
        }
        return changed ? CleanupText.render(CleanupText.normalize(t)) : text
    }

    static func deleteRange(_ t: inout [CleanupToken], from: Int, to repairStart: Int, capitalize: Bool) {
        t.removeSubrange(from..<repairStart)
        if capitalize, from < t.count, t[from].kind == .word { t[from].text = CleanupText.capitalized(t[from].text) }
    }

    // MARK: Lists

    static let strongListCues: Set<String> = ["list", "checklist", "agenda", "steps", "to-do", "todo", "to-dos", "todos", "items", "groceries"]
    static let cardinalWords = [""] + (1...10).map { n in CleanupText.cardinals.first { $0.value == n }!.key }
    static let ordinalWords = [""] + (1...10).map { n in CleanupText.ordinals.first { $0.value == n && !$0.key.hasSuffix("ly") }!.key }

    /// Matches enumeration marker k at token i; returns the last token index of the marker.
    static func matchMarker(_ t: [CleanupToken], _ i: Int, _ k: Int) -> Int? {
        guard k < ordinalWords.count, t[i].kind == .word else { return nil }
        let w = t[i].lower
        if w == ordinalWords[k] || w == ordinalWords[k] + "ly" { return i }
        if ["number", "step", "item"].contains(w), let n = CleanupText.nextNonSpace(t, i), n == i + 2, t[n].lower == cardinalWords[k] { return n }
        return nil
    }

    static func formatList(_ text: String) -> String {
        let t = CleanupText.tokenize(text)
        for m1 in t.indices {
            guard let m1End = matchMarker(t, m1, 1) else { continue }
            // Lead-in: sentence containing the first marker, or the previous sentence if the marker starts one.
            var s = m1
            while s > 0, !t[s - 1].isTerminal { s -= 1 }
            let leadTokens = Array(t[s..<m1])
            var leadIsPrevSentence = false
            var leadStart = s
            let hasLeadWords = leadTokens.contains { $0.kind == .word }
            var cueTokens = leadTokens
            if !hasLeadWords {
                guard s > 0, t[s - 1].text == "." || t[s - 1].text == ":" else { continue }
                var ps = s - 1
                while ps > 0, !t[ps - 1].isTerminal { ps -= 1 }
                cueTokens = Array(t[ps..<s])
                leadStart = ps; leadIsPrevSentence = true
            }
            let leadEndsColon = cueTokens.last(where: { $0.kind != .space })?.text == ":"
            // A list noun counts as a cue only near the end of the lead-in ("my to-do list for today",
            // "the steps are"), not as a modifier earlier in it ("our list price went up").
            let leadWords = cueTokens.filter { $0.kind == .word }
            let leadHasCue = leadWords.suffix(4).contains { strongListCues.contains($0.lower) }
            guard leadEndsColon || leadHasCue else { continue }
            if !leadIsPrevSentence, let p = CleanupText.prevNonSpace(t, m1), !(t[p].text == ":" || t[p].isComma || t[p].kind == .word) { continue }

            // Markers 2...n, each after a separator (, ; .) optionally followed by "and"/"then".
            var markers: [(start: Int, end: Int, sep: Int)] = [(m1, m1End, m1)]
            var k = 2
            var j = m1End + 1
            var lastItemEnd = t.count
            let mask = CleanupText.quotedMask(t)
            if mask[m1] { continue }
            while j < t.count {
                if t[j].text == "\n" { lastItemEnd = j; break }
                if let e = matchMarker(t, j, k), !mask[j] {
                    var p = CleanupText.prevNonSpace(t, j)
                    while let pp = p, t[pp].kind == .word, ["and", "then"].contains(t[pp].lower) { p = CleanupText.prevNonSpace(t, pp) }
                    if let pp = p, t[pp].isComma || t[pp].text == ";" || t[pp].isTerminal {
                        // A question or exclamation mark belongs to its item.
                        let sep = (t[pp].text == "?" || t[pp].text == "!") ? pp + 1 : pp
                        markers.append((j, e, sep)); k += 1; j = e + 1; continue
                    }
                }
                if t[j].isTerminal {
                    // Sentence-final punctuation ends the list unless the next marker follows it.
                    if let n = CleanupText.nextWord(t, from: j + 1), matchMarker(t, n, k) != nil, CleanupText.nextNonSpace(t, j) == n { j += 1; continue }
                    lastItemEnd = (t[j].text == "?" || t[j].text == "!") ? j + 1 : j; break
                }
                j += 1
            }
            guard markers.count >= 2 else { continue }
            var items: [String] = []
            for (n, m) in markers.enumerated() {
                let end = n + 1 < markers.count ? markers[n + 1].sep : lastItemEnd
                var body = Array(t[(m.end + 1)..<end])
                while let f = body.first, f.kind == .space || f.isComma || f.text == ":" || f.isDash { body.removeFirst() }
                while let l = body.last, l.kind == .space || l.isComma || l.text == ";" || l.text == "." { body.removeLast() }
                let wordsInItem = body.filter { $0.kind == .word }.count
                guard wordsInItem >= 1, wordsInItem <= 25 else { items = []; break }
                if let fi = body.firstIndex(where: { $0.kind == .word }) { body[fi].text = CleanupText.capitalized(body[fi].text) }
                items.append("\(n + 1). " + CleanupText.render(body))
            }
            guard items.count == markers.count else { continue }
            let prefix = CleanupText.render(Array(t[0..<leadStart])).trimmingCharacters(in: .whitespaces)
            var lead = CleanupText.render(Array(t[leadStart..<(leadIsPrevSentence ? s : m1)])).trimmingCharacters(in: .whitespaces)
            if !leadIsPrevSentence || !(lead.hasSuffix(".") || lead.hasSuffix(":")) {
                while let l = lead.last, l == "," || l == ":" || l == " " || l == ";" || l == "-" || l == "\u{2014}" { lead.removeLast() }
                lead += ":"
            }
            var restStart = lastItemEnd
            if restStart < t.count, t[restStart].isTerminal, t[restStart - 1].text != "?", t[restStart - 1].text != "!" { restStart += 1 }
            let rest = restStart < t.count ? CleanupText.render(Array(t[restStart...])).trimmingCharacters(in: .whitespaces) : ""
            var out = prefix.isEmpty ? lead : prefix + " " + lead
            out += "\n" + items.joined(separator: "\n")
            if !rest.isEmpty { out += "\n" + rest }
            return out
        }
        return text
    }
}

// MARK: - Spoken commands, numbers, symbols and email layout

extension RuleCleaner {
    /// Dictated marks and layout. "dash" is an em dash between words; "hyphen" joins its neighbors.
    static let markCommands: [([String], String)] = [
        (["new", "paragraph"], "\n\n"), (["new", "line"], "\n"), (["question", "mark"], "?"), (["exclamation", "mark"], "!"),
        (["exclamation", "point"], "!"), (["full", "stop"], "."), (["period"], "."), (["comma"], ","), (["colon"], ":"),
        (["semicolon"], ";"), (["hyphen"], "-"), (["dash"], "\u{2014}"),
    ]
    /// Spoken forms of "@", before a handle; `Dictionary` targets them too.
    static let atPhrases: [[String]] = [["at", "the", "rate"], ["at", "sign"]]
    /// A word before a command that makes it an ordinary noun: "a period", "the colon", "notice period", "a new line".
    static let commandBlockersBefore: Set<String> = [
        "a", "an", "the", "this", "that", "these", "those", "my", "your", "his", "her", "our", "their", "its", "of", "per",
        "each", "every", "any", "some", "no", "one", "another", "which", "what", "whole", "entire", "first", "second", "third",
        "fourth", "last", "next", "same", "single", "double", "extra", "missing", "stray", "final", "for", "with", "by", "about",
        "use", "using", "add", "type", "typed", "put", "insert", "oxford", "serial", "trial", "grace", "waiting", "notice",
        "time", "cooling", "probation", "probationary", "billing", "reporting", "holding", "transition", "lock", "rest",
        "sample", "study", "given", "short", "long", "school", "class", "free", "fiscal", "accounting", "pay", "payment",
        "return", "refund", "warranty", "introductory", "en", "em", "say", "said", "word", "called",
    ]
    /// A word after a command that makes it an ordinary noun: "a period of time", "the period ends", "colon cancer".
    static let commandBlockersAfter: Set<String> = [
        "of", "is", "was", "are", "were", "will", "has", "had", "lasts", "ends", "starts", "begins", "for", "during", "in",
        "when", "between", "cancer", "key", "sign", "symbol", "character", "button", "mark", "marks",
    ]
    /// Words a dash or hyphen never follows as a command: "I'll dash home", "we dash off".
    static let dashBlockersBefore: Set<String> = CleanupGuard.modals.union(["i", "we", "you", "they", "he", "she", "to", "i'll", "we'll", "you'll", "they'll", "let's", "quick"])
    static let domainEndings: Set<String> = ["com", "org", "net", "io", "co", "ai", "dev", "app", "edu", "gov", "uk"]
    static let spokenScales: [String: Int] = ["thousand": 1_000, "lakh": 100_000, "lakhs": 100_000, "million": 1_000_000, "crore": 10_000_000, "crores": 10_000_000, "billion": 1_000_000_000]
    static let indianScales: Set<String> = ["lakh", "lakhs", "crore", "crores"]

    /// Integer part of a spoken number, fed one word at a time. Only well-formed English counting is
    /// accepted: tens then a unit ("forty two"), a unit, teen or tens before "hundred" ("twelve hundred"),
    /// and scale words that only get smaller ("two lakh fifty thousand"). "three thirty", "nineteen
    /// ninety" and "five five five" are not numbers here, so times, years and digit strings stay words.
    struct SpokenNumber {
        enum Last { case none, unit, teen, tens, hundred, scale, and }
        var total = 0, group = 0, lastScale = Int.max, groupHundred = false, last = Last.none
        var scales: [String] = []
        var hasScale: Bool { !scales.isEmpty || groupHundred || last == .hundred }
        var value: Int { total + group }

        mutating func feed(_ w: String) -> Bool {
            let v = CleanupText.cardinals[w]
            if let v, v >= 1, v <= 9 {
                guard [.none, .hundred, .scale, .and].contains(last) || (last == .tens && group % 10 == 0) else { return false }
                group += v; last = .unit; return true
            }
            if let v, v >= 10, v <= 19 {
                guard [.none, .hundred, .scale, .and].contains(last) else { return false }
                group += v; last = .teen; return true
            }
            if let v, v >= 20, v <= 90 {
                guard [.none, .hundred, .scale, .and].contains(last) else { return false }
                group += v; last = .tens; return true
            }
            if w == "hundred" {
                guard [.unit, .teen, .tens].contains(last), !groupHundred, (1...99).contains(group) else { return false }
                group *= 100; groupHundred = true; last = .hundred; return true
            }
            if let s = RuleCleaner.spokenScales[w] {
                guard [.unit, .teen, .tens, .hundred].contains(last), group > 0, s < lastScale else { return false }
                total += group * s; group = 0; groupHundred = false; lastScale = s; scales.append(w); last = .scale; return true
            }
            if w == "and" {
                guard last == .hundred || last == .scale else { return false }
                last = .and; return true
            }
            return false
        }
    }

    /// Digits with Western grouping from 10,000 ("1200", "250,000"), or Indian grouping ("2,50,000").
    static func groupedDigits(_ v: Int, indian: Bool) -> String {
        let s = String(v)
        guard v >= 10_000 else { return s }
        var digits = Array(s), parts: [String] = [String(digits.suffix(3))]
        digits.removeLast(3)
        let size = indian ? 2 : 3
        while !digits.isEmpty { parts.insert(String(digits.suffix(size)), at: 0); digits.removeLast(min(size, digits.count)) }
        return parts.joined(separator: ",")
    }

    /// Spoken commands, numbers and symbols, then the email layout. Conservative: a rule that is
    /// unsure leaves the words. Quoted speech is never changed. Runs first in `clean`, and
    /// `CleanupGuard.check` accepts output judged against this function's result.
    /// ponytail: word lists, not a parser of English; "the period ended" stays, but an unlisted
    /// noun phrase ("the cooling-off period") can still be read as a command. The guard keeps every
    /// word, name and number value either way.
    static func spokenCommands(_ text: String) -> String {
        let t = CleanupText.tokenize(text)
        let lowerWords = t.filter { $0.kind == .word }.map(\.lower)
        // A command said on its own writes just the mark.
        if let (_, mark) = markCommands.first(where: { $0.0 == lowerWords }), !t.contains(where: { $0.kind == .punct && ["\"", "\u{201C}", "\u{201D}"].contains($0.text) }) {
            return mark
        }
        let mask = CleanupText.quotedMask(t)
        var out: [CleanupToken] = [], changed = false, capitalizeNext = false, i = 0
        func word(_ k: Int) -> String? { k < t.count && t[k].kind == .word && !mask[k] ? t[k].lower : nil }
        /// The word right after token `k`, across one plain space.
        func after(_ k: Int) -> Int? { k + 2 < t.count && t[k + 1].kind == .space && !t[k + 1].text.contains("\n") && word(k + 2) != nil ? k + 2 : nil }
        func phrase(_ k: Int, _ ws: [String]) -> Int? {
            var idx = k
            for (n, w) in ws.enumerated() {
                if n > 0 { guard let nx = after(idx) else { return nil }; idx = nx }
                guard word(idx) == w else { return nil }
            }
            return idx
        }
        func prevWord() -> String? { out.last(where: { $0.kind == .word })?.lower }
        func trimTrailing(_ marks: Bool) {
            while let l = out.last, l.kind == .space || (marks && l.kind == .punct && [",", ".", ";", ":", "!", "?"].contains(l.text)) { out.removeLast() }
        }
        func emit(_ tok: CleanupToken) {
            var tok = tok
            if tok.kind == .word && capitalizeNext { tok.text = CleanupText.capitalized(tok.text); capitalizeNext = false }
            out.append(tok)
        }
        /// A number run at `k`: its last token, digits, and whether it carries hundred or a scale word.
        func number(_ k: Int) -> (end: Int, digits: String, scaled: Bool)? {
            guard let first = word(k), let v = CleanupText.cardinals[first.split(separator: "-").first.map(String.init) ?? ""], v >= 1, v < 100 else { return nil }
            var n = SpokenNumber(), idx = k, end: Int? = nil, best = n, failedOnNumber = false
            while let w = word(idx) {
                var trial = n
                let parts = w.split(separator: "-").map(String.init)
                guard parts.allSatisfy({ trial.feed($0) }) else {
                    failedOnNumber = parts.contains { CleanupText.cardinals[$0] != nil || spokenScales[$0] != nil }
                    break
                }
                n = trial
                if n.last != .and { end = idx; best = n }
                guard let nx = after(idx) else { break }
                idx = nx
            }
            guard var end else { return nil }
            // Back off a run cut short by another number ("one hundred and two hundred") to before its "and".
            if failedOnNumber {
                var k2 = k, n2 = SpokenNumber(), lastGood: (Int, SpokenNumber)? = nil
                while k2 <= end, let w = word(k2) {
                    if w == "and" { break }
                    for p in w.split(separator: "-") { _ = n2.feed(String(p)) }
                    lastGood = (k2, n2)
                    guard let nx = after(k2) else { break }
                    k2 = nx
                }
                guard let (e, s) = lastGood, e < end, after(e).flatMap(word) == "and" else { return nil }
                end = e; best = s
            }
            // A decimal before a scale word or "percent": "two point five million", "two point five percent".
            if !best.hasScale, let p = after(end), word(p) == "point" {
                var digits = "", d = p
                while let nx = after(d), let w = word(nx), let v = CleanupText.cardinals[w], v <= 9 { digits += String(v); d = nx }
                if !digits.isEmpty, let s = after(d), let w = word(s), spokenScales[w] != nil || w == "percent" {
                    return w == "percent" ? (d, "\(best.value).\(digits)", true) : (s, "\(best.value).\(digits) \(t[s].text)", true)
                }
            }
            // "two million", "three hundred million": the scale word stays.
            if best.scales.count == 1, ["million", "billion"].contains(best.scales[0]), best.group == 0 {
                let s = spokenScales[best.scales[0]]!
                return (end, "\(best.total / s) \(best.scales[0])", true)
            }
            return (end, groupedDigits(best.value, indian: best.scales.contains { indianScales.contains($0) }), best.hasScale)
        }

        while i < t.count {
            let tok = t[i]
            guard tok.kind == .word, let w = word(i) else { emit(tok); i += 1; continue }

            // Marks and layout.
            if let (ws, mark) = markCommands.first(where: { phrase(i, $0.0) != nil }), let e = phrase(i, ws) {
                let prev = prevWord(), next = after(e).flatMap(word)
                let isDash = mark == "-" || mark == "\u{2014}"
                var allowed = prev != nil && !commandBlockersBefore.contains(prev!) && !(next.map { commandBlockersAfter.contains($0) || CleanupText.cardinals[$0] != nil || Int($0) != nil } ?? false)
                if isDash { allowed = allowed && next != nil && !dashBlockersBefore.contains(prev!) }
                if allowed {
                    changed = true
                    var k = e + 1
                    if mark == "-" {
                        trimTrailing(false); out.append(CleanupToken(kind: .punct, text: "-"))
                        while k < t.count, t[k].kind == .space, !t[k].text.contains("\n") { k += 1 }
                    } else {
                        trimTrailing(true)
                        // Punctuation speech recognition put around the command word goes ("Hello, comma, how").
                        while k < t.count, (t[k].kind == .space && !t[k].text.contains("\n")) || (t[k].kind == .punct && [",", ".", ";", ":", "!", "?"].contains(t[k].text)) { k += 1 }
                        if mark == "\u{2014}" { out.append(CleanupToken(kind: .space, text: " ")) }
                        for c in mark { out.append(CleanupToken(kind: .punct, text: String(c))) }
                        if !mark.hasPrefix("\n") { out.append(CleanupToken(kind: .space, text: " ")) }
                        capitalizeNext = [".", "?", "!"].contains(mark) || mark.hasPrefix("\n")
                    }
                    i = k; continue
                }
            }
            // "hashtag launch" -> "#launch"; "hash forty two" / "hashtag forty two" -> "#42". "hash out" stays.
            if w == "hash" || w == "hashtag", let n = after(i) {
                if let num = number(n), !num.digits.contains(" ") {
                    emit(CleanupToken(kind: .punct, text: "#")); out.append(CleanupToken(kind: .word, text: num.digits.replacingOccurrences(of: ",", with: "")))
                    changed = true; i = num.end + 1; continue
                }
                if w == "hashtag", CleanupText.numberValue(word(n)!) == nil {
                    emit(CleanupToken(kind: .punct, text: "#")); out.append(t[n])
                    changed = true; i = n + 1; continue
                }
            }
            // "at the rate KV" / "at sign KV" -> "@KV"; "name at the rate gmail dot com" -> "name@gmail.com".
            // Not "at the rate of", "at the rate we're going" or "at the rate things are going".
            if w == "at", let e = atPhrases.lazy.compactMap({ phrase(i, $0) }).first, let x = after(e), let handle = word(x),
               handle != "of", !CleanupGuard.grammarWords.contains(handle), !CleanupGuard.persons.contains(where: { $0.contains(handle) }),
               !CleanupGuard.modals.contains(handle), CleanupText.numberValue(handle) == nil {
                var parts = [t[x]], end = x
                while let d = after(end), word(d) == "dot", let tld = after(d), let ending = word(tld), domainEndings.contains(ending) {
                    parts += [CleanupToken(kind: .punct, text: "."), t[tld]]; end = tld
                }
                let domain = parts.count > 1
                let lastWord = after(end) == nil
                if domain || t[x].text.contains(where: \.isUppercase) || lastWord {
                    if domain, out.last?.kind == .space, out.dropLast().last?.kind == .word { out.removeLast() }
                    out.append(CleanupToken(kind: .punct, text: "@")); out += parts
                    changed = true; i = end + 1; continue
                }
            }
            // "example dot com" -> "example.com".
            if w == "dot", let n = after(i), let ending = word(n), domainEndings.contains(ending), i >= 2, t[i - 1].kind == .space,
               let p = word(i - 2), !CleanupGuard.grammarWords.contains(p), out.last?.kind == .space {
                out.removeLast(); out += [CleanupToken(kind: .punct, text: "."), t[n]]
                changed = true; i = n + 1; continue
            }
            // Digits before "percent" too, as the prompt asks: "12 percent" -> "12%".
            if w.allSatisfy(\.isNumber), let p = after(i), word(p) == "percent" {
                emit(CleanupToken(kind: .word, text: tok.text + "%"))
                changed = true; i = p + 1; continue
            }
            // Numbers: with hundred or a scale word, or before "percent". "give me five minutes",
            // "a hundred times", ordinals, years in pairs and digit strings stay words.
            if let num = number(i) {
                if let p = after(num.end), word(p) == "percent", !num.digits.contains(" ") {
                    emit(CleanupToken(kind: .word, text: num.digits + "%"))
                    changed = true; i = p + 1; continue
                }
                if num.scaled {
                    emit(CleanupToken(kind: .word, text: num.digits))
                    changed = true; i = num.end + 1; continue
                }
            }
            emit(tok); i += 1
        }
        var result = changed ? CleanupText.render(CleanupText.normalize(out)) : text
        if let email = emailLayout(result) { result = email }
        return result
    }

    static let emailFrame = try! NSRegularExpression(pattern: #"^\s*(?:please\s+)?(?:write|draft|compose)\s+(?:an?\s+)?(?:email|e-mail)\s+to\s+(\p{L}[\p{L}'’-]*(?:\s+\p{L}[\p{L}'’-]*){0,2}?)[,:]?\s+(?:saying|that says)\b[,:]?\s*(.+)$"#, options: [.caseInsensitive, .dotMatchesLineSeparators])
    static let signOff = try! NSRegularExpression(pattern: #"^(.*?)[\s,.;:!\x{2014}-]*\b(best regards|kind regards|warm regards|warmest regards|regards|best wishes|all the best|many thanks|thanks|thank you|cheers|yours sincerely|sincerely|yours truly|warmly)[,.!]?\s+(\p{L}[\p{L}'’-]*(?:\s+\p{L}[\p{L}'’-]*)?)[.!]?\s*$"#, options: [.caseInsensitive, .dotMatchesLineSeparators])

    /// "Write an email to Sarah saying … best regards, Kartik" -> "Hi Sarah," / body / "Best regards," / "Kartik".
    /// Only for that explicit frame at the start; the frame words go and "Hi" is the only word added.
    static func emailLayout(_ text: String) -> String? {
        let ns = text as NSString
        guard let m = emailFrame.firstMatch(in: text, range: NSRange(location: 0, length: ns.length)) else { return nil }
        func plain(_ s: String) -> Bool {
            s.split(separator: " ").allSatisfy { w in let l = w.lowercased(); return !CleanupGuard.grammarWords.contains(l) && CleanupText.numberValue(l) == nil }
        }
        func titled(_ s: String) -> String { s.split(whereSeparator: \.isWhitespace).map { CleanupText.capitalized(String($0)) }.joined(separator: " ") }
        let recipient = ns.substring(with: m.range(at: 1))
        var body = ns.substring(with: m.range(at: 2)), closing: String? = nil
        guard plain(recipient) else { return nil }
        let bs = body as NSString
        if let s = signOff.firstMatch(in: body, range: NSRange(location: 0, length: bs.length)), plain(bs.substring(with: s.range(at: 3))) {
            let phrase = bs.substring(with: s.range(at: 2)).lowercased()
            closing = CleanupText.capitalized(phrase) + ",\n" + titled(bs.substring(with: s.range(at: 3)))
            body = bs.substring(with: s.range(at: 1))
        }
        body = body.trimmingCharacters(in: CharacterSet(charactersIn: " ,;:\u{2014}-").union(.whitespacesAndNewlines))
        guard !body.isEmpty else { return nil }
        body = CleanupText.capitalized(body)
        if let last = body.last, !".?!".contains(last) { body += "." }
        return "Hi \(titled(recipient)),\n\n\(body)" + (closing.map { "\n\n" + $0 } ?? "")
    }
}
