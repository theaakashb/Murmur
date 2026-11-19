// deps: ProfessionalHint
import Foundation

// The signal's word lists are private. Read their Swift source declaration, as the Learning export does.
@main struct Gen {
    static func main() throws {
        let source = try String(contentsOfFile: "app/ProfessionalHint.swift", encoding: .utf8)
        var lists: [String: [String]] = [:]
        for name in ["correctionCues", "listWords", "fillers"] {
            guard let line = source.split(separator: "\n").first(where: { $0.contains("static let \(name)") }),
                  let body = line.components(separatedBy: " = ").last else { fatalError("Missing \(name)") }
            lists[name] = try JSONDecoder().decode([String].self, from: Data(body.utf8))
        }
        var data = try JSONSerialization.jsonObject(with: JSONEncoder().encode(lists)) as! [String: Any]
        data["sentenceParts"] = Dictionary(uniqueKeysWithValues:
            [ProfessionalSignal.Reason.runOn, .correction, .list, .fillers].map { reason in
                let sentence = ProfessionalSignal.sentence([reason])
                precondition(sentence.hasPrefix("Professional would ") && sentence.hasSuffix("."))
                return (reason.rawValue, String(sentence.dropFirst("Professional would ".count).dropLast()))
            })
        let encoded = try JSONSerialization.data(withJSONObject: data, options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: encoded, as: UTF8.self))
    }
}
