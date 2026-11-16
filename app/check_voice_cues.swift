import Foundation

// Voice cues: matching, joining and the store. Foundation only. The processing-level guarantees
// (no provider sees cue text, the guard, tone and dictionary leave it alone) are in check_processing.swift.

private var failures = 0
private var passes = 0
private func check(_ condition: @autoclosure () throws -> Bool, _ label: String, line: Int = #line) rethrows {
    if try condition() { passes += 1 } else { failures += 1; print("FAIL [line \(line)]: \(label)") }
}

private let url = "https://example.com/in/sample?a=b&c=d", email = "sam@example.com", address = "1 Sample St\nTown, ST 00000", work = "sam@work.example.com"
private func cue(_ phrase: String, _ text: String) -> VoiceCue { VoiceCue(id: UUID(), phrase: phrase, text: text, createdAt: Date(timeIntervalSince1970: 0)) }
private let library = [cue("my LinkedIn", url), cue("my email", email), cue("my email address", work), cue("my address", address),
                       cue("Sam's phone", "555-0100"), cue("my signature", "Thanks,\nSam\n")]

/// What ordinary cleanup does to a stretch of words: a capital and a closing period.
private func clean(_ text: String) -> String {
    guard let first = text.first else { return text }
    let sentence = first.uppercased() + text.dropFirst()
    return ".!?".contains(sentence.last!) ? sentence : sentence + "."
}

private func run(_ raw: String, cues: [VoiceCue] = library, keepsCase: (String) -> Bool = { _ in false }) -> String? {
    guard let pieces = VoiceCueText.split(raw, cues: cues) else { return nil }
    let processed = pieces.map { piece -> String in if case .text(let text) = piece { clean(text) } else { "" } }
    return VoiceCueText.join(pieces, processed: processed, keepsCase: keepsCase)
}

@main struct CueChecks {
    static func main() throws {
        matching(); joining(); try store()
        print("\(passes) checks passed, \(failures) failed")
        if failures > 0 { exit(1) }
    }

    static func matching() {
        // Rule 1: the whole dictation is the phrase.
        for raw in ["my LinkedIn", "  My LinkedIn! ", "MY LINKEDIN.", "my, linkedin?", "\"my LinkedIn\"", "insert my LinkedIn", "Paste my LinkedIn here."] {
            check(VoiceCueText.split(raw, cues: library) == [.expansion(url, closing: "")] || VoiceCueText.split(raw, cues: library) == [.expansion(url, closing: ".")], "whole phrase: \(raw)")
        }
        check(run("my LinkedIn") == url && run("My email address.") == work, "whole phrase gives the cue text alone")
        // Rule 2: commands anywhere, with or without "here".
        check(run("Reach me at insert my LinkedIn here if you have questions") == "Reach me at \(url) if you have questions.", "inline command with here: mid-sentence segment keeps lower case, no period before the command")
        check(run("Reach me at paste my email if you like") == "Reach me at \(email) if you like.", "paste, no here")
        check(run("INSERT MY LINKEDIN HERE") == url, "case ignored")
        check(run("insert my LinkedIn, here") == url && run("insert, my LinkedIn here") == url, "punctuation inside the command")
        check(run("Insert my email here.") == email + ".", "closing period stays after the expansion")
        check(run("insert my email here and insert my LinkedIn here") == "\(email) and \(url)", "two commands")
        check(run("insert my email here insert my email here") == "\(email) \(email)", "adjacent commands")
        // Rule 3: a bare phrase or a different lead word does not fire.
        for raw in ["my LinkedIn is down", "send my email address to Sam", "please insert the table here", "please update my LinkedIn",
                    "inserting my LinkedIn", "insert my", "insert. my LinkedIn", "I want to add my LinkedIn", "insert here", "my link"] {
            check(run(raw) == nil, "does not fire: \(raw)")
        }
        // Rule 4: apostrophes, possessives, curly quotes; the longest phrase wins.
        for raw in ["insert Sam's phone here", "insert Sams phone here", "insert Sam\u{2019}s phone here", "Sam's phone"] {
            check(run(raw) == "555-0100", "apostrophe variant: \(raw)")
        }
        check(run("insert my email address here") == work && run("insert my email here") == email, "longest phrase wins")
        check(run("insert my email addresses here") == "\(email) addresses here.", "a longer word is not the phrase")
        check(run("call me then insert my LinkedIn. Here we go") == "Call me then \(url). Here we go.", "a sentence end ends the command; Here after it is words")
        check(run("insert my LinkedIn here", cues: []) == nil, "no cues, no match")
        // Expansions are never matched again.
        check(run("insert my email here", cues: [cue("my email", "insert my LinkedIn here")]) == "insert my LinkedIn here", "no second pass")
    }

    static func joining() {
        check(run("Insert my email here and call me") == "\(email) and call me.", "starts with a command")
        check(run("Write to me at paste my email") == "Write to me at \(email)", "ends with a command, no period added")
        check(run("Here is my page insert my LinkedIn here. Thanks for reading") == "Here is my page \(url). Thanks for reading.", "capital kept after a sentence end")
        check(run("Email me at insert my email here, thanks") == "Email me at \(email), thanks.", "no space before a comma")
        check(run("Is it insert my email here?") == "Is it \(email)?", "no space before a question mark")
        check(run("insert my email here and insert my LinkedIn here and so on") == "\(email) and \(url) and so on.", "middle segment is not capitalized and keeps no period")
        check(run("Here is my page. insert my LinkedIn here") == "Here is my page. \(url)", "a period the speaker supplied stays")
        check(run("Please sign insert my signature here Next week") == "Please sign Thanks,\nSam\nNext week.", "no space after an expansion that ends a line; capital after a line break")
        check(run("insert my email here I will reply") == "\(email) I will reply.", "I is never lowered")
        check(run("insert my email here, i will reply") == "\(email), I will reply.", "cleaned I stays a capital")
        check(run("insert my email here If you can") == "\(email) If you can.", "a capital the speaker was heard to use stays")
        check(run("insert my email here akme said hi", keepsCase: { $0 == "akme" }) == "\(email) Akme said hi.", "a dictionary spelling keeps its capital")
        check(run("insert my email here akme said hi") == "\(email) akme said hi.", "otherwise the first word keeps its spoken case")
        check(run("send insert my address here please") == "Send \(address) please.", "multi-line expansion in a sentence")
        // Whitespace the dictation already carried.
        check(run("see  insert my email here  now") == "See \(email) now.", "extra spaces collapse around the command")
    }

    static func store() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-cues-\(UUID())")
        defer { try? FileManager.default.removeItem(at: dir) }
        let cues = VoiceCueStore(directory: dir)
        let added = try cues.add(phrase: "  my   LinkedIn ", text: url)
        check(added.phrase == "my LinkedIn" && cues.cues.count == 1, "phrase whitespace collapses")
        try cues.add(phrase: "my address", text: address + "\n\nwith  odd   spacing ")
        func rejects(_ phrase: String, _ text: String, _ label: String, line: Int = #line) {
            check((try? cues.add(phrase: phrase, text: text)) == nil, label, line: line)
        }
        rejects("", "x", "empty phrase"); rejects("1234", "x", "a phrase needs a letter"); rejects(String(repeating: "a", count: 61), "x", "phrase over 60")
        rejects("Insert", "x", "reserved insert"); rejects(" paste ", "x", "reserved paste"); rejects("Here!", "x", "reserved here")
        rejects("MY  linkedin!", "x", "duplicate after normalization"); rejects("my linked-in", "y", "hyphen does not make a new phrase")
        rejects("ok phrase", "", "empty text"); rejects("ok phrase", " \n ", "blank text"); rejects("ok phrase", String(repeating: "a", count: 4001), "text over 4000")
        check((try? cues.add(phrase: String(repeating: "a", count: 60), text: String(repeating: "b", count: 4000))) != nil, "limits are inclusive")
        check((try? cues.add(phrase: "insert something", text: "x")) != nil, "a phrase may contain a command word")

        let mode = { (url: URL) in (try? FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber)?.intValue }
        check(mode(dir) == 0o700 && mode(dir.appendingPathComponent(VoiceCueStore.fileName)) == 0o600, "owner-only directory and file")
        let again = VoiceCueStore(directory: dir)
        check(again.cues == cues.cues && !again.unreadable, "round trip")
        check(again.cues[1].text == address + "\n\nwith  odd   spacing ", "text stored verbatim")

        try again.update(id: added.id, phrase: "my LinkedIn page", text: "new")
        check(again.cues[0].phrase == "my LinkedIn page" && again.cues[0].createdAt == added.createdAt, "update keeps id and date")
        check((try? again.update(id: added.id, phrase: "my address", text: "x")) == nil, "update cannot take another phrase")
        check((try? again.update(id: UUID(), phrase: "zzz", text: "x")) == nil, "update of a missing cue fails")
        try again.delete(id: added.id)
        check(VoiceCueStore(directory: dir).cues.count == again.cues.count && !again.cues.contains { $0.id == added.id }, "delete persists")

        // A damaged file leaves the store empty and read-only, and is not overwritten.
        let file = dir.appendingPathComponent(VoiceCueStore.fileName)
        try Data("not json".utf8).write(to: file)
        let damaged = VoiceCueStore(directory: dir)
        check(damaged.unreadable && damaged.cues.isEmpty && (try? damaged.add(phrase: "x y", text: "z")) == nil, "damaged file: empty and read-only")
        try check(Data(contentsOf: file) == Data("not json".utf8), "damaged file untouched")
        // After Delete All Data the directory is gone: an empty store.
        try FileManager.default.removeItem(at: dir)
        check(VoiceCueStore(directory: dir).cues.isEmpty, "absent file is an empty store")
    }
}
