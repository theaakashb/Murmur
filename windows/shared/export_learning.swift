// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Exports the constant tables and thresholds of CorrectionFilter and the ledger file name. Sets are sorted arrays.
// CorrectionFilter.inflectionSuffixes is private to Learning.swift, so it is read from that source line (never hand-copied);
// making it internal in a Mac change would let this generator read it directly.
struct Export: Encodable {
    var maxWords: Int, minLetters: Int, minLettersWithLexicon: Int
    var directSimilarity: Double, phoneticSimilarity: Double
    var negations: [String], negatingPrefixes: [String], numberWords: [String], contextualWords: [String]
    var antonyms: [[String]], inflectionSuffixes: [String]
    var ledgerFileName: String
}

func inflectionSuffixesFromSource() throws -> [String] {
    let source = try String(contentsOfFile: "app/Learning.swift", encoding: .utf8)
    guard let line = source.split(separator: "\n").first(where: { $0.contains("static let inflectionSuffixes") }),
          let open = line.firstIndex(of: "[") else { fatalError("inflectionSuffixes not found in app/Learning.swift") }
    let list = String(line[open...])
    let found = try NSRegularExpression(pattern: "\"([^\"]*)\"").matches(in: list, range: NSRange(list.startIndex..., in: list))
        .map { String(list[Range($0.range(at: 1), in: list)!]) }
    precondition(!found.isEmpty)
    return found
}

@main struct Gen {
    static func main() throws {
        let export = Export(
            maxWords: CorrectionFilter.maxWords, minLetters: CorrectionFilter.minLetters, minLettersWithLexicon: CorrectionFilter.minLettersWithLexicon,
            directSimilarity: CorrectionFilter.directSimilarity, phoneticSimilarity: CorrectionFilter.phoneticSimilarity,
            negations: CorrectionFilter.negations.sorted(), negatingPrefixes: CorrectionFilter.negatingPrefixes,
            numberWords: CorrectionFilter.numberWords.sorted(), contextualWords: CorrectionFilter.contextualWords.sorted(),
            antonyms: CorrectionFilter.antonyms.map { [$0.0, $0.1] }, inflectionSuffixes: try inflectionSuffixesFromSource(),
            ledgerFileName: CorrectionLedger.fileName)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
