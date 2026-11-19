// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner EditPrompt
import Foundation

@main struct Gen {
    static func main() throws {
        let prompt = EditPrompt.build(selection: "\u{E001}", instruction: "\u{E002}", nonce: "QZXNONCE")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode([
            "system": prompt.system.replacingOccurrences(of: "QZXNONCE", with: "{nonce}"),
            "user": prompt.user.replacingOccurrences(of: "QZXNONCE", with: "{nonce}")
                .replacingOccurrences(of: "\u{E001}", with: "{selection}").replacingOccurrences(of: "\u{E002}", with: "{instruction}")
        ])
        print(String(decoding: data, as: UTF8.self))
    }
}
