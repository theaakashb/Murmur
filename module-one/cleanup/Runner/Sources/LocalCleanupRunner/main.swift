import Foundation
import MLXLLM
import MLXLMCommon

// Managed local cleanup helper: one JSON object per line on stdin, one reply per line on stdout.
//   {"id":1,"text":"...","tone":"formal"|"casual","terms":["..."]}  ->  {"id":1,"text":"..."} | {"id":1,"error":"too-long"|"failed"}
// No networking and no transcript logging. A fresh chat session per request, so nothing carries over.
// argv[1] is the verified model directory. The app owns cancellation by terminating this process.

let maximumInputTokens = 400   // Input limit, counted over the whole prompt: system line, header and text.
let maximumOutputTokens = 512
let system = "Clean up this dictation."

func send(_ object: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) else { return }
    FileHandle.standardOutput.write(data + Data("\n".utf8))
}

func header(tone: String, terms: [String]) -> String {
    var lines: [String] = []
    if tone == "formal" { lines.append("[tone: formal]") }
    let vocab = terms.prefix(12).map { $0.replacingOccurrences(of: ";", with: ",").replacingOccurrences(of: "\n", with: " ") }
    if !vocab.isEmpty { lines.append("[vocab: " + vocab.joined(separator: "; ") + "]") }
    return lines.isEmpty ? "" : lines.joined(separator: "\n") + "\n"
}

@main enum Main {
    static func main() async {
        guard CommandLine.arguments.count > 1 else { exit(2) }
        replacementTokenizers["Qwen3_5Tokenizer"] = "PreTrainedTokenizer"  // Same BPE as the upstream Qwen2/Qwen3 mapping.
        let model: ModelContainer
        do { model = try await LLMModelFactory.shared.loadContainer(configuration: .init(directory: URL(fileURLWithPath: CommandLine.arguments[1]))) }
        catch { send(["ready": false]); exit(3) }
        send(["ready": true])
        while let line = readLine(strippingNewline: true) {
            guard let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  let id = object["id"] as? Int, let text = object["text"] as? String else { send(["id": -1, "error": "failed"]); continue }
            let prompt = header(tone: object["tone"] as? String ?? "casual", terms: object["terms"] as? [String] ?? []) + text
            let tokens = await model.perform { context in context.tokenizer.encode(text: system + "\n" + prompt).count }
            guard tokens <= maximumInputTokens else { send(["id": id, "error": "too-long"]); continue }
            let session = ChatSession(model, instructions: system, generateParameters: .init(maxTokens: maximumOutputTokens, temperature: 0),
                                      additionalContext: ["enable_thinking": false])
            do { send(["id": id, "text": try await session.respond(to: prompt)]) } catch { send(["id": id, "error": "failed"]) }
        }
    }
}
