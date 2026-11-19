// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning VoiceCues Retention
import Foundation

// Golden outputs of DictionaryStore (apply, relevantEntries, vocabularyTerms, helperVocabulary, entry(forVariant), skipLearnedVariant,
// soundsLike, validation and persistence sequences) and DictionarySuggestions over synthetic dictionaries and the cleanup corpus inputs.
// Lexicons: "none" (nil), "file" (windows/golden/lexicon.txt, exact-word lookup) or "list" (the case's own words). One case per line.
// Run from the repository root.
let lexiconWords: Set<String> = Set(try! String(contentsOfFile: "windows/golden/lexicon.txt", encoding: .utf8).split(separator: "\n").map(String.init))
func lexicon(_ mode: String, _ words: [String]) -> ((String) -> Bool)? {
    switch mode {
    case "file": return { lexiconWords.contains($0) }
    case "list": let set = Set(words); return { set.contains($0) }
    default: return nil
    }
}

struct Ent: Encodable { var preferred: String, variants: [String], source: String, enabled: Bool }
struct Dict: Encodable { var name: String, entries: [Ent] }
struct ApplyCase: Encodable { var dict: Int, lex: String, words: [String], input: String, output: String }
struct RelevantCase: Encodable { var dict: Int, input: String, result: [String] }
struct InfoCase: Encodable { var dict: Int, lex: String, words: [String], vocabulary: [String], helper: [String], command: [Bool], skip: [[Bool]] }
struct VariantCase: Encodable { var dict: Int, phrase: String, preferred: String? }
struct SoundCase: Encodable { var a: String, b: String, value: Bool }
struct Sug: Encodable { var preferred: String, heard: [String], dictations: Int }
struct SuggestCase: Encodable {
    var entries: [Ent], dictations: [[String]], dismissed: [String], lex: String, words: [String]
    var minDictations: Int, minSingle: Int, limit: Int, result: [Sug]
}
struct StoreCase: Encodable { var label: String, ops: [[String]], results: [String], states: [String] }

let base = Date(timeIntervalSince1970: 1_790_000_000)
func ent(_ preferred: String, _ variants: [String] = [], learned: Bool = false, enabled: Bool = true) -> Ent {
    Ent(preferred: preferred, variants: variants, source: learned ? "learned" : "manual", enabled: enabled)
}

func build(_ dir: URL, _ d: Dict) throws -> DictionaryStore {
    let store = try DictionaryStore(directory: dir)
    for (i, e) in d.entries.enumerated() {
        try store.add(preferred: e.preferred, variants: e.variants, source: e.source == "learned" ? .learned : .manual, enabled: e.enabled, now: base.addingTimeInterval(Double(i) * 60))
    }
    return store
}

struct Entry: Decodable { var input: String, expected: String }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

func describe(_ store: DictionaryStore) -> String {
    store.entries.map { "\($0.preferred)=\($0.variants.joined(separator: "|"))/\($0.source.rawValue)/\($0.enabled ? 1 : 0)" }.joined(separator: ";")
}

@main struct Gen {
    static func main() throws {
        let corpus = try JSONDecoder().decode([Entry].self, from: Data(contentsOf: URL(fileURLWithPath: "app/cleanup-corpus.json")))
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-golden-dictionary-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }

        let dicts: [Dict] = [
            Dict(name: "corpus", entries: [
                ent("Bredd", ["bread"]), ent("Mylk Co", ["milk", "the milk"]), ent("Tuesdee", ["tuesday"]), ent("Sorrowful", ["sorry"]), ent("Akme", ["acme"]),
                ent("question mark", ["quest mark"]), ent("Marc", ["mark"]), ent("Meetly", ["meeting"], learned: true), ent("Bluebird", ["blue"], learned: true), ent("Apple Inc", ["apples"], learned: true),
                ent("Thirdy", ["third", "the third"], learned: true), ent("Entros"), ent("Kathrynn"), ent("Katherine"), ent("Kubernetes"), ent("clouddev"),
                ent("Notionally"), ent("Buzzati", ["Buzari"], learned: true), ent("Demonz", ["demos"], enabled: false), ent("full stop", ["fool stop"]),
                ent("at sign", ["add sign"]), ent("Deal, Inc", ["big deal"]),
            ]),
            Dict(name: "check", entries: [
                ent("Akme", ["Acme", "ack me"]), ent("New York City", ["new york city", "nyc"]), ent("New Yorker", ["new york"]), ent("kubectl", ["cube cuddle"]), ent("iPhone", ["i phone"]),
            ]),
            Dict(name: "commands", entries: [
                ent("question mark", ["Kocia Mark"]), ent("Marc", ["mark"]), ent("Kocia Inc", ["kocia mark please"]), ent("new line", ["nu line"]), ent("period", ["peer ode"]),
                ent("at the rate", ["at there it"]),
            ]),
            Dict(name: "safety", entries: [
                ent("clouddev", ["Cloud", "cloudnip"], learned: true), ent("Akme", ["Acme"], learned: true), ent("Zed", ["zero"]), ent("GitHub", ["git hub"], learned: true),
                ent("CLOUDDEV", ["cloud"], learned: true, enabled: false), ent("Cerebras", ["Serbrass"], learned: true),
            ]),
        ]
        var dictionaryStores: [DictionaryStore] = []
        for (i, d) in dicts.enumerated() { dictionaryStores.append(try build(root.appendingPathComponent("d\(i)"), d)) }

        let cd = ["cloud", "zero", "cipher"]
        let hand: [String] = [
            "Acme and Acmeville", "Acme's plan, Acme-based, MyAcme, acme.", "call ACME now", "please ack me later", "ack\nme", "ack, me", "I love new york city.", "I love new york.",
            "Cube cuddle is installed. cube cuddle again", "I phone you. My i phone", "Acne and Akmee and acmes", "café ☕ Acme 🙂", "", "Is it done kocia mark", "Kocia Mark", "kocia mark please", "ask mark",
            "Cloud zero cloudnip Acme git hub", "Cloud", "cloud zero", "ask kathryn about it", "ask Kathryn", "see catherine today", "kubernetes and Kubernetes", "send intros and Intros",
            "Hello.\"Acme\" said (acme) and 'acme' ...Acme", "x\n\nacme", "First sentence. acme second. Acme third! cube cuddle? kocia mark", "\"cube cuddle\" and “cube cuddle”",
            "acme\u{2019}s and Acme\u{2019}s", "Serbrass said hi", "serbrass", "the Buzari report", "Buzzati Buzari buzzati", "peer ode", "nu line here", "at there it", "add sign", "fool stop. Fool stop",
            "milk and the milk", "The Milk", "buy bread, milk. Tuesday sorry", "third and the third", "meeting meetings", "Demos demonz", "Notionally notional notion", "tab\tcube\tcuddle", "cube  cuddle", "cube \n cuddle",
            "ZOË acme", "日本語 acme 日本語", "👨‍👩‍👧‍👦 acme", "a\u{2028}acme", "ACME\r\nacme", "\u{201C}acme", "[acme]", "({acme})", "  acme", ".acme", "!acme", "end?acme", "wait... acme", "e\u{301}acme acme\u{301}",
        ]
        var applies: [ApplyCase] = []
        func apply(_ di: Int, _ lex: String, _ words: [String], _ input: String) {
            applies.append(ApplyCase(dict: di, lex: lex, words: words, input: input, output: dictionaryStores[di].apply(to: input, isCommonWord: lexicon(lex, words))))
        }
        for e in corpus { apply(0, "none", [], e.input); apply(0, "file", [], e.input) }
        for text in hand { for di in dicts.indices { apply(di, "none", [], text); apply(di, "file", [], text) } }
        for text in hand { apply(3, "list", cd, text) }

        // The store's own lexicon (set once where the store is built) is used when no override is passed.
        var relevant: [RelevantCase] = []
        let relevantInputs = ["Ask Acme about it", "nothing here", "Akme already right", "Acmeville", "deploy it to coopernetties", "ship cloud dev today", "Can we meet for lunch tomorrow at the office?",
                              "cube cuddle", "kocia mark", "question mark", "new york city and nyc", "Is it done", "kathryn went", "send the meeting notes to Sally", "the buzari report", "serbrass", "node js", "Kubernetes cluster",
                              "ack me", "ACK ME", "demos intros", "a b c", "", "the third milk"]
        for di in dicts.indices { for text in relevantInputs { relevant.append(RelevantCase(dict: di, input: text, result: dictionaryStores[di].relevantEntries(for: text).map(\.preferred))) } }
        // Extra terms for the near-match path.
        let nearDict = Dict(name: "near", entries: [ent("Kubernetes"), ent("clouddev"), ent("Sean"), ent("Serebrass"), ent("Proxmox"), ent("Node.js"), ent("Zorblatt")])
        let nearStore = try build(root.appendingPathComponent("near"), nearDict)
        let dictsAll = dicts + [nearDict]
        dictionaryStores.append(nearStore)
        for text in relevantInputs + ["coopernetties cloud dev", "mean what you say", "Zorblat", "node js rocks", "prox mox"] {
            relevant.append(RelevantCase(dict: 4, input: text, result: nearStore.relevantEntries(for: text).map(\.preferred)))
        }

        var infos: [InfoCase] = []
        for (di, store) in dictionaryStores.enumerated() {
            for (lex, words) in [("none", [String]()), ("file", []), ("list", cd)] {
                store.isCommonWord = lexicon(lex, words)
                let helper = store.helperVocabulary.map { item -> String in
                    let aliases = item["aliases"] as? [String]
                    return (item["text"] as! String) + (aliases.map { "=" + $0.joined(separator: "|") } ?? "")
                }
                let skip = store.entries.map { e in e.variants.map { DictionaryStore.skipLearnedVariant($0, entry: e, isCommonWord: lexicon(lex, words)) } }
                infos.append(InfoCase(dict: di, lex: lex, words: words, vocabulary: store.vocabularyTerms, helper: helper,
                                      command: store.entries.map { DictionaryStore.isCommandTarget($0) }, skip: skip))
            }
            store.isCommonWord = nil
        }
        var variants: [VariantCase] = []
        for (di, store) in dictionaryStores.enumerated() {
            for phrase in ["acme", "ACME", "Acme Corp", "ack me", "ack, me", "cube cuddle", "new york", "kocia mark", "mark", "milk", "the milk", "the  milk", "buzari", "git hub", "cloud", "Cloud", "zero", "serbrass", "", "  ", "a,b", "demos"] {
                variants.append(VariantCase(dict: di, phrase: phrase, preferred: store.entry(forVariant: phrase)?.preferred))
            }
        }
        var sounds: [SoundCase] = []
        for (a, b) in [("mean", "sean"), ("serbrass", "cerebras"), ("buzari", "buzzati"), ("akme", "acme"), ("acme", "akme"), ("kubernetes", "coopernetties"), ("abc", "abcd"), ("abcd", "abcd"), ("", ""),
                       ("lunch", "launch"), ("casual", "causal"), ("shelfmud", "shelfmate"), ("cubectal", "kubectl"), ("philip", "filip"), ("jon", "john"), ("katherine", "catherine"), ("proxmox", "proxmoxx"), ("abcdef", "abcdeg"),
                       ("tokens", "token"), ("intros", "entros"), ("zorblat", "zorblatt")] {
            sounds.append(SoundCase(a: a, b: b, value: DictionaryStore.soundsLike(a, b)))
        }

        // Suggestions.
        var suggests: [SuggestCase] = []
        func suggest(_ entries: [Ent], _ dictations: [(String, String)], dismissed: [String] = [], lex: String, words: [String], minDictations: Int = 2, minSingle: Int = 4, limit: Int = 12) {
            let stored = entries.map { DictionaryEntry(id: UUID(), preferred: $0.preferred, variants: $0.variants, source: .manual, createdAt: base, enabled: true) }
            let common = lexicon(lex, words) ?? { _ in false }
            let found = DictionarySuggestions.find(dictations.map { (raw: $0.0, final: $0.1) }, entries: stored, dismissed: Set(dismissed), isCommonWord: common,
                                                   minDictations: minDictations, minSingle: minSingle, limit: limit)
            suggests.append(SuggestCase(entries: entries, dictations: dictations.map { [$0.0, $0.1] }, dismissed: dismissed, lex: lex, words: words, minDictations: minDictations, minSingle: minSingle, limit: limit,
                                        result: found.map { Sug(preferred: $0.preferred, heard: $0.heard, dictations: $0.dictations) }))
        }
        let commonWords = ["we", "use", "for", "the", "tokens", "ask", "about", "send", "to", "today", "call", "and", "fast", "is", "very"]
        let said: [(String, String)] = [
            ("we use Serbrass for the tokens", "We use Cerebras for the tokens."), ("ask Serebrass about tokens", "Ask Cerebras about tokens."), ("Serobrass is very fast", "Serobrass is very fast."),
            ("send Buzari's tokens today", "Send Buzzati's tokens today."), ("call Buzati today", "Call Buzati today."), ("send Proxmox tokens", "Send Proxmox tokens."), ("call Akme and Akme", "Call Akme and Akme."),
        ]
        suggest([ent("Akme")], said, lex: "list", words: commonWords)
        suggest([], said, dismissed: ["serbrass", "serebrass", "serobrass"], lex: "list", words: commonWords)
        suggest([], said + Array(repeating: ("ask Proxmox", "Ask Proxmox."), count: 3), lex: "list", words: commonWords)
        suggest([], said, lex: "list", words: commonWords, minDictations: 3)
        suggest([], said, lex: "list", words: commonWords, minDictations: 1, minSingle: 1, limit: 2)
        suggest([], said, lex: "none", words: [])
        suggest([ent("Buzzati", ["Buzari"])], said, lex: "file", words: [])
        suggest([], [], lex: "file", words: [])
        suggest([], [("Zorblatt’s plan and Zorblatt's", "Zorblatt’s plan"), ("zorblatt again", "Zorblatt again"), ("O’Brienx and O'Brienx", "O'Brienx")], lex: "file", words: [])
        suggest([], corpus.map { ($0.input, $0.expected) }, lex: "file", words: [], minDictations: 2, minSingle: 3)
        suggest([], corpus.map { ($0.input, $0.expected) }, lex: "file", words: [], minDictations: 2, minSingle: 2, limit: 50)

        // Store validation and persistence sequences (ids are random, so entries are addressed by index).
        var stores: [StoreCase] = []
        func seq(_ label: String, _ ops: [[String]]) {
            let dir = root.appendingPathComponent("seq-\(UUID())")
            var store = try! DictionaryStore(directory: dir)
            var results: [String] = [], states: [String] = []
            var clock = 0
            for op in ops {
                clock += 1
                let a = Array(op.dropFirst())
                func id(_ s: String) -> UUID { Int(s)! < store.entries.count ? store.entries[Int(s)!].id : UUID() }
                do {
                    switch op[0] {
                    case "add":
                        try store.add(preferred: a[0], variants: a[1].isEmpty ? [] : a[1].components(separatedBy: "|"), source: a[2] == "learned" ? .learned : .manual, enabled: a[3] == "1",
                                      now: base.addingTimeInterval(Double(clock)))
                        results.append("ok")
                    case "update":
                        let current = Int(a[0])! < store.entries.count ? store.entries[Int(a[0])!] : DictionaryEntry(id: UUID(), preferred: "x", variants: [], source: .manual, createdAt: base, enabled: true)
                        var changed = current
                        changed.preferred = a[1]; changed.variants = a[2].isEmpty ? [] : a[2].components(separatedBy: "|"); changed.enabled = a[3] == "1"
                        try store.update(changed); results.append("ok")
                    case "delete": try store.delete(id: id(a[0])); results.append("ok")
                    case "resetLearned": results.append("removed:\(try store.resetLearned().count)")
                    case "reopen": store = try DictionaryStore(directory: dir); results.append("ok")
                    case "apply": results.append(store.apply(to: a[0]))
                    case "damage":
                        try Data("not json".utf8).write(to: dir.appendingPathComponent(DictionaryStore.fileName)); results.append("ok")
                    case "version":
                        try Data(#"{"entries":[],"version":2}"#.utf8).write(to: dir.appendingPathComponent(DictionaryStore.fileName)); results.append("ok")
                    case "reopenOrFail":
                        do { store = try DictionaryStore(directory: dir); results.append("ok") } catch let e as StoreError { results.append("error:" + e.description) } catch { results.append("error") }
                    default: fatalError("unknown op \(op[0])")
                    }
                } catch let e as StoreError { results.append("error:" + e.description) } catch { results.append("error") }
                states.append(describe(store))
            }
            try? FileManager.default.removeItem(at: dir)
            stores.append(StoreCase(label: label, ops: ops, results: results, states: states))
        }
        let long = String(repeating: "w", count: 101), ok100 = String(repeating: "w", count: 100)
        seq("validation of preferred and variants", [
            ["add", "", "", "manual", "1"], ["add", "   ", "", "manual", "1"], ["add", "!!!", "", "manual", "1"], ["add", long, "", "manual", "1"], ["add", ok100, "", "manual", "1"],
            ["add", "Akme", "Acme|ack me", "manual", "1"], ["add", "Other", "acme", "manual", "1"], ["add", "Other", "ACME", "manual", "1"], ["add", "X", "a,b", "manual", "1"],
            ["add", "X", "one two three four five six", "manual", "1"], ["add", "X", "one two three four five six seven", "manual", "1"], ["add", "X", long, "manual", "1"],
            ["add", "Dup", "same|Same|SAME", "manual", "1"], ["add", "Off", "acme", "manual", "0"], ["add", "  New   York  ", "  new   york ", "manual", "1"], ["add", "Quote", "it\u{2019}s|its", "manual", "1"],
            ["add", "Café", "cafe|CAFÉ", "manual", "1"], ["add", "Z", "|  |ok", "manual", "1"], ["add", "Q", "a-b|c'd", "manual", "1"],
        ])
        seq("update, delete and reset", [
            ["add", "Akme", "Acme", "manual", "1"], ["add", "Zylo", "xylo", "learned", "1"], ["add", "Kubectl", "cube cuddle", "manual", "1"],
            ["update", "0", "Akme", "Acme|acme corp", "1"], ["update", "0", "Akme", "cube cuddle", "1"], ["update", "0", "Akme", "cube cuddle", "0"], ["apply", "Acme cube cuddle acme corp"],
            ["update", "9", "x", "", "1"], ["update", "1", "Zylo2", "xylo2", "1"], ["delete", "9"], ["delete", "0"], ["apply", "Acme xylo2"], ["resetLearned"], ["resetLearned"], ["reopen"],
            ["update", "0", "Kubectl", "", "1"], ["apply", "cube cuddle"],
        ])
        seq("persistence and bad files", [
            ["add", "Akme", "Acme", "manual", "1"], ["reopen"], ["apply", "Acme"], ["damage"], ["reopenOrFail"], ["version"], ["reopenOrFail"],
        ])
        seq("apply with learned and manual entries", [
            ["add", "Beta", "alpha", "learned", "1"], ["add", "Gamma", "alpha", "manual", "1"], ["apply", "alpha alpha."], ["add", "kubectl", "cube cuddle", "manual", "1"], ["apply", "Cube cuddle. cube cuddle"],
            ["add", "iPhone", "i phone", "manual", "1"], ["apply", "I phone. i phone"], ["add", "question mark", "quest mark|ques mark", "manual", "1"], ["apply", "is it ques mark and quest mark"],
        ])

        print("{\n" + [lines("dictionaries", dictsAll), lines("apply", applies), lines("relevant", relevant), lines("info", infos), lines("variants", variants), lines("sounds", sounds),
                       lines("suggestions", suggests), lines("store", stores)].joined(separator: ",\n") + "\n}")
    }
}
