// deps: ../module-one/models/Runner/Sources/ModelBench/BoostFilter
import Foundation

// Golden outputs of BoostFilter (filter, replacements with UTF-16 ranges, words, withoutUnknown, similarity) over synthetic transcripts.
// Lexicons: "file" (windows/golden/lexicon.txt, exact-word lookup) or "list" (the case's own words). One case per line. Run from the repository root.
let lexiconWords: Set<String> = Set(try! String(contentsOfFile: "windows/golden/lexicon.txt", encoding: .utf8).split(separator: "\n").map(String.init))

struct Rep: Encodable { var start: Int, end: Int, from: String, to: String }
struct FilterCase: Encodable { var raw: String, rescored: String, aliases: [String], lex: String, words: [String], output: String, reps: [Rep] }
struct UnknownCase: Encodable { var input: String, output: String }
struct WordsCase: Encodable { var input: String, words: [String] }
struct SimCase: Encodable { var a: String, b: String, value: Double }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

@main struct Gen {
    static func main() {
        var filters: [FilterCase] = []
        func f(_ raw: String, _ rescored: String, aliases: [String] = [], lex: String = "file", words: [String] = []) {
            let set = lex == "file" ? lexiconWords : Set(words)
            let common: (String) -> Bool = { set.contains($0) }
            let reps = BoostFilter.replacements(raw: raw, rescored: rescored, aliases: Set(aliases), isCommonWord: common).map {
                Rep(start: $0.range.lowerBound.utf16Offset(in: raw), end: $0.range.upperBound.utf16Offset(in: raw), from: $0.from, to: $0.to)
            }
            filters.append(FilterCase(raw: raw, rescored: rescored, aliases: aliases, lex: lex, words: words, output: BoostFilter.filter(raw: raw, rescored: rescored, aliases: Set(aliases), isCommonWord: common), reps: reps))
        }
        let common = ["i", "mean", "it", "is", "not", "a", "big", "deal", "nothing", "else", "option", "works", "for", "me", "remind", "to", "call", "dad", "getting", "the", "build", "green", "open", "router",
                      "through", "calls", "basin", "store", "rows", "in", "coming", "like", "that", "run", "get", "pods", "send", "report", "cube", "cuddle", "have", "seen", "neither", "she", "said", "i'm"]
        // The check_boost_filter.swift cases, with the check's lexicon.
        let checks: [(String, String, [String])] = [
            ("Output the word approved and nothing else.", "Output the word approved and Notion else.", []), ("Neither option works for me.", "Neither Notion works for me.", []),
            ("She said, um, I'm not coming.", "She said, um, I'm Notion", []), ("Remind me to call dad at 7.", "Gemini me to call dad at 7.", []), ("I mean, it is not a big deal.", "I Sean, it is not a big deal.", []),
            ("Run cubectal get pods.", "Run kubectl get pods.", []), ("Send the ShelfMud report.", "Send the Shelfmate report.", []),
            ("Store the Rosin Supa Basin root calls through open router.", "Store the Rosin Supabase root calls through OpenRouter", []), ("Push the change to CloudEv now.", "Push the change to Clouddev now.", []),
            ("run cube cuddle now", "run kubectl now", ["cube cuddle"]), ("run cube cuddle now", "run kubectl now", []), ("Run cubectal, I mean nothing else.", "Run kubectl, I Sean Notion else.", []),
            ("Unchanged text.", "Unchanged text.", []), ("Ask Priya about it.", "Ask Notion it.", []), ("Nor does it support offline mode.", "Nor does it support offline mode", []),
            ("Bring it, I mean, if you can.", "Bring it, I Sean if you can.", []), ("", "", []),
        ]
        for (raw, rescored, aliases) in checks { f(raw, rescored, aliases: aliases, lex: "list", words: common); f(raw, rescored, aliases: aliases) }
        // More synthetic cases (file lexicon, then a small list).
        let more: [(String, String, [String])] = [
            ("open router is nice", "OpenRouter is nice", []), ("open  router", "OpenRouter", []), ("Open-Router", "OpenRouter", []), ("café noir", "Cafe noir", []), ("Zoë and José", "Zoe and Jose", []),
            ("cubectal 😀 get pods", "kubectl 😀 get pods", []), ("😀 cubectal", "😀 kubectl", []), ("it\u{2019}s cubectal time", "it's kubectl time", []), ("don't stop", "don\u{2019}t stop", []),
            ("a b c d e f", "a x y d e f", []), ("a b c d e f", "a b c", []), ("a b c", "a b c d e f", []), ("one two three", "uno dos tres", []), ("one two three", "one-two three", []),
            ("send it to Priya and Anjali", "send it to Priyanka and Anjali", []), ("send it to Priya and Anjali", "send it to Priyanka Anjali", []),
            ("The quick brown fox", "The quik brown fox", []), ("The quick brown fox", "The QUICK brown fox", []), ("x1 y2 z3", "x1 yy2 z3", []), ("日本語 テスト", "日本語 テスト2", []),
            ("tab\tseparated words", "tab\tseparated word", []), ("line one\nline two", "line one\nline too", []), ("kubectl", "Kubectl", []), ("Kubectl", "kubectl", []),
            ("remind me", "Gemini me", []), ("remind me", "Gemini me", ["remind"]), ("run cube cuddle now", "run kubectl now", ["Cube Cuddle"]),
            ("nothing", "Notion", []), ("nothing else matters", "Notion else matters", []), ("1,000 apples", "1000 apples", []), ("a.b c.d", "a.b c.e", []),
            ("cubectal; get pods, now!", "kubectl get pods now", []), ("trailing cubectal", "trailing kubectl", []), ("cubectal leading", "kubectl leading", []),
            ("same same same", "same other same", []), ("twice cubectal and cubectal", "twice kubectl and kubectl", []), ("twice cubectal and cubectal", "twice kubectl and cubectl", []),
        ]
        for (raw, rescored, aliases) in more { f(raw, rescored, aliases: aliases); f(raw, rescored, aliases: aliases, lex: "list", words: common) }

        var unknowns: [UnknownCase] = []
        for s in ["El Ni<unk>o", "<unk> hi", "hi <unk>", "a <unk> b", "hi <unk>.", "<unk><unk>", "<unk>", "No unknown here.", "", "  <unk>  x  <unk>  ", "a  <unk>  b", "x <unk>,y <unk>; z <unk>:",
                  "tab\t<unk>\tx", "\t<unk>", "<unk>\u{00A0}x", "\u{3000}<unk>\u{3000}", "line\n<unk>\nbreak", "caf<unk> noir", "😀 <unk> 😀", "<unk>!<unk>?", "<UNK> upper", "a   b   <unk>"] {
            unknowns.append(UnknownCase(input: s, output: BoostFilter.withoutUnknown(s)))
        }
        var words: [WordsCase] = []
        for s in ["Hello, world!", "don't won\u{2019}t e-mail -dash- 'quote'", "x1 y2", "Zoë café", "Zoe\u{0301} x", "日本語 テスト", "😀 smile 😀", "a--b", "a'-b", "a'", "'a", "-", "", "1,000.50", "tab\tx\ny", "👨\u{200D}👩\u{200D}👧 family",
                  "ß straße İstanbul", "Ǆ ǅ ǆ", "O\u{2019}Brien-Smith's", "end."] {
            words.append(WordsCase(input: s, words: BoostFilter.words(s).map { "\($0.start.utf16Offset(in: s)),\($0.end.utf16Offset(in: s)),\($0.folded)" }))
        }
        var sims: [SimCase] = []
        for (a, b) in [("akash", "aakash"), ("", ""), ("", "a"), ("a", ""), ("kitten", "sitting"), ("é", "e\u{0301}"), ("😀", "😁"), ("abc", "abc"), ("openrouter", "openrouter"), ("cubectal", "kubectl")] {
            sims.append(SimCase(a: a, b: b, value: BoostFilter.similarity(a, b)))
        }
        print("{\n" + [lines("filter", filters), lines("unknown", unknowns), lines("words", words), lines("similarity", sims)].joined(separator: ",\n") + "\n}")
    }
}
