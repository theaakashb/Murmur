// deps: ../module-one/models/Runner/Sources/ModelBench/BoostFilter
import Foundation

// Exports the transcription helper's unknown-token piece.
struct Export: Encodable { var unknown: String }

@main struct Gen {
    static func main() throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(Export(unknown: BoostFilter.unknown)), as: UTF8.self))
    }
}
