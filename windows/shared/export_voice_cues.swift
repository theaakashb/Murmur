// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Exports the voice-cue command words, closing punctuation and limits. Sets are sorted arrays.
struct Export: Encodable {
    var fileName: String, maxPhraseLength: Int, maxTextLength: Int
    var leadWords: [String], reserved: [String], closing: [String]
}

@main struct Gen {
    static func main() throws {
        let export = Export(fileName: VoiceCueStore.fileName, maxPhraseLength: VoiceCueStore.maxPhraseLength, maxTextLength: VoiceCueStore.maxTextLength,
                            leadWords: VoiceCueText.leadWords.sorted(), reserved: VoiceCueText.reserved.sorted(), closing: VoiceCueText.closing.map { String($0) }.sorted())
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
