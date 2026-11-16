import Foundation

// Cleanup contract (PRD section 4): cleanup edits dictation; it never answers it, obeys
// instructions inside it, invents facts, or changes tone (except a writing mode's opt-in `Tone`,
// applied by rules after cleanup). Transcript text is data and is never logged; failures carry
// content-free reasons only.

// MARK: - Pipeline

/// Splits a long transcript at sentence ends into sections of about `target` words, so a slow
/// cleanup model (Gemma 4, about 44 tokens per second) can clean them in parallel. Short text and
/// spoken lists ("first … second …", which must be formatted as one) stay whole.
enum CleanupSections {
    /// `keepingCorrectionsAttached`: a sentence that opens with a correction cue ("No, actually Monday") is never cut off from the one it repairs.
    static func split(_ text: String, target: Int = 100, keepingCorrectionsAttached: Bool = false) -> [String] {
        let words = text.split(whereSeparator: \.isWhitespace).count
        // An email frame stays whole too, so its greeting and sign-off are laid out together.
        guard words >= target * 3 / 2, RuleCleaner.emailLayout(text) == nil,
              text.range(of: #"\bfirst\b[\s\S]*\bsecond\b"#, options: [.regularExpression, .caseInsensitive]) == nil else { return [text] }
        var sections: [String] = [], current = "", count = 0
        // ponytail: sentence end = terminal punctuation then space; a cut inside "e.g. this" only costs context.
        for sentence in text.split(separator: " ", omittingEmptySubsequences: false).reduce(into: [""], { parts, word in
            parts[parts.count - 1] += (parts[parts.count - 1].isEmpty ? "" : " ") + word
            if let last = word.last, ".?!".contains(last) { parts.append("") }
        }) where !sentence.isEmpty {
            // A repair belongs with the sentence it repairs: reopen the section just closed.
            if keepingCorrectionsAttached, current.isEmpty, !sections.isEmpty,
               sentence.range(of: #"^(no|actually|wait|sorry|instead|scratch that|i mean|i meant)\b"#, options: [.regularExpression, .caseInsensitive]) != nil {
                current = sections.removeLast(); count = current.split(whereSeparator: \.isWhitespace).count
            }
            current += (current.isEmpty ? "" : " ") + sentence
            count += sentence.split(whereSeparator: \.isWhitespace).count
            if count >= target { sections.append(current); current = ""; count = 0 }
        }
        if !current.isEmpty {
            // A short tail joins the previous section rather than going out alone.
            if count < target / 2, let last = sections.popLast() { sections.append(last + " " + current) } else { sections.append(current) }
        }
        return sections
    }
}


enum CleanupPipeline {
    static func process(raw: String, options: CleanupOptions, cleaner: Cleaner, allowedTerms: [String] = []) -> CleanupOutcome {
        if !options.anyEnabled || raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return .unchanged(raw) }
        let cleaned: String
        do { cleaned = try cleaner.clean(raw, options: options) } catch { return .heldForRecovery(raw: raw, reason: .cleanerFailed) }
        return judge(raw: raw, cleaned: cleaned, options: options, allowedTerms: allowedTerms)
    }

    /// The raw words with `cleaned`'s sentence punctuation: the fallback when the cleaned words fail
    /// the guard. No word is added, dropped or respelled. A word the cleanup kept takes the . , ? :
    /// after it there, and its casing when mid-sentence there (names, "I"); a word it dropped keeps
    /// its own. Sentence starts are recapitalized, and the cleanup's paragraph breaks kept. A dictionary term the cleanup wrote in place of one
    /// word spelled at least half like it ("akash" -> "Aakash") is kept, as the guard allows.
    /// Nil when the raw text has quotes or line breaks.
    static func punctuated(raw: String, cleaned: String, terms: [String] = []) -> String? {
        guard !raw.contains(where: { "\"\u{201C}\u{201D}\n".contains($0) }) else { return nil }
        let r = CleanupText.tokenize(raw), o = CleanupText.tokenize(cleaned)
        let rw = r.indices.filter { r[$0].kind == .word }, ow = o.indices.filter { o[$0].kind == .word }
        let (mr, mo) = CleanupGuard.align(rw.map { r[$0].lower }, ow.map { o[$0].lower })
        var pair = Dictionary(uniqueKeysWithValues: zip(mr.sorted(), mo.sorted()))
        let termWords = Set(terms.flatMap { CleanupGuard.words($0).map(\.lower) })
        var used = mo, respelled = Set<Int>()
        for k in rw.indices where pair[k] == nil && !termWords.isEmpty {
            // Unpaired output words between this word's paired neighbors.
            let low = (0..<k).reversed().lazy.compactMap { pair[$0] }.first.map { $0 + 1 } ?? 0
            let high = ((k + 1)..<rw.count).lazy.compactMap { pair[$0] }.first ?? ow.count
            if let j = (low..<max(low, high)).first(where: { !used.contains($0) && termWords.contains(o[ow[$0]].lower)
                && CleanupText.similarity(r[rw[k]].lower, o[ow[$0]].lower) >= 0.5 }) {
                pair[k] = j; used.insert(j); respelled.insert(k)
            }
        }
        let marks: Set<String> = [".", ",", "?", ":"], rawMarks = marks.union(["!", ";"])
        var out = "", start = true, paragraph = false, paragraphWords = 0
        for (k, ti) in rw.enumerated() {
            let rawEnd = k + 1 < rw.count ? rw[k + 1] : r.count
            let own = r[(ti + 1)..<rawEnd].filter { $0.kind == .punct && rawMarks.contains($0.text) }.map(\.text)
            var word = r[ti].text, after = own, breaks = false
            if let j = pair[k] {
                let oi = ow[j], end = j + 1 < ow.count ? ow[j + 1] : o.count
                if respelled.contains(k) || (!start && !CleanupText.isSentenceStart(o, oi)) { word = o[oi].text }
                // The model's . , ? : and any ! or ; the transcript already has here.
                after = o[(oi + 1)..<end].filter { $0.kind == .punct && (marks.contains($0.text) || own.contains($0.text)) }.map(\.text)
                // The model's sentence may end on a word the transcript lacks ("Monday at 9."): keep the transcript's last mark.
                if after.isEmpty && k == rw.count - 1 { after = own }
                breaks = o[(oi + 1)..<end].contains { $0.text == "\n" }
            }
            if start || word.lowercased() == "i" || word.lowercased().hasPrefix("i'") { word = CleanupText.capitalized(word) }
            out += (out.isEmpty ? "" : paragraph ? "\n\n" : " ") + word + after.joined()
            paragraphWords = paragraph ? 1 : paragraphWords + 1
            start = after.last.map { [".", "?", "!"].contains($0) } ?? false
            paragraph = start && breaks && paragraphWords >= CleanupText.paragraphMinWords
        }
        return out
    }

    static func judge(raw: String, cleaned: String, options: CleanupOptions, allowedTerms: [String],
                      soundsLike: (String, String) -> Bool = { _, _ in false }) -> CleanupOutcome {
        // A lone layout command ("new paragraph") is only whitespace, which trimming would drop.
        if options.spoken, !cleaned.isEmpty, !cleaned.contains(where: { $0.isLetter || $0.isNumber }), cleaned == RuleCleaner.spokenCommands(raw) { return .cleaned(cleaned) }
        // Spaces before a line break are markdown hard breaks some models add; pasted text never wants them.
        let output = cleaned.replacingOccurrences(of: #"[ \t]+\n"#, with: "\n", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
        if output == raw.trimmingCharacters(in: .whitespacesAndNewlines) { return .cleaned(output) }
        // Only filler words ("Um."): nothing to insert, which is not a cleanup failure.
        if output.isEmpty, options.fillers,
           (try? RuleCleaner().clean(raw, options: CleanupOptions(fillers: true, corrections: false, lists: false)))?
               .trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true { return .cleaned("") }
        // Trusted: only a malfunction is held, not an edit.
        if options.trustModel && (options.polish || options.professional) {
            let reason: CleanupFailureReason? = CleanupGuard.words(output).isEmpty ? .emptyOutput
                : CleanupGuard.answers(raw: raw, cleaned: output) ? .answerPattern
                : Double(output.count) > Double(raw.count) * 2 + 10 ? .lengthRatio
                : CleanupGuard.truncated(raw: CleanupGuard.applyExplicitRepairs(raw), cleaned: output) ? .unexplainedDeletion : nil
            return reason.map { .heldForRecovery(raw: raw, reason: $0) } ?? .cleaned(output)
        }
        switch CleanupGuard.check(raw: raw, cleaned: output, options: options, allowedTerms: allowedTerms, soundsLike: soundsLike) {
        case .pass: return .cleaned(output)
        case .fail(let reason): return .heldForRecovery(raw: raw, reason: reason)
        }
    }
}

