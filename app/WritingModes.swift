import Foundation

// Writing modes: presets, dictation style, persistence, migrations, matching and trigger stripping.

/// A user-defined writing mode. Unset overrides inherit the Cleanup settings.
struct WritingMode: Codable, Equatable, Identifiable, Sendable {
    var id = UUID()
    var name = "New mode"
    var enabled = true
    /// Bundle IDs of the app focused when recording started.
    var apps: [String] = []
    /// "host", "host/path", "*.host" or "host/path/*"; a subdomain matches its host.
    var websites: [String] = []
    /// Said at the start or end of a dictation; stripped from the text.
    var triggers: [String] = []
    /// Nil inherits the Cleanup settings.
    var options: CleanupOptions? = nil
    /// Formal is the default and leaves text as cleanup wrote it.
    var tone: Tone = .formal
    /// Nil inherits the global screen-context setting.
    var screenContext: Bool? = nil
    /// Sent to AI cleanup routes only; capped at `WritingModeLimits.instructions` characters.
    var instructions = ""
    /// Set on the built-in presets: a separate, explicit rewrite contract on AI routes (never ordinary cleanup).
    /// Its triggers match only at the start of a dictation.
    var transform: ModeTransform? = nil
}

extension WritingMode {
    /// Seeded once into the user's list; fully editable and deletable afterwards.
    static let presets: [WritingMode] = [
        WritingMode(id: UUID(uuidString: "6D1A0001-5E5E-4A4A-9A9A-000000000001")!, name: "Prompt Mode", triggers: ["prompt mode", "prompt engineer"], instructions: TransformPrompt.baseline(.aiPrompt), transform: .aiPrompt),
        WritingMode(id: UUID(uuidString: "6D1A0001-5E5E-4A4A-9A9A-000000000002")!, name: "Email", triggers: ["email mode"], instructions: TransformPrompt.baseline(.email), transform: .email),
        WritingMode(id: UUID(uuidString: "6D1A0001-5E5E-4A4A-9A9A-000000000003")!, name: "Notes", triggers: ["notes mode"], instructions: TransformPrompt.baseline(.notes), transform: .notes),
    ]
}

/// How one dictation is processed: the global settings with the matched mode's overrides.
struct DictationStyle: Equatable, Sendable {
    var options: CleanupOptions
    var tone: Tone = .formal
    var instructions = ""
    var transform: ModeTransform? = nil
    /// A writing mode applied: this dictation runs on the AI engine (AIEngine.swift), not the cleanup route.
    var usesEngine = false
    /// Captured screen text, used only when the mode or the global setting has screen context on
    /// and only on an AI route. Set it to nil otherwise.
    var context: ScreenContext? = nil

    init(options: CleanupOptions, tone: Tone = .formal, instructions: String = "", context: ScreenContext? = nil) {
        self.options = options; self.tone = tone; self.instructions = instructions; self.context = context
    }

    /// `context` is dropped unless screen context is on for this mode (or globally, when the mode does not say).
    init(mode: WritingMode?, settings: ProcessingSettings, context: ScreenContext? = nil) {
        self.init(options: mode?.options ?? settings.options, tone: mode?.tone ?? .formal,
                  instructions: String((mode?.instructions ?? "").prefix(WritingModeLimits.instructions)),
                  context: (mode?.screenContext ?? settings.screenContext) ? context : nil)
        options.trustModel = settings.options.trustModel
        transform = mode?.transform
        usesEngine = mode != nil
        // With every option off nothing runs. Natural keeps it: the Local model cleans Natural with it (the app reads it only then).
        if !options.anyEnabled { self.context = nil }
    }

    /// Trigger modes are chosen after transcription, so capture for any enabled candidate that needs it.
    /// `localModel`: the cleanup route is the Local model, which also runs Natural as a model pass.
    static func needsScreenContext(mode: WritingMode?, modes: [WritingMode], settings: ProcessingSettings, localModel: Bool = false) -> Bool {
        func needs(_ mode: WritingMode?) -> Bool {
            let style = DictationStyle(mode: mode, settings: settings)
            return (mode?.screenContext ?? settings.screenContext) && (style.usesModel || (localModel && !style.usesEngine && style.options.anyEnabled))
        }
        return needs(mode) || modes.contains { $0.enabled && !$0.triggers.isEmpty && needs($0) }
    }

    /// Only the rewrite styles and preset rewrites use the AI model.
    var usesModel: Bool { options.polish || options.professional || transform != nil }
}

enum WritingModes {
    static let defaultsKey = "writingModes"

    static let presetsSeededKey = "writingModePresetsSeeded"
    static let editableBaselinesKey = "writingModeEditableBaselines"
    /// Where an unreadable saved list is kept, so the next save cannot silently replace it with an empty one.
    static let unreadableKey = "writingModesUnreadable"

    /// The saved modes; the first call adds the presets after them (existing modes keep priority), once.
    static func load(from defaults: UserDefaults) -> [WritingMode] {
        var modes: [WritingMode] = []
        if let saved = defaults.data(forKey: defaultsKey) {
            if let decoded = try? JSONDecoder().decode([WritingMode].self, from: saved) { modes = decoded } else { defaults.set(saved, forKey: unreadableKey) }
        }
        if !defaults.bool(forKey: presetsSeededKey) {
            modes += WritingMode.presets.filter { preset in !modes.contains { $0.id == preset.id } }
            save(modes, to: defaults); defaults.set(true, forKey: presetsSeededKey)
        }
        if !defaults.bool(forKey: editableBaselinesKey) {
            for i in modes.indices {
                guard let preset = WritingMode.presets.first(where: { $0.id == modes[i].id }),
                      modes[i].transform == preset.transform else { continue }
                if modes[i].instructions.isEmpty { modes[i].instructions = preset.instructions }
                if ["AI Prompt", "Prompt Engineer"].contains(modes[i].name) { modes[i].name = "Prompt Mode" }
            }
            save(modes, to: defaults)
            defaults.set(true, forKey: editableBaselinesKey)
        }
        if let i = modes.firstIndex(where: { $0.id == WritingMode.presets[0].id && $0.transform == .aiPrompt && [TransformPrompt.previousAIPromptBaseline, TransformPrompt.previousDetailAIPromptBaseline, TransformPrompt.previousConditionalAIPromptBaseline, TransformPrompt.previousStructuredAIPromptBaseline].contains($0.instructions) }) {
            modes[i].instructions = TransformPrompt.baseline(.aiPrompt)
            if ["AI Prompt", "Prompt Engineer"].contains(modes[i].name) {
                modes[i].name = "Prompt Mode"
                modes[i].triggers = WritingMode.presets[0].triggers
            }
            save(modes, to: defaults)
        }
        if let i = modes.firstIndex(where: { $0.id == WritingMode.presets[1].id && $0.transform == .email && [TransformPrompt.previousEmailBaseline, TransformPrompt.previousStructuredEmailBaseline].contains($0.instructions) }) {
            modes[i].instructions = TransformPrompt.baseline(.email)
            save(modes, to: defaults)
        }
        if let i = modes.firstIndex(where: { $0.id == WritingMode.presets[2].id && $0.transform == .notes && [TransformPrompt.previousNotesBaseline, TransformPrompt.previousStructuredNotesBaseline].contains($0.instructions) }) {
            modes[i].instructions = TransformPrompt.baseline(.notes)
            save(modes, to: defaults)
        }
        return modes
    }

    static func save(_ modes: [WritingMode], to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(modes) { defaults.set(data, forKey: defaultsKey) }
    }

    /// The browser URL is read only when an enabled mode lists a website.
    static func needsURL(_ modes: [WritingMode]) -> Bool { modes.contains { $0.enabled && !$0.websites.isEmpty } }

    /// The mode for the app (and page) focused when recording started: a website match wins over an
    /// app match; within a tier the first mode in the list wins.
    static func match(_ modes: [WritingMode], bundleID: String?, url: URL?) -> WritingMode? {
        let enabled = modes.filter(\.enabled)
        if let url, let mode = enabled.first(where: { $0.websites.contains { websiteMatches($0, url: url) } }) { return mode }
        guard let bundleID = bundleID?.lowercased(), !bundleID.isEmpty else { return nil }
        return enabled.first { $0.apps.contains { $0.lowercased() == bundleID } }
    }

    /// "github.com" matches github.com and any subdomain, never "notgithub.com". A path matches
    /// itself and anything below it by whole segments; "/*" and a leading "*." add nothing.
    /// Scheme, port, query and case are ignored.
    static func websiteMatches(_ pattern: String, url: URL) -> Bool {
        var p = pattern.trimmingCharacters(in: .whitespaces).lowercased()
        for prefix in ["https://", "http://", "*.", "www."] where p.hasPrefix(prefix) { p.removeFirst(prefix.count) }
        let slash = p.firstIndex(of: "/") ?? p.endIndex
        let hostPattern = p[..<slash].split(separator: ":").first.map(String.init) ?? ""
        var path = String(p[slash...])
        while path.hasSuffix("*") || path.hasSuffix("/") { path.removeLast() }
        guard !hostPattern.isEmpty, let host = url.host?.lowercased() else { return false }
        guard host == hostPattern || host.hasSuffix("." + hostPattern) else { return false }
        guard !path.isEmpty else { return true }
        let urlPath = url.path.lowercased()
        return urlPath == path || urlPath.hasPrefix(path + "/")
    }

    /// The first enabled mode whose trigger is said at the start or the end, with the trigger removed.
    static func trigger(_ modes: [WritingMode], in transcript: String) -> (mode: WritingMode, text: String)? {
        for mode in modes where mode.enabled {
            for phrase in mode.triggers {
                if let text = stripTrigger(phrase, from: transcript, startOnly: mode.transform != nil) { return (mode, text) }
            }
        }
        return nil
    }

    /// Whole words only ("code" never matches "codebase"), and only at either end; a dictation that
    /// is nothing but the trigger does not match. "Slack mode, fix it" -> "Fix it";
    /// "Fix it, slack mode." -> "Fix it."
    /// `startOnly` (the presets) ignores a trigger said at the end.
    static func stripTrigger(_ phrase: String, from text: String, startOnly: Bool = false) -> String? {
        // "E-mail mode" is one word to the tokenizer, so hyphens are ignored on both sides.
        func fold(_ word: String) -> String { word.replacingOccurrences(of: "-", with: "") }
        let words = CleanupGuard.words(phrase).map { fold($0.lower) }
        guard !words.isEmpty else { return nil }
        var t = CleanupText.tokenize(text)
        let wi = t.indices.filter { t[$0].kind == .word }
        guard wi.count > words.count else { return nil }
        func separator(_ tok: CleanupToken) -> Bool { tok.kind == .space || tok.isComma || tok.isDash || tok.text == ":" || tok.text == ";" }
        if wi.prefix(words.count).map({ fold(t[$0].lower) }) == words {
            let capital = t[wi[0]].text.first?.isUppercase ?? false
            var k = wi[words.count - 1] + 1
            if k < t.count, t[k].isTerminal, t[k].text != "\n" { k += 1 } // "Email mode. Fix it."
            while k < t.count, separator(t[k]) || t[k].text == "\n" { k += 1 }
            t.removeSubrange(0..<k)
            if capital, let first = t.firstIndex(where: { $0.kind == .word }) { t[first].text = CleanupText.capitalized(t[first].text) }
            return CleanupText.render(t)
        }
        if !startOnly, wi.suffix(words.count).map({ fold(t[$0].lower) }) == words {
            let start = wi[wi.count - words.count]
            let end = t.lastIndex(where: { $0.kind != .space })!
            let mark = t[end].isTerminal && t[end].text != "\n" ? t[end].text : ""
            var k = start
            while k > 0, separator(t[k - 1]) { k -= 1 }
            // "Fix it. Slack mode." keeps the period that ended the dictated sentence.
            if k > 0, t[k - 1].isTerminal { return CleanupText.render(Array(t[..<k])) }
            return CleanupText.render(Array(t[..<k])) + mark
        }
        return nil
    }
}
