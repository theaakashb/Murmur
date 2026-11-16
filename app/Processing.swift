import Foundation

// Text processing between transcription and insertion: dictionary, then optional cleanup, then
// the PRD bound check. Foundation only, so app/check_processing.swift runs on Linux.
//
// Order: DictionaryStore.apply runs first and in every mode (faithful included, PRD §4), so its
// preferred spellings are already in the text the cleanup guard compares against. Relevant
// entries are also passed as allowed terms, and as prompt terms on the cloud route.
//
// Failure policy (user decision, October 6, 2026: behave like Wispr Flow): a dictation is never held.
// When the guard rejects the cleaned words, the transcript's own words go in with the model's
// punctuation (`CleanupPipeline.punctuated`), retaining guarded rule-based spoken corrections.
// A provider error, or a transfer that also fails the guard, falls back to local rules, and failing
// those, to the dictionary-applied transcript. Every
// text inserted has passed the guard or is the user's own words. Reasons stay content-free.

/// Where cleanup runs. `.local` is the RuleCleaner. `.cloud` means "through a `CleanupProvider`":
/// Gemini, a custom OpenAI-compatible endpoint, a user-run local model server, or Apple's on-device
/// model (see `CleanupChoice` in Routes.swift).
enum CleanupRoute: String, Codable, CaseIterable, Sendable {
    case local
    case cloud
}

/// What the controller does with a finished transcript.
enum ProcessingOutcome: Equatable, Sendable {
    /// Insert (or save, if insertion is off or unsafe) this text.
    case insert(String)
    /// Save `raw` (the unmodified transcript) for recovery and do not insert automatically. Only
    /// the controller produces this now (processing unavailable); `reason` is content-free.
    case heldForRecovery(raw: String, reason: CleanupFailureReason)
}

struct ProcessingResult: Sendable {
    var outcome: ProcessingOutcome
    var providerError: ProviderError?
    var fallback: CleanupFailureReason?
    var rejectedOutput: String?
    var cleanupPath: CleanupPath?
    var modelOutput: String?
    var guardVerdict: String?
    var promptHash: String?
    var terms: [String] = []
    var cleanupInput: String?
    /// Screen context went to the cleanup provider with this dictation.
    var contextUsed = false
}

/// Settings owned by processing, persisted in UserDefaults.
struct ProcessingSettings: Equatable, Sendable {
    var options: CleanupOptions = CleanupOptions(punctuation: true, spoken: true, trustModel: true)
    var route: CleanupRoute = .local
    var learningEnabled = true
    var learningRequiresReadableField = false
    /// Applied by AppController, as soon as the setting changes, through HistoryRouter.
    var historyMode: HistoryMode = .on
    /// Read by AppController for the automatic update check.
    var updateChecksEnabled = true
    /// Screen context for AI cleanup routes (gap #8): opt-in, off by default. A writing mode can override it.
    var screenContext = false

    enum Key {
        static let fillers = "cleanupFillers"
        static let corrections = "cleanupCorrections"
        static let lists = "cleanupLists"
        static let punctuation = "cleanupPunctuation"
        static let polish = "cleanupPolish"
        static let professional = "cleanupProfessional"
        static let spoken = "cleanupSpokenCommands"
        static let route = "cleanupRoute"
        static let learning = "learningEnabled"
        static let readableLearning = "learningRequiresReadableField"
        static let historyMode = "historyMode"
        static let updateChecks = "updateChecksEnabled"
        static let screenContext = "cleanupScreenContext"
    }

    /// Missing or unknown values fall back to the approved defaults (all comforts on, local route).
    static func load(from defaults: UserDefaults) -> ProcessingSettings {
        var settings = ProcessingSettings()
        func flag(_ key: String, _ fallback: Bool) -> Bool { defaults.object(forKey: key) as? Bool ?? fallback }
        settings.options = CleanupOptions(fillers: flag(Key.fillers, true), corrections: flag(Key.corrections, true), lists: flag(Key.lists, true), punctuation: flag(Key.punctuation, true), polish: flag(Key.polish, false), professional: flag(Key.professional, false), spoken: flag(Key.spoken, true), trustModel: true)
        settings.route = defaults.string(forKey: Key.route).flatMap(CleanupRoute.init(rawValue:)) ?? .local
        settings.learningEnabled = flag(Key.learning, true)
        settings.learningRequiresReadableField = flag(Key.readableLearning, false)
        settings.historyMode = defaults.string(forKey: Key.historyMode).flatMap(HistoryMode.init(rawValue:)) ?? .on
        settings.updateChecksEnabled = flag(Key.updateChecks, true)
        settings.screenContext = flag(Key.screenContext, false)
        return settings
    }

    func save(to defaults: UserDefaults) {
        defaults.set(options.fillers, forKey: Key.fillers)
        defaults.set(options.corrections, forKey: Key.corrections)
        defaults.set(options.lists, forKey: Key.lists)
        defaults.set(options.punctuation, forKey: Key.punctuation)
        defaults.set(options.polish, forKey: Key.polish)
        defaults.set(options.professional, forKey: Key.professional)
        defaults.set(options.spoken, forKey: Key.spoken)
        defaults.set(route.rawValue, forKey: Key.route)
        defaults.set(learningEnabled, forKey: Key.learning)
        defaults.set(learningRequiresReadableField, forKey: Key.readableLearning)
        defaults.set(historyMode.rawValue, forKey: Key.historyMode)
        defaults.set(updateChecksEnabled, forKey: Key.updateChecks)
        defaults.set(screenContext, forKey: Key.screenContext)
    }
}

/// Owns the dictionary and correction ledger and turns a raw transcript into an outcome.
/// Not thread-safe: the app calls it from the main thread only.
final class ProcessingCoordinator {
    let dictionary: DictionaryStore
    let ledger: CorrectionLedger
    private(set) var settings: ProcessingSettings
    private let defaults: UserDefaults
    /// The local route's cleaner. Tests substitute failing or misbehaving cleaners.
    var localCleaner: Cleaner = RuleCleaner()
    /// The cloud route's provider. Nil means the cloud route is not configured, so local rules run.
    /// AppController sets it from `RoutePlan` (selected provider and Keychain key) at each start.
    var cloudCleaner: CleanupProvider?
    /// What the punctuation transfer is guarded as: sentence punctuation and capitals, no word changes.
    static let punctuationOnly = CleanupOptions(fillers: false, corrections: false, lists: false, punctuation: true)

    /// `directory` holds dictionary.json and ledger.json (MurmurDataLocations.standard layout).
    /// `isCommonWord` guards learning and dictionary respelling (NSSpellChecker in the app).
    init(directory: URL, defaults: UserDefaults = .standard, isCommonWord: ((String) -> Bool)? = nil) throws {
        self.defaults = defaults
        dictionary = try DictionaryStore(directory: directory, isCommonWord: isCommonWord)
        ledger = try CorrectionLedger(directory: directory, dictionary: dictionary)
        settings = ProcessingSettings.load(from: defaults)
        if ledger.learningEnabled != settings.learningEnabled { try ledger.setLearningEnabled(settings.learningEnabled) }
    }

    /// Persists settings and keeps the ledger's learning switch in step with them.
    func update(_ next: ProcessingSettings, persist: Bool = true) throws {
        if ledger.learningEnabled != next.learningEnabled { try ledger.setLearningEnabled(next.learningEnabled) }
        settings = next
        if persist { next.save(to: defaults) }
    }

    // MARK: Processing

    /// Dictionary-applied text and the relevant terms for this transcript.
    private func prepare(_ raw: String) -> (text: String, terms: [String]) {
        let applied = dictionary.apply(to: raw)
        var seen = Set<UUID>()
        var terms: [String] = []
        for entry in dictionary.relevantEntries(for: raw) + dictionary.relevantEntries(for: applied) where seen.insert(entry.id).inserted {
            terms.append(entry.preferred)
        }
        return (applied, terms)
    }

    /// Local rules, guarded; the dictionary-applied text itself if they fail the guard.
    private func localClean(_ text: String, terms: [String], options: CleanupOptions, evidence: inout ProcessingResult) -> String {
        let local = Self.localRules(text, terms: terms, options: options, cleaner: localCleaner)
        if let reason = local.reason { evidence.fallback = evidence.fallback ?? reason }
        return local.text
    }

    /// Every enabled local operation (spoken commands, fillers, corrections, lists), guarded. A nil
    /// `reason` means the rules passed the guard; otherwise `text` is the input with only the
    /// spoken commands and "instead" corrections resolved.
    static func localRules(_ text: String, terms: [String], options: CleanupOptions, cleaner: Cleaner = RuleCleaner()) -> (text: String, reason: CleanupFailureReason?) {
        var options = options
        let text = options.corrections && options.polish ? RuleCleaner.applyInsteadCorrections(options.spoken ? RuleCleaner.spokenCommands(text) : text) : text
        options.polish = false // Broader AI grammar and phrasing edits have no local fallback.
        options.professional = false
        options.trustModel = false // Trust is for the AI model, never local rules.
        switch CleanupPipeline.process(raw: text, options: options, cleaner: cleaner, allowedTerms: terms) {
        case .cleaned(let cleaned), .unchanged(let cleaned): return (cleaned, nil)
        case .heldForRecovery(_, let reason): return (text, reason)
        }
    }

    /// Synchronous path, used by the app on the local route. It never calls the network: on the
    /// cloud route it uses local rules. The app calls `processAsync` whenever the route is not local rules.
    /// `style` carries a writing mode's overrides; nil uses the settings. Screen context and custom
    /// instructions are never used here: local rules send nothing and take no instructions.
    func process(_ raw: String, style: DictationStyle? = nil) -> ProcessingResult {
        var evidence = ProcessingResult(outcome: .insert(raw))
        let style = style ?? DictationStyle(mode: nil, settings: settings)
        let prepared = prepare(raw)
        evidence.terms = prepared.terms; evidence.cleanupInput = prepared.text == raw ? nil : prepared.text
        // Reaching here on the cloud route is a failure only for styles that wanted the model.
        let wantedModel = settings.route == .cloud && style.usesModel
        if wantedModel && style.options.anyEnabled { evidence.fallback = .cleanerFailed }
        evidence.cleanupPath = !style.options.anyEnabled ? .off : wantedModel ? .localRules : .localRoute
        evidence.outcome = .insert(toned(localClean(prepared.text, terms: prepared.terms, options: style.options, evidence: &evidence), input: prepared.text, style: style)); return evidence
    }

    /// The mode's tone, applied last to whichever text was chosen (gap #7). Never on the email layout,
    /// and only as exactly `Tone.apply`, which `CleanupGuard.acceptsTone` checks.
    private func toned(_ text: String, input: String, style: DictationStyle) -> String {
        guard style.options.anyEnabled, style.tone != .formal, !text.isEmpty, !(style.options.spoken && RuleCleaner.emailLayout(input) != nil) else { return text }
        let next = style.tone.apply(text)
        return CleanupGuard.acceptsTone(base: text, toned: next, tone: style.tone) ? next : text
    }

    /// Full path including the cloud route. Always inserts: see the failure policy above.
    /// Main actor: the dictionary is read on the main thread; only the provider calls run elsewhere.
    @MainActor func processAsync(_ raw: String, style: DictationStyle? = nil) async -> ProcessingResult {
        var evidence = ProcessingResult(outcome: .insert(raw))
        let style = style ?? DictationStyle(mode: nil, settings: settings)
        // Only the rewrite styles and preset rewrites use the model; Strict and Faithful stay on this Mac.
        guard settings.route == .cloud, style.usesModel else { return process(raw, style: style) }
        let prepared = prepare(raw)
        let options = style.options, terms = prepared.terms
        evidence.terms = terms; evidence.cleanupInput = prepared.text == raw ? nil : prepared.text
        var context = style.context, instructions = style.instructions
        // The provider declares what it takes (CleanupShape): a fixed prompt gets no context or style notes (rewrite presets fall back below), a small window gets capped context.
        if let shape = cloudCleaner?.shape {
            if !shape.takesContext { context = nil; instructions = "" }
            if let caps = shape.contextCaps, var c = context {
                c.selected = String(c.selected.prefix(caps.selected)); c.clipboard = String(c.clipboard.prefix(caps.clipboard)); c.window = String(c.window.prefix(caps.window))
                context = c
            }
        }
        if let kind = style.transform, let provider = cloudCleaner, !prepared.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            if let attempt = await Self.transformClean(prepared.text, kind: kind, provider: provider, terms: terms, instructions: instructions) {
                evidence.cleanupPath = .modelAccepted; evidence.modelOutput = attempt.accepted; evidence.promptHash = attempt.promptHash; evidence.guardVerdict = attempt.verdict
                evidence.outcome = .insert(attempt.text ?? prepared.text); return evidence
            }
            evidence.fallback = .cleanerFailed // Rewrite failed or was rejected: ordinary cleanup below.
        }
        if !options.anyEnabled { evidence.cleanupPath = .off }
        if !options.anyEnabled || prepared.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { evidence.outcome = .insert(toned(prepared.text, input: prepared.text, style: style)); return evidence }
        guard let provider = cloudCleaner else { evidence.fallback = .cleanerFailed; evidence.cleanupPath = .localRules; evidence.outcome = .insert(toned(localClean(prepared.text, terms: terms, options: options, evidence: &evidence), input: prepared.text, style: style)); return evidence }
        // Providers with a slow model or a small window clean long text in parallel sections, each guarded on its own (CleanupShape).
        let shape = provider.shape
        let sections = shape.sectionTarget.map { CleanupSections.split(prepared.text, target: $0, keepingCorrectionsAttached: shape.keepsCorrectionsAttached) } ?? [prepared.text]
        evidence.contextUsed = context != nil
        let results = await withTaskGroup(of: (Int, CloudAttempt).self) { group in
            for (index, section) in sections.enumerated() {
                group.addTask { (index, await Self.cloudClean(section, provider: provider, options: options, terms: terms, context: context, instructions: instructions)) }
            }
            var out = Array(repeating: CloudAttempt(text: nil, reason: .cleanerFailed, error: nil), count: sections.count)
            for await (index, attempt) in group { out[index] = attempt }
            return out
        }
        // The worst section decides.
        evidence.cleanupPath = results.contains { $0.text == nil } ? .localRules : results.contains { $0.reason != nil } ? .modelPunctuationOnLocal : .modelAccepted
        var parts: [String] = []
        for (section, attempt) in zip(sections, results) {
            if let reason = attempt.reason { evidence.fallback = evidence.fallback ?? reason }
            if let error = attempt.error { evidence.providerError = evidence.providerError ?? error }
            // A rejected output was never pasted and may echo screen context, so it is not kept for History when
            // context was sent. An accepted output is what was pasted (before tone and spacing), so keeping it
            // stores nothing History does not already hold, and it is the model's exact output.
            if context == nil, let rejected = attempt.rejected { evidence.rejectedOutput = (evidence.rejectedOutput.map { $0 + "\n\n" } ?? "") + rejected }
            if let accepted = attempt.accepted { evidence.modelOutput = (evidence.modelOutput.map { $0 + "\n\n" } ?? "") + accepted }
            evidence.promptHash = evidence.promptHash ?? attempt.promptHash
            let text = attempt.text ?? localClean(section, terms: terms, options: options, evidence: &evidence)
            if !text.isEmpty { parts.append(text) }
        }
        var verdicts: [String] = []
        for attempt in results where !verdicts.contains(attempt.verdict) { verdicts.append(attempt.verdict) }
        evidence.guardVerdict = verdicts.joined(separator: ",")
        evidence.outcome = .insert(toned(parts.joined(separator: " "), input: prepared.text, style: style)); return evidence
    }

    /// A short, stable name for a system prompt: the template without the per-call tag nonce and without the
    /// style notes, screen-context note and dictionary terms appended after it, so a changed template is the
    /// only thing that changes the hash. FNV-1a, 8 hex digits.
    static func promptHash(_ system: String) -> String {
        var base = system
        for marker in ["\n\nThe user's style notes", "\n\nThe user message may start with reference text", "\n\nPreferred spellings from"] {
            if let range = base.range(of: marker) { base = String(base[..<range.lowerBound]) }
        }
        base = base.replacingOccurrences(of: #"-[0-9A-Fa-f]{32}"#, with: "-N", options: .regularExpression)
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in base.utf8 { hash = (hash ^ UInt64(byte)) &* 0x100000001b3 }
        return String(format: "%016llx", hash).prefix(8).description
    }

    /// One section's cloud result. Nil `text` means use local rules; `reason` is why the full
    /// cleanup was not used, `error` the provider error, and `rejected` the output the guard refused.
    struct CloudAttempt: Sendable {
        var text: String?
        var reason: CleanupFailureReason?
        var error: ProviderError?
        var rejected: String? = nil
        /// The model's output the guard accepted, as returned.
        var accepted: String? = nil
        /// "cleaned", "unchanged", "rejected" or "provider-error".
        var verdict = "provider-error"
        var promptHash: String? = nil
    }

    /// A preset's rewrite in one provider call, no sections, no tone or cleanup. Nil on a provider error or a rejected output.
    private static func transformClean(_ text: String, kind: ModeTransform, provider: CleanupProvider, terms: [String], instructions: String) async -> CloudAttempt? {
        let messages = TransformPrompt.build(kind, transcript: text, dictionaryTerms: terms, instructions: instructions)
        guard let output = try? await provider.cleanup(messages) else { return nil }
        let out = output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard TransformGuard.accepts(input: text, output: out) else { return nil }
        return CloudAttempt(text: out, reason: nil, error: nil, accepted: out, verdict: "transform", promptHash: promptHash(messages.system))
    }

    /// One provider call, no retry (a rate limit or an outage would only fail again). If the guard
    /// rejects the cleaned words, the transcript's words take the model's punctuation instead.
    /// The guard judges against the transcript section only: screen context never widens what it accepts.
    private static func cloudClean(_ section: String, provider: CleanupProvider, options: CleanupOptions, terms: [String],
                                   context: ScreenContext? = nil, instructions: String = "") async -> CloudAttempt {
        let output: String
        let messages = CleanupPrompt.build(transcript: section, options: options, dictionaryTerms: terms, context: context, instructions: instructions)
        // The managed model's prompt is fixed inside the helper, so the built prompt's hash would be misleading.
        let hash = provider.id == .localModel ? "local-fixed" : promptHash(messages.system)
        do { output = try await provider.cleanup(messages) }
        catch { return CloudAttempt(text: nil, reason: .cleanerFailed, error: error as? ProviderError, promptHash: hash) }
        let reason: CleanupFailureReason
        // The managed local model is always held to the full guard (numbers, names, negations), whatever the global trust setting.
        var judged = options
        if provider.id == .localModel { judged.trustModel = false }
        switch CleanupPipeline.judge(raw: section, cleaned: output, options: judged, allowedTerms: terms, soundsLike: DictionaryStore.soundsLike) {
        case .cleaned(let text): return CloudAttempt(text: text, reason: nil, error: nil, accepted: output, verdict: "cleaned", promptHash: hash)
        case .unchanged(let text): return CloudAttempt(text: text, reason: nil, error: nil, accepted: output, verdict: "unchanged", promptHash: hash)
        case .heldForRecovery(_, let why): reason = why
        }
        // Transfer punctuation onto the full local cleanup, so rejecting the model's rewrite never
        // skips the enabled local operations (fillers, corrections, lists, spoken commands).
        let fallback = localRules(section, terms: terms, options: options).text
        if options.punctuation, let words = CleanupPipeline.punctuated(raw: fallback, cleaned: output, terms: terms),
           case .cleaned(let text) = CleanupPipeline.judge(raw: fallback, cleaned: words, options: punctuationOnly, allowedTerms: terms) {
            return CloudAttempt(text: text, reason: reason, error: nil, rejected: output, verdict: "rejected", promptHash: hash)
        }
        return CloudAttempt(text: nil, reason: reason, error: nil, rejected: output, verdict: "rejected", promptHash: hash)
    }

    // MARK: Learning

    /// One change to a watched dictated span (AppController polls `DictationObservation`).
    /// An edit becomes provisional evidence; back to the original (undo) or lost attribution
    /// (pass `original`) drops this session's evidence. Call `settle` when the window closes.
    /// Returns what the ledger made of it, or nil when the span went back to the original.
    @discardableResult
    func observeSpan(sessionID: UUID, original: String, current: String) throws -> CorrectionLedger.RecordOutcome? {
        // The ledger diffs the span word by word, so punctuation and unchanged words never reach the filter.
        if current == original { try ledger.revert(sessionID: sessionID); return nil }
        return try observe(ObservedCorrection(sessionID: sessionID, source: original, target: current, observedAt: Date()))
    }

    /// Feeds one attributed correction while its observation window is open. With learning off
    /// nothing is recorded.
    @discardableResult
    func observe(_ correction: ObservedCorrection) throws -> CorrectionLedger.RecordOutcome {
        guard settings.learningEnabled else { return .learningDisabled }
        return try ledger.record(correction)
    }

    /// The observation window closed: commit evidence. Returns newly learned entries.
    @discardableResult
    func settle(sessionID: UUID) throws -> [DictionaryEntry] {
        try ledger.settle(sessionID: sessionID)
    }

    // MARK: Dictionary editing

    /// Variants are comma-separated; blanks are dropped.
    private static func variantList(_ variants: String) -> [String] {
        variants.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    @discardableResult
    func addEntry(preferred: String, variants: String) throws -> DictionaryEntry {
        try dictionary.add(preferred: preferred, variants: Self.variantList(variants), source: .manual)
    }

    /// Edits an entry in place; a learned entry stays learned. Its ledger promotion is keyed by
    /// entry ID, so old evidence cannot re-promote the original spelling alongside the edit.
    func updateEntry(id: UUID, preferred: String, variants: String, enabled: Bool) throws {
        guard var entry = dictionary.entries.first(where: { $0.id == id }) else { throw DictionaryError("Dictionary entry not found") }
        entry.preferred = preferred; entry.variants = Self.variantList(variants); entry.enabled = enabled
        try dictionary.update(entry)
    }

    /// A learned entry is tombstoned so old evidence cannot re-promote it.
    func deleteEntry(id: UUID) throws {
        if dictionary.entries.first(where: { $0.id == id })?.source == .learned { try ledger.deleteLearned(id: id) }
        else { try dictionary.delete(id: id) }
    }

    /// Removes learned entries and all learning evidence; manual entries stay.
    func resetLearned() throws { try ledger.reset() }
}
