// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Golden outputs of VoiceCueText (words, split, join) and VoiceCueStore (validation and persistence sequences) over synthetic cues.
// Pieces are encoded ["t", rawText] or ["e", expansionText, closing]; join runs on the processed form of each text piece
// (a capital and a closing period, as ordinary cleanup would give), recorded in the case. One case per line. Run from the repository root.
struct WordsCase: Encodable { var input: String, words: [String] }
struct CueSpec: Encodable { var phrase: String, text: String }
struct SplitCase: Encodable { var cues: [CueSpec], raw: String, keeps: [String], pieces: [[String]]?, processed: [String]?, joined: String? }
struct StoreCase: Encodable { var label: String, ops: [[String]], results: [String], states: [String] }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

func clean(_ text: String) -> String {
    guard let first = text.first else { return text }
    let sentence = first.uppercased() + text.dropFirst()
    return ".!?".contains(sentence.last!) ? sentence : sentence + "."
}

func cueOf(_ spec: CueSpec) -> VoiceCue { VoiceCue(id: UUID(), phrase: spec.phrase, text: spec.text, createdAt: Date(timeIntervalSince1970: 0)) }

@main struct Gen {
    static func main() throws {
        let url = "https://example.com/in/sample?a=b&c=d", email = "sam@example.com", address = "1 Sample St\nTown, ST 00000", work = "sam@work.example.com"
        let library = [CueSpec(phrase: "my LinkedIn", text: url), CueSpec(phrase: "my email", text: email), CueSpec(phrase: "my email address", text: work),
                       CueSpec(phrase: "my address", text: address), CueSpec(phrase: "Sam's phone", text: "555-0100"), CueSpec(phrase: "my signature", text: "Thanks,\nSam\n")]

        var words: [WordsCase] = []
        for s in ["my LinkedIn", "Sam's phone", "Sam\u{2019}s phone", "e-mail me", "John's-O'Brien", "", "  ", "...", "a1 b2", "Zoë café", "日本語 テスト", "😀 smile", "insert here", "x\ty\nz", "COVID-19"] {
            words.append(WordsCase(input: s, words: VoiceCueText.words(s)))
        }

        var splits: [SplitCase] = []
        func split(_ cues: [CueSpec], _ raw: String, keeps: [String] = []) {
            let pieces = VoiceCueText.split(raw, cues: cues.map(cueOf))
            guard let pieces else { splits.append(SplitCase(cues: cues, raw: raw, keeps: keeps, pieces: nil, processed: nil, joined: nil)); return }
            let processed = pieces.map { piece -> String in if case .text(let text) = piece { return clean(text) } else { return "" } }
            let encoded = pieces.map { piece -> [String] in
                switch piece {
                case .text(let t): return ["t", t]
                case .expansion(let t, let closing): return ["e", t, closing]
                }
            }
            splits.append(SplitCase(cues: cues, raw: raw, keeps: keeps, pieces: encoded, processed: processed,
                                    joined: VoiceCueText.join(pieces, processed: processed, keepsCase: { keeps.contains($0) })))
        }
        let raws: [String] = [
            "my LinkedIn", "  My LinkedIn! ", "MY LINKEDIN.", "my, linkedin?", "\"my LinkedIn\"", "insert my LinkedIn", "Paste my LinkedIn here.", "My email address.",
            "Reach me at insert my LinkedIn here if you have questions", "Reach me at paste my email if you like", "INSERT MY LINKEDIN HERE", "insert my LinkedIn, here", "insert, my LinkedIn here",
            "Insert my email here.", "insert my email here and insert my LinkedIn here", "insert my email here insert my email here",
            "my LinkedIn is down", "send my email address to Sam", "please insert the table here", "please update my LinkedIn", "inserting my LinkedIn", "insert my", "insert. my LinkedIn", "I want to add my LinkedIn",
            "insert here", "my link", "insert Sam's phone here", "insert Sams phone here", "insert Sam\u{2019}s phone here", "Sam's phone", "insert my email address here", "insert my email here",
            "insert my email addresses here", "call me then insert my LinkedIn. Here we go", "Insert my email here and call me", "Write to me at paste my email",
            "Here is my page insert my LinkedIn here. Thanks for reading", "Email me at insert my email here, thanks", "Is it insert my email here?",
            "insert my email here and insert my LinkedIn here and so on", "Here is my page. insert my LinkedIn here", "Please sign insert my signature here Next week", "insert my email here I will reply",
            "insert my email here, i will reply", "insert my email here If you can", "insert my email here akme said hi", "send insert my address here please", "see  insert my email here  now",
            "", " ", "insert", "insert my email here;", "insert my email here ... and more", "insert my email here!!! wow", "insert my email here\nnext line", "paste\tmy\temail\there", "insert my email\u{00A0}here",
            "insert my email here : colon", "insert my email here ,comma", "x insert my email here y insert my address here z", "insert my email here\u{2026}", "insert my email here。", "insert my LinkedIn, here, thanks.",
            "so insert my email here. So then", "so insert my email here? Then", "I said insert my email here i think", "insert my email here\r\nnext", "insert café here", "insert 日本語 here", "😀 insert my email here 😀",
            "please PASTE my EMAIL ADDRESS HERE.", "insert my email here. . .", "my email address here", "paste here", "insert my signature here and then more words.", "insert my signature here. More words.",
        ]
        for raw in raws { split(library, raw); if raw.contains("akme") { split(library, raw, keeps: ["akme"]) }; if raw.contains("If") { split(library, raw, keeps: ["If"]) } }
        let accented = [CueSpec(phrase: "café order", text: "one café"), CueSpec(phrase: "日本語 cue", text: "ok")]
        for raw in ["insert café order here", "insert Café order", "insert 日本語 cue here", "日本語 cue", "insert cafe order here"] { split(accented, raw) }
        split([], "insert my LinkedIn here"); split([CueSpec(phrase: "same", text: "a"), CueSpec(phrase: "same one", text: "b")], "insert same one here and paste same")
        split([CueSpec(phrase: "my email", text: "insert my LinkedIn here")], "insert my email here")
        split([CueSpec(phrase: "a b", text: "ab"), CueSpec(phrase: "a b c", text: "abc"), CueSpec(phrase: "a", text: "A")], "insert a b c here insert a b insert a")

        var stores: [StoreCase] = []
        func seq(_ label: String, _ ops: [[String]]) {
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-golden-cues-\(UUID())")
            defer { try? FileManager.default.removeItem(at: dir) }
            var store = VoiceCueStore(directory: dir)
            var results: [String] = [], states: [String] = []
            for op in ops {
                let a = Array(op.dropFirst())
                func id(_ s: String) -> UUID { Int(s)! < store.cues.count ? store.cues[Int(s)!].id : UUID() }
                do {
                    switch op[0] {
                    case "add": try store.add(phrase: a[0], text: a[1]); results.append("ok")
                    case "update": try store.update(id: id(a[0]), phrase: a[1], text: a[2]); results.append("ok")
                    case "delete": try store.delete(id: id(a[0])); results.append("ok")
                    case "reopen": store = VoiceCueStore(directory: dir); results.append("ok")
                    case "damage": try Data("not json".utf8).write(to: dir.appendingPathComponent(VoiceCueStore.fileName)); results.append("ok")
                    case "version": try Data(#"{"cues":[],"version":2}"#.utf8).write(to: dir.appendingPathComponent(VoiceCueStore.fileName)); results.append("ok")
                    case "remove": try FileManager.default.removeItem(at: dir); results.append("ok")
                    default: fatalError("unknown op \(op[0])")
                    }
                } catch let e as StoreError { results.append("error:" + e.description) } catch { results.append("error") }
                states.append(store.cues.map { "\($0.phrase)\u{1F}\($0.text)" }.joined(separator: "\u{1E}") + (store.unreadable ? " #unreadable" : ""))
            }
            stores.append(StoreCase(label: label, ops: ops, results: results, states: states))
        }
        let long60 = String(repeating: "a", count: 60), text4000 = String(repeating: "b", count: 4000)
        seq("validation", [
            ["add", "  my   LinkedIn ", url], ["add", "my address", address + "\n\nwith  odd   spacing "], ["add", "", "x"], ["add", "1234", "x"], ["add", String(repeating: "a", count: 61), "x"],
            ["add", "Insert", "x"], ["add", " paste ", "x"], ["add", "Here!", "x"], ["add", "MY  linkedin!", "x"], ["add", "my linked-in", "y"], ["add", "ok phrase", ""], ["add", "ok phrase", " \n "],
            ["add", "ok phrase", String(repeating: "a", count: 4001)], ["add", long60, text4000], ["add", "insert something", "x"], ["add", "🙂 smile", "x"], ["add", "café", "é"], ["add", "Sam's", "t"], ["add", "Sams", "t"],
            ["add", "日本語", "t"], ["add", "a\tb", "t\u{00A0}"], ["add", "x1", "\u{2028}"],
        ])
        seq("update, delete and persistence", [
            ["add", "my LinkedIn", url], ["add", "my address", address], ["reopen"], ["update", "0", "my LinkedIn page", "new"], ["update", "0", "my address", "x"], ["update", "9", "zzz", "x"], ["update", "0", "Here", "x"],
            ["delete", "0"], ["reopen"], ["delete", "9"], ["remove"], ["reopen"], ["add", "again", "ok"],
        ])
        seq("damaged file is read-only and untouched", [["add", "x y", "z"], ["damage"], ["reopen"], ["add", "x y", "z"], ["delete", "0"], ["update", "0", "q r", "s"], ["version"], ["reopen"], ["add", "k l", "m"]])

        print("{\n" + [lines("words", words), lines("split", splits), lines("store", stores)].joined(separator: ",\n") + "\n}")
    }
}
