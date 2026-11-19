// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention
import Foundation

// Exports the constant tables of CleanupText, CleanupTypes (ScreenContext) and CleanupGuard.grammarWords.
// Sets are sorted arrays; maps are objects (the encoder sorts keys).
struct Export: Encodable {
    var functionWords: [String], hesitations: [String], basicNegations: [String], negationCore: [String]
    var negationStems: [String], negationWords: [String], apostropheNegations: [String]
    var truncatedNegations: [String: [String]]
    var cardinals: [String: Int], ordinals: [String: Int]
    var abbreviations: [String], titles: [String], grammarWords: [String]
    var screenPrivateTypes: [String]
    var selectedLimit: Int, clipboardLimit: Int, windowLimit: Int, instructionsLimit: Int, sectioningDefaultTarget: Int
    var paragraphMinWords: Int
    // Swift's Character/String Unicode behavior, exported so the C# port does not depend on .NET's Unicode tables.
    // Ranges are ("cased" is the Unicode Cased property: Character.isUppercase/isLowercase need it) "start-end" hex pairs of scalars where Character.isLetter / isNumber / isWhitespace is true;
    // mappings are "scalar:result" (hex, result scalars joined by "+") where String.lowercased() / uppercased() changes a scalar.
    var unicode: [String: String]
}

func ranges(_ f: (Character) -> Bool) -> String { scalarRanges { f(Character($0)) } }

func scalarRanges(_ f: (Unicode.Scalar) -> Bool) -> String {
    var out: [String] = [], start: UInt32? = nil, prev: UInt32 = 0
    func close() { if let s = start { out.append(String(s, radix: 16) + "-" + String(prev, radix: 16)); start = nil } }
    for v in UInt32(0)...0x10FFFF {
        guard let sc = Unicode.Scalar(v) else { close(); continue }
        if f(sc) { if start == nil { start = v } else if v != prev + 1 { close(); start = v }; prev = v } else { close() }
    }
    close()
    return out.joined(separator: ",")
}

// Scalars with a non-zero canonical combining class, as "start-end:class" (hex ranges, decimal class); Hangul syllables are left to the algorithm.
func classes() -> String {
    var out: [String] = [], start: UInt32 = 0, prev: UInt32 = 0, cls: UInt8 = 0
    func close() { if cls != 0 { out.append(String(start, radix: 16) + "-" + String(prev, radix: 16) + ":" + String(cls)); cls = 0 } }
    for v in UInt32(0)...0x10FFFF {
        guard let sc = Unicode.Scalar(v) else { close(); continue }
        let c = sc.properties.canonicalCombiningClass.rawValue
        if c != 0 && c == cls && v == prev + 1 { prev = v; continue }
        close()
        if c != 0 { start = v; prev = v; cls = c }
    }
    close()
    return out.joined(separator: ",")
}

func mapping(skip: ClosedRange<UInt32>? = nil, _ f: (String) -> String) -> String {
    var out: [String] = []
    for v in UInt32(0)...0x10FFFF {
        guard let sc = Unicode.Scalar(v), !(skip?.contains(v) ?? false) else { continue }
        let s = String(sc), m = f(s)
        // String == is canonical equivalence, so compare scalars.
        if !m.unicodeScalars.elementsEqual(s.unicodeScalars) { out.append(String(v, radix: 16) + ":" + m.unicodeScalars.map { String($0.value, radix: 16) }.joined(separator: "+")) }
    }
    return out.joined(separator: ",")
}

@main struct Gen { static func main() throws {
let e = Export(
    functionWords: CleanupText.functionWords.sorted(), hesitations: CleanupText.hesitations.sorted(),
    basicNegations: CleanupText.basicNegations.sorted(), negationCore: CleanupText.negationCore.sorted(),
    negationStems: CleanupText.negationStems, negationWords: CleanupText.negationWords.sorted(),
    apostropheNegations: CleanupText.apostropheNegations.sorted(),
    truncatedNegations: CleanupText.truncatedNegations.mapValues { $0.sorted() },
    cardinals: CleanupText.cardinals, ordinals: CleanupText.ordinals,
    abbreviations: CleanupText.abbreviations.sorted(), titles: CleanupText.titles.sorted(),
    grammarWords: CleanupGuard.grammarWords.sorted(),
    screenPrivateTypes: ScreenContext.privateTypes.sorted(),
    selectedLimit: ScreenContext.selectedLimit, clipboardLimit: ScreenContext.clipboardLimit, windowLimit: ScreenContext.windowLimit,
    instructionsLimit: WritingModeLimits.instructions, sectioningDefaultTarget: CleanupSectioning.defaultTarget,
    paragraphMinWords: CleanupText.paragraphMinWords,
    unicode: ["letter": ranges { $0.isLetter }, "number": ranges { $0.isNumber }, "whitespace": ranges { $0.isWhitespace }, "cased": scalarRanges { $0.properties.isCased },
              "lower": mapping { $0.lowercased() }, "upper": mapping { $0.uppercased() },
              "decomposition": mapping(skip: 0xAC00...0xD7A3) { $0.decomposedStringWithCanonicalMapping }, "combiningClass": classes()])
let enc = JSONEncoder()
enc.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
print(String(decoding: try enc.encode(e), as: UTF8.self))
}}
