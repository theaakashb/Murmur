// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention
import Foundation

// Exports RuleCleaner.markCommands and RuleCleaner.atPhrases, which both the rule cleaner and the dictionary use.
struct Command: Encodable { var words: [String]; var mark: String }
struct Export: Encodable { var markCommands: [Command]; var atPhrases: [[String]] }

@main struct Gen {
    static func main() throws {
        let export = Export(markCommands: RuleCleaner.markCommands.map { Command(words: $0.0, mark: $0.1) }, atPhrases: RuleCleaner.atPhrases)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
