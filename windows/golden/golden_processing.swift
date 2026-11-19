// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup CleanupPrompt PrivateFiles Dictionary Learning VoiceCues Retention Settings Credentials ProviderTypes Processing WritingModes ../windows/golden/cleanup_cases
import Foundation

// Golden outcomes of ProcessingCoordinator over every app/cleanup-corpus.json entry, one case per line. Each outcome is
// "PATH|FALLBACK|VERDICT|TEXT" (raw values, "-" for none, "=" for text equal to the input). Run from the repository root.
// - "local": process(input, style: DictationStyle(options:)) on the local route for each GoldenCases.optionSets + trustedSets entry,
//   and "ownLocal" with the entry's own options.
// - one array per mock provider ("echo", "period", "answer", "number", "empty", "fail"): processAsync on the cloud route with the
//   same option sets. The mocks (GoldenMock below) are ASCII-only transforms of the transcript between its tags, mirrored in
//   windows/tests/Murmur.Core.Tests/ProcessingGoldenTests.cs; keep the two identical.
// - "context": synthetic dictations with screen context and a mock that appends the context's name, on trusted and strict polish
//   (spec/pipeline.md §9: trusted routes do not guard screen-context words). Adds whether context was used and a rejected output kept.
// - "promptHashes": ProcessingCoordinator.promptHash of the cleanup prompt for each option set.

final class GoldenMock: CleanupProvider, @unchecked Sendable {
    let id = ProviderID.customAPI
    let kind: String
    init(_ kind: String) { self.kind = kind }
    var disclosure: DataDisclosure { DataDisclosure(provider: id, stage: .cleanup, destinationHost: "example.invalid", leavesDevice: ["transcript"], staysOnDevice: ["audio"], retentionNote: "golden") }
    func cleanup(_ messages: CleanupMessages) async throws -> String {
        let body = messages.user.components(separatedBy: "\n").drop { !$0.hasPrefix("<transcript-") }.dropFirst().prefix { !$0.hasPrefix("</transcript-") }.joined(separator: "\n")
        switch kind {
        case "echo": return body
        case "period":
            // Punctuation only: an ASCII lowercase first letter capitalized, a final period added when none of . ? ! ends it.
            var out = body
            if let first = out.unicodeScalars.first, ("a"..."z").contains(first) { out = first.properties.uppercaseMapping + String(out.unicodeScalars.dropFirst()) }
            if let last = out.unicodeScalars.last, ".?!".unicodeScalars.contains(last) { return out }
            return out + "."
        case "answer": return "Sure! " + body
        case "number": return body + " 17"
        case "empty": return ""
        case "copy": return body + " Ayesha Khan"
        default: throw ProviderError.network(.customAPI)
        }
    }
    func validate() async throws {}
}

func encode(_ r: ProcessingResult, _ input: String) -> String {
    guard case .insert(let text) = r.outcome else { return "held" }
    let same = text.unicodeScalars.elementsEqual(input.unicodeScalars)
    return "\(r.cleanupPath?.rawValue ?? "-")|\(r.fallback?.rawValue ?? "-")|\(r.guardVerdict ?? "-")|\(same ? "=" : text)"
}

@main struct Gen {
    static func main() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-golden-processing-\(UUID())")
        let suite = "murmur-golden-processing-\(UUID())"
        let defaults = UserDefaults(suiteName: suite)!
        defer { try? FileManager.default.removeItem(at: root); defaults.removePersistentDomain(forName: suite) }
        let p = try ProcessingCoordinator(directory: root, defaults: defaults)
        let sets = (GoldenCases.optionSets + GoldenCases.trustedSets).map(\.1)
        let mocks = ["echo", "period", "answer", "number", "empty", "fail"]
        var local = p.settings; local.route = .local
        var cloud = p.settings; cloud.route = .cloud

        var corpus: [String] = []
        for e in GoldenCases.corpus {
            try p.update(local, persist: false)
            var line = "{\"id\":\(GoldenCases.json(e.id)),\"input\":\(GoldenCases.json(e.input)),\"own\":\(GoldenCases.json(GoldenCases.encode(e.options)))"
            line += ",\"ownLocal\":\(GoldenCases.json(encode(p.process(e.input, style: DictationStyle(options: e.options)), e.input)))"
            line += ",\"local\":\(GoldenCases.json(sets.map { encode(p.process(e.input, style: DictationStyle(options: $0)), e.input) }))"
            try p.update(cloud, persist: false)
            for kind in mocks {
                p.cloudCleaner = GoldenMock(kind)
                var outcomes: [String] = []
                for options in sets { outcomes.append(encode(await p.processAsync(e.input, style: DictationStyle(options: options)), e.input)) }
                line += ",\"\(kind)\":\(GoldenCases.json(outcomes))"
            }
            corpus.append(line + "}")
        }

        struct ContextCase: Encodable { var input: String; var trusted: String; var strict: String }
        let polish = CleanupOptions(punctuation: true, polish: true, spoken: true)
        var contextTrusted = polish; contextTrusted.trustModel = true
        let screen = ScreenContext(selected: "", clipboard: "Ayesha Khan", window: "")
        p.cloudCleaner = GoldenMock("copy")
        try p.update(cloud, persist: false)
        var context: [String] = []
        for input in ["send the deck to the client", "um so we ship it on friday", "Please review the plan with the team.", "call her back tomorrow at nine"] {
            func run(_ options: CleanupOptions) async -> String {
                let r = await p.processAsync(input, style: DictationStyle(options: options, context: screen))
                return encode(r, input) + "|\(r.contextUsed)|\(r.rejectedOutput != nil)"
            }
            context.append(GoldenCases.json(ContextCase(input: input, trusted: await run(contextTrusted), strict: await run(polish))))
        }

        let hashes = sets.map { GoldenCases.json(ProcessingCoordinator.promptHash(CleanupPrompt.build(transcript: "x", options: $0).system)) }
        GoldenCases.emit([("corpus", corpus), ("context", context), ("promptHashes", hashes)])
    }
}
