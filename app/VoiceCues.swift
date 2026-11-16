import Foundation

// Voice cues: a cue is a spoken phrase and the text to insert in its place, stored as cues.json beside
// dictionary.json. Foundation only. Matching works on the raw transcript, before the dictionary, so a
// phrase is stored the way speech recognition writes it. The expansion is never processed (README, Cues).

struct VoiceCue: Codable, Identifiable, Equatable {
    let id: UUID
    var phrase: String
    /// Inserted exactly as stored, newlines included.
    var text: String
    let createdAt: Date
}

/// A dictation cut at its cue commands.
enum VoiceCuePiece: Equatable {
    /// Spoken words between commands, still raw; processed like any dictation.
    case text(String)
    /// The cue text, and the ", . ! ? ; :" that followed the command.
    case expansion(String, closing: String)
}

enum VoiceCueText {
    static let leadWords: Set<String> = ["insert", "paste"]
    static let reserved: Set<String> = ["insert", "paste", "here"]
    static let closing: Set<Character> = [",", ".", "!", "?", ";", ":"]

    /// A phrase's comparable words: case, punctuation, apostrophes and hyphens ignored ("John's" = "Johns").
    static func words(_ phrase: String) -> [String] {
        TermText.tokens(phrase).map(word)
    }
    private static func word(_ token: TermText.Token) -> String { token.folded.filter { $0 != "'" && $0 != "-" } }

    /// Nil when no cue fires. A whole dictation that is just a phrase becomes the cue alone; otherwise
    /// `insert <phrase>` or `paste <phrase>`, optionally followed by `here`, is replaced in full. A sentence end
    /// inside the command breaks it ("insert my link. Here we go" keeps "Here"). The longest phrase wins.
    static func split(_ raw: String, cues: [VoiceCue]) -> [VoiceCuePiece]? {
        let phrases = cues.map { (words: words($0.phrase), text: $0.text) }.sorted { $0.words.count > $1.words.count }
        let chars = Array(raw), tokens = TermText.tokens(chars), spoken = tokens.map(word)
        guard !phrases.isEmpty, !tokens.isEmpty else { return nil }
        if let whole = phrases.first(where: { $0.words == spoken }) { return [.expansion(whole.text, closing: "")] }
        func joined(_ from: Int, _ to: Int) -> Bool { !chars[tokens[from].end..<tokens[to].start].contains { ".!?".contains($0) } }
        var pieces: [VoiceCuePiece] = [], cursor = 0, i = 0
        while i < tokens.count {
            guard leadWords.contains(spoken[i]), let phrase = phrases.first(where: { phrase in
                i + phrase.words.count < tokens.count && Array(spoken[(i + 1)...(i + phrase.words.count)]) == phrase.words && joined(i, i + phrase.words.count)
            }) else { i += 1; continue }
            var last = i + phrase.words.count
            if last + 1 < tokens.count, spoken[last + 1] == "here", joined(last, last + 1) { last += 1 }
            var end = tokens[last].end
            var k = end
            while k < chars.count, chars[k] == " " || chars[k] == "\t" { k += 1 }
            var run = k
            while run < chars.count, closing.contains(chars[run]) { run += 1 }
            let punctuation = String(chars[k..<run])
            if run > k { end = run }
            let before = String(chars[cursor..<tokens[i].start])
            if !before.allSatisfy(\.isWhitespace) { pieces.append(.text(before.trimmingCharacters(in: .whitespacesAndNewlines))) }
            pieces.append(.expansion(phrase.text, closing: punctuation))
            cursor = end; i = last + 1
        }
        guard !pieces.isEmpty else { return nil }
        let rest = String(chars[cursor...])
        if !rest.allSatisfy(\.isWhitespace) { pieces.append(.text(rest.trimmingCharacters(in: .whitespacesAndNewlines))) }
        return pieces
    }

    /// The finished text. `processed[i]` is the processed form of `.text` piece i. One space joins neighbours
    /// unless one side already has whitespace or the next starts with closing punctuation. Words after an
    /// expansion that did not end a sentence keep the case they were spoken in (a separately cleaned segment
    /// would otherwise start with a capital, unless `keepsCase` says a dictionary spelling made it), and a
    /// segment before an expansion loses a period only cleanup added.
    static func join(_ pieces: [VoiceCuePiece], processed: [String], keepsCase: (String) -> Bool = { _ in false }) -> String {
        var out = "", midSentence = false
        func glue(_ text: String) {
            guard let first = text.first, let last = out.last else { out += text; return }
            out += last.isWhitespace || first.isWhitespace || closing.contains(first) ? text : " " + text
        }
        for (index, piece) in pieces.enumerated() {
            switch piece {
            case .expansion(let text, let end):
                glue(text); out += end
                midSentence = end.isEmpty ? text.last?.isNewline != true : !end.contains { ".!?".contains($0) }
            case .text(let raw):
                var text = processed[index].trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                if midSentence { text = unCapitalized(text, spoken: raw, keepsCase: keepsCase) }
                if text.hasSuffix("."), !text.hasSuffix(".."), !raw.hasSuffix("."), index + 1 < pieces.count { text.removeLast() }
                glue(text); midSentence = false
            }
        }
        return out
    }

    private static func unCapitalized(_ text: String, spoken: String, keepsCase: (String) -> Bool) -> String {
        guard let first = TermText.tokens(text).first, first.start == 0, let heard = TermText.tokens(spoken).first,
              spoken[spoken.index(spoken.startIndex, offsetBy: heard.start)].isLowercase, heard.folded == first.folded,
              first.text.first?.isUppercase == true, first.text.dropFirst().allSatisfy({ !$0.isUppercase }),
              first.folded != "i", !first.folded.hasPrefix("i'"), !keepsCase(heard.text) else { return text }
        return text.prefix(1).lowercased() + text.dropFirst()
    }
}

/// Persists cues as `cues.json` in `directory` (the dictionary's folder). Every mutation writes through.
/// A file that cannot be read leaves the store empty and read-only, so dictation still works and a save
/// cannot overwrite the damaged file.
final class VoiceCueStore {
    static let fileName = "cues.json"
    static let maxPhraseLength = 60
    static let maxTextLength = 4000

    let directory: URL
    private(set) var cues: [VoiceCue] = []
    private(set) var unreadable = false
    private var file: URL { directory.appendingPathComponent(Self.fileName) }

    private struct FileFormat: Codable {
        var version = 1
        var cues: [VoiceCue]
    }

    init(directory: URL) {
        self.directory = directory
        unreadable = (try? reload()) == nil
    }

    /// Re-reads from disk; an absent file means no cues (e.g. after Delete all data).
    func reload() throws {
        cues = []
        try PrivateFiles.prepareDirectory(directory)
        guard let data = try PrivateFiles.read(file) else { return }
        let decoded = try JSONDecoder().decode(FileFormat.self, from: data)
        guard decoded.version == 1 else { throw StoreError("Unsupported cues version") }
        cues = decoded.cues
    }

    private func save() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try PrivateFiles.write(try encoder.encode(FileFormat(cues: cues)), to: file)
    }

    @discardableResult
    func add(phrase: String, text: String, now: Date = Date()) throws -> VoiceCue {
        let cue = try validate(VoiceCue(id: UUID(), phrase: phrase, text: text, createdAt: now))
        cues.append(cue)
        try save()
        return cue
    }

    func update(id: UUID, phrase: String, text: String) throws {
        guard let index = cues.firstIndex(where: { $0.id == id }) else { throw StoreError("Cue not found") }
        cues[index] = try validate(VoiceCue(id: id, phrase: phrase, text: text, createdAt: cues[index].createdAt))
        try save()
    }

    func delete(id: UUID) throws {
        guard let index = cues.firstIndex(where: { $0.id == id }) else { throw StoreError("Cue not found") }
        guard !unreadable else { throw StoreError("Cues could not be read") }
        cues.remove(at: index)
        try save()
    }

    private func validate(_ cue: VoiceCue) throws -> VoiceCue {
        guard !unreadable else { throw StoreError("Cues could not be read") }
        let phrase = TermText.collapseWhitespace(cue.phrase)
        let key = VoiceCueText.words(phrase)
        guard (1...Self.maxPhraseLength).contains(phrase.count), phrase.contains(where: \.isLetter), !key.isEmpty else {
            throw StoreError("The phrase needs at least one letter and at most \(Self.maxPhraseLength) characters")
        }
        guard !(key.count == 1 && VoiceCueText.reserved.contains(key[0])) else { throw StoreError("“\(phrase)” is already a command word") }
        guard !cues.contains(where: { $0.id != cue.id && VoiceCueText.words($0.phrase) == key }) else { throw StoreError("Another cue already uses that phrase") }
        guard (1...Self.maxTextLength).contains(cue.text.count), !cue.text.allSatisfy(\.isWhitespace) else {
            throw StoreError("The text needs 1 to \(Self.maxTextLength) characters")
        }
        return VoiceCue(id: cue.id, phrase: phrase, text: cue.text, createdAt: cue.createdAt)
    }
}
