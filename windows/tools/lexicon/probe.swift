// Usage: swiftc -O probe.swift -o probe && ./probe words.txt > labels.tsv
// Asks NSSpellChecker the way app/Controller+Dictionary.swift does (language "en"), for each word as written
// and lowercased. Output: word<TAB>asWritten(1=common)<TAB>lowercased(1=common).
import AppKit

func common(_ w: String) -> Int {
    NSSpellChecker.shared.checkSpelling(of: w, startingAt: 0, language: "en", wrap: false, inSpellDocumentWithTag: 0, wordCount: nil).location == NSNotFound ? 1 : 0
}

let words = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8).split(separator: "\n").map(String.init)
for w in words { print("\(w)\t\(common(w))\t\(common(w.lowercased()))") }
