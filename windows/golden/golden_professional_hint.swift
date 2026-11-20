// deps: ProfessionalHint
import Foundation

// Golden outputs of ProfessionalSignal over synthetic transcripts: which reasons fire (in order) and the offer sentence. Run from the repository root.
@main struct Gen {
    static func main() throws {
        let filler = String(repeating: "and then we talked about the plan ", count: 6)
        let texts = [
            filler + "no I mean the other plan", "Let's meet Thursday at the cafe.", filler, filler + filler, "First we ship. Second we test. Finally we write up.",
            "First we ship. Second we test. Scratch that, we write up.", "um so uh the thing um is ready no wait it is not", "", "   ", "no wait. Actually, I meant it. Hold on—stop",
            "Um, uh, er, like first and then second", "I don't think it's ready, I mean it isn't.", String(repeating: "word ", count: 34) + "word. I mean it", String(repeating: "word ", count: 35) + "I mean it",
            String(repeating: "Zoë 東京 ", count: 40) + "scratch that", String(repeating: "stop! ", count: 80), String(repeating: "a ", count: 70),
        ]
        let reasonNames: [ProfessionalSignal.Reason: String] = [.runOn: "runOn", .correction: "correction", .list: "list", .fillers: "fillers"]
        let cases: [[String: Any]] = texts.map { t in
            let r = ProfessionalSignal.reasons(t)
            return ["text": t, "reasons": r.map { reasonNames[$0]! }, "sentence": ProfessionalSignal.sentence(r), "longestRun": ProfessionalSignal.longestRun(t)]
        }
        let all: [ProfessionalSignal.Reason] = [.runOn, .correction, .list, .fillers]
        let sentences: [[String: Any]] = all.flatMap { a in all.map { b in [a, b] } }.map { rs in ["reasons": rs.map { reasonNames[$0]! }, "sentence": ProfessionalSignal.sentence(rs)] }
        let data = try JSONSerialization.data(withJSONObject: ["signals": cases, "sentences": sentences], options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
