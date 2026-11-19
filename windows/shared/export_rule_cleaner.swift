// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup PrivateFiles Dictionary Learning Retention
import Foundation

// Exports RuleCleaner's constant tables and regex patterns (markCommands and atPhrases are in rule_cleaner_commands.json),
// CleanupSections.correctionOpening (Cleanup.swift, built from the rule engine's cues), and the ICU character sets that
// ICU's \b uses, so the C# port can match NSRegularExpression's word boundaries. Sets are sorted arrays.
struct Cue: Encodable { var words: [String]; var cue: String }
struct Pattern: Encodable { var pattern: String; var caseInsensitive: Bool; var dotMatchesLineSeparators: Bool }
struct Export: Encodable {
    var stutterWords: [String], commaKeepers: [String], cuePatterns: [Cue], apologyLead: [String], anchors: [String]
    var weekdays: [String], months: [String], monthsAndMay: [String], relativeTimes: [String], colors: [String]
    var strongListCues: [String], cardinalWords: [String], ordinalWords: [String]
    var commandBlockersBefore: [String], commandBlockersAfter: [String], dashBlockersBefore: [String], domainEndings: [String]
    var spokenScales: [String: Int], indianScales: [String]
    var insteadFragment: Pattern, splitCue: Pattern, emailFrame: Pattern, signOff: Pattern
    var correctionOpening: String
    /// Scalar ranges ("start-end" hex) where ICU's \w, \p{L} and \p{N} match, and where ICU's \b skips a character
    /// (Grapheme_Extend or Cf): .NET regex sees UTF-16 units and its own Unicode tables, so the port builds these classes itself.
    var icuWord: String, icuIgnorable: String, icuLetter: String, icuNumber: String
    /// Non-ASCII scalars (hex) that a case-insensitive ICU pattern matches with ASCII letters (their case folding): "17f" -> "s",
    /// "fb06" -> "st". .NET case-insensitivity uses simple folding only, so the port spells these out in pattern literals.
    var icuFolds: [String: String]
}

func folds() -> [String: String] {
    var out: [String: String] = [:]
    for v in UInt32(0x80)...0x10FFFF {
        guard let sc = Unicode.Scalar(v) else { continue }
        let s = String(sc), f = s.folding(options: .caseInsensitive, locale: nil)
        guard !f.isEmpty, f.unicodeScalars.allSatisfy({ ("a"..."z").contains($0) }) else { continue }
        let r = try! NSRegularExpression(pattern: "^" + f + "$", options: .caseInsensitive)
        if r.firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length)) != nil { out[String(v, radix: 16)] = f }
    }
    return out
}

func pattern(_ r: NSRegularExpression) -> Pattern {
    Pattern(pattern: r.pattern, caseInsensitive: r.options.contains(.caseInsensitive), dotMatchesLineSeparators: r.options.contains(.dotMatchesLineSeparators))
}

func scalarRanges(_ regex: String) -> String {
    let r = try! NSRegularExpression(pattern: regex)
    var out: [String] = [], start: UInt32? = nil, prev: UInt32 = 0
    func close() { if let s = start { out.append(String(s, radix: 16) + "-" + String(prev, radix: 16)); start = nil } }
    for v in UInt32(0)...0x10FFFF {
        guard let sc = Unicode.Scalar(v) else { close(); continue }
        let s = String(sc)
        if r.firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length)) != nil {
            if start == nil { start = v }; prev = v
        } else { close() }
    }
    close()
    return out.joined(separator: ",")
}

@main struct Gen { static func main() throws {
    let e = Export(
        stutterWords: RuleCleaner.stutterWords.sorted(), commaKeepers: RuleCleaner.commaKeepers.sorted(),
        cuePatterns: RuleCleaner.cuePatterns.map { Cue(words: $0.0, cue: "\($0.1)") },
        apologyLead: RuleCleaner.apologyLead.sorted(), anchors: RuleCleaner.anchors.sorted(),
        weekdays: RuleCleaner.weekdays.sorted(), months: RuleCleaner.months.sorted(), monthsAndMay: RuleCleaner.monthsAndMay.sorted(),
        relativeTimes: RuleCleaner.relativeTimes.sorted(), colors: RuleCleaner.colors.sorted(),
        strongListCues: RuleCleaner.strongListCues.sorted(), cardinalWords: RuleCleaner.cardinalWords, ordinalWords: RuleCleaner.ordinalWords,
        commandBlockersBefore: RuleCleaner.commandBlockersBefore.sorted(), commandBlockersAfter: RuleCleaner.commandBlockersAfter.sorted(),
        dashBlockersBefore: RuleCleaner.dashBlockersBefore.sorted(), domainEndings: RuleCleaner.domainEndings.sorted(),
        spokenScales: RuleCleaner.spokenScales, indianScales: RuleCleaner.indianScales.sorted(),
        insteadFragment: pattern(RuleCleaner.insteadFragment), splitCue: pattern(RuleCleaner.splitCue),
        emailFrame: pattern(RuleCleaner.emailFrame), signOff: pattern(RuleCleaner.signOff),
        correctionOpening: CleanupSections.correctionOpening,
        icuWord: scalarRanges(#"^\w$"#), icuIgnorable: scalarRanges(#"^[\p{Grapheme_Extend}\p{Cf}]$"#),
        icuLetter: scalarRanges(#"^\p{L}$"#), icuNumber: scalarRanges(#"^\p{N}$"#), icuFolds: folds())
    let enc = JSONEncoder()
    enc.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
    print(String(decoding: try enc.encode(e), as: UTF8.self))
}}
