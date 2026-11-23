// deps: AudioFormat Capture ModelHelper ModelFiles ModelManifest ModelDownload ModelText Digests Retention HistoryRouter DictationLanguages
import Foundation
import os

// ModelHelper normally gets the logger from the GUI controller.
let timingLog = Logger(subsystem: "cc.clouddev.dictation", category: "chunker-golden")

@main struct GoldenChunker {
    static func main() throws {
        let joins: [([String], [Bool])] = [
            (["Hello there", "General Kenobi."], [true, false]),
            (["we were going", "I think to the store."], [false, false]),
            (["Done.", " ", "Next one?", "Yes"], [true, true, true, false]),
            (["It works.", "The next one."], [false, false]),
            (["appears in the.", "Windows and doors."], [true, false]),
            (["to value", "The mysterious qualities"], [false, false]),
            (["It works.", "The next one."], [true, false]),
            (["It works.", "Finally done."], [false, false]),
            (["I think so.", "The plan works."], [true, false]),
            (["Wait...", "So what?"], [false, false]),
        ]
        let repairs = [
            ["Route calls through open.", "Router, then deploy.", "open router", "OpenRouter"],
            ["Deploy it to Cooper.", "Netties today.", "coopernetties", "Kubernetes"],
            ["Ask about it.", "Then go.", "open router", "OpenRouter"],
            ["", "open router", "open router", "OpenRouter"],
        ]
        let cuts: [([Double], Int)] = [
            ((0..<200).map { (50...52).contains($0) || (130...132).contains($0) ? 0 : 1 }, 1),
            (Array(repeating: 1, count: 200), 1),
        ]
        let pauses = [10, 2].map { (cut: $0, energies: (0..<20).map { (8...12).contains($0) ? 0.0 : 1.0 }) }
        let data: [String: [[String: Any]]] = [
            "joins": joins.map { ["texts": $0.0, "sentenceEnds": $0.1, "expected": Chunker.join($0.0, sentenceEnds: $0.1)] },
            "repairs": repairs.map { words -> [String: Any] in
                let found = Chunker.repair(tail: words[0], head: words[1], from: words[2], to: words[3])
                return ["words": words, "expected": found.map { [$0.tail, $0.head] } as Any? ?? NSNull()]
            },
            "cuts": cuts.map { energies, frames in
                ["energies": energies, "frames": frames, "expected": Chunker.quietestCut(energies.map { (frames, $0) }, rate: 10)]
            },
            "pauses": pauses.map { cut, energies in
                ["cut": cut, "energies": energies, "expected": Chunker.pauseLength(energies.map { (10, $0) }, at: cut, rate: 10)]
            },
        ]
        print("{")
        let groups = try data.keys.sorted().map { key in
            let rows = try data[key]!.map { row in
                String(decoding: try JSONSerialization.data(withJSONObject: row, options: [.sortedKeys]), as: UTF8.self)
            }
            return "  \"\(key)\": [\n    " + rows.joined(separator: ",\n    ") + "\n  ]"
        }
        print(groups.joined(separator: ",\n"))
        print("}")
    }
}
