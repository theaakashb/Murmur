import Foundation

// Tokenizer, normalization, similarity and the negation/number lexicons shared by the guards and rules.


struct CleanupToken: Equatable {
    enum Kind { case word, punct, space }
    var kind: Kind
    var text: String
    var lower: String { text.lowercased().replacingOccurrences(of: "\u{2019}", with: "'") }
    var isTerminal: Bool { kind == .punct && (text == "." || text == "?" || text == "!" || text == "\n") }
    var isDash: Bool { kind == .punct && (text == "\u{2014}" || text == "\u{2013}" || text == "-" || text == "--") }
    var isComma: Bool { kind == .punct && text == "," }
    /// Any punctuation that bounds a phrase (comma, dash, colon, semicolon, terminal).
    var isBoundary: Bool { kind == .punct && (isTerminal || isComma || isDash || text == ";" || text == ":") }
}

enum CleanupText {
    /// 1 - Levenshtein distance / longer length, over Characters.
    static func similarity(_ a: String, _ b: String) -> Double {
        let x = Array(a), y = Array(b)
        guard !x.isEmpty, !y.isEmpty else { return x.isEmpty && y.isEmpty ? 1 : 0 }
        var previous = Array(0...y.count)
        for i in 1...x.count {
            var current = [i] + Array(repeating: 0, count: y.count)
            for j in 1...y.count { current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1)) }
            previous = current
        }
        return 1 - Double(previous[y.count]) / Double(max(x.count, y.count))
    }

    static func isWordChar(_ c: Character) -> Bool { c.isLetter || c.isNumber || c == "'" || c == "\u{2019}" }

    static func tokenize(_ s: String) -> [CleanupToken] {
        let chars = Array(s)
        var out: [CleanupToken] = []
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c == "\n" {
                out.append(CleanupToken(kind: .punct, text: "\n")); i += 1
            } else if c.isWhitespace {
                var j = i
                while j < chars.count, chars[j].isWhitespace, chars[j] != "\n" { j += 1 }
                out.append(CleanupToken(kind: .space, text: String(chars[i..<j]))); i = j
            } else if isWordChar(c) || (c == "$" && i + 1 < chars.count && chars[i + 1].isNumber) {
                var j = i + 1
                while j < chars.count {
                    let d = chars[j]
                    if isWordChar(d) { j += 1; continue }
                    if d == "%" && chars[j - 1].isNumber { j += 1; continue }
                    if j + 1 < chars.count {
                        let prev = chars[j - 1], next = chars[j + 1]
                        if d == "-" && isWordChar(prev) && isWordChar(next) { j += 1; continue }
                        if (d == "." || d == "," || d == ":") && prev.isNumber && next.isNumber { j += 1; continue }
                    }
                    break
                }
                // Honorifics keep their period so "Ms. Alvarez" is not read as a sentence break.
                if j < chars.count, chars[j] == ".", abbreviations.contains(String(chars[i..<j]).lowercased()) { j += 1 }
                out.append(CleanupToken(kind: .word, text: String(chars[i..<j]))); i = j
            } else if c == "-" {
                var j = i
                while j < chars.count, chars[j] == "-" { j += 1 }
                out.append(CleanupToken(kind: .punct, text: j - i > 1 ? "--" : "-")); i = j
            } else {
                out.append(CleanupToken(kind: .punct, text: String(c))); i += 1
            }
        }
        return out
    }

    static func render(_ tokens: [CleanupToken]) -> String { tokens.map(\.text).joined() }

    /// Spacing/punctuation repair applied only after an edit has been made.
    static func normalize(_ tokens: [CleanupToken]) -> [CleanupToken] {
        var t: [CleanupToken] = []
        for tok in tokens {
            if tok.kind == .space {
                if t.isEmpty || t.last!.kind == .space || t.last!.text == "\n" { continue }
                t.append(CleanupToken(kind: .space, text: " "))
                continue
            }
            if tok.kind == .punct, [",", ".", "?", "!", ";", ":"].contains(tok.text) {
                while let last = t.last, last.kind == .space { t.removeLast() }
                if tok.text == "," {
                    if t.isEmpty || t.last!.isTerminal || t.last!.isComma || t.last!.text == ":" { continue }
                } else if let last = t.last, last.isComma {
                    t.removeLast()
                }
            }
            if tok.text == "\n" { while let last = t.last, last.kind == .space { t.removeLast() } }
            t.append(tok)
        }
        while let last = t.last, last.kind == .space { t.removeLast() }
        while let first = t.first, first.kind == .space || first.isComma { t.removeFirst() }
        return t
    }

    static func capitalized(_ word: String) -> String {
        guard let first = word.first, first.isLowercase else { return word }
        // Leave mixed-case words such as "iPhone" or "eBay" alone.
        if word.dropFirst().contains(where: { $0.isUppercase }) { return word }
        return first.uppercased() + word.dropFirst()
    }

    static func prevNonSpace(_ t: [CleanupToken], _ i: Int) -> Int? {
        var j = i - 1
        while j >= 0 { if t[j].kind != .space { return j }; j -= 1 }
        return nil
    }

    static func nextNonSpace(_ t: [CleanupToken], _ i: Int) -> Int? {
        var j = i + 1
        while j < t.count { if t[j].kind != .space { return j }; j += 1 }
        return nil
    }

    static func isSentenceStart(_ t: [CleanupToken], _ i: Int) -> Bool {
        guard let p = prevNonSpace(t, i) else { return true }
        return t[p].isTerminal
    }

    static func nextWord(_ t: [CleanupToken], from i: Int) -> Int? {
        var j = i
        while j < t.count { if t[j].kind == .word { return j }; j += 1 }
        return nil
    }

    /// True for tokens inside quotation marks. Quoted speech is never edited; an unbalanced
    /// straight quote protects everything after it.
    static func quotedMask(_ t: [CleanupToken]) -> [Bool] {
        var mask = Array(repeating: false, count: t.count)
        var inStraight = false, curlyDepth = 0
        for (i, tok) in t.enumerated() {
            if tok.kind == .punct && tok.text == "\"" { inStraight.toggle(); mask[i] = true; continue }
            if tok.kind == .punct && tok.text == "\u{201C}" { curlyDepth += 1; mask[i] = true; continue }
            if tok.kind == .punct && tok.text == "\u{201D}" { curlyDepth = max(0, curlyDepth - 1); mask[i] = true; continue }
            mask[i] = inStraight || curlyDepth > 0
        }
        return mask
    }

    /// Words a paragraph needs before its closing line break counts as a paragraph break.
    /// ponytail: a word floor, not a sentence model; it stops one-word paragraphs ("We.\n\nNeed.").
    static let paragraphMinWords = 3

    /// `text` with its paragraph breaks joined into spaces: line breaks right after a sentence end
    /// (a . ? or ! token, so not "Dr.") that close at least `paragraphMinWords` words.
    static func joiningParagraphs(_ text: String) -> String {
        let t = tokenize(text)
        var out = "", words = 0, i = 0
        while i < t.count {
            if t[i].text == "\n" {
                if words >= paragraphMinWords, let p = prevNonSpace(t, i), t[p].kind == .punct, [".", "?", "!"].contains(t[p].text) {
                    while i < t.count, t[i].text == "\n" || t[i].kind == .space { i += 1 }
                    out += " "; words = 0; continue
                }
                words = 0
            } else if t[i].kind == .word { words += 1 }
            out += t[i].text; i += 1
        }
        return out
    }

    /// Closed-class words shared by `CleanupGuard.grammarWords` (polish grammar fixes) and `CorrectionFilter.contextualWords` (never learned).
    static let functionWords: Set<String> = [
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "am", "has", "have", "had", "do", "does", "did",
        "to", "of", "in", "on", "at", "for", "with", "from", "by", "than", "then", "that", "this", "these", "those", "there",
        "and", "or", "but", "so", "if", "too", "very", "off", "it", "its", "it's", "i", "me", "my", "you're", "your", "he", "him",
        "his", "she", "her", "we", "us", "our", "they", "them", "their", "they're",
    ]

    static let hesitations: Set<String> = ["um", "umm", "ummm", "uh", "uhh", "uhm", "erm", "er", "hmm", "hm", "mm", "mmm"]

    /// Negation words every list shares. `negationWords` (cleanup guard) adds apostrophe-less contractions,
    /// `CorrectionFilter.negations` adds apostrophe contractions and "without", `TransformGuard` uses only `basicNegations` plus "without".
    static let basicNegations: Set<String> = ["not", "no", "never", "cannot"]
    static let negationCore: Set<String> = basicNegations.union(["nor", "neither", "none", "nobody", "nothing", "nowhere"])
    /// Stems of negative contractions: stem + "t" ("dont", ASR text) or stem + "'t" ("don't").
    static let negationStems = ["don", "didn", "doesn", "isn", "wasn", "aren", "weren", "couldn", "shouldn", "wouldn", "haven", "hasn", "hadn", "can", "won", "ain", "mustn"]
    static let negationWords: Set<String> = negationCore.union(negationStems.map { $0 + "t" })
    static let apostropheNegations: Set<String> = Set(negationStems.map { $0 + "'t" })

    /// Negative contractions speech recognition sometimes returns without "'t" ("it doesn spread"),
    /// each with the completions cleanup may write in its place: its own, plus listed exceptions.
    /// Not "haven", "don", "won" or "can": those are also ordinary words.
    static let truncatedNegations: [String: Set<String>] = Dictionary(uniqueKeysWithValues:
        ["doesn", "didn", "isn", "wasn", "couldn", "wouldn", "shouldn", "hasn", "hadn", "aren", "weren", "mustn", "needn"].map { ($0, [$0 + "'t"]) })
        .merging(["doesn": ["don't"]]) { $0.union($1) }

    static func isNegation(_ lower: String) -> Bool {
        negationWords.contains(lower) || lower.hasSuffix("n't") || truncatedNegations[lower] != nil
    }

    static let cardinals: [String: Int] = [
        "zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8,
        "nine": 9, "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15,
        "sixteen": 16, "seventeen": 17, "eighteen": 18, "nineteen": 19, "twenty": 20, "thirty": 30,
        "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90,
        "hundred": 100, "thousand": 1000, "million": 1_000_000, "billion": 1_000_000_000,
    ]
    static let ordinals: [String: Int] = [
        "first": 1, "second": 2, "third": 3, "fourth": 4, "fifth": 5, "sixth": 6, "seventh": 7,
        "eighth": 8, "ninth": 9, "tenth": 10, "eleventh": 11, "twelfth": 12, "twentieth": 20,
        "firstly": 1, "secondly": 2, "thirdly": 3, "fourthly": 4, "fifthly": 5,
    ]

    /// A normalized numeric value for a word, or nil when the word carries no number.
    static let abbreviations: Set<String> = ["mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "mt", "vs", "etc"]
    static let titles: Set<String> = ["mr.", "mrs.", "ms.", "dr.", "prof."]

    static func numberValue(_ lower: String) -> String? {
        if let v = cardinals[lower] ?? ordinals[lower] { return String(v) }
        if lower.contains("-") {
            // "sixty-eight", "twenty-first"
            let parts = lower.split(separator: "-").map(String.init)
            let values = parts.compactMap { cardinals[$0] ?? ordinals[$0] }
            if values.count == parts.count, !values.isEmpty { return String(values.reduce(0, +)) }
        }
        guard lower.contains(where: { $0.isNumber }) else { return nil }
        var s = lower.replacingOccurrences(of: "$", with: "").replacingOccurrences(of: "%", with: "")
        if !s.contains(":") { s = s.replacingOccurrences(of: ",", with: "") }
        // Strip trailing letters such as "3pm", "1st", "20th".
        while let last = s.last, last.isLetter || last == "'" { s.removeLast() }
        return s.isEmpty ? nil : s
    }
}

