// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Golden outputs of CorrectionFilter (evaluate, replacements, plausibleEdit, phoneticKey, letters, embedsOrdinary) and CorrectionLedger
// over synthetic text. Lexicons: "none" (nil), "file" (windows/golden/lexicon.txt, exact-word lookup) or "list" (the case's own words).
// Ledger cases are op sequences replayed against temp-folder stores; entry ids and session ids are random, so ops name sessions and
// refer to entries by index. One case per line. Run from the repository root.
let lexiconWords: Set<String> = Set(try! String(contentsOfFile: "windows/golden/lexicon.txt", encoding: .utf8).split(separator: "\n").map(String.init))
func lexicon(_ mode: String, _ words: [String]) -> ((String) -> Bool)? {
    switch mode {
    case "file": return { lexiconWords.contains($0) }
    case "list": let set = Set(words); return { set.contains($0) }
    default: return nil
    }
}

struct FilterCase: Encodable { var source: String, target: String, manual: [String], lex: String, words: [String], kind: String, a: String, b: String }
struct ReplCase: Encodable { var original: String, current: String, failure: String?, changes: [[String]]? }
struct PlausCase: Encodable { var original: String, candidates: [String], lex: String, words: [String], result: String? }
struct KeyCase: Encodable { var input: String, letters: String, phonetic: String, tokens: [String], phraseKey: String?, collapsed: String, folded: String }
struct EmbedCase: Encodable { var a: String, b: String, value: Bool }
struct LedgerCase: Encodable { var label: String, ops: [[String]], results: [String], states: [String] }

func verdictFields(_ v: CandidateVerdict) -> (String, String, String) {
    switch v {
    case .accepted(let s, let t): return ("accepted", s, t)
    case .rejected(let r): return ("rejected", r.rawValue, "")
    }
}

// MARK: ledger

let t0 = Date(timeIntervalSince1970: 1_790_000_000)

final class Env {
    var dir: URL
    var dict: DictionaryStore
    var ledger: CorrectionLedger
    var sessions: [String: UUID] = [:]
    var lex: ((String) -> Bool)?
    var counter = 0
    init(_ dir: URL) throws {
        self.dir = dir
        dict = try DictionaryStore(directory: dir)
        ledger = try CorrectionLedger(directory: dir, dictionary: dict)
    }
    func sid(_ name: String) -> UUID { if let id = sessions[name] { return id }; let id = UUID(); sessions[name] = id; return id }
    var learned: [DictionaryEntry] { dict.entries.filter { $0.source == .learned } }
    func at(_ offset: String) -> Date { t0.addingTimeInterval(Double(offset) ?? 0) }
    func state() -> String {
        let entries = dict.entries.map { "\($0.preferred)=\($0.variants.joined(separator: "|"))/\($0.source.rawValue)/\($0.enabled ? 1 : 0)" }.joined(separator: ";")
        let evidence = ledger.evidence.map { "\($0.source)>\($0.target)" }.joined(separator: ";")
        let blocked = ledger.blocked.map { b -> String in switch b { case .heard(let s): return "heard:\(s)"; case .spelling(let s): return "spelling:\(s)" } }.joined(separator: ";")
        return ["E:" + entries, "V:" + evidence, "P:\(ledger.provisionalSessions.count)", "S:" + ledger.suppressedSources.sorted().joined(separator: ","),
                "T:" + ledger.tombstones.map { "\($0.source)>\($0.target)" }.sorted().joined(separator: ";"), "B:" + blocked, "L:\(ledger.learningEnabled ? 1 : 0)"].joined(separator: " # ")
    }
    func outcome(_ o: CorrectionLedger.RecordOutcome) -> String {
        switch o {
        case .learningDisabled: return "learningDisabled"
        case .sessionClosed: return "sessionClosed"
        case .provisional: return "provisional"
        case .suppressed: return "suppressed"
        case .tombstoned: return "tombstoned"
        case .rejected(let r): return "rejected:" + r.rawValue
        }
    }
    func run(_ op: [String]) throws -> String {
        let a = Array(op.dropFirst())
        switch op[0] {
        case "lex": lex = lexicon(a[0], Array(a.dropFirst())); ledger.isCommonWord = lex; return "ok"
        case "record": return outcome(try ledger.record(ObservedCorrection(sessionID: sid(a[0]), source: a[1], target: a[2], observedAt: at(a[3]))))
        case "settle": return try ledger.settle(sessionID: sid(a[0])).map(\.preferred).joined(separator: ",")
        case "revert": try ledger.revert(sessionID: sid(a[0])); return "ok"
        case "undo": try ledger.undoLearned(entryID: learned[Int(a[0])!].id); return "ok"
        case "undoStale": try ledger.undoLearned(entryID: UUID()); return "ok"
        case "deleteLearned": try ledger.deleteLearned(id: learned[Int(a[0])!].id); return "ok"
        case "deleteAny": try ledger.deleteLearned(id: dict.entries[Int(a[0])!].id); return "ok"
        case "dictDelete": try dict.delete(id: learned[Int(a[0])!].id); return "ok"
        case "reset": try ledger.reset(); return "ok"
        case "unblockHeard": try ledger.unblock(.heard(a[0])); return "ok"
        case "unblockSpelling": try ledger.unblock(.spelling(a[0])); return "ok"
        case "learning": try ledger.setLearningEnabled(a[0] == "on"); return "ok"
        case "edit": return try ledger.learnFromEdit(original: a[0], edited: a[1], at: at(a[2])).map(\.preferred).joined(separator: ",")
        case "addManual": try dict.add(preferred: a[0], variants: a[1].split(separator: "|").map(String.init)); return "ok"
        case "addLearned": try dict.add(preferred: a[0], variants: a[1].split(separator: "|").map(String.init), source: .learned); return "ok"
        case "apply": return dict.apply(to: a[0])
        case "reopen":
            dict = try DictionaryStore(directory: dir, isCommonWord: lex); ledger = try CorrectionLedger(directory: dir, dictionary: dict); return "ok"
        case "legacy":
            // a[0] is the ledger file with {ID} (first learned entry) and {OLD} (a fixed session id) filled in.
            let json = a[0].replacingOccurrences(of: "{ID}", with: learned[0].id.uuidString).replacingOccurrences(of: "{OLD}", with: "11111111-1111-1111-1111-111111111111")
            try PrivateFiles.write(Data(json.utf8), to: dir.appendingPathComponent(CorrectionLedger.fileName))
            ledger = try CorrectionLedger(directory: dir, dictionary: dict); ledger.isCommonWord = lex; return "ok"
        default: fatalError("unknown op \(op[0])")
        }
    }
}

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

@main struct Gen {
    static func main() {
        var filter: [FilterCase] = []
        func f(_ s: String, _ t: String, manual: [String] = [], lex: String = "none", words: [String] = []) {
            let (k, a, b) = verdictFields(CorrectionFilter.evaluate(source: s, target: t, manualPreferred: Set(manual), isCommonWord: lexicon(lex, words)))
            filter.append(FilterCase(source: s, target: t, manual: manual, lex: lex, words: words, kind: k, a: a, b: b))
        }
        // Run under the fixed file lexicon and with none.
        func both(_ s: String, _ t: String, manual: [String] = []) {
            f(s, t, manual: manual); f(s, t, manual: manual, lex: "file")
        }

        let pairs: [(String, String)] = [
            ("Acme", "Akme"), ("I accept", "I decline"), ("accept", "decline"), ("Acme", "acme"), ("Acme", "Acme!"), ("e mail", "e-mail"), ("Acme", ""), ("Acme Corp", "Acme"),
            ("Acme", "Acme Corp"), ("Acme", "big Acme"), ("one two three", "four five six"), ("alpha beta gamma delta", "alpho betta gamme delto"), ("will go", "will not go"),
            ("is able", "is unable"), ("should go", "shouldn't go"), ("Room 12", "Room 21"), ("seven", "eleven"), ("there", "their"), ("report", "reports"), ("walk", "walked"),
            ("sing", "sang"), ("Jon", "John"), ("table", "chair"), ("Acme,", "Akme,"), ("Catherine", "Kathryn"), ("cooper netties", "Kubernetes"), ("the Acme team", "the Akme team"),
            ("Fred", "Quorbix"), ("casual", "causal"), ("lunch", "launch"), ("Cloud", "Claude"), ("Cloud", "clouddev"), ("Zero cloud", "Zero clouddev"), ("Akmee", "Akme"),
            ("cloud zero", "clouddev zero"), ("cloud", "CLOUDDEV"), ("Acme", "Akme"), ("jira", "Jira"), ("git hub", "GitHub"), ("para keet", "Parakeet"), ("a p i", "API"),
            ("J son", "JSON"), ("call jira", "call Jira"), ("notion", "Notion"), ("spacey boy", "spaceyboi"), ("rust C", "rustc"), ("every one", "everyone"), ("a boy", "aboy"),
            ("A boy", "aboy"), ("I am", "iam"), ("ChadJuT", "ChatGPT"), ("In Buzari's", "In Buzzati's"), ("Buzari", "buzzard"), ("codecs", "Codex"), ("claud", "Claude"),
            ("Quorbix", "Fredlin"), ("", ""), (" ", "x"), ("x", " "), ("same", "same"), ("  spaced   out ", "spaced out"), ("Zoë", "Zoe"), ("Zoë", "Zoë"), ("Zoe\u{301}", "Zoë"), ("Jose", "José"),
            ("Nguyen", "Nguyễn"), ("Muller", "Müller"), ("Zurich", "Zürich"), ("東京", "東京都"), ("日本", "ニホン"), ("😀 Acme", "😀 Akme"), ("Acme 😀", "Akme 😀"), ("Don't", "Dont"),
            ("don't go", "dont go"), ("O\u{2019}Brien", "O'Brien"), ("O'Brian", "O'Brien"), ("Buzari\u{2019}s", "Buzzati\u{2019}s"), ("e-mail", "email"), ("email", "e-mail"),
            ("anti social", "antisocial"), ("non profit", "nonprofit"), ("happy", "unhappy"), ("legal", "illegal"), ("possible", "impossible"), ("agree", "disagree"), ("take", "mistake"),
            ("first", "last"), ("one", "won"), ("two", "too"), ("for", "four"), ("seeing", "see"), ("party", "parties"), ("parties", "party"), ("cries", "cried"), ("cat", "cut"),
            ("ran", "run"), ("sang", "sing"), ("walks", "walk"), ("walker", "walk"), ("fastest", "fast"), ("quick", "quickly"), ("child's", "childs"), ("kids", "kids'"),
            ("Kubernetes", "kubernetes"), ("kubectl", "kubeCTL"), ("cube cuddle", "kubectl"), ("kube ctl", "kubectl"), ("open ai", "OpenAI"), ("open A I", "OpenAI"),
            ("Pie Torch", "PyTorch"), ("pie torch", "PyTorch"), ("py torch", "PyTorch"), ("Type script", "TypeScript"), ("sequel", "SQL"), ("S Q L", "SQL"), ("see sharp", "C#"),
            ("node js", "Node.js"), ("Next JS", "Next.js"), ("get hub", "GitHub"), ("gethub", "GitHub"), ("Github", "GitHub"), ("GitHub", "Github"), ("iphone", "iPhone"), ("IPhone", "iPhone"),
            ("Aakash", "Akash"), ("Akash", "Aakash"), ("Aakesh", "Aakash"), ("Sean", "Shawn"), ("Shawn", "Sean"), ("Stephen", "Steven"), ("Steven", "Stephen"), ("Katherine", "Catherine"),
            ("Philip", "Phillip"), ("Jeffrey", "Geoffrey"), ("Alan", "Allen"), ("Alan", "Allan"), ("Marc", "Mark"), ("Mark", "Marc"), ("Eric", "Erik"), ("Zhang", "Zang"),
            ("A B C", "ABC"), ("abc", "ABC"), ("ABC", "abc"), ("1 2 3", "123"), ("room 12", "room 12a"), ("v2", "v3"), ("COVID19", "COVID-19"), ("x y z", "xyz"), ("alpha beta", "alpha-beta"),
            ("alpha beta gamma", "alpha gamma"), ("alpha gamma", "alpha beta gamma"), ("a b c d", "abcd"), (String(repeating: "word ", count: 5), String(repeating: "ward ", count: 5)),
            ("the Acme", "the Akme"), ("Acme the", "Akme the"), ("the the", "the the the"), ("hot", "cold"), ("open", "close"), ("opened", "closed"), ("before", "after"), ("on", "off"),
        ]
        for (s, t) in pairs { both(s, t) }
        for (s, t) in [("Fred", "Quorbix"), ("accept", "decline"), ("Cloud", "Claude"), ("Cloud", "clouddev"), ("Zero cloud", "Zero clouddev"), ("Fred", "Quorbix"), ("Akme", "Acme"), ("Kubernetes", "Kubernetes"), ("Cooper", "Kubernetes")] {
            both(s, t, manual: ["quorbix", "decline", "claude", "clouddev", "clouddev zero", "akme", "kubernetes"])
        }
        // Check-script lexicons (each case carries its own words).
        let lexicon1 = ["casual", "causal", "lunch", "launch", "cloud", "acme", "team"]
        f("casual", "causal", lex: "list", words: lexicon1); f("lunch", "launch", lex: "list", words: lexicon1); f("Acme", "Akme", lex: "list", words: lexicon1)
        f("Cloud", "Claude", manual: ["claude"], lex: "list", words: lexicon1 + ["claude"])
        let cd = ["cloud", "zero", "cipher"]
        f("Cloud", "clouddev", manual: ["clouddev"], lex: "list", words: cd); f("Zero cloud", "Zero clouddev", manual: ["clouddev"], lex: "list", words: cd)
        f("Akmee", "Akme", manual: ["akme"], lex: "list", words: cd); f("cloud zero", "clouddev zero", manual: ["clouddev zero"], lex: "list", words: cd)
        f("cloud", "CLOUDDEV", lex: "list", words: cd); f("Cloud", "clouddev", lex: "list", words: ["cloud", "acme", "cloud's"]); f("Acme", "Akme", lex: "list", words: ["cloud", "acme", "cloud's"])
        let words2 = lexicon1 + ["notion", "git", "hub", "every", "one", "everyone", "api", "son", "call"]
        for (s, t) in [("jira", "Jira"), ("git hub", "GitHub"), ("para keet", "Parakeet"), ("a p i", "API"), ("J son", "JSON"), ("Jon", "John"), ("call jira", "call Jira"), ("notion", "Notion"), ("every one", "everyone")] {
            f(s, t, lex: "list", words: words2)
        }
        f("spacey boy", "spaceyboi", lex: "list", words: words2 + ["spacey", "boy"])
        f("rust C", "rustc", lex: "list", words: ["rust", "c"])
        f("a boy", "aboy", lex: "list", words: ["a", "boy", "aboy"])
        f("A boy", "aboy", lex: "list", words: ["a", "boy", "i", "am"]); f("I am", "iam", lex: "list", words: ["a", "boy", "i", "am"])
        let names = ["codecs", "codex", "claud", "claude", "buzzard", "lunch", "launch"]
        for (s, t) in [("ChadJuT", "ChatGPT"), ("In Buzari's", "In Buzzati's"), ("Buzari", "buzzard"), ("codecs", "Codex"), ("claud", "Claude"), ("Quorbix", "Fredlin")] { f(s, t, lex: "list", words: names) }

        var replacements: [ReplCase] = []
        func r(_ o: String, _ c: String) {
            switch CorrectionFilter.replacements(o, c) {
            case .success(let changes): replacements.append(ReplCase(original: o, current: c, failure: nil, changes: changes.map { [$0.source] + $0.targets }))
            case .failure(let reason): replacements.append(ReplCase(original: o, current: c, failure: reason.rawValue, changes: nil))
            }
        }
        for (o, c) in [("call ACME about Gira", "call Akme about Jira"), ("pay Acme", "pay Akme and more"), ("call Acme today", "call Akme today and more"), ("call Acme today", "call today"),
                       ("maybe the team wants a new plan", "I think we should ship old stuff"), ("acme", "Acme"), ("", ""), ("a", ""), ("", "a"), ("same words", "same words"),
                       ("Same Words", "same words"), ("git hub", "GitHub"), ("GitHub", "git hub"), ("a b", "a, b"), ("Acme", "Acme."), ("one two three four five six", "one 2 three four five six"),
                       ("pay Acme", "pay Akme and then some more words"), ("pay Acme now", "pay Akme now now"), ("Acme Acme Acme", "Akme Akme Akme"), ("Acme met Zorblat", "Akme met Zorblatt"),
                       ("a b c d e f g h", "x y z d e f g h"), ("a b c d e f g h", "x y z w e f g h"), ("a b c d e f g h i j k l", "a b c d e f g h i j k l m n o"), ("café noir", "cafe noir"),
                       ("don't go", "dont go"), ("O\u{2019}Brien came", "O'Brien came"), ("日本 語", "日本語"), ("😀 Acme", "😀 Akme"), ("x", "y"), ("x y", "y x"), ("one", "one two"), ("one two", "two"),
                       ("Is it Acme?", "Is it Akme?"), ("the cat sat", "the cat sat down"), ("the cat sat", "a cat sat"), ("the cat sat on a mat", "the dog sat on a rug")] { r(o, c) }

        var plausible: [PlausCase] = []
        func p(_ o: String, _ c: [String], lex: String = "none", words: [String] = []) {
            plausible.append(PlausCase(original: o, candidates: c, lex: lex, words: words, result: CorrectionFilter.plausibleEdit(original: o, candidates: c, isCommonWord: lexicon(lex, words))))
        }
        for lex in ["none", "file"] {
            p("call Acme today", ["call Akme today", "call Akme today", "call Acme today"], lex: lex)
            p("call Acme today", ["call Akme today", "call Acmee today"], lex: lex)
            p("call Acme Acme today", ["call Akme Acme today", "call Acme Akme today"], lex: lex)
            p("send the cloud report", ["send the clouddev report", "send the cloud reports"], lex: lex)
            p("ask Priya about it", ["ask Priyaa about it", "ask Priya abou it", "ask Priya about its"], lex: lex)
            p("git hub issue", ["GitHub issue", "git Hub issue"], lex: lex)
            p("jira ticket", ["Jira ticket", "jira Ticket"], lex: lex)
            p("a p i key", ["API key"], lex: lex)
            p("same", ["same"], lex: lex)
            p("", ["x"], lex: lex)
            p("hello", [], lex: lex)
        }

        var keys: [KeyCase] = []
        var keyInputs = ["Acme", "Akme", "Kubernetes", "Coopernetties", "phone", "Philip", "photo graph", "Zoë", "e-mail", "O\u{2019}Brien", "don't", "a.b c", "  spaced   out ", "", " ", "x", "ChatGPT", "ChadJuT", "123 abc", "日本語", "😀", "Node.js", "New York City", "Mr. Smith's", "AI-based, yes", "\ta\tb\n", "tab\u{00A0}nbsp", "CRLF\r\nnext", "a\u{2028}b"]
        keyInputs += lexiconWords.sorted().prefix(40)
        for s in keyInputs {
            keys.append(KeyCase(input: s, letters: CorrectionFilter.letters(s), phonetic: CorrectionFilter.phoneticKey(s),
                                tokens: TermText.tokens(s).map { "\($0.start),\($0.end),\($0.text)" }, phraseKey: TermText.phraseKey(s),
                                collapsed: TermText.collapseWhitespace(s), folded: TermText.fold(s)))
        }
        var embeds: [EmbedCase] = []
        for (a, b) in [("cloud", "clouddev"), ("clouddev", "cloud"), ("cloud", "cloud"), ("cloud", "Cloud"), ("acme", "akme"), ("a b", "ab"), ("x", "y"), ("cloud's", "clouds"), ("sun", "sunny"), ("", "x")] {
            embeds.append(EmbedCase(a: a, b: b, value: CorrectionFilter.embedsOrdinary(a, b)))
        }

        var ledgerCases: [LedgerCase] = []
        func seq(_ label: String, _ ops: [[String]]) {
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-golden-learning-\(UUID())")
            defer { try? FileManager.default.removeItem(at: dir) }
            let env = try! Env(dir)
            var results: [String] = [], states: [String] = []
            for op in ops {
                do { results.append(try env.run(op)) } catch let e as StoreError { results.append("error:" + e.description) } catch { results.append("error") }
                states.append(env.state())
            }
            ledgerCases.append(LedgerCase(label: label, ops: ops, results: results, states: states))
        }
        func rec(_ s: String, _ src: String, _ tgt: String, _ off: Int = 0) -> [String] { ["record", s, src, tgt, String(off)] }
        func settle(_ s: String) -> [String] { ["settle", s] }

        let legacyPromotion = #"{"evidence":[],"learningEnabled":true,"promotions":[{"entryID":"{ID}","pair":{"source":"acme","target":"akme"},"sessions":[]}],"suppressed":[],"tombstones":[],"version":1}"#
        let legacyHint = #"{"evidence":[{"correction":{"observedAt":0,"sessionID":"{OLD}","source":"Acme","target":"Akme"},"settled":true}],"learningEnabled":true,"promotions":[{"entryID":"{ID}","kind":"hint","pair":{"source":"acme","target":"akme"},"sessions":["{OLD}"]}],"suppressed":[],"tombstones":[],"version":1}"#

        seq("old provisional candidate is not promoted after the guard changed", [["addManual", "clouddev", ""], rec("s", "cloud", "clouddev"), ["lex", "list", "cloud"], settle("s")])
        seq("one settled fix learns and replaces", [rec("a", "Acme", "Akme"), ["apply", "Ask Acme today"], settle("a"), ["apply", "Ask Acme today"], rec("b", "acme", "AKME", 10), settle("b"), ["reopen"]])
        seq("two observations in the same session learn once", [rec("a", "Acme", "Akme"), rec("a", "Acme", "Akme", 5), settle("a"), settle("a"), rec("a", "Acme", "Akme", 6)])
        seq("early settle then keep watching", [rec("a", "Acme", "Akme"), settle("a"), ["apply", "Ask Acme"], rec("a", "Acme", "Akme", 1), rec("a", "Acme", "Akmee", 2), settle("a"),
                                                 rec("a", "Acme", "Akme", 3), settle("a"), settle("a"), rec("b", "Acme", "Akme", 10), settle("b"), ["apply", "Ask Acme"]])
        seq("second fix after an early settle keeps the first", [rec("d", "Acme", "Akme"), settle("d"), rec("e", "Acme met Zorblat", "Akme met Zorblat", 10), settle("e"),
                                                                 rec("e", "Acme met Zorblat", "Akme met Zorblatt", 11), ["apply", "Ask Acme"], settle("e"), ["apply", "Ask Acme met Zorblat"]])
        seq("undo after an early settle", [rec("c", "Acme", "Akme"), settle("c"), ["revert", "c"]])
        seq("notice undo removes what it learned and tombstones it", [rec("a", "Akash", "Aakash"), settle("a"), rec("b", "Aakesh", "Aakash"), settle("b"), ["undo", "0"], ["apply", "Aakesh"],
                                                                      rec("c", "Aakesh", "Aakash"), ["undo", "0"], ["undoStale"], rec("d", "Akash", "Aakash")])
        seq("history edit learns through the same filter", [["edit", "I talked to Acme today.", "I talked to Akme today.", "0"], ["apply", "Ask Acme"], ["edit", "I accept the terms.", "I decline the terms.", "1"],
                                                            ["edit", "Same text.", "Same text.", "2"], ["edit", "Acme shipped it.", "Akmee shipped it.", "3"], ["unblockHeard", "acme"],
                                                            ["edit", "Acme shipped it.", "Akme shipped it.", "4"], ["deleteLearned", "0"], ["edit", "Acme again", "Akme again", "5"],
                                                            ["learning", "off"], ["edit", "Zorblat", "Zorblatt", "6"]])
        seq("accept to decline never promotes", [rec("s0", "I accept", "I decline"), settle("s0"), rec("s1", "I accept", "I decline", 1), settle("s1"), rec("s2", "I accept", "I decline", 2), settle("s2")])
        seq("case-only and punctuation-only are not stored", [rec("a", "acme", "Acme"), rec("b", "Acme", "Acme.")])
        seq("conflicting second fix suppresses until reset", [rec("a", "Acme", "Akme"), settle("a"), ["apply", "Acme"], rec("b", "Acme", "Acmee"), settle("b"), ["apply", "Acme"],
                                                               rec("c", "Acme", "Akme"), settle("c"), ["reset"], rec("d", "Acme", "Akme"), settle("d")])
        seq("conflict after promotion", [rec("s0", "Acme", "Akme"), settle("s0"), rec("s1", "Acme", "Akme"), settle("s1"), rec("s2", "Acme", "Acmee"), settle("s2")])
        seq("revert removes evidence and withdraws the promotion", [rec("a", "Acme", "Akme"), ["revert", "a"], settle("a"), rec("b", "Acme", "Akme"), settle("b"), ["revert", "b"], ["apply", "Acme"]])
        seq("rejected later snapshot replaces earlier evidence", [rec("a", "Acme", "Akme"), rec("a", "Acme", "Acme!")])
        seq("deleted learned entry is not re-promoted", [rec("a", "Acme", "Akme"), settle("a"), rec("b", "Acme", "Akme"), settle("b"), rec("c", "Acme", "Akme"), ["deleteLearned", "0"], ["apply", "Acme"], settle("c"),
                                                          rec("d", "Acme", "Akme"), rec("x", "Zorblat", "Zorblatt"), settle("x"), rec("y", "Zorblat", "Zorblatt"), settle("y"), ["dictDelete", "0"], rec("z", "Zorblat", "Zorblatt")])
        seq("user reverting a learned replacement", [rec("s0", "Acme", "Akme"), settle("s0"), rec("s1", "Acme", "Akme"), settle("s1"), rec("r", "Akme", "Acme"), settle("r")])
        seq("case fix with a lexicon", [["lex", "list", "call", "the"], rec("first", "call jira", "call Jira"), settle("first"), ["apply", "open jira now"], rec("r", "Jira", "jira"), settle("r"),
                                        ["unblockSpelling", "jira"], ["unblockHeard", "jira"], rec("s", "call jira", "call Jira")])
        seq("two mishearings of one spelling share an entry", [rec("s0", "Akash", "Aakash"), settle("s0"), rec("s1", "Akash", "Aakash"), settle("s1"), rec("s2", "Aakesh", "Aakash"), settle("s2"),
                                                                rec("s3", "Aakesh", "Aakash"), settle("s3"), ["deleteLearned", "0"], rec("s4", "Akesh", "Aakash")])
        seq("legacy promotion without kind", [["addLearned", "Akme", "Acme"], ["legacy", legacyPromotion], rec("r", "Akme", "Acme"), settle("r")])
        seq("legacy hint is upgraded by the next fix", [["addLearned", "Akme", ""], ["legacy", legacyHint], rec("x", "Zorblat", "Zorblatt"), settle("x"), ["apply", "Ask Acme"],
                                                        rec("y", "Acme", "Akme"), settle("y"), ["apply", "Ask Acme"], ["undo", "0"], ["apply", "Ask Acme"]])
        seq("manual entry already owns the source", [["addManual", "Akme", "Acme"], rec("s", "Acme", "Akme"), settle("s")])
        seq("ledger lexicon rejects an ordinary-word swap", [["lex", "list", "lunch", "launch"], rec("s", "lunch", "launch"), settle("s")])
        seq("garbled name learned from one edit", [["lex", "list", "errors", "across", "the", "or", "codex", "app", "latest", "version"], rec("s", "errors across the ChadJuT or Codex app", "errors across the ChatGPT or Codex app"),
                                                   settle("s"), ["apply", "the ChadJuT app"]])
        seq("learning off records nothing", [rec("a", "Acme", "Akme"), ["learning", "off"], rec("s0", "Acme", "Akme"), settle("s0"), rec("s1", "Acme", "Akme"), settle("s1"), ["reopen"]])
        seq("persistence across launches", [rec("a", "Acme", "Akme"), settle("a"), rec("c", "Zorblat", "Zorblatt"), ["reopen"], rec("b", "Acme", "Akme"), settle("b"), ["reopen"], ["revert", "a"]])
        seq("lexicon sequence with the file lexicon", [["lex", "file"], rec("a", "git hub", "GitHub"), settle("a"), ["apply", "open git hub now"], rec("b", "Jon", "John"), settle("b"), rec("c", "ChadJuT", "ChatGPT"), settle("c"),
                                                        ["apply", "the ChadJuT and Jon and git hub"], ["reset"], ["apply", "the ChadJuT and Jon and git hub"]])
        seq("two fixes in one dictation and a deleted manual entry", [rec("a", "call ACME about Gira", "call Akme about Jira"), settle("a"), ["apply", "call Acme about Gira"], ["addManual", "Quorbix", "kworbix"], ["deleteAny", "2"]])
        seq("fix then typing after the span", [rec("a", "pay Acme", "pay Akme and more"), settle("a"), ["apply", "pay Acme"]])
        seq("tombstone then unblock spelling", [rec("a", "Acme", "Akme"), settle("a"), ["undo", "0"], rec("b", "Acme", "Akme"), ["unblockSpelling", "Akme"], rec("c", "Acme", "Akme"), settle("c")])
                print("{\n" + [lines("filter", filter), lines("replacements", replacements), lines("plausible", plausible), lines("keys", keys), lines("embeds", embeds), lines("ledger", ledgerCases)].joined(separator: ",\n") + "\n}")
    }
}
