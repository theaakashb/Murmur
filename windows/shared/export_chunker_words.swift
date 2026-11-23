// deps: AudioFormat Capture ModelHelper ModelFiles ModelManifest ModelDownload ModelText Digests Retention HistoryRouter DictationLanguages
import Foundation
import os

let timingLog = Logger(subsystem: "cc.clouddev.dictation", category: "chunker-export")

@main struct ExportChunkerWords {
    static func main() throws {
        let words = ["danglingWords": Chunker.danglingWords.sorted(), "lowercaseStarts": Chunker.lowercaseStarts.sorted()]
        let json = try JSONSerialization.data(withJSONObject: words, options: [.sortedKeys, .prettyPrinted])
        print(String(decoding: json, as: UTF8.self))
    }
}
