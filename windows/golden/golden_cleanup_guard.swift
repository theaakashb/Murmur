// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup PrivateFiles Dictionary Learning Retention ../windows/golden/cleanup_cases
import Foundation

// Golden outputs of CleanupGuard (and TransformGuard, CleanupPipeline.punctuated and .judge, which take the same pairs).
// "text": per GoldenCases.texts, words() and the correction helpers (a key is left out when it is empty or equals the input).
// "pairs": (raw, output) pairs: corpus (input, label), (input, rule output), (input, input); check_cleanup.swift's pairs; edges.
//   v: CleanupGuard.check verdict ("-" = pass) for each GoldenCases.optionSets entry, joined by ","; own: the corpus options and
//   their verdict; trusted: judge() outcomes for GoldenCases.trustedSets ("c" = cleaned to exactly `out`).
// "mutations": check_cleanup.swift's adversarial corpus mutations, judged as check_cleanup.swift does: own options, + polish,
//   + polish + professional, + spoken.
// "terms": dictionary-term pairs, without and with GoldenCases.soundsLike. One case per line. Run from the repository root.
struct TextCase: Encodable {
    var input: String
    var words: [String]
    var window, loose, commaFree: String?
    var commaFreeApplied, explicit, cued: String?
    var replaced: String?
    var markers: [String]?
    var nonList: Int
}
struct PairCase: Encodable {
    var raw: String, out: String
    var own: String?, ownVerdict: String?
    var v: String
    var truncated: Bool, answers: Bool, transform: Bool
    var punctuated: String?
    var trusted: String
}
struct MutationCase: Encodable { var label: String, raw: String, out: String, options: String, v: String }
struct TermCase: Encodable { var raw: String, out: String, terms: [String], v: String, sounds: String }
struct WordPair: Encodable { var a: String, b: String, flip: Bool, swap: Bool }

func same(_ a: String, _ b: String) -> Bool { a.unicodeScalars.elementsEqual(b.unicodeScalars) }
func ints(_ s: Set<Int>) -> String? { s.isEmpty ? nil : s.sorted().map(String.init).joined(separator: ",") }
func verdict(_ r: CleanupGuardResult) -> String { if case .fail(let reason) = r { return reason.rawValue }; return "-" }
func outcome(_ o: CleanupOutcome, _ out: String) -> String {
    switch o {
    case .cleaned(let t): return same(t, out) ? "c" : "c:" + t
    case .unchanged(let t): return "u:" + t
    case .heldForRecovery(_, let r): return "h:" + r.rawValue
    }
}

@main struct Gen { static func main() throws {
    var text: [String] = []
    for s in GoldenCases.texts {
        let w = CleanupGuard.words(s).map { "\($0.text)|\($0.lower)|" + [$0.sentenceStart, $0.commaBefore, $0.commaAfter, $0.endAfter].map { $0 ? "1" : "0" }.joined() }
        let rep = CleanupGuard.replacedSentences(CleanupGuard.words(s))
        let replaced = rep.cued.isEmpty && rep.restated.isEmpty && rep.cuts.isEmpty ? nil
            : "\(ints(rep.cued) ?? "");\(ints(rep.restated) ?? "");" + rep.cuts.map { "\($0.lowerBound)-\($0.upperBound)" }.joined(separator: ",")
        func changed(_ o: String) -> String? { same(o, s) ? nil : o }
        let markers = CleanupGuard.listMarkerValues(s).sorted()
        text.append(GoldenCases.json(TextCase(
            input: s, words: w, window: ints(CleanupGuard.correctionWindow(s)), loose: ints(CleanupGuard.correctionWindow(s, loose: true)),
            commaFree: ints(CleanupGuard.correctionWindow(s, loose: true, commaFreeOnly: true)),
            commaFreeApplied: changed(CleanupGuard.applyCommaFreeCorrections(s)), explicit: changed(CleanupGuard.applyExplicitRepairs(s)),
            cued: changed(CleanupGuard.applyCuedSentenceRepairs(s)), replaced: replaced, markers: markers.isEmpty ? nil : markers,
            nonList: CleanupGuard.nonListLineCount(s))))
    }

    let cleaner = RuleCleaner()
    var pairList: [(String, String, CleanupOptions?)] = []
    for e in GoldenCases.corpus {
        pairList.append((e.input, e.expected, e.options))
        pairList.append((e.input, try cleaner.clean(e.input, options: e.options), e.options))
        pairList.append((e.input, e.input, e.options))
    }
    for (raw, out) in GoldenCases.checkPairs { pairList.append((raw, out, nil)) }
    for s in GoldenCases.edges { pairList.append((s, s, nil)); pairList.append((s, try cleaner.clean(s, options: .standard), nil)) }
    var seen = Set<String>()
    var pairs: [String] = []
    for (raw, out, own) in pairList {
        let key = GoldenCases.json([raw, out, own.map(GoldenCases.encode) ?? ""])
        guard seen.insert(key).inserted else { continue }
        let v = GoldenCases.optionSets.map { verdict(CleanupGuard.check(raw: raw, cleaned: out, options: $0.1)) }.joined(separator: ",")
        let trusted = GoldenCases.trustedSets.map { outcome(CleanupPipeline.judge(raw: raw, cleaned: out, options: $0.1, allowedTerms: []), out) }.joined(separator: "|")
        pairs.append(GoldenCases.json(PairCase(
            raw: raw, out: out, own: own.map(GoldenCases.encode), ownVerdict: own.map { verdict(CleanupGuard.check(raw: raw, cleaned: out, options: $0)) },
            v: v, truncated: CleanupGuard.truncated(raw: raw, cleaned: out), answers: CleanupGuard.answers(raw: raw, cleaned: out),
            transform: TransformGuard.accepts(input: raw, output: out), punctuated: CleanupPipeline.punctuated(raw: raw, cleaned: out), trusted: trusted)))
    }

    let mutations = GoldenCases.mutations.map { m -> String in
        var polish = m.options; polish.polish = true
        var professional = polish; professional.professional = true
        var spoken = m.options; spoken.spoken = true
        let v = [m.options, polish, professional, spoken].map { verdict(CleanupGuard.check(raw: m.raw, cleaned: m.output, options: $0)) }.joined(separator: ",")
        return GoldenCases.json(MutationCase(label: m.label, raw: m.raw, out: m.output, options: GoldenCases.encode(m.options), v: v))
    }

    let termCases: [(String, String, [String])] = [
        ("Email acme about the order.", "Email Akme about the order.", ["Akme"]),
        ("Shelf made the report concise.", "Shelf made the Shelfmate concise.", ["Shelfmate"]),
        ("Send the report now.", "Send the Shelfmate report now.", ["Shelfmate"]),
        ("Ship cloud dev today.", "Ship clouddev today.", ["clouddev"]),
        ("Send a shelf made report.", "Send a Shelfmate report.", ["Shelfmate"]),
        ("Call John and Akme today", "Call on and Akme today.", ["Akme"]),
        ("send the notes to shreia before noon", "Send the notes to Shreya before noon.", ["Shreya"]),
        ("tell zoe and ægir about it", "Tell Zoë and Ægir about it.", ["Zoë", "Ægir"]),
        ("ask kubernetes team about cooper netties", "Ask Kubernetes team about Kubernetes.", ["Kubernetes"]),
        ("meet priya at the café", "Meet Shreya at the café.", ["Shreya"]),
        ("the 東京 office", "The 東京 office.", ["東京"]),
        ("Pay dana 20 dollars", "Pay Dana 20 dollars.", ["Dana", "Dan\nMaria", ""]),
    ]
    let terms = termCases.map { c -> String in
        func verdicts(_ sounds: Bool) -> String {
            GoldenCases.optionSets.map { verdict(CleanupGuard.check(raw: c.0, cleaned: c.1, options: $0.1, allowedTerms: c.2, soundsLike: sounds ? GoldenCases.soundsLike : { _, _ in false })) }.joined(separator: ",")
        }
        return GoldenCases.json(TermCase(raw: c.0, out: c.1, terms: c.2, v: verdicts(false), sounds: verdicts(true)))
    }

    let wordPairs = [("increase", "decrease"), ("include", "exclude"), ("like", "dislike"), ("plan", "plant"), ("could", "should"), ("him", "he"), ("me", "you"),
                     ("must", "much"), ("shreia", "shreya"), ("approve", "disapprove"), ("upload", "download"), ("overflow", "underflow"), ("café", "cafe"),
                     ("zoë", "zoe\u{301}"), ("act", "react"), ("it", "its"), ("abc", "xyz"), ("", "")].map {
        GoldenCases.json(WordPair(a: $0.0, b: $0.1, flip: CleanupGuard.polarityFlip($0.0, $0.1), swap: CleanupGuard.polishSwap($0.0, $0.1)))
    }

    GoldenCases.emit([("optionSets", GoldenCases.optionSets.map { GoldenCases.json([$0.0, GoldenCases.encode($0.1)]) }),
                      ("trustedSets", GoldenCases.trustedSets.map { GoldenCases.json([$0.0, GoldenCases.encode($0.1)]) }),
                      ("wordPairs", wordPairs), ("terms", terms), ("text", text), ("pairs", pairs), ("mutations", mutations)])
}}
