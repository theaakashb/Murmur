import Foundation

/// Word-level diff of the raw transcript against the inserted text, and the rule for when a
/// just-inserted span may be swapped back to the raw transcript.
enum WordDiff {
    /// `tweaked`: one word that differs from the raw only by punctuation or case; `text` is the new word and `marks` the positions of the characters that changed.
    enum Kind { case same, removed, added, tweaked }
    struct Run: Equatable { let kind: Kind; let text: String; var marks: [Int] = [] }

    /// Whitespace-separated words, punctuation attached, so "Hello," vs "hello" shows as a change.
    static func runs(raw: String, final: String) -> [Run] {
        runs(a: words(raw), b: words(final), key: { w in let k = w.lowercased().filter { $0.isLetter || $0.isNumber }; return k.isEmpty ? w : k })
    }
    /// The positions in `new` of the characters that are not shared with `old` (character-level LCS), so every mark is one changed letter or punctuation mark.
    private static func changedChars(from old: String, to new: String) -> [Int] {
        let a = Array(old), b = Array(new)
        var table = [[Int]](repeating: [Int](repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in stride(from: a.count - 1, through: 0, by: -1) {
            for j in stride(from: b.count - 1, through: 0, by: -1) { table[i][j] = a[i] == b[j] ? table[i + 1][j + 1] + 1 : max(table[i + 1][j], table[i][j + 1]) }
        }
        var marks: [Int] = [], i = 0, j = 0
        while j < b.count {
            if i < a.count, a[i] == b[j] { i += 1; j += 1 }
            else if i < a.count, table[i + 1][j] >= table[i][j + 1] { i += 1 }
            else { marks.append(j); j += 1 }
        }
        return marks
    }
    private static func words(_ text: String) -> [String] { text.split(whereSeparator: \.isWhitespace).map(String.init) }
    /// Words align on `key` (letters and digits, case-folded for the display diff), so a deleted word next to a case or punctuation change still pairs the right words.
    private static func runs(a: [String], b: [String], key: (String) -> String = { $0 }) -> [Run] {
        // Longest-common-subsequence table; dictations are short, so O(n*m) is fine.
        var table = [[Int]](repeating: [Int](repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in stride(from: a.count - 1, through: 0, by: -1) {
            for j in stride(from: b.count - 1, through: 0, by: -1) {
                table[i][j] = key(a[i]) == key(b[j]) ? table[i + 1][j + 1] + 1 : max(table[i + 1][j], table[i][j + 1])
            }
        }
        var out: [Run] = []
        func add(_ kind: Kind, _ word: String) {
            if let last = out.last, last.kind == kind { out[out.count - 1] = Run(kind: kind, text: last.text + " " + word) }
            else { out.append(Run(kind: kind, text: word)) }
        }
        var i = 0, j = 0
        while i < a.count || j < b.count {
            if i < a.count, j < b.count, key(a[i]) == key(b[j]) {
                if a[i] == b[j] { add(.same, a[i]) } else { out.append(Run(kind: .tweaked, text: b[j], marks: changedChars(from: a[i], to: b[j]))) }
                i += 1; j += 1
            }
            else if i < a.count, j == b.count || table[i + 1][j] >= table[i][j + 1] { add(.removed, a[i]); i += 1 }
            else { add(.added, b[j]); j += 1 }
        }
        return out
    }

    /// Words removed plus words added, folded into replacements: "teh"→"the" counts once.
    /// Counted on letters and digits only, so "Tuesday" to "Tuesday." is not a change; punctuation-only edits still show in `runs`.
    static func changeCount(raw: String, final: String) -> Int {
        func core(_ text: String) -> [String] { words(text).map { $0.lowercased().filter { $0.isLetter || $0.isNumber } }.filter { !$0.isEmpty } }
        var count = 0, pendingRemoved = 0
        for run in runs(a: core(raw), b: core(final)) {
            switch run.kind {
            case .removed: pendingRemoved = run.text.split(separator: " ").count
            case .added: count += max(pendingRemoved, run.text.split(separator: " ").count); pendingRemoved = 0
            case .same, .tweaked: count += pendingRemoved > 0 ? 1 : 0; pendingRemoved = 0
            }
        }
        return count + (pendingRemoved > 0 ? 1 : 0)
    }

    /// The text to put back: the raw transcript, keeping the leading space the insertion added.
    static func revertText(raw: String, inserted: String) -> String {
        inserted.hasPrefix(" ") && !raw.hasPrefix(" ") ? " " + raw : raw
    }
}
