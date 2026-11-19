// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Exports the dictionary's limits, file name and spoken-command targets (derived on the Mac from RuleCleaner.markCommands and atPhrases).
struct Export: Encodable {
    var fileName: String, maxVariantWords: Int, maxLength: Int, maxNearMatches: Int
    var commandPhrases: [String]
}

@main struct Gen {
    static func main() throws {
        let export = Export(fileName: DictionaryStore.fileName, maxVariantWords: DictionaryStore.maxVariantWords, maxLength: DictionaryStore.maxLength,
                            maxNearMatches: DictionaryStore.maxNearMatches, commandPhrases: DictionaryStore.commandPhrases.sorted())
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
