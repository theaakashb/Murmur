// deps: WordDiff
import Foundation

// Golden outputs of WordDiff over synthetic strings and a seeded run of random word lists (which pins how ties are broken): the display
// runs, the change count and the revert text. Run from the repository root.
@main struct Gen {
    static func main() throws {
        var pairs: [(String, String)] = [
            ("um so the meeting is at 3 pm", "So the meeting is at 3 PM."), ("same words", "same words"), ("hello world", "Hello, world"), ("", ""), ("", "new words"), ("gone words", ""),
            ("see the doc", "see the doc."), ("ok knock it", "ok Knock it"), ("hello, there", "hello there"), ("on tuesday we", "on Tuesday. We"),
            ("um so the", "So the"), ("see teh doc", "see the doc."), ("a  b\n\tc", "a b c"), ("Zoë 😀 café", "zoë 😀 Café"), ("e\u{301}x", "\u{E9}x!"),
            ("ship it to cube control", "ship it to kubectl"), ("...", "!!!"), ("it's 5 o'clock", "It's 5 o’clock."),
        ]
        var seed: UInt64 = 20_260_901
        func next(_ n: Int) -> Int { seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407; return Int((seed >> 33) % UInt64(n)) }
        let vocab = ["a", "A", "b", "b,", "the", "The", "doc", "doc.", "um", "x1"]
        for _ in 0..<150 {
            let a = (0..<next(9)).map { _ in vocab[next(vocab.count)] }, b = (0..<next(9)).map { _ in vocab[next(vocab.count)] }
            pairs.append((a.joined(separator: " "), b.joined(separator: " ")))
        }
        let kinds: [WordDiff.Kind: String] = [.same: "same", .removed: "removed", .added: "added", .tweaked: "tweaked"]
        let runs: [[String: Any]] = pairs.map { raw, final in
            ["raw": raw, "final": final, "changes": WordDiff.changeCount(raw: raw, final: final),
             "runs": WordDiff.runs(raw: raw, final: final).map { ["kind": kinds[$0.kind]!, "text": $0.text, "marks": $0.marks] as [String: Any] }]
        }
        let reverts: [[String: String]] = [("hi there", " Hi there."), ("hi there", "Hi there."), (" hi", " Hi"), ("", " x"), ("é", " É")].map {
            ["raw": $0.0, "inserted": $0.1, "text": WordDiff.revertText(raw: $0.0, inserted: $0.1)]
        }
        let data = try JSONSerialization.data(withJSONObject: ["runs": runs, "reverts": reverts], options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
