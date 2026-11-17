import Foundation

// The bound checks between raw text and rewritten text: `CleanupGuard` (cleanup) and `TransformGuard` (mode rewrites).


/// Bound check between raw transcript and cleanup output. Rejects output that answers or
/// obeys the dictation, adds facts, or makes edits the enabled options cannot explain.
enum CleanupGuard {
    struct Word {
        var text: String
        var lower: String
        var sentenceStart: Bool
        var commaBefore = false
        var commaAfter = false
        var endAfter = false
        var capitalized: Bool { text.first?.isUppercase ?? false }
        var isName: Bool { capitalized && !sentenceStart && lower != "i" && !lower.hasPrefix("i'") }
    }

    /// Assistant-specific phrases that rarely occur in dictation.
    static let assistantPhrases = [
        "as an ai", "as a language model", "i'm an ai", "i am an ai", "i hope this helps", "hope this helps",
        "happy to help", "i'd be happy", "i would be happy", "i can't help", "i cannot help",
        "<transcript", "</transcript", "cleaned transcript",
    ]
    static let answerPhrases = [
        "sure,", "sure!", "sure.", "certainly", "of course", "here's", "here is", "here are",
        "i'm sorry, but", "great question", "absolutely", "the answer is", "let me know if", "transcript:",
    ] + assistantPhrases

    /// The output contains an answer phrase the speaker did not say. Compared without punctuation on
    /// the raw side: added punctuation alone ("let's make sure" -> "Let's make sure.") is not an answer.
    static func answers(raw: String, cleaned: String) -> Bool {
        func bare(_ s: String) -> String { String(s.map { $0.isWhitespace ? " " : $0 }).filter { $0.isLetter || $0.isNumber || $0 == "'" || $0 == " " } }
        let rawBare = bare(raw.lowercased().replacingOccurrences(of: "\u{2019}", with: "'"))
        let outLower = cleaned.lowercased().replacingOccurrences(of: "\u{2019}", with: "'")
        return answerPhrases.contains { outLower.contains($0) && !rawBare.contains(bare($0)) }
    }

    /// Nouns that make a preceding ordinal part of a phrase, not a list marker ("first impressions", "second nature").
    static let ordinalPhraseNouns: Set<String> = ["impression", "impressions", "chance", "chances", "nature", "thought", "thoughts", "opinion", "aid"]
    /// Discourse fillers polish may remove without commas, with the fillers option. "like" is one
    /// unless it follows a word that makes it a verb or a comparison ("I like", "it's like").
    static let polishFillers: Set<String> = ["yeah", "yep", "yup", "basically", "literally"]
    static let polishFillerPairs: Set<[String]> = [["you", "know"], ["i", "mean"]]
    static let meaningfulLikeAfter: Set<String> = ["i", "you", "we", "they", "he", "she", "it", "who", "people", "would", "i'd", "you'd", "we'd", "they'd", "to", "it's", "that's", "looks", "look", "looked", "seems", "sounds", "feel", "feels", "felt", "just"]
    /// Closed-class words a polish grammar fix may add or drop. Not negations, numbers or modals.
    static let grammarWords: Set<String> = CleanupText.functionWords.union([
        "as", "more", "most", "just", "really", "also", "up", "out", "about", "into", "i'm", "you", "going", "gonna",
    ])
    /// Grammar words polish may drop: not pronouns or the joins and, or, but, if ("salt and pepper").
    static let droppableGrammarWords = grammarWords.subtracting(persons.reduce(into: Set<String>()) { $0.formUnion($1) }).subtracting(["and", "or", "but", "if"])
    static let modals: Set<String> = ["can", "could", "should", "shall", "may", "might", "must", "will", "would"]
    /// A grammar fix may change a pronoun's case ("him" -> "he") but not whom it refers to.
    static let persons: [Set<String>] = [
        ["i", "me", "my", "mine", "i'm", "i've", "i'll", "i'd"], ["you", "your", "yours", "you're", "you've", "you'll", "you'd"],
        ["he", "him", "his", "he's"], ["she", "her", "hers", "she's"], ["it", "its", "it's"],
        ["we", "us", "our", "ours", "we're", "we've"], ["they", "them", "their", "theirs", "they're", "they've"],
    ]
    /// Words that differ only by an opposite prefix ("increase"/"decrease", "include"/"exclude", "like"/"dislike").
    static func polarityFlip(_ x: String, _ y: String) -> Bool {
        let common = zip(x.reversed(), y.reversed()).prefix { $0 == $1 }.count
        guard common >= 3 else { return false }
        let px = String(x.dropLast(common)), py = String(y.dropLast(common))
        let prefixes: Set<String> = ["", "in", "de", "ex", "un", "dis", "non", "im", "il", "ir", "mis", "anti", "up", "down", "over", "under", "pre", "post"]
        return px != py && prefixes.contains(px) && prefixes.contains(py)
    }
    /// A polish replacement: a respelling or misheard word, never a different modal or person.
    static func polishSwap(_ heard: String, _ written: String) -> Bool {
        if modals.contains(heard) && modals.contains(written) { return false }
        let a = persons.firstIndex { $0.contains(heard) }, b = persons.firstIndex { $0.contains(written) }
        if a != nil || b != nil { return a == b }
        return CleanupText.similarity(heard, written) >= 0.5
    }
    /// The rule engine's cues plus "actually no", which the rules reach through "actually" and "no" separately.
    static let correctionCues: [[String]] = RuleCleaner.cuePatterns.map(\.0) + [["actually", "no"]]

    static func words(_ s: String) -> [Word] {
        let tokens = CleanupText.tokenize(s.replacingOccurrences(of: "\u{2019}", with: "'"))
        var out: [Word] = []
        var start = true
        var lineStart = true
        var commaPending = false
        var i = 0
        while i < tokens.count {
            let t = tokens[i]
            if t.kind == .word {
                // A list marker "3." / "3)" at line start is formatting, not content.
                if lineStart, t.text.allSatisfy(\.isNumber), let n = CleanupText.nextNonSpace(tokens, i),
                   tokens[n].text == "." || tokens[n].text == ")" {
                    i = n + 1; start = true; lineStart = false; continue
                }
                // "250,000", "2,50,000" and "250000" are the same word.
                let lower = t.lower.first?.isNumber == true && t.lower.allSatisfy({ $0.isNumber || $0 == "," || $0 == "." }) ? t.lower.replacingOccurrences(of: ",", with: "") : t.lower
                // "42-dollar" is the two spoken words "42 dollar".
                let parts = lower.split(separator: "-", maxSplits: 1).map(String.init)
                if parts.count == 2, parts[0].allSatisfy(\.isNumber), parts[1].first?.isLetter == true {
                    out.append(Word(text: parts[0], lower: parts[0], sentenceStart: start, commaBefore: commaPending))
                    out.append(Word(text: String(t.text.dropFirst(parts[0].count + 1)), lower: parts[1], sentenceStart: false, commaBefore: false))
                } else {
                    out.append(Word(text: t.text, lower: lower, sentenceStart: start, commaBefore: commaPending))
                }
                start = false; lineStart = false; commaPending = false
            } else if t.kind == .punct {
                if t.isComma || t.isDash {
                    if !out.isEmpty { out[out.count - 1].commaAfter = true }
                    commaPending = true
                } else { commaPending = false }
                if t.isTerminal, !out.isEmpty { out[out.count - 1].endAfter = true }
                if t.isTerminal || t.text == ":" { start = true }
                if t.text == "\n" { lineStart = true }
            }
            i += 1
        }
        return out
    }

    static func listMarkerValues(_ s: String) -> Set<String> {
        var values = Set<String>()
        for line in s.split(separator: "\n") {
            let trimmed = line.drop(while: { $0 == " " })
            let digits = trimmed.prefix(while: \.isNumber)
            if !digits.isEmpty, trimmed.dropFirst(digits.count).first.map({ $0 == "." || $0 == ")" }) == true {
                values.insert(String(digits))
            }
        }
        return values
    }

    static func nonListLineCount(_ s: String) -> Int {
        s.split(separator: "\n", omittingEmptySubsequences: true).filter { line in
            let trimmed = line.drop(while: { $0 == " " })
            let digits = trimmed.prefix(while: \.isNumber)
            return digits.isEmpty || !(trimmed.dropFirst(digits.count).first.map { $0 == "." || $0 == ")" } ?? false)
        }.count
    }

    /// Cues professional accepts without a comma: unambiguous, unlike "sorry", "no" or "I mean".
    /// "I mean" only before a determiner ("by 5 I mean the quarterly one"), never "I mean business".
    static let looseCorrectionCues: [[String]] = correctionCues.filter { !commaRequiredCues.contains($0) }
    /// The ordinary words that are corrections only after a comma (or dash).
    static let commaRequiredCues: Set<[String]> = [["sorry"], ["actually"], ["no"]]
    /// "no" before one of these quantifies it ("no one", "no longer") and is not a correction cue.
    /// After a cue these make it a statement, not a repair: "I meant it", "I mean business", "what I meant was".
    static let declarativeAfterCue: Set<String> = ["it", "business", "well", "to", "was", "is"]
    static let noQuantified: Set<String> = ["one", "body", "longer", "matter", "way", "doubt", "problem", "idea", "more"]

    /// Raw word indices that a spoken correction could legitimately delete: the cue itself plus up
    /// to eight preceding words in the same sentence (whole sentence for "scratch that"). With
    /// `loose`, a `looseCorrectionCues` cue needs no comma, but then covers only the preceding words
    /// whose content words all recur in the next eight words ("we are not going, wait no I meant he
    /// is not going"), so it never frees an unrelated name, number or negation.
    static func correctionWindow(_ raw: String, loose: Bool = false, commaFreeOnly: Bool = false) -> Set<Int> {
        let tokens = CleanupText.tokenize(raw.replacingOccurrences(of: "\u{2019}", with: "'"))
        var wordIndexOfToken: [Int: Int] = [:]
        var tokenOfWord: [Int] = []
        for (ti, t) in tokens.enumerated() where t.kind == .word {
            wordIndexOfToken[ti] = tokenOfWord.count; tokenOfWord.append(ti)
        }
        var window = Set<Int>()
        for (wi, ti) in tokenOfWord.enumerated() {
            for cue in correctionCues {
                guard wi + cue.count <= tokenOfWord.count else { continue }
                let match = (0..<cue.count).allSatisfy { tokens[tokenOfWord[wi + $0]].lower == cue[$0] }
                guard match else { continue }
                guard let p = CleanupText.prevNonSpace(tokens, ti) else { continue }
                let scratch = cue == ["scratch", "that"]
                let commaFree = !(tokens[p].isComma || tokens[p].isDash || (scratch && tokens[p].isTerminal))
                guard !commaFree || (loose && looseCorrectionCues.contains(cue) && tokens[p].kind == .word) else { continue }
                if commaFreeOnly && !commaFree { continue }
                if commaFree && cue == ["i", "mean"] {
                    guard wi + 2 < tokenOfWord.count, ["the", "a", "an", "this", "that"].contains(tokens[tokenOfWord[wi + 2]].lower) else { continue }
                }
                // Declarative uses are not repairs: "what I meant was", "I meant to", "I meant it".
                if commaFree {
                    // "no" quantifying the next word is not a cue: "wait no one blamed Bob".
                    if cue.last == "no", wi + cue.count < tokenOfWord.count,
                       noQuantified.contains(tokens[tokenOfWord[wi + cue.count]].lower) { continue }
                    let before = wi > 0 ? tokens[tokenOfWord[wi - 1]].lower : ""
                    let after = wi + cue.count < tokenOfWord.count ? tokens[tokenOfWord[wi + cue.count]].lower : ""
                    if ["what", "which", "who", "how", "whatever", "all", "if"].contains(before)
                        // A bare comma-free cue is also a statement before "that", "by" or "for" ("I mean that", "I meant by").
                        // `applyExplicitRepairs` leaves those out on purpose: after "no I mean" they open a real repair ("no I mean by Friday").
                        || declarativeAfterCue.union(["that", "by", "for"]).contains(after) { continue }
                    // A bare "I meant" repairs only right after a cue word ("wait no I meant", "actually I meant"),
                    // never in a nested clause ("I told him I meant he told me").
                    if cue == ["i", "meant"] && !["wait", "no", "know", "oh", "sorry", "actually", "um", "uh"].contains(before) { continue }
                }
                // A repair must follow the cue before the sentence ends.
                let lastCueToken = tokenOfWord[wi + cue.count - 1]
                var j = lastCueToken + 1
                var hasRepair = false
                while j < tokens.count, !tokens[j].isTerminal {
                    if tokens[j].kind == .word { hasRepair = true; break }
                    j += 1
                }
                guard hasRepair else { continue }
                for k in 0..<cue.count { window.insert(wi + k) }
                // Interregnum hesitations directly after the cue.
                var after = wi + cue.count
                while after < tokenOfWord.count, CleanupText.hesitations.contains(tokens[tokenOfWord[after]].lower) {
                    window.insert(after); after += 1
                }
                // A short negated restatement before the repair ("no, not Tuesday, Thursday").
                if after < tokenOfWord.count, tokens[tokenOfWord[after]].lower == "not" {
                    var end = after + 1
                    while end < tokenOfWord.count, end - after <= 3,
                          !tokens[(tokenOfWord[end - 1] + 1)..<tokenOfWord[end]].contains(where: { $0.isBoundary }) { end += 1 }
                    if end < tokenOfWord.count, end - after <= 4,
                       tokens[(tokenOfWord[end - 1] + 1)..<tokenOfWord[end]].contains(where: { $0.isComma || $0.isDash }) {
                        for x in after..<end { window.insert(x) }
                    }
                }
                // Preceding words in the same sentence. "Scratch that" after a period reaches back
                // across that one period into the previous sentence.
                var limit = scratch ? 30 : 8
                if commaFree {
                    // The repair restates right away: within its first four words, not later in the sentence
                    // ("actually I meant every word when I said I was serious" is not a repair).
                    let repair = (wi + cue.count)..<min(tokenOfWord.count, wi + cue.count + 4)
                    // Interregnum words, including "know" misheard for "no" ("wait I know I meant"), do not count.
                    func interregnum(_ w: String) -> Bool { ["wait", "no", "know", "oh", "ah", "sorry", "actually"].contains(w) || CleanupText.hesitations.contains(w) }
                    // Restated means the same word, an inflection of it, or the same kind of word
                    // ("Friday" -> "Monday"); never a merely similar spelling ("mad" / "said").
                    func restates(_ x: String, _ y: String) -> Bool {
                        let kinds = [RuleCleaner.weekdays, RuleCleaner.monthsAndMay, RuleCleaner.relativeTimes, RuleCleaner.colors]
                        return x == y || zip(x, y).prefix { $0 == $1 }.count >= 4
                            || kinds.contains { $0.contains(x) && $0.contains(y) }
                            || (CleanupText.numberValue(x) != nil && CleanupText.numberValue(y) != nil)
                    }
                    func recurs(_ w: String) -> Bool {
                        grammarWords.contains(w) || repair.contains { restates(w, tokens[tokenOfWord[$0]].lower) }
                    }
                    limit = 0
                    var reach = 8, restated = false
                    while limit < reach, wi - 1 - limit >= 0 {
                        let w = tokens[tokenOfWord[wi - 1 - limit]].lower
                        if interregnum(w) { reach += 1 } else if !recurs(w) { break }
                        else if !grammarWords.contains(w) && !["what", "which", "who", "how", "when", "where", "why"].contains(w) { restated = true }
                        limit += 1
                    }
                    // Remove only as many words before the restated word as the repair puts before it:
                    // "we are not ... I meant he is not" takes "we are"; "I was wrong actually I meant wrong"
                    // keeps "I was".
                    func word(_ x: Int) -> String { tokens[tokenOfWord[x]].lower }
                    let content = ((wi - limit)..<wi).filter { !grammarWords.contains(word($0)) && !interregnum(word($0)) }
                    if let e = content.first(where: { x in repair.contains { restates(word(x), word($0)) } }),
                       let r = repair.first(where: { p in content.contains { restates(word($0), word(p)) } }) {
                        limit = wi - max(wi - limit, e - (r - repair.lowerBound))
                    }
                    // Without a restated content word it is not a correction ("I did not say that I meant
                    // it"), except "I mean the ..." clarifying, where only the cue goes.
                    // A leading join stays ("go so we are not ... I meant he is not" keeps "so").
                    while limit > 0, ["so", "and", "but", "then"].contains(tokens[tokenOfWord[wi - limit]].lower) { limit -= 1 }
                    // Only two repairs are safe to apply here: a whole clause after a join or a sentence start
                    // ("go so we are not ... I meant he is not"), or a phrase restarted with the same preposition
                    // or determiner ("on Friday ... I meant on Monday"). Anything else ("I told Alice actually I
                    // meant Alice left") is left for the model, so a kept prefix is never spliced onto a new clause.
                    if limit > 0 && cue != ["i", "mean"] {
                        let start = wi - limit
                        let clause = start == 0 || ["so", "and", "but", "then"].contains(word(start - 1))
                            || tokens[tokenOfWord[start - 1]..<tokenOfWord[start]].contains { $0.isTerminal }
                        let phrase = word(start) == word(wi + cue.count)
                            && ["on", "at", "in", "to", "for", "with", "by", "from", "the", "a", "an", "my", "your", "our", "their", "this", "that"].contains(word(start))
                        if !clause && !phrase {
                            for k in 0..<cue.count { window.remove(wi + k) }
                            continue
                        }
                    }
                    if cue == ["i", "mean"] { limit = 0 } else if !restated {
                        for k in 0..<cue.count { window.remove(wi + k) }
                        continue
                    }
                }
                var k = wi - 1
                var count = 0
                var allowCross = scratch && tokens[p].isTerminal
                while k >= 0, count < limit {
                    let gap = tokens[(tokenOfWord[k] + 1)..<tokenOfWord[k + 1]]
                    if gap.contains(where: \.isTerminal) {
                        if allowCross && count == 0 { allowCross = false } else { break }
                    }
                    window.insert(k); count += 1; k -= 1
                }
            }
        }
        return window
    }

    /// Professional applies comma-free corrections before the model sees the text: the model alone
    /// often kept both versions or deleted an earlier clause. Removes only what `check` would allow.
    static func applyCommaFreeCorrections(_ text: String) -> String {
        let window = correctionWindow(text, loose: true, commaFreeOnly: true)
        guard !window.isEmpty else { return text }
        var t = CleanupText.tokenize(text.replacingOccurrences(of: "\u{2019}", with: "'")), word = -1
        t.removeAll { tok in
            guard tok.kind == .word else { return false }
            word += 1
            return window.contains(word)
        }
        return CleanupText.render(CleanupText.normalize(t))
    }

    /// "No I mean" / "No I meant" prefix the ordinary cues; "actually no" and "scratch that" are shared; "let me start again" is its own.
    static let explicitRepairCues: [[String]] = [["i", "mean"], ["i", "meant"]].map { ["no"] + $0 }
        + correctionCues.filter { [["actually", "no"], ["scratch", "that"]].contains($0) } + [["let", "me", "start", "again"]]

    /// Polish and Professional: `raw` with each explicitly cued repair applied ("um what time is it no I mean
    /// what day is it" -> "um what day is it"). The cue goes with the whole clause before it, back to the
    /// sentence start, a comma or "so"/"and"/"but" (at most eight words; a longer clause is left alone),
    /// keeping a leading "hey" or "um". Only when the repair restates one of that clause's content words
    /// within its first four words and is not shorter than the clause, so a fragment ("the report is not
    /// ready no I mean the slides") never replaces a whole clause. `check` judges output against this text
    /// too, so the output may drop the clause's names, numbers and negations, but adds nothing the repair
    /// did not say and cannot keep both versions.
    /// ponytail: word lists and a length bound, not a parser; Strict and the cleaner itself never use it.
    static func applyExplicitRepairs(_ raw: String) -> String {
        var tokens = CleanupText.tokenize(raw.replacingOccurrences(of: "\u{2019}", with: "'"))
        let tokenOfWord = tokens.indices.filter { tokens[$0].kind == .word }
        func word(_ x: Int) -> String { tokens[tokenOfWord[x]].lower }
        func between(_ x: Int, _ y: Int, _ test: (CleanupToken) -> Bool) -> Bool { tokens[(tokenOfWord[x] + 1)..<tokenOfWord[y]].contains(where: test) }
        let interregnum = CleanupText.hesitations.union(["wait", "oh", "no", "actually", "sorry"])
        let openers = CleanupText.hesitations.union(["hey", "okay", "ok", "well", "oh", "so", "yeah", "alright"])
        var cuts: [ClosedRange<Int>] = [], wi = 0
        while wi < tokenOfWord.count {
            defer { wi += 1 }
            guard let cue = explicitRepairCues.first(where: { cue in wi + cue.count <= tokenOfWord.count
                && (0..<cue.count).allSatisfy { word(wi + $0) == cue[$0] } && !(wi..<(wi + cue.count - 1)).contains { between($0, $0 + 1, \.isTerminal) } }) else { continue }
            var start = wi, end = wi + cue.count
            while start > (cuts.last?.upperBound ?? -1) + 1, interregnum.contains(word(start - 1)), !between(start - 1, start, \.isTerminal) { start -= 1 }
            while end < tokenOfWord.count, CleanupText.hesitations.contains(word(end)), !between(end - 1, end, \.isTerminal) { end += 1 }
            // A declarative or quantifying use is not a cue: "no I mean it", "actually no one came".
            guard end < tokenOfWord.count, !between(end - 1, end, \.isTerminal),
                  !noQuantified.union(declarativeAfterCue).contains(word(end)),
                  start > (cuts.last?.upperBound ?? -1) + 1,
                  // "No, I mean" may follow a question it corrects ("What time is it? No, I mean, what day is it?"):
                  // a speaker does not answer their own question with "I mean". A bare "No," there stays an answer.
                  cue.first == "no" || !between(start - 1, start, \.isTerminal) else { continue }
            var s = start - 1
            while s > 0, !between(s - 1, s, \.isBoundary), !["so", "and", "but"].contains(word(s - 1)), start - s < 8 { s -= 1 }
            guard s == 0 || between(s - 1, s, \.isBoundary) || ["so", "and", "but"].contains(word(s - 1)) else { continue }
            while s < start, openers.contains(word(s)) { s += 1 }
            var repairEnd = end
            while repairEnd + 1 < tokenOfWord.count, !between(repairEnd, repairEnd + 1, \.isTerminal) { repairEnd += 1 }
            let clause = s..<start, repair = end..<min(repairEnd + 1, end + 4)
            // Each number and name the clause loses has one of its kind in the repair ("20" -> "20", "Dana" -> "Dan"):
            // "send 12 boxes wait no I meant send them friday" refines the boxes, it does not replace them.
            func count(_ r: Range<Int>, _ test: (Int) -> Bool) -> Int { r.filter(test).count }
            func number(_ x: Int) -> Bool { CleanupText.numberValue(word(x)) != nil }
            func name(_ x: Int) -> Bool { tokens[tokenOfWord[x]].text.first?.isUppercase == true && x > 0 && !between(x - 1, x, \.isTerminal) && !word(x).hasPrefix("i'") && word(x) != "i" }
            guard !clause.isEmpty, repairEnd + 1 - end >= clause.count,
                  count(clause, number) <= count(end..<(repairEnd + 1), number), count(clause, name) <= count(end..<(repairEnd + 1), name),
                  clause.contains(where: { x in !grammarWords.contains(word(x)) && repair.contains { word($0) == word(x) || zip(word($0), word(x)).prefix { $0 == $1 }.count >= 4 } }) else { continue }
            cuts.append(s...(end - 1)); wi = end - 1
        }
        for cut in cuts.reversed() { tokens.removeSubrange(tokenOfWord[cut.lowerBound]...tokenOfWord[cut.upperBound]) }
        return cuts.isEmpty ? raw : CleanupText.render(CleanupText.normalize(tokens))
    }

    /// Trusted output that lost substance: under 60% of the spoken words, a vanished tail of 3+ content words,
    /// or every negation gone. Self-corrections (the correction window) may drop words, and numbers.
    /// shortcut: length and tail floors, not a semantic check; a same-length meaning change passes, upgrade with a verifier.
    static func truncated(raw: String, cleaned: String) -> Bool {
        // Judge the comma form of a split cue ("Tuesday. No, Wednesday." -> "Tuesday, no, Wednesday").
        let raw = RuleCleaner.mergeSplitCues(raw)
        let r = words(raw), o = words(cleaned)
        let a = r.map(\.lower), b = o.map(\.lower)
        let window = correctionWindow(raw, loose: true)
        // Words a correction replaces are not substance, so they do not count toward the length floor.
        let spoken = words(RuleCleaner.removeFillers(raw)).count - window.count
        if spoken >= 8 && Double(o.count) < Double(spoken) * 0.6 { return true }
        let matched = align(a, b).0
        let kept = Set(b.map { String($0.prefix(5)) })
        var tail = 0, k = a.count - 1
        while k >= 0, !matched.contains(k) { k -= 1 }
        for i in (k + 1)..<a.count where !window.contains(i) && !grammarWords.contains(a[i]) && !CleanupText.hesitations.contains(a[i]) && !kept.contains(String(a[i].prefix(5))) { tail += 1 }
        if tail >= 3 { return true }
        // A restriction connector ("unless QA signs off") outside a correction must survive somewhere.
        for i in a.indices where !window.contains(i) {
            if ["unless", "until", "except"].contains(a[i]) && !b.contains(a[i]) { return true }
        }
        let rawNeg = a.indices.filter { CleanupText.isNegation(a[$0]) && !window.contains($0) }.count
        return rawNeg > 0 && !b.contains(where: { CleanupText.isNegation($0) || $0 == "without" })
    }

    static func align(_ a: [String], _ b: [String]) -> (Set<Int>, Set<Int>) {
        let n = a.count, m = b.count
        if n == 0 || m == 0 { return ([], []) }
        var dp = Array(repeating: Array(repeating: 0, count: m + 1), count: n + 1)
        for i in stride(from: n - 1, through: 0, by: -1) {
            for j in stride(from: m - 1, through: 0, by: -1) {
                dp[i][j] = a[i] == b[j] ? dp[i + 1][j + 1] + 1 : max(dp[i + 1][j], dp[i][j + 1])
            }
        }
        var ma = Set<Int>(), mb = Set<Int>()
        var i = 0, j = 0
        // On ties, skip the earlier raw copy: a correction's reparandum precedes its repair, so a
        // repeated word ("at the office, sorry, at the cafe") should match the later copy.
        while i < n && j < m {
            if a[i] == b[j] && dp[i + 1][j] < dp[i][j] { ma.insert(i); mb.insert(j); i += 1; j += 1 }
            else if dp[i + 1][j] >= dp[i][j + 1] { i += 1 } else { j += 1 }
        }
        return (ma, mb)
    }

    /// `soundsLike(heard, term)` (lowercased letters only) lets a term replace 1–3 raw words that
    /// sound like it, beyond the built-in exact or half-similar spelling.
    /// With `spoken`, output that fails against the raw text may instead match the raw text after
    /// `RuleCleaner.spokenCommands` (digits for spoken numbers, marks for commands, the email layout):
    /// a deterministic rewrite of the raw, so answers, inserted words, names, negations and number
    /// values are still checked against what was said.
    /// ponytail: a model must make the same conversions as the rules, or its output falls back.
    static func check(raw: String, cleaned: String, options: CleanupOptions, allowedTerms: [String] = [],
                      soundsLike: (String, String) -> Bool = { _, _ in false }) -> CleanupGuardResult {
        // Compare equivalent horizontal spaces without changing the text returned for insertion.
        let raw = raw.replacingOccurrences(of: "\\p{Zs}", with: " ", options: .regularExpression)
        let cleaned = cleaned.replacingOccurrences(of: "\\p{Zs}", with: " ", options: .regularExpression)
        let result = checkWords(raw: raw, cleaned: cleaned, options: options, allowedTerms: allowedTerms, soundsLike: soundsLike)
        if result != .pass, options.corrections, options.polish {
            for repaired in [RuleCleaner.applyInsteadCorrections(raw), applyExplicitRepairs(raw)] where repaired != raw
                && checkWords(raw: repaired, cleaned: cleaned, options: options, allowedTerms: allowedTerms, soundsLike: soundsLike) == .pass { return .pass }
        }
        guard result != .pass, options.spoken else { return result }
        let spoken = RuleCleaner.spokenCommands(raw)
        guard spoken != raw else { return result }
        var spokenOptions = options; spokenOptions.spoken = false
        return check(raw: spoken, cleaned: cleaned, options: spokenOptions, allowedTerms: allowedTerms, soundsLike: soundsLike) == .pass ? .pass : result
    }

    private static func checkWords(raw: String, cleaned: String, options: CleanupOptions, allowedTerms: [String],
                                   soundsLike: (String, String) -> Bool) -> CleanupGuardResult {
        // Judge corrections against the comma form the cleaner rewrote ("Tuesday. No wait." -> "Tuesday, no wait,").
        let raw = options.corrections ? RuleCleaner.mergeSplitCues(raw) : raw
        let rawWords = words(raw)
        let outWords = words(cleaned)
        if rawWords.isEmpty {
            let out = cleaned.trimmingCharacters(in: .whitespacesAndNewlines)
            return out.isEmpty || out == raw.trimmingCharacters(in: .whitespacesAndNewlines) ? .pass : .fail(.insertedWords)
        }
        if outWords.isEmpty { return .fail(.emptyOutput) }
        if answers(raw: raw, cleaned: cleaned) { return .fail(.answerPattern) } // Inserted words are caught below.
        for marker in ["**", "##", "```", "\n- ", "\n* ", "\u{2022}"] where cleaned.contains(marker) && !raw.contains(marker) {
            return .fail(.structureAdded)
        }
        // No new symbols or emphasis: only list/sentence punctuation may increase.
        var mayIncrease: Set<Character> = [".", ",", ":", "'", "\u{2019}", "-", "\n", " "]
        if options.punctuation { mayIncrease.insert("?") }
        if options.professional { mayIncrease.insert(";") }
        var rawChars: [Character: Int] = [:], outChars: [Character: Int] = [:]
        for c in raw where !c.isLetter && !c.isNumber { rawChars[c, default: 0] += 1 }
        for c in cleaned where !c.isLetter && !c.isNumber { outChars[c, default: 0] += 1 }
        for (c, n) in outChars where n > rawChars[c, default: 0] && !mayIncrease.contains(c) {
            return .fail(.structureAdded)
        }
        // Quoted speech is kept verbatim unless a correction removes the whole quote.
        let rawTokens = CleanupText.tokenize(raw)
        let mask = CleanupText.quotedMask(rawTokens)
        var spanStart: Int? = nil
        for i in rawTokens.indices {
            let isMark = rawTokens[i].kind == .punct && ["\"", "\u{201C}", "\u{201D}"].contains(rawTokens[i].text)
            if isMark && spanStart == nil && mask[i] { spanStart = i; continue }
            if let s0 = spanStart, isMark || i == rawTokens.count - 1 {
                let inner = CleanupText.render(Array(rawTokens[(s0 + 1)..<(isMark ? i : i + 1)])).trimmingCharacters(in: .whitespaces)
                spanStart = nil
                if inner.isEmpty || cleaned.contains(inner) { continue }
                // A correction may drop a whole quoted reparandum; partial edits are never allowed.
                let outSet = Set(outWords.map(\.lower))
                if options.corrections && !correctionWindow(raw).isEmpty && words(inner).allSatisfy({ !outSet.contains($0.lower) }) {
                    continue
                }
                return .fail(.quoteEdited)
            }
        }
        let rawLines = raw.split(separator: "\n", omittingEmptySubsequences: true).count
        // With punctuation on, paragraph breaks change no words, so only other line breaks count
        // as added structure.
        let lines = options.punctuation ? CleanupText.joiningParagraphs(cleaned) : cleaned
        if options.lists {
            if nonListLineCount(lines) > rawLines + 2 { return .fail(.structureAdded) }
        } else if lines.split(separator: "\n", omittingEmptySubsequences: true).count > rawLines {
            return .fail(.structureAdded)
        }

        let rawLen = Double(raw.count), outLen = Double(cleaned.count)
        var minRatio = 0.9
        if options.lists { minRatio = min(minRatio, 0.5) } // "number three, " becomes "3. "
        if options.fillers { minRatio = min(minRatio, 0.4) }
        if options.corrections { minRatio = min(minRatio, 0.35) }
        if options.fillers && options.corrections { minRatio = 0.25 }
        let maxRatio = options.lists ? 1.35 : 1.1
        if outLen < rawLen * minRatio - 10 || outLen > rawLen * maxRatio + 10 { return .fail(.lengthRatio) }

        // Word-level edit distance (insertions + deletions in an LCS alignment), by option.
        let a = rawWords.map(\.lower), b = outWords.map(\.lower)
        let (matchedRaw, matchedOut) = align(a, b)
        let deletions = a.count - matchedRaw.count, insertions = b.count - matchedOut.count
        var maxEdit = 0.15
        if options.fillers { maxEdit += 0.45 }
        if options.corrections { maxEdit += 0.45 }
        if options.lists { maxEdit += 0.35 }
        maxEdit = min(maxEdit, 0.9)
        // Polish and professional ask for grammar fixes ("it go" -> "it goes", "me and John was" -> "John and
        // I were"): short utterances may swap two words, or three reordered in professional. Names, numbers,
        // negations and inserted words are still checked below.
        // ponytail: flat floor, not a grammar-aware diff; a short full rewrite relies on the later checks.
        let editFloor = options.professional ? 6.0 : options.polish ? 4 : 0
        if Double(deletions + insertions) > max(maxEdit * Double(a.count) + 1, editFloor) { return .fail(.editDistance) }

        let window = options.corrections ? correctionWindow(raw, loose: options.professional) : []
        // A correction is applied whole or not at all: a dropped cue takes the word it corrects with it,
        // so "for 2, no, 3 people" never becomes "for 2, 3 people". A cue that reaches no word before it
        // ("I mean the quarterly one") drops alone.
        var cueSpans: [Range<Int>] = []
        for i in a.indices {
            for cue in correctionCues where i + cue.count <= a.count && Array(a[i..<(i + cue.count)]) == cue && (i..<(i + cue.count)).allSatisfy(window.contains) {
                cueSpans.append(i..<(i + cue.count))
            }
        }
        let cueWords = Set(cueSpans.flatMap { $0 })
        for span in cueSpans where span.contains(where: { !matchedRaw.contains($0) }) {
            var k = span.lowerBound - 1, m = span.upperBound
            while k >= 0, cueWords.contains(k) || CleanupText.hesitations.contains(a[k]) { k -= 1 }
            while m < a.count, cueWords.contains(m) || CleanupText.hesitations.contains(a[m]) { m += 1 }
            if k >= 0, m < a.count, window.contains(k), matchedRaw.contains(k), matchedRaw.contains(m) { return .fail(.unexplainedDeletion) }
        }
        var markerValues = options.lists ? listMarkerValues(cleaned) : []
        // Professional may write spoken markers ("item one milk item two eggs") as an inline list:
        // they count as markers when the raw counts 1, 2, ... with them.
        if options.professional && options.lists {
            let spoken = a.indices.dropLast().filter { ["number", "step", "item"].contains(a[$0]) }.compactMap { CleanupText.cardinals[a[$0 + 1]] ?? Int(a[$0 + 1]) }
            if spoken.count >= 2, spoken == Array(1...spoken.count) { markerValues.formUnion(spoken.map(String.init)) }
        }
        // Stutters and restarts ("I I", "don't, uh, don't") that filler removal may collapse.
        var repeats = Set<Int>()
        if options.fillers {
            for i in a.indices {
                var j = i + 1
                while j < a.count, j <= i + 3 {
                    if a[j] == a[i] { repeats.insert(i); repeats.insert(j); break }
                    if !CleanupText.hesitations.contains(a[j]) { break }
                    j += 1
                }
            }
            // Restarted phrases, often split by a chunk cut ("the final reason that. The final reason
            // that shaped"): only the first copy of an immediately repeated 2–6 word run may go.
            for n in 2...6 {
                for i in a.indices where i + 2 * n <= a.count && a[i..<(i + n)] == a[(i + n)..<(i + 2 * n)] {
                    repeats.formUnion(i..<(i + n))
                }
            }
        }
        func dropAllowed(_ i: Int) -> Bool { window.contains(i) || repeats.contains(i) }
        // Polish and Professional: a short sentence that the next, longer one restarts with the same two
        // opening words ("Shaker settlements were organized. Shaker settlements were built to be...") may go
        // whole. Its numbers and negations stay protected; its names may go only because the restart repeats them.
        // ponytail: opening words can't tell an abandoned sentence from a parallel one ("The project is late.
        // The project is also over budget."); the prompt asks the model to keep those, and Strict never drops.
        var restarts = Set<Int>()
        if options.corrections && options.polish {
            let starts = [0] + rawWords.indices.filter { $0 > 0 && rawWords[$0].sentenceStart } + [a.count]
            for k in starts.indices.dropLast(2) {
                let s = starts[k], t = starts[k + 1], next = starts[k + 2]
                if (3...12).contains(t - s), t - s < next - t, a[s..<(s + 2)] == a[t..<(t + 2)] { restarts.formUnion(s..<t) }
            }
        }
        // Filler words the fillers option may delete: hesitations anywhere; "like" and "you know"
        // only when set off by commas or sentence-initial (never "I like you", "you know the answer").
        func fillerDeletable(_ i: Int) -> Bool {
            let w = rawWords[i]
            if CleanupText.hesitations.contains(w.lower) { return true }
            if w.lower == "like" { return (w.commaBefore || w.sentenceStart) && w.commaAfter }
            if w.lower == "you", i + 1 < a.count, a[i + 1] == "know" {
                let know = rawWords[i + 1]
                return (w.commaBefore && (know.commaAfter || know.endAfter || i + 2 == a.count)) || (w.sentenceStart && know.commaAfter)
            }
            if w.lower == "know", i > 0, a[i - 1] == "you" { return fillerDeletable(i - 1) }
            return false
        }
        let hasListMarkers = !markerValues.isEmpty
        // A list starts only where the speaker announces one (the prompt's rule, and `RuleCleaner.formatList`'s):
        // a list noun before the first marker in its sentence ("my list", "the steps are"), a colon just before
        // that sentence, or a list noun in the sentence before it ("Here's the checklist. First, ...").
        func announced(_ i: Int) -> Bool {
            var start = i
            while start > 0, !rawWords[start].sentenceStart { start -= 1 }
            if (start..<i).contains(where: { RuleCleaner.strongListCues.contains(a[$0]) }) { return true }
            guard start > 0 else { return false }
            if !rawWords[start - 1].endAfter { return true }
            guard start == i else { return false }
            var lead = start - 1
            while lead > 0, !rawWords[lead].sentenceStart { lead -= 1 }
            return (lead..<start).contains { RuleCleaner.strongListCues.contains(a[$0]) }
        }
        // Professional also takes digits, as speech recognition often writes them ("item 1 milk").
        func spokenMarker(_ i: Int) -> Int? {
            guard i > 0, ["number", "step", "item"].contains(a[i - 1]) else { return nil }
            return CleanupText.cardinals[a[i]] ?? (options.professional ? Int(a[i]) : nil)
        }
        // Ordinals inside a fixed phrase are prose ("Remember: first impressions matter, second chances are rare").
        // ponytail: a closed list; an unlisted phrase can still pass as a list.
        func ordinalMarker(_ i: Int) -> Int? {
            guard let n = CleanupText.ordinals[a[i]] else { return nil }
            if i + 1 < a.count, !rawWords[i].commaAfter, ordinalPhraseNouns.contains(a[i + 1]) { return nil }
            // A date's day ("May first", "June second") is never a list marker.
            if i > 0, RuleCleaner.monthsAndMay.contains(a[i - 1]) { return nil }
            return n
        }
        // A marker counts inside a counted sequence ("first ... second") that starts at an announced first
        // marker, so "First, preheat the oven" and "Step one was easy; step two took all night" stay prose.
        // Professional's prompt also lists spoken markers and ordinals without an announcement, so they need only
        // the sequence ("First we went home, second we ate"); Polish and Strict keep that prose.
        func inSequence(_ i: Int, _ marker: (Int) -> Int?, markerWords: Int, announce: Bool) -> Bool {
            guard let n = marker(i) else { return false }
            let values = a.indices.compactMap(marker)
            guard values.contains(n + 1) || values.contains(n - 1) else { return false }
            return !announce || (0...i).contains { marker($0) == 1 && announced($0 - markerWords) }
        }
        // An ordinal modifying a noun is prose even in Professional: after a determiner ("the first"), or before
        // the same word as another ordinal ("first floor ... second floor"), unless that word is a subject ("first we").
        // ponytail: a heuristic; an unannounced "first X ... second Y" with distinct nouns can still pass as a list.
        func adjectival(_ i: Int) -> Bool {
            if i > 0, ["the", "a", "an", "my", "our", "your", "his", "her", "their", "its", "this", "that"].contains(a[i - 1]) { return true }
            guard i + 1 < a.count, !rawWords[i].commaAfter, !["i", "we", "you", "he", "she", "they", "it", "let's", "lets"].contains(a[i + 1]) else { return false }
            return a.indices.contains { $0 != i && $0 + 1 < a.count && CleanupText.ordinals[a[$0]] != nil && a[$0 + 1] == a[i + 1] }
        }
        // Enumeration words a list may replace with "N." markers: ordinals, "number/step/item"
        // plus a cardinal, and an "and"/"then" joining the next marker.
        func isMarkerWord(_ i: Int) -> Bool {
            guard options.lists && hasListMarkers else { return false }
            let w = a[i]
            if CleanupText.ordinals[w] != nil { return inSequence(i, ordinalMarker, markerWords: 0, announce: !options.professional || adjectival(i)) }
            if spokenMarker(i) != nil { return inSequence(i, spokenMarker, markerWords: 1, announce: !options.professional) }
            if ["number", "step", "item"].contains(w), i + 1 < a.count, spokenMarker(i + 1) != nil { return isMarkerWord(i + 1) }
            if w == "and" || w == "then", i + 1 < a.count, isMarkerWord(i + 1) { return true }
            // Professional: also across a filler ("eggs and yeah item three").
            if options.professional, w == "and" || w == "then", i + 2 < a.count,
               CleanupText.hesitations.contains(a[i + 1]) || polishFillers.contains(a[i + 1]), isMarkerWord(i + 2) { return true }
            return false
        }
        // A new "N." line needs a spoken enumeration of at least two items that reaches N: never a lone
        // "1. First, preheat the oven", nor more items than the speaker counted.
        let addedMarkers = listMarkerValues(cleaned).subtracting(listMarkerValues(raw)).compactMap { Int($0) }
        if !addedMarkers.isEmpty {
            let spokenCount = a.indices.filter { (CleanupText.ordinals[a[$0]] != nil || CleanupText.cardinals[a[$0]] != nil || Int(a[$0]) != nil) && isMarkerWord($0) }.count
            if spokenCount < 2 || addedMarkers.max()! > spokenCount { return .fail(.structureAdded) }
        }

        // Professional: "one" as a pronoun ("the quarterly one not the monthly one") may go.
        // It needs a determiner right before it or before one modifier ("the quarterly one"), never "won one".
        func pronounOne(_ i: Int) -> Bool {
            let determiners: Set<String> = ["the", "this", "that", "which", "each", "every", "another", "a", "any"]
            guard options.professional, a[i] == "one", i > 0,
                  determiners.contains(a[i - 1]) || (i > 1 && determiners.contains(a[i - 2]) && CleanupText.numberValue(a[i - 1]) == nil) else { return false }
            return i + 1 == a.count || grammarWords.contains(a[i + 1]) || CleanupText.isNegation(a[i + 1])
        }
        // Numbers: nothing new; drops only inside a correction or as a list marker.
        var rawNumbers: [String: Int] = [:], outNumbers: [String: Int] = [:], allowedNumberDrops: [String: Int] = [:]
        for (i, w) in rawWords.enumerated() {
            guard let v = CleanupText.numberValue(w.lower) else { continue }
            rawNumbers[v, default: 0] += 1
            if pronounOne(i) || dropAllowed(i) || (isMarkerWord(i) && markerValues.contains(v) && !matchedRaw.contains(i)) { allowedNumberDrops[v, default: 0] += 1 }
        }
        for w in outWords { if let v = CleanupText.numberValue(w.lower) { outNumbers[v, default: 0] += 1 } }
        for (v, c) in outNumbers where c > rawNumbers[v, default: 0] { return .fail(.numberAdded) }
        for (v, c) in rawNumbers where outNumbers[v, default: 0] < c - allowedNumberDrops[v, default: 0] { return .fail(.numberDropped) }

        // Negation: count may not rise, and may drop only inside a correction.
        let rawNeg = rawWords.filter { CleanupText.isNegation($0.lower) }.count
        let outNeg = outWords.filter { CleanupText.isNegation($0.lower) }.count
        let negInWindow = rawWords.enumerated().filter { dropAllowed($0.offset) && CleanupText.isNegation($0.element.lower) }.count
        if outNeg > rawNeg || outNeg < rawNeg - negInWindow { return .fail(.negationChanged) }

        let rawSet = Set(a)
        let termWords = Set(allowedTerms.flatMap { words($0).map(\.lower) })
        // Cleanup never inserts words: every unaligned output word must be a supplied dictionary term.
        // A correction may restate a word from its corrected span in a new position ("three
        // pizzas, sorry, four" -> "four pizzas"); such a word consumes that unmatched raw word.
        var consumedRaw = matchedRaw
        // Matches keep order, so the raw words an unmatched output word could replace lie between the
        // raw words matched to its matched neighbors: the k-th matched raw word pairs with the k-th output.
        let pairedRaw = matchedRaw.sorted(), pairedOut = matchedOut.sorted()
        func gap(_ j: Int) -> Range<Int> {
            let before = pairedOut.filter { $0 < j }.count
            return (before == 0 ? 0 : pairedRaw[before - 1] + 1)..<(before < pairedRaw.count ? pairedRaw[before] : rawWords.count)
        }
        /// `gap` widened by one kept word on each side.
        func nearGap(_ j: Int) -> Range<Int> {
            let before = pairedOut.filter { $0 < j }.count
            return (before < 2 ? 0 : pairedRaw[before - 2] + 1)..<(before + 1 < pairedRaw.count ? pairedRaw[before + 1] : rawWords.count)
        }
        var polishInserted: [Int] = []
        for (j, w) in outWords.enumerated() where !matchedOut.contains(j) && !termWords.contains(w.lower) {
            if let i = rawWords.indices.first(where: { window.contains($0) && !consumedRaw.contains($0) && a[$0] == w.lower }) {
                consumedRaw.insert(i); continue
            }
            // A negative contraction may complete one speech recognition truncated in the same place,
            // with the same auxiliary ("couldn" -> "couldn't", never "shouldn't"; "doesn" may be "don't").
            if let i = gap(j).first(where: { !consumedRaw.contains($0) && CleanupText.truncatedNegations[a[$0]]?.contains(w.lower) == true }) {
                consumedRaw.insert(i); continue
            }
            if w.isName && !rawSet.contains(w.lower) {
                // Polish and Professional: a name may respell one lowercase raw word in its place ("shreia" ->
                // "Shreya", as screen context spells it), judged on the transcript alone like a dictionary term,
                // but closer: similarity 0.7 or `soundsLike`, so "priya" -> "Shreya" and "team" -> "Tim" (0.5) fail.
                // A capitalized antonym is not a respelling ("include" -> "Exclude").
                func letters(_ s: String) -> String { String(s.filter(\.isLetter)) }
                guard options.polish, let i = gap(j).first(where: { x in !consumedRaw.contains(x) && !rawWords[x].isName && !grammarWords.contains(a[x])
                    && !persons.contains { $0.contains(a[x]) } && CleanupText.numberValue(a[x]) == nil && !CleanupText.isNegation(a[x]) && !polarityFlip(a[x], w.lower)
                    && (CleanupText.similarity(a[x], w.lower) >= 0.7 || soundsLike(letters(a[x]), letters(w.lower))) }) else { return .fail(.nameAdded) }
                consumedRaw.insert(i); continue
            }
            // Professional: a number or negation never moves; a new one replaces one in its own gap
            // ("don't" -> "doesn't"), so "paid 10 ... paid 20" cannot swap and "not" cannot change clause.
            if options.professional, let v = CleanupText.numberValue(w.lower) {
                // Same value and same form: "one" never becomes "first", nor "12pm" "12am" or "1" "1st".
                func form(_ t: String) -> String { CleanupText.ordinals[t] != nil ? "ordinal" : t.first?.isNumber == true ? String(t.drop { $0.isNumber || $0 == "." || $0 == "," }) : "" }
                guard let i = gap(j).first(where: { !consumedRaw.contains($0) && CleanupText.numberValue(a[$0]) == v && form(a[$0]) == form(w.lower) }) else { return .fail(.numberAdded) }
                consumedRaw.insert(i); continue
            }
            if options.professional, CleanupText.isNegation(w.lower) {
                // Only within the not/n't family ("don't" -> "doesn't", "not" -> "won't"); never/no/nothing stay exact.
                func notFamily(_ t: String) -> Bool { t == "not" || t.hasSuffix("n't") || CleanupText.truncatedNegations[t] != nil }
                guard let i = gap(j).first(where: { !consumedRaw.contains($0) && CleanupText.isNegation(a[$0]) && (a[$0] == w.lower || (notFamily(a[$0]) && notFamily(w.lower))) }) else { return .fail(.negationChanged) }
                consumedRaw.insert(i); continue
            }
            // Professional may reorder locally ("me and John was" -> "John and I were"): a moved word consumes its
            // raw copy within one kept word of its gap, so objects cannot trade clauses ("likes cats ... likes dogs").
            // Only coordination reorders: the word sits next to "and"/"or" on either side, so a verb or modal
            // never moves ("should I accept" -> "I should accept", "I sent my manager" -> "my manager sent me").
            // ...and one conjunct is a pronoun ("me and John" -> "John and I"), so actions never trade order
            // ("open and close" -> "close and open").
            func coordinated(_ i: Int) -> Bool {
                let joins: Set<String> = ["and", "or"]
                func pronoun(_ w: String) -> Bool { persons.contains { $0.contains(w) } }
                func pairs(_ ws: [String], _ k: Int) -> Bool {
                    (joins.contains(ws[k]) && ((k > 0 && pronoun(ws[k - 1])) || (k + 1 < ws.count && pronoun(ws[k + 1]))))
                        || (k > 1 && joins.contains(ws[k - 1]) && pronoun(ws[k - 2]))
                        || (k + 2 < ws.count && joins.contains(ws[k + 1]) && pronoun(ws[k + 2]))
                        || (pronoun(ws[k]) && ((k > 0 && joins.contains(ws[k - 1])) || (k + 1 < ws.count && joins.contains(ws[k + 1]))))
                }
                return pairs(a, i) || pairs(b, j)
            }
            if options.professional, let i = nearGap(j).first(where: { !consumedRaw.contains($0) && a[$0] == w.lower && coordinated($0) }) {
                consumedRaw.insert(i); continue
            }
            if options.polish { polishInserted.append(j); continue }
            return .fail(.insertedWords)
        }
        var outNameCounts: [String: Int] = [:]
        for w in outWords { outNameCounts[w.lower, default: 0] += 1 }
        var rawNameCounts: [String: Int] = [:], allowedNameDrops: [String: Int] = [:]
        for (i, w) in rawWords.enumerated() where w.isName {
            rawNameCounts[w.lower, default: 0] += 1
            if dropAllowed(i) || restarts.contains(i) { allowedNameDrops[w.lower, default: 0] += 1 }
        }
        for (n, c) in rawNameCounts where outNameCounts[n, default: 0] < c - allowedNameDrops[n, default: 0] {
            if termWords.isEmpty { return .fail(.nameDropped) }
            // A dictionary respelling may replace a misheard name.
            if !outWords.contains(where: { termWords.contains($0.lower) }) { return .fail(.nameDropped) }
        }

        // Every deleted word must be explained by an enabled option, or by a dictionary respelling.
        // Each unaligned term word must replace its own run of 1–3 adjacent raw words that spell it
        // ("cloud dev" -> "clouddev"), are spelled at least half like it ("acme" -> "Akme"), or sound
        // like it per `soundsLike` ("shelf made" -> "Shelfmate"). A term with no such run is an
        // inserted word, so a term never pays for deleting an unrelated word. The run must sit in the
        // term's own alignment gap: matches keep order, so the k-th matched raw word pairs with the
        // k-th matched output word.
        func spelled(_ text: String) -> String { String(text.filter { $0.isLetter || $0.isNumber }) }
        for (j, w) in outWords.enumerated() where !matchedOut.contains(j) && termWords.contains(w.lower) {
            let target = spelled(w.lower)
            let gap = gap(j)
            var replaced: Range<Int>? = nil
            search: for length in stride(from: 3, through: 1, by: -1) {
                for start in gap where start + length <= gap.upperBound {
                    let run = start..<(start + length)
                    guard run.allSatisfy({ !consumedRaw.contains($0) }) else { continue }
                    let heard = spelled(run.map { a[$0] }.joined())
                    if heard == target || CleanupText.similarity(heard, target) >= 0.5 || soundsLike(heard, target) { replaced = run; break search }
                }
            }
            guard let replaced else { return .fail(.insertedWords) }
            consumedRaw.formUnion(replaced)
        }
        // Polish: grammar fixes swap a few words in place; fillers and false starts may go.
        // ponytail: closed word lists and similarity, not a grammar model; names, numbers and negations
        // are still checked above, and strict mode stays the meaning-safe choice.
        func filler(_ i: Int) -> Bool {
            func pair(_ x: Int) -> Bool {
                guard x >= 0, x + 2 < a.count, polishFillerPairs.contains([a[x], a[x + 1]]) else { return false }
                // Professional: also at a sentence start or after another filler ("yeah you know me and John").
                // Never before a meaningful complement ("I mean business", "you know the answer").
                let meaningful: Set<String> = ["the", "a", "an", "what", "that", "it", "how", "where", "why", "who", "business", "to", "this", "him", "her", "them", "his", "everything", "nothing", "exactly"]
                if options.professional && !meaningful.contains(a[x + 2]) && (rawWords[x].sentenceStart || (x > 0 && polishFillers.contains(a[x - 1]))
                    || ["and", "but", "so"].contains(a[x + 2])) { return true }
                // Without punctuation, only a following personal subject clearly starts a new thought.
                return ["i", "we", "you", "he", "she", "they", "it's", "we're", "you're", "they're"].contains(a[x + 2])
            }
            if a[i] == "like" {
                return i > 0 && i + 1 < a.count && !meaningfulLikeAfter.contains(a[i - 1])
                    && !["the", "a", "an", "this", "that", "these", "those"].contains(a[i + 1])
            }
            return CleanupText.hesitations.contains(a[i]) || polishFillers.contains(a[i]) || pair(i) || pair(i - 1)
        }
        func polishCheck(unexplained: [Int], inserted: [Int]) -> CleanupGuardResult {
            func longestRun(_ xs: [Int]) -> Int {
                var best = 0, run = 0, last = Int.min
                for x in xs.sorted() { run = x == last + 1 ? run + 1 : 1; best = max(best, run); last = x }
                return best
            }
            // New words: a few, inside the text (never a prefix like "Sure," or an appended answer), no long run.
            if inserted.count > max(2, a.count * 8 / 100) || longestRun(inserted) > 3 { return .fail(.insertedWords) }
            if let first = pairedOut.first, let last = pairedOut.last, inserted.contains(where: { $0 < first || $0 > last }) { return .fail(.insertedWords) }
            // Each new word replaces a word in its gap that it respells, or is a grammar word.
            var deleted = Set(unexplained)
            for j in inserted {
                let w = outWords[j].lower
                // A name is never swapped, whatever dictionary terms explained earlier.
                if let i = gap(j).first(where: { deleted.contains($0) && !rawWords[$0].isName && polishSwap(a[$0], w) }) { deleted.remove(i); continue }
                if !grammarWords.contains(w) { return .fail(.insertedWords) }
            }
            // Removed words: fillers (with the fillers option), grammar words, or a false start that the
            // speaker restates within the next four words (with the corrections option).
            var dropped: [Int] = []
            for i in deleted.sorted() {
                if rawWords[i].isName { return .fail(.nameDropped) }
                if filler(i) { if options.fillers { continue } else { return .fail(.unexplainedDeletion) } }
                dropped.append(i)
            }
            if dropped.count > max(4, a.count / 10) || longestRun(dropped) > 6 { return .fail(.unexplainedDeletion) }
            var k = 0
            while k < dropped.count {
                var end = k
                while end + 1 < dropped.count, dropped[end + 1] == dropped[end] + 1 { end += 1 }
                let run = dropped[k...end], after = (run.last! + 1)..<min(a.count, run.last! + 5)
                // Every content word of the dropped phrase must come back, not just a shared "the".
                let content = run.filter { !grammarWords.contains(a[$0]) }
                let restated = !content.isEmpty && content.allSatisfy { i in after.contains { CleanupText.similarity(a[i], a[$0]) >= 0.5 } }
                if !run.allSatisfy({ droppableGrammarWords.contains(a[$0]) }) && !(options.corrections && restated) { return .fail(.unexplainedDeletion) }
                k = end + 1
            }
            return .pass
        }
        var unexplained: [Int] = []
        for i in rawWords.indices where !consumedRaw.contains(i) {
            if options.fillers && (fillerDeletable(i) || repeats.contains(i)) { continue }
            if options.corrections && window.contains(i) { continue }
            if restarts.contains(i) { continue }
            if isMarkerWord(i) { continue }
            unexplained.append(i)
        }
        // Professional: budgets only. ponytail: a reworded word can change meaning ("accept" ->
        // "decline"); names, numbers, negations, answers and quotes are checked above, and the
        // settings say so. Polish stays the voice-keeping choice.
        // `long` is `short` with a regular English ending: plan/planned, make/making, try/tried, table/tables;
        // never plan/plant.
        func inflection(_ short: String, _ long: String) -> Bool {
            guard short.count >= 2, long.count > short.count, !polarityFlip(short, long) else { return false }
            let endings = ["s", "es", "ed", "d", "ing"] // Not -ly, -er, -est: hard/hardly, safe/safer differ.
            let stems = [short, String(short.dropLast()), String(short.dropLast()) + "i", short + String(short.last!)]
            return stems.contains { stem in endings.contains { stem + $0 == long } }
        }
        if options.professional {
            // A kept number or negation keeps its context: a content word dropped on one side of it may
            // not come back on the other ("Book table 5" -> "Book 5 tables").
            for k in pairedRaw.indices where CleanupText.numberValue(a[pairedRaw[k]]) != nil || CleanupText.isNegation(a[pairedRaw[k]]) {
                let i = pairedRaw[k], j = pairedOut[k]
                let rawBefore = (k > 0 ? pairedRaw[k - 1] + 1 : 0)..<i, outBefore = (k > 0 ? pairedOut[k - 1] + 1 : 0)..<j
                let rawAfter = (i + 1)..<(k + 1 < pairedRaw.count ? pairedRaw[k + 1] : a.count)
                let outAfter = (j + 1)..<(k + 1 < pairedOut.count ? pairedOut[k + 1] : b.count)
                func crosses(_ from: Range<Int>, _ to: Range<Int>) -> Bool {
                    from.contains { x in !grammarWords.contains(a[x]) && !filler(x) && to.contains { CleanupText.similarity(a[x], b[$0]) >= 0.5 } }
                }
                if crosses(rawBefore, outAfter) || crosses(rawAfter, outBefore) { return .fail(.numberAdded) }
            }
            let inserted = polishInserted.map { outWords[$0].lower }
            if inserted.count > max(4, a.count / 4) { return .fail(.insertedWords) }
            if let first = pairedOut.first, let last = pairedOut.last, polishInserted.contains(where: { $0 < first || $0 > last }) { return .fail(.insertedWords) }
            // Names keep their order ("Alice paid Bob" never becomes "Bob paid Alice"), counting a capitalized
            // sentence start that is not a grammar word. Every occurrence counts, whatever its case: a sentence
            // start said again in lowercase ("Send it. Then send it again") is the same word, not a moved name.
            let nameSet = Set(rawWords.filter { $0.isName || ($0.capitalized && !grammarWords.contains($0.lower)) }.map(\.lower))
            let rawNames = rawWords.map(\.lower).filter(nameSet.contains)
            var rest = rawNames[...]
            for n in outWords.map(\.lower) where nameSet.contains(n) {
                guard let k = rest.firstIndex(of: n) else { return .fail(.nameDropped) }
                rest = rest[(k + 1)...]
            }
            // Pronoun persons keep their order ("I told him" never becomes "he told me"); "it" may come and go.
            func personSequence(_ ws: [String]) -> [Int] { ws.compactMap { w in persons.firstIndex { $0.contains(w) } }.filter { $0 != 4 } }
            var restPersons = personSequence(rawWords.indices.filter { !dropAllowed($0) && !filler($0) }.map { a[$0] })[...]
            for person in personSequence(b) {
                guard let k = restPersons.firstIndex(of: person) else { return .fail(.unexplainedDeletion) }
                restPersons = restPersons[(k + 1)...]
            }
            // Removed words: fillers (with that option), droppable grammar words, a pronoun replaced in the
            // same person ("me" -> "I"), or a content word replaced by a similar new word ("table" -> "tables").
            // A dissimilar replacement is refused: a lowercase name ("sam") or an antonym looks the same.
            var replacements = polishInserted.filter { !grammarWords.contains(b[$0]) }, dropped = 0
            for i in unexplained {
                if rawWords[i].isName { return .fail(.nameDropped) }
                if filler(i) { if options.fillers { continue } else { return .fail(.unexplainedDeletion) } }
                dropped += 1
                // A question's leading auxiliary stays ("do you accept" never becomes "You accept").
                if rawWords[i].sentenceStart, ["do", "does", "did", "is", "are", "was", "were", "can", "could", "should", "would", "will", "shall", "may", "might", "must", "have", "has", "had"].contains(a[i]) { return .fail(.unexplainedDeletion) }
                // A preposition is never swapped for another ("to Paris" -> "from Paris", "for Bob" -> "with Bob").
                let prepositions: Set<String> = ["to", "from", "for", "with", "in", "on", "at", "by", "of", "into", "off", "up", "out", "about", "than", "as"]
                if prepositions.contains(a[i]), polishInserted.contains(where: { gap($0).contains(i) && prepositions.contains(b[$0]) && b[$0] != a[i] }) { return .fail(.unexplainedDeletion) }
                // Nor is a relational one dropped: "This is for Bob" never becomes "This is Bob".
                if ["for", "from", "with", "by", "at", "on", "in", "into", "about"].contains(a[i]) { return .fail(.unexplainedDeletion) }
                // A tense word may change only within its tense ("was" -> "were", never "was" -> "is").
                let tenses: [Set<String>] = [["was", "were", "had", "did"], ["is", "are", "am", "has", "have", "do", "does"], ["will", "shall"]]
                if let tense = tenses.firstIndex(where: { $0.contains(a[i]) }),
                   polishInserted.contains(where: { j in gap(j).contains(i) && tenses.firstIndex { $0.contains(b[j]) }.map { $0 != tense } == true }) { return .fail(.unexplainedDeletion) }
                // Particles carry meaning ("turn it up", "take it off"); they are never dropped.
                if ["up", "off", "out", "more", "most", "down", "over", "back", "away", "too", "very"].contains(a[i]) { return .fail(.unexplainedDeletion) }
                // Informal forms the rewrite spells out ("gotta" -> "have to").
                if droppableGrammarWords.contains(a[i]) || ["gotta", "wanna", "kinda", "sorta"].contains(a[i]) || pronounOne(i) { continue }
                // A sentence-opening interjection ("hey do you know" -> "Do you know").
                if rawWords[i].sentenceStart, ["hey", "okay", "ok", "well", "oh", "alright", "anyway"].contains(a[i]) { continue }
                // A join dropped where the rewrite ends a sentence ("by Thursday, and I" -> "by Thursday. I").
                if ["and", "so", "but", "because", "anyway"].contains(a[i]), let k = pairedRaw.lastIndex(where: { $0 < i }), outWords[pairedOut[k]].endAfter { continue }
                if let person = persons.first(where: { $0.contains(a[i]) }) {
                    if outWords.indices.contains(where: { !matchedOut.contains($0) && person.contains(outWords[$0].lower) }) { continue }
                    return .fail(.unexplainedDeletion)
                }
                // The same word elsewhere is a move, not a replacement, and moves are limited above.
                // A replacement is an inflection of the same word in the same place ("table" -> "tables"),
                // not a similar-looking different word ("liked" -> "loved", "increase" -> "decrease").
                guard let r = replacements.firstIndex(where: { gap($0).contains(i) && (inflection(a[i], b[$0]) || inflection(b[$0], a[i])) }) else { return .fail(.unexplainedDeletion) }
                replacements.remove(at: r)
            }
            // Every new content word or modal replaces a dropped word; a pronoun contraction ("he's") is the
            // only free one. "I approve" never becomes "I should approve", nor "send" "send and delete".
            // An inserted auxiliary, adverb or join takes the place of a dropped grammar word ("was" -> "were",
            // "is going to" -> "be"): "Alice approved" never becomes "Alice has approved", nor "likes" "also likes".
            // ...of the same kind: an auxiliary or modal for an auxiliary, modal or informal form, a join for a join;
            // an adverb is never added ("has approved" -> "also approved", "really likes" -> "also likes").
            let auxiliaries: Set<String> = modals.union(["has", "have", "had", "been", "be", "is", "are", "was", "were", "am", "do", "does", "did", "going", "gonna", "gotta", "wanna"])
            let joins: Set<String> = ["and", "but", "or", "so", "if", "then", "than"]
            let adverbs: Set<String> = ["also", "just", "really", "very", "too", "more", "most", "only", "even", "still", "already"]
            for j in polishInserted {
                let w = b[j]
                if adverbs.contains(w) { return .fail(.insertedWords) }
                let kind = auxiliaries.contains(w) ? auxiliaries : joins.contains(w) ? joins : nil
                if let kind, !gap(j).contains(where: { unexplained.contains($0) && kind.contains(a[$0]) }) { return .fail(.insertedWords) }
            }
            // A bare pronoun is new only as a same-person swap in its place ("me" -> "I"), never an added
            // object ("Alice approves it today").
            for j in polishInserted where !b[j].contains("'") {
                guard let person = persons.first(where: { $0.contains(b[j]) }) else { continue }
                if !nearGap(j).contains(where: { unexplained.contains($0) && person.contains(a[$0]) }) { return .fail(.insertedWords) }
            }
            // An inflection of a dropped word in the same place also counts ("do" -> "doing"); a contraction
            // ("he's") is the only free word, never a bare pronoun ("approves it today").
            if replacements.contains(where: { j in !(b[j].contains("'") && persons.contains { $0.contains(b[j]) })
                && !gap(j).contains { unexplained.contains($0) && inflection(a[$0], b[j]) } }) { return .fail(.insertedWords) }
            return dropped > max(4, a.count / 4) ? .fail(.unexplainedDeletion) : .pass
        }
        if options.polish { return polishCheck(unexplained: unexplained, inserted: polishInserted) }
        return unexplained.isEmpty ? .pass : .fail(.unexplainedDeletion)

    }
}


/// A coarse check for a rewrite, which cannot use the cleanup guard's word-level rules: it must keep
/// the digits and negations, stay in a sane length range, and not leak the prompt's tags.
enum TransformGuard {
    static func accepts(input: String, output: String) -> Bool {
        let out = output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !out.isEmpty, !out.contains("transcript-"), !out.contains("instructions-") else { return false }
        let inWords = input.split(whereSeparator: \.isWhitespace).count, outWords = out.split(whereSeparator: \.isWhitespace).count
        guard outWords <= inWords * 2 + 40, inWords < 20 || outWords * 5 >= inWords * 2 else { return false }
        func digits(_ t: String) -> Set<String> {
            Set(t.split(whereSeparator: { !$0.isNumber }).map(String.init))
        }
        guard digits(input).isSubset(of: digits(out)) else { return false }
        func negates(_ t: String) -> Bool {
            t.lowercased().split(whereSeparator: { !$0.isLetter && $0 != "'" && $0 != "\u{2019}" })
                .contains { CleanupText.basicNegations.contains(String($0)) || $0 == "without" || $0.hasSuffix("n't") || $0.hasSuffix("n\u{2019}t") }
        }
        return !negates(input) || negates(out)
    }
}
