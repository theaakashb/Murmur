import Foundation

// Voice snippets: a spoken cue and the text to insert in its place, stored as snippets.json beside
// dictionary.json. Foundation only. Matching works on the raw transcript, before the dictionary, so a
// cue is stored the way speech recognition writes it. The expansion is never processed (README, Snippets).

struct Snippet: Codable, Identifiable, Equatable {
    let id: UUID
    var cue: String
    /// Inserted exactly as stored, newlines included.
    var text: String
    let createdAt: Date
}

/// A dictation cut at its snippet commands.
enum SnippetPiece: Equatable {
    /// Spoken words between commands, still raw; processed like any dictation.
    case text(String)
    /// The snippet text, and the ", . ! ? ; :" that followed the command.
    case expansion(String, closing: String)
}

enum SnippetText {
    static let leadWords: Set<String> = ["insert", "paste"]
    static let reserved: Set<String> = ["insert", "paste", "here"]
    static let closing: Set<Character> = [",", ".", "!", "?", ";", ":"]

    /// A phrase's comparable words: case, punctuation, apostrophes and hyphens ignored ("John's" = "Johns").
    static func words(_ phrase: String) -> [String] {
        TermText.tokens(phrase).map(word)
    }
    private static func word(_ token: TermText.Token) -> String { token.folded.filter { $0 != "'" && $0 != "-" } }

    /// Nil when no snippet fires. A whole dictation that is just a cue becomes the snippet alone; otherwise
    /// `insert <cue>` or `paste <cue>`, optionally followed by `here`, is replaced in full. A sentence end
    /// inside the command breaks it ("insert my link. Here we go" keeps "Here"). The longest cue wins.
    static func split(_ raw: String, snippets: [Snippet]) -> [SnippetPiece]? {
        let cues = snippets.map { (words: words($0.cue), text: $0.text) }.sorted { $0.words.count > $1.words.count }
        let chars = Array(raw), tokens = TermText.tokens(chars), spoken = tokens.map(word)
        guard !cues.isEmpty, !tokens.isEmpty else { return nil }
        if let whole = cues.first(where: { $0.words == spoken }) { return [.expansion(whole.text, closing: "")] }
        func joined(_ from: Int, _ to: Int) -> Bool { !chars[tokens[from].end..<tokens[to].start].contains { ".!?".contains($0) } }
        var pieces: [SnippetPiece] = [], cursor = 0, i = 0
        while i < tokens.count {
            guard leadWords.contains(spoken[i]), let cue = cues.first(where: { cue in
                i + cue.words.count < tokens.count && Array(spoken[(i + 1)...(i + cue.words.count)]) == cue.words && joined(i, i + cue.words.count)
            }) else { i += 1; continue }
            var last = i + cue.words.count
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
            pieces.append(.expansion(cue.text, closing: punctuation))
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
    static func join(_ pieces: [SnippetPiece], processed: [String], keepsCase: (String) -> Bool = { _ in false }) -> String {
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

/// Persists snippets as `snippets.json` in `directory` (the dictionary's folder). Every mutation writes through.
/// A file that cannot be read leaves the store empty and read-only, so dictation still works and a save
/// cannot overwrite the damaged file.
final class SnippetStore {
    static let fileName = "snippets.json"
    static let maxCueLength = 60
    static let maxTextLength = 4000

    let directory: URL
    private(set) var snippets: [Snippet] = []
    private(set) var unreadable = false
    private var file: URL { directory.appendingPathComponent(Self.fileName) }

    private struct FileFormat: Codable {
        var version = 1
        var snippets: [Snippet]
    }

    init(directory: URL) {
        self.directory = directory
        unreadable = (try? reload()) == nil
    }

    /// Re-reads from disk; an absent file means no snippets (e.g. after Delete all data).
    func reload() throws {
        snippets = []
        try PrivateFiles.prepareDirectory(directory)
        guard let data = try PrivateFiles.read(file) else { return }
        let decoded = try JSONDecoder().decode(FileFormat.self, from: data)
        guard decoded.version == 1 else { throw StoreError("Unsupported snippets version") }
        snippets = decoded.snippets
    }

    private func save() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        try PrivateFiles.write(try encoder.encode(FileFormat(snippets: snippets)), to: file)
    }

    @discardableResult
    func add(cue: String, text: String, now: Date = Date()) throws -> Snippet {
        let snippet = try validate(Snippet(id: UUID(), cue: cue, text: text, createdAt: now))
        snippets.append(snippet)
        try save()
        return snippet
    }

    func update(id: UUID, cue: String, text: String) throws {
        guard let index = snippets.firstIndex(where: { $0.id == id }) else { throw StoreError("Snippet not found") }
        snippets[index] = try validate(Snippet(id: id, cue: cue, text: text, createdAt: snippets[index].createdAt))
        try save()
    }

    func delete(id: UUID) throws {
        guard let index = snippets.firstIndex(where: { $0.id == id }) else { throw StoreError("Snippet not found") }
        guard !unreadable else { throw StoreError("Snippets could not be read") }
        snippets.remove(at: index)
        try save()
    }

    private func validate(_ snippet: Snippet) throws -> Snippet {
        guard !unreadable else { throw StoreError("Snippets could not be read") }
        let cue = TermText.collapseWhitespace(snippet.cue)
        let key = SnippetText.words(cue)
        guard (1...Self.maxCueLength).contains(cue.count), cue.contains(where: \.isLetter), !key.isEmpty else {
            throw StoreError("The cue needs at least one letter and at most \(Self.maxCueLength) characters")
        }
        guard !(key.count == 1 && SnippetText.reserved.contains(key[0])) else { throw StoreError("“\(cue)” is already a command word") }
        guard !snippets.contains(where: { $0.id != snippet.id && SnippetText.words($0.cue) == key }) else { throw StoreError("Another snippet already uses that cue") }
        guard (1...Self.maxTextLength).contains(snippet.text.count), !snippet.text.allSatisfy(\.isWhitespace) else {
            throw StoreError("The text needs 1 to \(Self.maxTextLength) characters")
        }
        return Snippet(id: snippet.id, cue: cue, text: snippet.text, createdAt: snippet.createdAt)
    }
}
