// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention
import Foundation

// Exports CleanupGuard's constant tables as the Swift side sees them, derived ones included (correctionCues,
// droppableGrammarWords, ...), so the C# port does not rebuild them. Sets are sorted arrays; ordered lists keep their order.
// grammarWords is in cleanup_text.json.
struct Export: Encodable {
    var assistantPhrases: [String], answerPhrases: [String]
    var ordinalPhraseNouns: [String], polishFillers: [String], polishFillerPairs: [[String]], meaningfulLikeAfter: [String]
    var droppableGrammarWords: [String], modals: [String], persons: [[String]]
    var correctionCues: [[String]], looseCorrectionCues: [[String]], commaRequiredCues: [[String]]
    var declarativeAfterCue: [String], noQuantified: [String], explicitRepairCues: [[String]], sentenceRepairCues: [[String]]
}

@main struct Gen { static func main() throws {
    func pairs(_ s: Set<[String]>) -> [[String]] { s.sorted { $0.joined(separator: " ") < $1.joined(separator: " ") } }
    let e = Export(
        assistantPhrases: CleanupGuard.assistantPhrases, answerPhrases: CleanupGuard.answerPhrases,
        ordinalPhraseNouns: CleanupGuard.ordinalPhraseNouns.sorted(), polishFillers: CleanupGuard.polishFillers.sorted(),
        polishFillerPairs: pairs(CleanupGuard.polishFillerPairs), meaningfulLikeAfter: CleanupGuard.meaningfulLikeAfter.sorted(),
        droppableGrammarWords: CleanupGuard.droppableGrammarWords.sorted(), modals: CleanupGuard.modals.sorted(),
        persons: CleanupGuard.persons.map { $0.sorted() },
        correctionCues: CleanupGuard.correctionCues, looseCorrectionCues: CleanupGuard.looseCorrectionCues,
        commaRequiredCues: pairs(CleanupGuard.commaRequiredCues),
        declarativeAfterCue: CleanupGuard.declarativeAfterCue.sorted(), noQuantified: CleanupGuard.noQuantified.sorted(),
        explicitRepairCues: CleanupGuard.explicitRepairCues, sentenceRepairCues: CleanupGuard.sentenceRepairCues)
    let enc = JSONEncoder()
    enc.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
    print(String(decoding: try enc.encode(e), as: UTF8.self))
}}
