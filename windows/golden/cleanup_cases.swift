import Foundation

// Shared inputs of the cleanup golden generators (golden_rule_cleaner, golden_cleanup_guard, golden_cleanup_pipeline,
// golden_cleanup_prompt), compiled with them through their "// deps:" line. Synthetic text only:
// - every entry of app/cleanup-corpus.json, with its own options;
// - the string literals of app/check_cleanup.swift, read from its source, and the (raw, output) pairs it checks;
// - the adversarial outputs check_cleanup.swift builds from the corpus (its mutation functions, copied below);
// - edge cases: empty, whitespace, non-ASCII names, emoji, CJK, curly quotes, numbers, money, dates, long text.

struct GoldenCorpusEntry: Decodable { var id: String; var input: String; var options: CleanupOptions; var expected: String }

enum GoldenCases {
    static let corpus: [GoldenCorpusEntry] = try! JSONDecoder().decode([GoldenCorpusEntry].self, from: Data(contentsOf: URL(fileURLWithPath: "app/cleanup-corpus.json")))

    /// Swift string literal escapes; nil for a literal with interpolation.
    static func unescape(_ s: String) -> String? {
        var out = "", it = Array(s), i = 0
        while i < it.count {
            let c = it[i]
            guard c == "\\", i + 1 < it.count else { out.append(c); i += 1; continue }
            let n = it[i + 1]
            switch n {
            case "n": out += "\n"; i += 2
            case "t": out += "\t"; i += 2
            case "r": out += "\r"; i += 2
            case "0": out += "\0"; i += 2
            case "\\", "\"", "'": out.append(n); i += 2
            case "u":
                guard let close = it[(i + 2)...].firstIndex(of: "}"), let v = UInt32(String(it[(i + 3)..<close]), radix: 16), let sc = Unicode.Scalar(v) else { return nil }
                out.unicodeScalars.append(sc); i = close + 1
            default: return nil
            }
        }
        return out
    }

    static let checkSource = try! String(contentsOfFile: "app/check_cleanup.swift", encoding: .utf8)
    static let literalPattern = #""((?:[^"\\\n]|\\.)*)""#

    static func matches(_ pattern: String) -> [[String]] {
        let r = try! NSRegularExpression(pattern: pattern), ns = checkSource as NSString
        return r.matches(in: checkSource, range: NSRange(location: 0, length: ns.length)).compactMap { m in
            let groups = (1..<m.numberOfRanges).map { ns.substring(with: m.range(at: $0)) }.compactMap(unescape)
            return groups.count == m.numberOfRanges - 1 ? groups : nil
        }
    }

    /// Every plain string literal in check_cleanup.swift, in source order, without duplicates.
    static let checkLiterals: [String] = unique(matches(literalPattern).map { $0[0] })
    /// Tuples that start with two strings, and raw:/cleaned: (or output:) arguments.
    static let checkPairs: [(String, String)] = uniquePairs(
        matches(#"\(\s*"# + literalPattern + #"\s*,\s*"# + literalPattern).map { ($0[0], $0[1]) }
        + matches(#"raw:\s*"# + literalPattern + #"\s*,\s*(?:cleaned|output):\s*"# + literalPattern).map { ($0[0], $0[1]) })

    static let edges: [String] = [
        "", " ", "   \t  ", "\n", "\n\n", " \n ", "a\r\nb", "x\u{2028}y\u{00A0}z\u{3000}w", "Zoë met Zoe\u{0301} today", "Ñandú and José", "Send it to Ægir and Ðora",
        "😀", "Hi 😀 there!", "👨‍👩‍👧‍👦 family trip on Friday", "🇺🇸 flag", "👍🏽 ok", "日本語のテキスト", "你好，世界。", "Tokyo 東京 trip", "한국어 문장",
        "“Quoted speech.” She said \"hello\" and left.", "It\u{2019}s fine, don\u{2019}t worry", "don't won't can't ain't", "it doesn and we couldn go",
        "‘single’ quotes and “curly, um, quotes”", "say \"unbalanced quote", "mid—dash and – en and -- double",
        "COVID-19 and 3-4 and 4th, 1st 20th 3pm 10:30 1,000.50 $5 $1,200, 50% 12.5%", "Pay $1,200 by March 3rd, 2026 at 9:30 a.m.",
        "sixty-eight twenty-first Twenty-One", "Mr. Smith, Dr. Who, Ms. Alvarez and Prof. X", "End. Next one? Yes! Ok; fine: sure, done...",
        "um, uh... Um hmm", "one, two,, three ,, , lead", "Hello .  World ,  again !", "tab\tseparated\tvalues", "  leading and trailing  ",
        "First line.\nSecond line here today.\nThird line is longer than three words.\n\nFourth paragraph of text now.",
        "1. First item\n2. Second item\n- bullet", "٣ ١٢ ½ ² 一二三 Ⅳ ①", "email@example.com https://example.org/path?q=1 #hashtag @handle",
        "\u{200B}zero\u{200D}width\u{FEFF} i\u{200B} think", "ß straße İstanbul ﬁsh", "Σίσυφος ΣΑΣ", "x\u{301}i went", "i 𝐀i went",
        "Paint it red. 𝐁lue instead.", "Meet on 𝐌onday. Thursday instead.", "Call 𝒜lex. Sorry, Bob.", "write an email to 𝒜lex saying hi, thanks, 𝒦im",
        "Order 𝟑 boxes. No, 4.", "pay \u{FF13}\u{FF10} dollars, no, 40", "the café, sorry, the bar", "call Zoe\u{301}. Actually, Bob.",
        "um so i think i\u{2019}m ready, i mean, i\u{2019}ll go", "Meet Zoë on Tuesday, sorry, Thursday.", "Pay José 200 dollars, no, 300 dollars.",
        "write an email to Zoë saying the plan is fine, thanks, Ægir", "hashtag 東京 and at the rate Zoë",
        String(repeating: "We should ship the build on Friday after the review. ", count: 30),
        String(repeating: "um so the plan is, like, fine and we ship it on Monday, sorry, Tuesday. ", count: 12),
        String(repeating: "word ", count: 400),
    ]

    /// Corpus inputs and labels, check_cleanup.swift literals and the edges.
    static let texts: [String] = unique(corpus.flatMap { [$0.input, $0.expected] } + checkLiterals + edges)

    static func unique(_ xs: [String]) -> [String] { var seen = Set<[UInt32]>(); return xs.filter { seen.insert($0.unicodeScalars.map(\.value)).inserted } }
    static func uniquePairs(_ xs: [(String, String)]) -> [(String, String)] {
        var seen = Set<[UInt32]>()
        return xs.filter { seen.insert($0.0.unicodeScalars.map(\.value) + [0x110000] + $0.1.unicodeScalars.map(\.value)).inserted }
    }

    // MARK: check_cleanup.swift adversarialCases' corpus mutations (copied from there; keep in step)

    static func insertBeforeEnd(_ s: String, _ insert: String) -> String {
        var t = s
        var tail = ""
        while let last = t.last, ".?!\"\u{201D}".contains(last) { tail = String(last) + tail; t.removeLast() }
        return t + insert + tail
    }

    static func dropFirstNegation(_ s: String) -> String? {
        var tokens = CleanupText.tokenize(s)
        guard let i = tokens.firstIndex(where: { $0.kind == .word && CleanupText.isNegation($0.lower) }) else { return nil }
        let w = tokens[i].lower
        let replacement: String?
        switch w {
        case "can't", "cannot", "cant": replacement = "can"
        case "won't", "wont": replacement = "will"
        case "ain't": replacement = "am"
        default:
            if w.hasSuffix("n't") { replacement = String(tokens[i].text.dropLast(3)) } else { replacement = nil }
        }
        if let r = replacement {
            tokens[i].text = r
        } else {
            tokens.remove(at: i)
            if i < tokens.count, tokens[i].kind == .space { tokens.remove(at: i) }
        }
        return CleanupText.render(tokens)
    }

    static func changeFirstNumber(_ s: String) -> String? {
        var tokens = CleanupText.tokenize(s)
        for i in tokens.indices where tokens[i].kind == .word {
            if i + 1 < tokens.count, tokens[i + 1].text == ".", i == 0 || tokens[i - 1].text == "\n" { continue }
            let w = tokens[i].lower
            if let v = CleanupText.cardinals[w], v < 20 {
                tokens[i].text = v == 7 ? "eight" : "seven"; return CleanupText.render(tokens)
            }
            if let d = tokens[i].text.firstIndex(where: \.isNumber) {
                var t = tokens[i].text
                t.replaceSubrange(d...d, with: t[d] == "9" ? "8" : "9")
                tokens[i].text = t; return CleanupText.render(tokens)
            }
        }
        return nil
    }

    static func dropFirstName(_ s: String) -> String? {
        let words = CleanupGuard.words(s)
        guard let name = words.first(where: \.isName) else { return nil }
        var tokens = CleanupText.tokenize(s)
        guard let i = tokens.firstIndex(where: { $0.kind == .word && $0.text == name.text && $0.text.first?.isUppercase == true && !CleanupText.isSentenceStart(tokens, tokens.firstIndex(of: $0)!) }) else { return nil }
        tokens.remove(at: i)
        if i < tokens.count, tokens[i].kind == .space { tokens.remove(at: i) }
        return CleanupText.render(tokens)
    }

    struct Mutation { var label: String; var raw: String; var output: String; var options: CleanupOptions }

    static let mutations: [Mutation] = corpus.flatMap { e -> [Mutation] in
        let base = e.expected
        let windowFree = !e.options.corrections || CleanupGuard.correctionWindow(e.input).isEmpty
        var out: [Mutation] = []
        func add(_ label: String, _ o: String?) { if let o, o != base { out.append(Mutation(label: "\(e.id) \(label)", raw: e.input, output: o, options: e.options)) } }
        add("prefix-sure", "Sure! " + base)
        add("append-answer", base + " The answer is Paris.")
        add("append-number", insertBeforeEnd(base, " 17"))
        add("append-name", insertBeforeEnd(base, " and Gerald"))
        add("append-poem", base + "\nRoses bloom and rivers run,\nshining bright beneath the sun.")
        if windowFree {
            add("drop-negation", dropFirstNegation(base))
            add("change-number", changeFirstNumber(base))
            add("drop-name", dropFirstName(base))
        }
        return out
    }

    /// Option sets the generators run every case with, by name (the C# tests build the same sets).
    static let optionSets: [(String, CleanupOptions)] = [
        ("standard", .standard), ("faithful", .faithful), ("default", CleanupOptions()),
        ("fillers", CleanupOptions(fillers: true, corrections: false, lists: false)),
        ("corrections", CleanupOptions(fillers: false, corrections: true, lists: false)),
        ("lists", CleanupOptions(fillers: false, corrections: false, lists: true)),
        ("punctuation", CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true)),
        ("spoken", CleanupOptions(fillers: false, corrections: false, lists: false, spoken: true)),
        ("polish", CleanupOptions(punctuation: true, polish: true)),
        ("professional", CleanupOptions(punctuation: true, professional: true)),
        ("polishSpoken", CleanupOptions(punctuation: true, polish: true, spoken: true)),
        ("professionalSpoken", CleanupOptions(punctuation: true, professional: true, spoken: true)),
        ("leanPolish", CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true, polish: true)),
        ("leanProfessional", CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true, professional: true)),
    ]
    static let trustedSets: [(String, CleanupOptions)] = [
        ("trustedPolish", CleanupOptions(punctuation: true, polish: true, spoken: true, trustModel: true)),
        ("trustedProfessional", CleanupOptions(polish: true, professional: true, trustModel: true)),
        ("trustedStrict", CleanupOptions(punctuation: true, spoken: true, trustModel: true)),
    ]

    static func encode(_ o: CleanupOptions) -> String {
        [("f", o.fillers), ("c", o.corrections), ("l", o.lists), ("p", o.punctuation), ("o", o.polish), ("r", o.professional), ("s", o.spoken), ("t", o.trustModel)]
            .filter(\.1).map(\.0).joined()
    }

    /// A fixed sound-alike rule for golden checks (the app's comes from the dictionary): same first letter, length within two.
    static func soundsLike(_ heard: String, _ term: String) -> Bool { heard.first == term.first && abs(heard.count - term.count) <= 2 }

    /// One JSON value per line inside a top-level object of arrays, so diffs show the changed case.
    static func emit(_ sections: [(String, [String])]) {
        print("{")
        for (n, (name, lines)) in sections.enumerated() {
            print("\"\(name)\": [")
            for (k, line) in lines.enumerated() { print(line + (k + 1 < lines.count ? "," : "")) }
            print("]" + (n + 1 < sections.count ? "," : ""))
        }
        print("}")
    }

    static func json<T: Encodable>(_ v: T) -> String {
        let enc = JSONEncoder(); enc.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return String(decoding: try! enc.encode(v), as: UTF8.self)
    }
}
