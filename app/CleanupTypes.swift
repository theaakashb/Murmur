import Foundation

// The cleanup value vocabulary: options, tone, screen context, failure reasons, paths, guard results and outcomes.
// Shared by History, meetings and writing modes.


struct CleanupOptions: Codable, Equatable, Hashable, Sendable {
    var fillers: Bool
    var corrections: Bool
    var lists: Bool
    /// Sentence punctuation and capitalization; words are never changed.
    var punctuation: Bool
    /// Polish instead of strict: fillers also go without commas, false starts go without a cue, and
    /// small grammar fixes are allowed (see `CleanupGuard.check`). It can change a word's meaning.
    var polish: Bool
    /// Professional, above polish (and sets it): rewrites for grammar and a professional tone,
    /// reordering words if needed; names, numbers and negations are still checked.
    var professional: Bool
    /// Spoken commands and numbers: "comma", "new paragraph", "two hundred thousand" -> 200,000,
    /// "at the rate", "hashtag", "ten percent", and the explicit "write an email to X saying" layout
    /// (see `RuleCleaner.spokenCommands`).
    var spoken: Bool
    /// Opt-in for Polish and Professional: accept edits without `CleanupGuard.check`; `CleanupPipeline.judge` still
    /// rejects an empty output, an answer, or a runaway length.
    var trustModel: Bool

    init(fillers: Bool = true, corrections: Bool = true, lists: Bool = true, punctuation: Bool = false, polish: Bool = false, professional: Bool = false, spoken: Bool = false, trustModel: Bool = false) {
        self.fillers = fillers
        self.corrections = corrections
        self.lists = lists
        self.punctuation = punctuation
        self.polish = polish || professional
        self.professional = professional
        self.spoken = spoken
        self.trustModel = trustModel
    }

    /// Saved options from before punctuation or spoken commands existed decode with them off.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(fillers: try c.decode(Bool.self, forKey: .fillers), corrections: try c.decode(Bool.self, forKey: .corrections),
                  lists: try c.decode(Bool.self, forKey: .lists), punctuation: try c.decodeIfPresent(Bool.self, forKey: .punctuation) ?? false,
                  polish: try c.decodeIfPresent(Bool.self, forKey: .polish) ?? false,
                  professional: try c.decodeIfPresent(Bool.self, forKey: .professional) ?? false,
                  spoken: try c.decodeIfPresent(Bool.self, forKey: .spoken) ?? false,
                  trustModel: try c.decodeIfPresent(Bool.self, forKey: .trustModel) ?? false)
    }

    /// Approved default: every comfort on.
    static let standard = CleanupOptions(punctuation: true, spoken: true)
    /// Plainly labeled faithful-transcription preset: every cleanup operation off.
    static let faithful = CleanupOptions(fillers: false, corrections: false, lists: false)

    var anyEnabled: Bool { fillers || corrections || lists || punctuation || spoken }
}


/// A writing mode's opt-in tone, applied by rules after cleanup.
/// Formal leaves text unchanged. Commas inside numbers ("250,000") are part of the word and stay.
enum Tone: String, Codable, CaseIterable, Sendable {
    case formal, casual
    case veryCasual = "very-casual"
    case excited

    var displayName: String {
        switch self {
        case .formal: return "Formal"
        case .casual: return "Casual"
        case .veryCasual: return "Very casual"
        case .excited: return "Excited"
        }
    }

    /// Casual drops commas and the final period; Very casual also lowercases sentence starts,
    /// keeping "I" and mixed-case words ("iPhone", "NASA"); Excited turns the final period into "!".
    func apply(_ text: String) -> String {
        guard self != .formal else { return text }
        var t = CleanupText.tokenize(text)
        if let last = t.lastIndex(where: { $0.kind != .space }), t[last].text == ".",
           last > 0, t[last - 1].kind == .word { // Not an ellipsis.
            if self == .excited { t[last].text = "!" } else { t.remove(at: last) }
        }
        guard self != .excited else { return CleanupText.render(t) }
        var i = 0
        while i < t.count {
            if t[i].isComma {
                // "a,b" keeps a space between the words; "a, b" just loses the comma.
                if i + 1 < t.count, t[i + 1].kind == .word, i > 0, t[i - 1].kind == .word { t[i] = CleanupToken(kind: .space, text: " ") }
                else { t.remove(at: i); continue }
            }
            i += 1
        }
        if self == .veryCasual {
            for k in t.indices where t[k].kind == .word && CleanupText.isSentenceStart(t, k) {
                let w = t[k].text, lower = t[k].lower
                guard lower != "i", !lower.hasPrefix("i'"), !w.dropFirst().contains(where: \.isUppercase) else { continue }
                t[k].text = w.prefix(1).lowercased() + w.dropFirst()
            }
        }
        return CleanupText.render(t)
    }
}

/// The built-in rewrites. Each has its own prompt and output check (`TransformPrompt`, `TransformGuard`).
enum ModeTransform: String, Codable, Sendable {
    case aiPrompt, email, notes
}

/// Words per section when `CleanupSections` splits long text for parallel cleanup (the default; a provider may ask for another).
enum CleanupSectioning {
    static let defaultTarget = 100
}

enum WritingModeLimits {
    /// A writing mode's custom instructions are capped at this many characters.
    static let instructions = 2_000
}


/// Reference-only text from the screen for AI cleanup routes: the selected text, the clipboard and
/// the focused window's text. Never sent on the local-rules route, never inserted, never stored.
struct ScreenContext: Equatable, Sendable {
    /// Pasteboard markers for passwords and other private or generated items (nspasteboard.org).
    static let privateTypes: Set<String> = ["org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType"]
    static let selectedLimit = 1_000, clipboardLimit = 1_000, windowLimit = 3_000
    var selected = ""
    var clipboard = ""
    var window = ""
    var isEmpty: Bool { selected.isEmpty && clipboard.isEmpty && window.isEmpty }

    /// Nil while Secure Event Input is on or a secure field is focused. A clipboard carrying any
    /// private marker type is dropped whole. Delimiter tags are stripped and each part is capped.
    static func make(selected: String?, clipboard: String?, clipboardTypes: [String], window: String?, secureInput: Bool, secureField: Bool) -> ScreenContext? {
        guard !secureInput, !secureField else { return nil }
        let clip = clipboardTypes.contains(where: privateTypes.contains) ? nil : clipboard
        let context = ScreenContext(selected: sanitized(selected, limit: selectedLimit), clipboard: sanitized(clip, limit: clipboardLimit),
                                    window: sanitized(window, limit: windowLimit))
        return context.isEmpty ? nil : context
    }

    static let delimiter = try! NSRegularExpression(pattern: #"<\s*/?\s*(?:transcript|context|instructions)[^>]*>?"#, options: [.caseInsensitive])

    /// Removes anything shaped like the prompt's delimiter tags, then caps the length.
    static func sanitized(_ text: String?, limit: Int) -> String {
        guard let text else { return "" }
        let ns = text as NSString
        let stripped = delimiter.stringByReplacingMatches(in: text, range: NSRange(location: 0, length: ns.length), withTemplate: " ")
        return String(stripped.trimmingCharacters(in: .whitespacesAndNewlines).prefix(limit))
    }
}


/// Why cleanup output was rejected (and a fallback used). Never carries transcript text.
enum CleanupFailureReason: String, Codable, Equatable, Sendable, CaseIterable {
    case cleanerFailed
    case emptyOutput
    case lengthRatio
    case editDistance
    case unexplainedDeletion
    case insertedWords
    case numberAdded
    case numberDropped
    case nameAdded
    case nameDropped
    case negationChanged
    case answerPattern
    case structureAdded
    case quoteEdited
}

/// Which path supplied a dictation's final text, recorded in History. Content-free.
enum CleanupPath: String, Codable, Equatable, Sendable {
    /// No cleanup operation was enabled.
    case off
    case modelAccepted
    /// The model's words were rejected; its punctuation was applied to the local cleanup.
    case modelPunctuationOnLocal
    /// The model was rejected or failed (or not configured); local rules were used.
    case localRules
    /// The local route: local rules, no model.
    case localRoute
}

enum CleanupGuardResult: Equatable, Sendable {
    case pass
    case fail(CleanupFailureReason)
}


enum CleanupOutcome: Equatable, Sendable {
    /// Cleanup ran and passed the guard; insert this text.
    case cleaned(String)
    /// All options are off (or the transcript is blank); insert the raw text.
    case unchanged(String)
    /// Save raw text, show recovery, insert only after an explicit raw-text action.
    case heldForRecovery(raw: String, reason: CleanupFailureReason)
}

