// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup PrivateFiles Dictionary Learning Retention ../windows/golden/cleanup_cases
import Foundation

// Golden outputs of CleanupPipeline.process with RuleCleaner and of CleanupSections.split over GoldenCases.texts
// (judge and punctuated over pairs are in cleanup_guard.json). Outcomes: "u" unchanged (the input), "c=" cleaned to the input,
// "c:TEXT" cleaned, "h:REASON" held. "process" has one outcome per GoldenCases.optionSets entry, joined by "|"; corpus inputs also
// with their own options. "sections": split() with target 10 (plain and keeping corrections attached) and the default target,
// left out when the text stays whole. One case per line. Run from the repository root.
struct ProcessCase: Encodable { var input: String; var outcomes: [String]; var own: String?, ownOutcome: String? }
struct SectionCase: Encodable { var input: String; var ten: [String]?, tenAttached: [String]?, standard: [String]?, target150Attached: [String]? }

func encodeOutcome(_ o: CleanupOutcome, _ input: String) -> String {
    switch o {
    case .unchanged(let t): return t.unicodeScalars.elementsEqual(input.unicodeScalars) ? "u" : "u:" + t
    case .cleaned(let t): return t.unicodeScalars.elementsEqual(input.unicodeScalars) ? "c=" : "c:" + t
    case .heldForRecovery(_, let r): return "h:" + r.rawValue
    }
}

@main struct Gen { static func main() throws {
    let own = Dictionary(GoldenCases.corpus.map { ($0.input, $0.options) }, uniquingKeysWith: { a, _ in a })
    var process: [String] = [], sections: [String] = []
    for s in GoldenCases.texts {
        let outcomes = GoldenCases.optionSets.map { encodeOutcome(CleanupPipeline.process(raw: s, options: $0.1, cleaner: RuleCleaner()), s) }
        let o = own[s]
        process.append(GoldenCases.json(ProcessCase(input: s, outcomes: outcomes, own: o.map(GoldenCases.encode),
                                                    ownOutcome: o.map { encodeOutcome(CleanupPipeline.process(raw: s, options: $0, cleaner: RuleCleaner()), s) })))
        func split(_ target: Int?, _ attached: Bool) -> [String]? {
            let r = target.map { CleanupSections.split(s, target: $0, keepingCorrectionsAttached: attached) } ?? CleanupSections.split(s, keepingCorrectionsAttached: attached)
            return r.count == 1 && r[0].unicodeScalars.elementsEqual(s.unicodeScalars) ? nil : r
        }
        let c = SectionCase(input: s, ten: split(10, false), tenAttached: split(10, true), standard: split(nil, false), target150Attached: split(150, true))
        if c.ten != nil || c.tenAttached != nil || c.standard != nil || c.target150Attached != nil { sections.append(GoldenCases.json(c)) }
    }
    // Every corpus entry with its own options, by id (some inputs appear with more than one option set).
    struct CorpusCase: Encodable { var id: String; var input: String; var options: String; var outcome: String }
    let corpus = GoldenCases.corpus.map { e in
        GoldenCases.json(CorpusCase(id: e.id, input: e.input, options: GoldenCases.encode(e.options),
                                    outcome: encodeOutcome(CleanupPipeline.process(raw: e.input, options: e.options, cleaner: RuleCleaner()), e.input)))
    }
    GoldenCases.emit([("process", process), ("sections", sections), ("corpus", corpus)])
}}
