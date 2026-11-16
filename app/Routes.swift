import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Processing routes (PRD R4, R5): which transcriber and cleaner a dictation uses, what each one
// sends off this Mac, and the async glue between the providers and the controller.
// Foundation only, so app/check_routes.swift runs on Linux. Controller.swift keeps the AppKit
// calls (NSAlert, Keychain-backed store, NSSpellChecker) and only acts on these decisions.
//
// Rules enforced here:
// - No fallback. A cloud route that fails or is not configured never runs the local model or
//   another provider; the session ends in the Error phase with its audio or raw text kept.
// - Each stage that sends data off this Mac is disclosed inline in Settings. A local model
//   server on a loopback address sends nothing off this Mac and needs none.
// - Messages are built from fixed strings and `ProviderError.userMessage`; they never contain a
//   key, a response body or transcript text.

// MARK: - Route settings

enum TranscriptionRoute: String, CaseIterable, Sendable {
    /// Parakeet through the bundled ModelBench helper. The default.
    case local
    case cloud
}

/// Cloud transcribers offered in Settings. Other OpenAI-compatible services use the custom endpoint.
/// To add a provider, add a case here, a `displayName` and `providerID`, and one case in
/// `makeTranscriptionProvider(id:key:)`.
enum CloudTranscriber: String, CaseIterable, Identifiable, Sendable {
    /// A user-configured OpenAI-compatible audio transcription endpoint.
    case customAPI = "custom-api"
    case gemini

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .customAPI: return "Custom Provider"
        case .gemini: return "Gemini"
        }
    }

    /// The Keychain account that holds this transcriber's key.
    var providerID: ProviderID {
        switch self {
        case .customAPI: return .customAPI
        case .gemini: return .gemini
        }
    }

    var credentialID: ProviderID { self == .customAPI ? .customTranscription : providerID }
}

/// The one place a cloud transcriber is constructed. Custom servers may not require a key.
func makeTranscriptionProvider(id: CloudTranscriber, key: APIKey?, apiBaseURL: String = RouteSettings.defaultCustomAPIURL, model: String = "whisper-1", geminiModel: String = GeminiTranscribeProvider.defaultModel) -> TranscriptionProvider? {
    switch id {
    case .customAPI: return try? OpenAICompatibleTranscriptionProvider(baseURL: apiBaseURL, model: model, apiKey: key)
    case .gemini:
        guard let key else { return nil }
        let name = geminiModel.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return nil }
        guard name != GeminiTranscribeProvider.defaultModel else {
            var provider = GeminiTranscribeProvider(apiKey: key)
            provider.model = name
            provider.languageCodes = ["en-US"]  // Google's language table lists en-US, not a bare en.
            return provider
        }
        var provider = GeminiTranscriptionProvider(apiKey: key)
        provider.model = name
        return provider
    }
}

enum CleanupChoice: String, CaseIterable, Identifiable, Sendable {
    /// The built-in RuleCleaner. The default.
    case localRules = "local-rules"
    /// Apple's on-device system model (AppleCleanup.swift): no key, nothing leaves this Mac.
    case appleIntelligence = "apple-intelligence"
    /// The managed local model (LocalCleanup.swift): downloaded on request, runs on this Mac.
    case localModel = "local-model"
    /// No longer offered for dictation cleanup; kept for meeting notes. A saved dictation choice of Gemini loads as local rules.
    case gemini
    case customAPI = "custom-api"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .localRules: return "Local rules (this Mac)"
        case .appleIntelligence: return "Apple Intelligence (this Mac)"
        case .localModel: return "Local model (this Mac)"
        case .gemini: return "Gemini"
        case .customAPI: return "Custom Provider"
        }
    }

    /// ProcessingCoordinator's route: anything but the rule cleaner goes through a provider.
    var processingRoute: CleanupRoute { self == .localRules ? .local : .cloud }

    var providerID: ProviderID? {
        switch self {
        case .localRules: return nil
        case .appleIntelligence: return .appleIntelligence
        case .localModel: return .localModel
        case .gemini: return .gemini
        case .customAPI: return .customAPI
        }
    }

    /// What Settings offers for dictation cleanup.
    static let selectable: [CleanupChoice] = [.localRules, .appleIntelligence, .localModel, .customAPI]

    var credentialID: ProviderID? { self == .customAPI ? .customCleanup : providerID }
}

/// The one place a cleanup provider is constructed. Nil for local rules or invalid custom settings.
func makeCleanupProvider(_ choice: CleanupChoice, key: APIKey?, apiBaseURL: String, model: String) -> CleanupProvider? {
    switch choice {
    case .localRules: return nil
    case .appleIntelligence: return AppleCleanupProvider()
    case .localModel: return LocalModelCleanupProvider()
    case .gemini: return key.map { GeminiCleanupProvider(apiKey: $0) }
    case .customAPI:
        let name = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return nil }
        return try? OpenAICompatibleCleanupProvider(baseURL: apiBaseURL, model: name, apiKey: key, provider: .customAPI)
    }
}

/// Route settings persisted in UserDefaults. Keys are never stored here (they live in the Keychain).
struct RouteSettings: Equatable, Sendable {
    static let defaultCustomAPIURL = "https://api.cerebras.ai/v1"
    static let defaultCustomAPIModel = "gpt-oss-120b"

    var transcription: TranscriptionRoute = .local
    var cloudTranscriber: CloudTranscriber = .gemini
    var cleanup: CleanupChoice = .localRules
    var customAPIBaseURL: String = RouteSettings.defaultCustomAPIURL
    var customTranscriptionAPIBaseURL: String = RouteSettings.defaultCustomAPIURL
    var customAPIModel: String = RouteSettings.defaultCustomAPIModel
    var customTranscriptionModel: String = "whisper-1"

    /// The endpoint setting a custom provider's key is bound to (customTranscription follows its own address, customCleanup the
    /// cleanup one); nil for providers whose key has no destination. Changing it requires saving the key again (`CustomAPIKeyBinding`).
    func apiBaseURL(for provider: ProviderID) -> String? {
        switch provider {
        case .customTranscription: return customTranscriptionAPIBaseURL
        case .customCleanup: return customAPIBaseURL
        default: return nil
        }
    }
    var geminiTranscriptionModel: String = GeminiTranscribeProvider.defaultModel

    enum Key {
        static let transcription = "transcriptionRoute"
        static let cloudTranscriber = "cloudTranscriber"
        static let cleanup = "cleanupChoice"
        static let customAPIBaseURL = "customAPIBaseURL"
        static let customTranscriptionAPIBaseURL = "customTranscriptionAPIBaseURL"
        static let customAPIModel = "customAPIModel"
        static let customTranscriptionModel = "customTranscriptionModel"
        static let geminiTranscriptionModel = "geminiTranscriptionModel"
        static let geminiCleanupRemovedNotice = "geminiCleanupRemovedNotice"
        static let legacyServerURL = "localServerURL"
        static let legacyServerModel = "localServerModel"
    }

    static func legacyCustomAPIKey(from choice: String?) -> ProviderID? {
        switch choice {
        case "local-server": return .localServer
        case "openrouter": return .openrouter
        case "cerebras": return .cerebras
        default: return nil
        }
    }

    /// Persist migration only after copying the selected route's key, so failure can be retried.
    static func migrateCustomAPI(in defaults: UserDefaults, credentials: CredentialStore) throws {
        defaults.removeObject(forKey: "acknowledgedCloudRoutes")
        if let legacy = legacyCustomAPIKey(from: defaults.string(forKey: Key.cleanup)) ??
            (defaults.string(forKey: Key.cloudTranscriber) == "openrouter-whisper" ? .openrouter : nil) {
            if !credentials.exists(.customAPI), let key = try credentials.read(legacy) {
                // Old keys have no destination proof; edited preferences must not silently approve them.
                defaults.removeObject(forKey: CustomAPIKeyBinding.preference)
                try credentials.save(key, for: .customAPI)
            }
        }
        // Gemini is no longer a dictation cleanup choice: say so once in Settings instead of switching silently.
        if defaults.string(forKey: Key.cleanup) == "gemini" { defaults.set(true, forKey: Key.geminiCleanupRemovedNotice) }
        load(from: defaults).save(to: defaults)
        guard !defaults.bool(forKey: "customAPIKeysSplitMigrationComplete") else { return }
        let settings = load(from: defaults)
        if let legacyKey = try credentials.read(.customAPI) {
            try credentials.save(legacyKey, for: .customTranscription)
            try credentials.save(legacyKey, for: .customCleanup)
            if let bound = defaults.string(forKey: CustomAPIKeyBinding.preference),
               let normalized = settings.apiBaseURL(for: .customCleanup).flatMap({ try? CustomAPIKeyBinding.endpoint($0) }), bound == normalized {
                defaults.set(bound, forKey: CustomAPIKeyBinding.preference(.customCleanup))
            }
            if let bound = defaults.string(forKey: CustomAPIKeyBinding.preference),
               let normalized = settings.apiBaseURL(for: .customTranscription).flatMap({ try? CustomAPIKeyBinding.endpoint($0) }), bound == normalized {
                defaults.set(bound, forKey: CustomAPIKeyBinding.preference(.customTranscription))
            }
            try credentials.delete(.customAPI)
            defaults.removeObject(forKey: CustomAPIKeyBinding.preference)
        }
        defaults.set(true, forKey: "customAPIKeysSplitMigrationComplete")
    }

    /// Missing or unknown values fall back to the approved local defaults.
    static func load(from defaults: UserDefaults) -> RouteSettings {
        var settings = RouteSettings()
        settings.transcription = defaults.string(forKey: Key.transcription).flatMap(TranscriptionRoute.init(rawValue:)) ?? .local
        let legacyTranscription = defaults.string(forKey: Key.cloudTranscriber) == "openrouter-whisper"
        let oldTranscriber = defaults.string(forKey: Key.cloudTranscriber)
        settings.cloudTranscriber = legacyTranscription ? .customAPI : (oldTranscriber == "gemini-audio" || oldTranscriber == "gemini-transcribe" ? .gemini : oldTranscriber.flatMap(CloudTranscriber.init(rawValue:)) ?? .gemini)
        settings.customTranscriptionModel = defaults.string(forKey: Key.customTranscriptionModel) ?? (legacyTranscription ? "openai/whisper-large-v3-turbo" : "whisper-1")
        settings.geminiTranscriptionModel = defaults.string(forKey: Key.geminiTranscriptionModel) ??
            (oldTranscriber == "gemini-audio" ? GeminiTranscriptionProvider.defaultModel : GeminiTranscribeProvider.defaultModel)
        let oldChoice = defaults.string(forKey: Key.cleanup)
        settings.cleanup = oldChoice == "gemini" ? .localRules : oldChoice.flatMap(CleanupChoice.init(rawValue:)) ?? (legacyCustomAPIKey(from: oldChoice) == nil ? .localRules : .customAPI)
        settings.customAPIBaseURL = defaults.string(forKey: Key.customAPIBaseURL) ?? {
            switch oldChoice {
            case "cerebras": return "https://api.cerebras.ai/v1"
            case "openrouter": return "https://openrouter.ai/api/v1"
            case "local-server": return defaults.string(forKey: Key.legacyServerURL) ?? OpenAICompatibleCleanupProvider.ollamaDefault
            default: return legacyTranscription ? "https://openrouter.ai/api/v1" : defaultCustomAPIURL
            }
        }()
        settings.customTranscriptionAPIBaseURL = defaults.string(forKey: Key.customTranscriptionAPIBaseURL) ?? settings.customAPIBaseURL
        settings.customAPIModel = defaults.string(forKey: Key.customAPIModel) ?? {
            switch oldChoice {
            case "cerebras": return "gpt-oss-120b"
            case "openrouter": return "google/gemini-3.5-flash-lite"
            case "local-server": return defaults.string(forKey: Key.legacyServerModel) ?? ""
            default: return defaultCustomAPIModel
            }
        }()
        return settings
    }

    func save(to defaults: UserDefaults) {
        defaults.set(transcription.rawValue, forKey: Key.transcription)
        defaults.set(cloudTranscriber.rawValue, forKey: Key.cloudTranscriber)
        defaults.set(cleanup.rawValue, forKey: Key.cleanup)
        defaults.set(customAPIBaseURL, forKey: Key.customAPIBaseURL)
        defaults.set(customTranscriptionAPIBaseURL, forKey: Key.customTranscriptionAPIBaseURL)
        defaults.set(customAPIModel, forKey: Key.customAPIModel)
        defaults.set(customTranscriptionModel, forKey: Key.customTranscriptionModel)
        defaults.set(geminiTranscriptionModel, forKey: Key.geminiTranscriptionModel)
    }
}

/// Only the endpoint identifier lives in preferences; the secret stays in the Keychain.
enum CustomAPIKeyBinding {
    static let preference = "customAPIKeyEndpoint"
    static func preference(_ provider: ProviderID) -> String { provider == .customAPI ? preference : "\(preference).\(provider.rawValue)" }

    static func endpoint(_ raw: String) throws -> String {
        let url = try LocalEndpoint.normalize(raw)
        var components = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        if components.port == (components.scheme == "https" ? 443 : 80) { components.port = nil }
        return components.string!
    }

    static func save(_ key: APIKey, at address: String, defaults: UserDefaults, credentials: CredentialStore, provider: ProviderID = .customAPI) throws {
        let destination = try endpoint(address)
        // Fail closed if replacing a key fails partway through: never keep the old binding.
        defaults.removeObject(forKey: preference(provider))
        try credentials.save(key, for: provider)
        defaults.set(destination, forKey: preference(provider))
    }

    static func read(at address: String, defaults: UserDefaults, credentials: CredentialStore, provider: ProviderID = .customAPI) throws -> APIKey? {
        guard credentials.exists(provider) else { return nil }
        guard defaults.string(forKey: preference(provider)) == (try endpoint(address)) else { throw CredentialError.destinationMismatch }
        return try credentials.read(provider)
    }
}

// MARK: - Route plan

/// One stage that sends data off this Mac and its Settings disclosure.
struct DisclosureStage: Equatable, Sendable {
    let disclosure: DataDisclosure
}

/// What a dictation would use with these settings and keys, decided before recording starts.
struct RoutePlan {
    /// Nil means local transcription (Parakeet).
    let transcriber: TranscriptionProvider?
    /// Nil means the local rule cleaner, or cleanup that sends nothing (all options off).
    let cleaner: CleanupProvider?
    /// Why a dictation cannot start on this route, or nil. Never triggers a fallback.
    let refusal: String?
    /// Stages whose data leaves this Mac, in transcription-then-cleanup order.
    let stages: [DisclosureStage]

    var usesLocalModel: Bool { transcriber == nil && refusal == nil }

    /// Reads only the disclosure of a provider; this placeholder is never sent anywhere.
    private static let placeholderKey = APIKey("disclosure-only")!

    /// `cleanupSendsText` is false when every cleanup option is off: the provider is not called
    /// then, so nothing is sent and no key is needed.
    static func make(_ settings: RouteSettings, cleanupSendsText: Bool, key: (ProviderID) -> APIKey?) -> RoutePlan {
        var stages: [DisclosureStage] = []
        var refusal: String?
        var transcriber: TranscriptionProvider?
        if settings.transcription == .cloud {
            let choice = settings.cloudTranscriber
            let credentialID = choice.credentialID
            transcriber = makeTranscriptionProvider(id: choice, key: key(credentialID), apiBaseURL: settings.customTranscriptionAPIBaseURL, model: settings.customTranscriptionModel, geminiModel: settings.geminiTranscriptionModel)
            if transcriber == nil { refusal = ProviderError.invalidConfiguration(choice.providerID).userMessage }
            if let disclosure = (transcriber ?? makeTranscriptionProvider(id: choice, key: placeholderKey, apiBaseURL: settings.customTranscriptionAPIBaseURL, model: settings.customTranscriptionModel, geminiModel: settings.geminiTranscriptionModel))?.disclosure, !disclosure.staysOnThisMac {
                stages.append(DisclosureStage(disclosure: disclosure))
            }
        }
        var cleaner: CleanupProvider?
        if settings.cleanup != .localRules && cleanupSendsText, let provider = settings.cleanup.providerID {
            let storedKey = settings.cleanup == .appleIntelligence || settings.cleanup == .localModel ? nil : key(settings.cleanup.credentialID ?? provider)
            cleaner = makeCleanupProvider(settings.cleanup, key: storedKey, apiBaseURL: settings.customAPIBaseURL, model: settings.customAPIModel)
            if cleaner == nil, refusal == nil { refusal = ProviderError.invalidConfiguration(provider).userMessage }
            let preview = cleaner ?? makeCleanupProvider(settings.cleanup, key: storedKey ?? placeholderKey,
                                                        apiBaseURL: settings.customAPIBaseURL, model: settings.customAPIModel)
            if let disclosure = preview?.disclosure, !disclosure.staysOnThisMac {
                stages.append(DisclosureStage(disclosure: disclosure))
            }
        }
        return RoutePlan(transcriber: transcriber, cleaner: cleaner, refusal: refusal, stages: stages)
    }
}

enum DisclosureText {
    /// One-line description for Settings, shown before any cloud use.
    static func summary(_ plan: RoutePlan, stage: ProcessingStage) -> String? {
        plan.stages.first { $0.disclosure.stage == stage }?.disclosure.summary
    }
}

// MARK: - Cloud transcription

/// A transcriber that can tell from the recording's level that it should not be called.
protocol SilencePrecheckedTranscriber: TranscriptionProvider {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool
}

/// A language-model transcriber that flags reply-shaped output for recovery.
protocol LLMTranscriber: TranscriptionProvider {
    func transcribeChecked(_ audio: AudioPayload) async throws -> LLMTranscript
}

extension GeminiTranscriptionProvider: SilencePrecheckedTranscriber, LLMTranscriber {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool {
        Self.shouldSkipForSilence(peakLevel: peakLevel, durationSeconds: durationSeconds)
    }
}

/// Whisper-style models invent "Thank you." or "Okay." for silence, so silent clips are not sent.
extension OpenAICompatibleTranscriptionProvider: SilencePrecheckedTranscriber {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool {
        GeminiTranscriptionProvider.shouldSkipForSilence(peakLevel: peakLevel, durationSeconds: durationSeconds)
    }
}

enum CloudTranscriptionOutcome: Equatable, Sendable {
    case transcript(String)
    /// An LLM transcriber's output looked like an answer: save it for recovery, never insert.
    case heldAnswerSuspected(String)
    /// Silence was skipped before sending (`skipped`), or the provider heard nothing.
    case noSpeech(skipped: Bool)
    /// `message` is safe to show; `keepAudio` follows the provider error's disposition.
    case failed(message: String, keepAudio: Bool)
    case cancelled
}

/// What the pre-check and transcriber saw, kept in the History entry (never logged).
struct CloudTranscriptionDetail: Sendable, Equatable {
    var clipSeconds: Double? = nil
    var peakP50: Float? = nil, peakP90: Float? = nil
    /// The pre-check's level (second-highest 100 ms peak); nil when the audio was not decoded.
    var silencePeak: Float? = nil
    /// "passed", "skipped", or "unchecked" when the provider has no pre-check or no level.
    var precheck = "unchecked"
    var segments: [TranscriptSegment]? = nil
    /// Why the outcome was no speech: "precheck", "lowConfidence", "insufficientLoudness", "empty" or "punctuationOnly".
    var noSpeechReason: String? = nil

    /// Content-free line for the unified log: reason, clip loudness and pre-check level, segment scores. Never text.
    var noSpeechLogLine: String {
        func f(_ value: Float?) -> String { value.map { String(format: "%.4f", $0) } ?? "-" }
        let scores = (segments ?? []).compactMap(\.avgLogprob)
        let range = scores.isEmpty ? "-" : String(format: "%.2f/%.2f", scores.min()!, scores.max()!)
        return "no-speech reason=\(noSpeechReason ?? "unknown") clip=\(clipSeconds.map { String(format: "%.2f", $0) } ?? "-") p50=\(f(peakP50)) p90=\(f(peakP90)) "
            + "silence=\(f(silencePeak))/\(f(silencePeak == nil ? nil : AudioFormat.silencePeak)) segments=\(segments?.count ?? 0) logprob-min-max=\(range)"
    }
}

enum CloudTranscription {
    static let unreadableAudio = "The recording could not be read."
    static let unknownFailure = "Cloud transcription failed."

    /// Reads the recording off the main thread and runs `run(provider:audioData:vocabulary:)`.
    static func run(provider: TranscriptionProvider, audioFile: URL, vocabulary: [String] = []) async -> CloudTranscriptionOutcome {
        await runDetailed(provider: provider, audioFile: audioFile, vocabulary: vocabulary).outcome
    }

    static func run(provider: TranscriptionProvider, audioData: Data, vocabulary: [String] = []) async -> CloudTranscriptionOutcome {
        await runDetailed(provider: provider, audioData: audioData, vocabulary: vocabulary).outcome
    }

    static func runDetailed(provider: TranscriptionProvider, audioFile: URL, vocabulary: [String] = []) async -> (outcome: CloudTranscriptionOutcome, detail: CloudTranscriptionDetail) {
        guard let data = try? Data(contentsOf: audioFile) else { return (.failed(message: unreadableAudio, keepAudio: true), CloudTranscriptionDetail()) }
        return await runDetailed(provider: provider, audioData: data, vocabulary: vocabulary)
    }

    /// One provider call at most: silence pre-check, then one request, no retry, no fallback.
    /// `vocabulary` (dictionary spellings) reaches only a provider that `acceptsVocabulary`; the payload of any other carries none.
    /// The detail (clip loudness, pre-check, segments) is for History; it never changes the outcome.
    static func runDetailed(provider: TranscriptionProvider, audioData: Data, vocabulary: [String] = []) async -> (outcome: CloudTranscriptionOutcome, detail: CloudTranscriptionDetail) {
        var prepared = CloudAudio.prepare(audioData)
        if provider.acceptsVocabulary { prepared.payload.vocabulary = vocabulary }
        var detail = CloudTranscriptionDetail(clipSeconds: prepared.duration, silencePeak: prepared.peak)
        if let peaks = prepared.payload.levels.flatMap(OpenAITranscriptionFormat.peakPercentiles) { detail.peakP50 = peaks.p50; detail.peakP90 = peaks.p90 }
        if let checker = provider as? SilencePrecheckedTranscriber, let peak = prepared.peak {
            if checker.shouldSkip(peakLevel: peak, durationSeconds: prepared.duration) { detail.precheck = "skipped"; detail.noSpeechReason = "precheck"; return (.noSpeech(skipped: true), detail) }
            detail.precheck = "passed"
        }
        if Task.isCancelled { return (.cancelled, detail) }
        do {
            let text: String
            var suspected = false
            if let llm = provider as? LLMTranscriber {
                let result = try await llm.transcribeChecked(prepared.payload)
                text = result.text; suspected = result.answerSuspected
            } else if let detailed = provider as? DetailedTranscriber {
                let result = try await detailed.transcribeDetailed(prepared.payload)
                text = result.text; detail.segments = result.segments; detail.noSpeechReason = result.noSpeechReason
            } else {
                text = try await provider.transcribe(prepared.payload)
            }
            if Task.isCancelled { return (.cancelled, detail) }
            // A transcript of only punctuation (" .") is silence too: nothing is pasted.
            if !text.contains(where: { $0.isLetter || $0.isNumber }) {
                detail.noSpeechReason = detail.noSpeechReason ?? (!text.isEmpty ? "punctuationOnly" : "empty")
                return (.noSpeech(skipped: false), detail)
            }
            return (suspected ? .heldAnswerSuspected(text) : .transcript(text), detail)
        } catch let error as ProviderError {
            if error == .cancelled || Task.isCancelled { return (.cancelled, detail) }
            return (.failed(message: error.userMessage, keepAudio: error.disposition != .discard), detail)
        } catch {
            if Task.isCancelled { return (.cancelled, detail) }
            return (.failed(message: unknownFailure, keepAudio: true), detail)
        }
    }

    /// Text for the Error phase.
    static func message(_ outcome: CloudTranscriptionOutcome) -> String? {
        switch outcome {
        case .transcript: return nil
        case .heldAnswerSuspected: return "The transcript looked like an answer rather than dictation, so it was held for review. It is in History."
        case .noSpeech: return "No speech was detected. Check your microphone and try again."
        case .failed(let message, let keepAudio): return keepAudio ? message + " Audio is saved for retry for up to 24 hours." : message
        case .cancelled: return "Canceled. The recording was deleted."
        }
    }
}

// MARK: - Async glue

/// Runs one async request at a time for the controller. Main actor only. `cancel()` cancels the
/// Task (which cancels the HTTP request) and guarantees `deliver` is never called for it, even
/// if the work had already finished and was waiting for the main actor.
@MainActor final class CloudRequest {
    private var task: Task<Void, Never>?
    private var token = UUID()

    var isActive: Bool { task != nil }

    func start<T>(_ work: @escaping @MainActor () async -> T, deliver: @escaping @MainActor (T) -> Void) {
        cancel()
        let current = UUID()
        token = current
        task = Task { @MainActor [weak self] in
            let value = await work()
            guard let self, self.token == current, !Task.isCancelled else { return }
            self.task = nil
            deliver(value)
        }
    }

    func cancel() {
        token = UUID()
        task?.cancel()
        task = nil
    }
}
