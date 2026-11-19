// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup PrivateFiles Dictionary Learning Retention ../windows/golden/cleanup_cases
import Foundation

// Golden outputs of RuleCleaner over GoldenCases.texts: each rule function and clean() with every option set.
// A key is left out when the output equals the input (emailLayout: when it is nil). One case per line. Run from the repository root.
struct TextCase: Encodable {
    var input: String
    var spoken, fillers, corrections, merged, instead, comma, list, capitalized, email: String?
    var clean: [String: String]?
}
struct CorpusCase: Encodable { var id: String, input: String, options: String, clean: String }
struct GroupCase: Encodable { var value: Int, western: String, indian: String }

@main struct Gen { static func main() throws {
    func changed(_ input: String, _ output: String) -> String? { output.unicodeScalars.elementsEqual(input.unicodeScalars) ? nil : output }
    let cleaner = RuleCleaner()
    var text: [String] = []
    for s in GoldenCases.texts {
        var clean: [String: String] = [:]
        for (name, o) in GoldenCases.optionSets { if let c = changed(s, try cleaner.clean(s, options: o)) { clean[name] = c } }
        text.append(GoldenCases.json(TextCase(
            input: s, spoken: changed(s, RuleCleaner.spokenCommands(s)), fillers: changed(s, RuleCleaner.removeFillers(s)),
            corrections: changed(s, RuleCleaner.applyCorrections(s)), merged: changed(s, RuleCleaner.mergeSplitCues(s)),
            instead: changed(s, RuleCleaner.applyInsteadCorrections(s)), comma: changed(s, RuleCleaner.applyCommaCorrections(s)),
            list: changed(s, RuleCleaner.formatList(s)), capitalized: changed(s, RuleCleaner.capitalizeSentences(s)),
            email: RuleCleaner.emailLayout(s), clean: clean.isEmpty ? nil : clean)))
    }
    let corpus = GoldenCases.corpus.map { GoldenCases.json(CorpusCase(id: $0.id, input: $0.input, options: GoldenCases.encode($0.options), clean: try! cleaner.clean($0.input, options: $0.options))) }
    let grouped = [0, 7, 999, 1_200, 9_999, 10_000, 99_999, 100_000, 250_000, 1_000_000, 12_345_678, 30_000_000, 2_000_000_000, 987_654_321_012].map {
        GoldenCases.json(GroupCase(value: $0, western: RuleCleaner.groupedDigits($0, indian: false), indian: RuleCleaner.groupedDigits($0, indian: true)))
    }
    GoldenCases.emit([("corpus", corpus), ("grouped", grouped), ("text", text)])
}}
