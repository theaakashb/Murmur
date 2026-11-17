import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Processing routes: which transcriber and cleaner a dictation uses, what each one
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


enum TranscriptionRoute: String, CaseIterable, Sendable {
    /// Parakeet through the bundled ModelBench helper. The default.
    case local
    /// A custom OpenAI-compatible endpoint.
    case cloud
    /// Murmur Cloud (MurmurCloud.swift), with the Murmur Cloud key.
    case murmurCloud = "murmur-cloud"
}

/// The one place a cloud transcriber is constructed: a user-configured OpenAI-compatible audio transcription
/// endpoint. Custom servers may not require a key.
func makeTranscriptionProvider(key: APIKey?, apiBaseURL: String, model: String) -> TranscriptionProvider? {
    let name = model.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !name.isEmpty else { return nil }
    return try? OpenAICompatibleTranscriptionProvider(baseURL: apiBaseURL, model: name, apiKey: key)
}

enum CleanupChoice: String, CaseIterable, Identifiable, Sendable {
    /// The built-in RuleCleaner. The default.
    case localRules = "local-rules"
    /// Apple's on-device system model (AppleCleanup.swift): no key, nothing leaves this Mac.
    case appleIntelligence = "apple-intelligence"
    /// The managed local model (LocalCleanup.swift): downloaded on request, runs on this Mac.
    case localModel = "local-model"
    case customAPI = "custom-api"
    /// Murmur Cloud (MurmurCloud.swift): the server builds the prompt; the guard stays here.
    case murmurCloud = "murmur-cloud"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .localRules: return "Local rules (this Mac)"
        case .appleIntelligence: return "Apple Intelligence (this Mac)"
        case .localModel: return "Local model (this Mac)"
        case .customAPI: return "Custom Provider"
        case .murmurCloud: return "Murmur Cloud"
        }
    }

    /// ProcessingCoordinator's route: anything but the rule cleaner goes through a provider.
    var processingRoute: CleanupRoute { self == .localRules ? .local : .cloud }

    var providerID: ProviderID? {
        switch self {
        case .localRules: return nil
        case .appleIntelligence: return .appleIntelligence
        case .localModel: return .localModel
        case .customAPI: return .customAPI
        case .murmurCloud: return .murmurCloud
        }
    }

    var credentialID: ProviderID? { self == .customAPI ? .customCleanup : providerID }
}

/// The one place a cleanup provider is constructed. Nil for local rules or invalid custom settings.
func makeCleanupProvider(_ choice: CleanupChoice, key: APIKey?, apiBaseURL: String, model: String, reasoningEffort: String = "") -> CleanupProvider? {
    switch choice {
    case .localRules: return nil
    case .appleIntelligence: return AppleCleanupProvider()
    case .localModel: return LocalModelCleanupProvider()
    case .customAPI:
        let name = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return nil }
        var provider = try? OpenAICompatibleCleanupProvider(baseURL: apiBaseURL, model: name, apiKey: key, provider: .customAPI)
        let effort = reasoningEffort.trimmingCharacters(in: .whitespacesAndNewlines)
        provider?.reasoningEffort = effort.isEmpty ? nil : effort
        return provider
    case .murmurCloud: return key.map { MurmurCloudCleanupProvider(apiKey: $0) }
    }
}

/// Route settings persisted in UserDefaults. Keys are never stored here (they live in the Keychain).
struct RouteSettings: Equatable, Sendable {
    /// Shown in the empty address and model fields. A custom provider has no default: nothing is sent until the user enters their own.
    static let placeholderAPIURL = "https://api.example.com/v1"
    static let placeholderModel = "model-name"

    var transcription: TranscriptionRoute = .local
    var cleanup: CleanupChoice = .localRules
    var customAPIBaseURL = ""
    var customTranscriptionAPIBaseURL = ""
    var customAPIModel = ""
    var customTranscriptionModel = ""
    /// Sent to the custom cleanup provider as `reasoning_effort`; empty sends none. Low measured about 80% fewer output
    /// tokens and half the latency on gpt-oss (Groq, November 2026).
    var customAPIReasoningEffort = "low"

    /// The endpoint setting a custom provider's key is bound to (customTranscription follows its own address, customCleanup the
    /// cleanup one); nil for providers whose key has no destination. Changing it requires saving the key again (`CustomAPIKeyBinding`).
    func apiBaseURL(for provider: ProviderID) -> String? {
        switch provider {
        case .customTranscription: return customTranscriptionAPIBaseURL
        case .customCleanup: return customAPIBaseURL
        default: return nil
        }
    }

    enum Key {
        static let transcription = "transcriptionRoute"
        static let legacyCloudTranscriber = "cloudTranscriber"
        static let cleanup = "cleanupChoice"
        static let customAPIBaseURL = "customAPIBaseURL"
        static let customTranscriptionAPIBaseURL = "customTranscriptionAPIBaseURL"
        static let customAPIModel = "customAPIModel"
        static let customTranscriptionModel = "customTranscriptionModel"
        static let customAPIReasoningEffort = "customAPIReasoningEffort"
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
            (defaults.string(forKey: Key.legacyCloudTranscriber) == "openrouter-whisper" ? .openrouter : nil) {
            if !credentials.exists(.customAPI), let key = try credentials.read(legacy) {
                // Old keys have no destination proof; edited preferences must not silently approve them.
                defaults.removeObject(forKey: CustomAPIKeyBinding.preference)
                try credentials.save(key, for: .customAPI)
            }
        }
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
        let legacyTranscription = defaults.string(forKey: Key.legacyCloudTranscriber) == "openrouter-whisper"
        settings.customTranscriptionModel = defaults.string(forKey: Key.customTranscriptionModel) ?? (legacyTranscription ? "openai/whisper-large-v3-turbo" : "")
        let oldChoice = defaults.string(forKey: Key.cleanup)
        settings.cleanup = oldChoice.flatMap(CleanupChoice.init(rawValue:)) ?? (legacyCustomAPIKey(from: oldChoice) == nil ? .localRules : .customAPI)
        settings.customAPIBaseURL = defaults.string(forKey: Key.customAPIBaseURL) ?? {
            switch oldChoice {
            case "cerebras": return "https://api.cerebras.ai/v1"
            case "openrouter": return "https://openrouter.ai/api/v1"
            case "local-server": return defaults.string(forKey: Key.legacyServerURL) ?? OpenAICompatibleCleanupProvider.ollamaDefault
            default: return legacyTranscription ? "https://openrouter.ai/api/v1" : ""
            }
        }()
        settings.customTranscriptionAPIBaseURL = defaults.string(forKey: Key.customTranscriptionAPIBaseURL) ?? settings.customAPIBaseURL
        settings.customAPIModel = defaults.string(forKey: Key.customAPIModel) ?? {
            switch oldChoice {
            case "cerebras": return "gpt-oss-120b"
            case "openrouter": return "google/gemini-3.5-flash-lite"
            case "local-server": return defaults.string(forKey: Key.legacyServerModel) ?? ""
            default: return ""
            }
        }()
        settings.customAPIReasoningEffort = defaults.string(forKey: Key.customAPIReasoningEffort) ?? "low"
        return settings
    }

    func save(to defaults: UserDefaults) {
        defaults.set(transcription.rawValue, forKey: Key.transcription)
        defaults.set(cleanup.rawValue, forKey: Key.cleanup)
        defaults.set(customAPIBaseURL, forKey: Key.customAPIBaseURL)
        defaults.set(customTranscriptionAPIBaseURL, forKey: Key.customTranscriptionAPIBaseURL)
        defaults.set(customAPIModel, forKey: Key.customAPIModel)
        defaults.set(customTranscriptionModel, forKey: Key.customTranscriptionModel)
        defaults.set(customAPIReasoningEffort, forKey: Key.customAPIReasoningEffort)
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

    /// The key for any provider: a custom provider's only through its bound endpoint, the others straight from the store.
    static func readKey(for provider: ProviderID, routes: RouteSettings, defaults: UserDefaults, credentials: CredentialStore) throws -> APIKey? {
        guard let address = routes.apiBaseURL(for: provider) else { return try credentials.read(provider) }
        return try read(at: address, defaults: defaults, credentials: credentials, provider: provider)
    }

    /// True when the saved key is bound to a different endpoint than `address`, so it must be saved again.
    static func needsConfirmation(at address: String, defaults: UserDefaults, provider: ProviderID = .customAPI) -> Bool {
        guard let destination = try? endpoint(address) else { return false }
        return defaults.string(forKey: preference(provider)) != destination
    }

    /// Forgets the endpoint a key was bound to (the key itself is deleted through the store).
    static func clear(defaults: UserDefaults, provider: ProviderID = .customAPI) { defaults.removeObject(forKey: preference(provider)) }
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
    let stages: [DataDisclosure]

    var usesLocalModel: Bool { transcriber == nil && refusal == nil }

    /// Reads only the disclosure of a provider; this placeholder is never sent anywhere.
    private static let placeholderKey = APIKey("disclosure-only")!

    /// `cleanupSendsText` is false when every cleanup option is off: the provider is not called
    /// then, so nothing is sent and no key is needed.
    static func make(_ settings: RouteSettings, cleanupSendsText: Bool, key: (ProviderID) -> APIKey?) -> RoutePlan {
        var stages: [DataDisclosure] = []
        var refusal: String?
        var transcriber: TranscriptionProvider?
        switch settings.transcription {
        case .local: break
        case .cloud:
            transcriber = makeTranscriptionProvider(key: key(.customTranscription), apiBaseURL: settings.customTranscriptionAPIBaseURL, model: settings.customTranscriptionModel)
            if transcriber == nil { refusal = ProviderError.invalidConfiguration(.customAPI).userMessage }
            if let disclosure = transcriber?.disclosure, !disclosure.staysOnThisMac { stages.append(disclosure) }
        case .murmurCloud:
            transcriber = key(.murmurCloud).map { MurmurCloudTranscriptionProvider(apiKey: $0) }
            if transcriber == nil { refusal = ProviderError.invalidConfiguration(.murmurCloud).userMessage }
            // Shown before a key is saved too, so Settings discloses the route while it is set up.
            stages.append(MurmurCloudTranscriptionProvider(apiKey: placeholderKey).disclosure)
        }
        var cleaner: CleanupProvider?
        if settings.cleanup != .localRules && cleanupSendsText, let provider = settings.cleanup.providerID {
            let storedKey = settings.cleanup == .appleIntelligence || settings.cleanup == .localModel ? nil : key(settings.cleanup.credentialID ?? provider)
            cleaner = makeCleanupProvider(settings.cleanup, key: storedKey, apiBaseURL: settings.customAPIBaseURL, model: settings.customAPIModel, reasoningEffort: settings.customAPIReasoningEffort)
            if cleaner == nil, refusal == nil { refusal = ProviderError.invalidConfiguration(provider).userMessage }
            let preview = cleaner ?? makeCleanupProvider(settings.cleanup, key: storedKey ?? placeholderKey,
                                                        apiBaseURL: settings.customAPIBaseURL, model: settings.customAPIModel)
            if let disclosure = preview?.disclosure, !disclosure.staysOnThisMac {
                stages.append(disclosure)
            }
        }
        return RoutePlan(transcriber: transcriber, cleaner: cleaner, refusal: refusal, stages: stages)
    }
}

enum DisclosureText {
    /// One-line description for Settings, shown before any cloud use.
    static func summary(_ plan: RoutePlan, stage: ProcessingStage) -> String? {
        plan.stages.first { $0.stage == stage }?.summary
    }
}


/// A transcriber that can tell from the recording's level that it should not be called.
protocol SilencePrecheckedTranscriber: TranscriptionProvider {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool
}

/// Whisper-style models invent "Thank you." or "Okay." for silence, so silent clips are not sent.
extension OpenAICompatibleTranscriptionProvider: SilencePrecheckedTranscriber {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool {
        AudioFormat.shouldSkipForSilence(peakLevel: peakLevel, durationSeconds: durationSeconds)
    }
}

/// Murmur Cloud transcribes with Whisper too.
extension MurmurCloudTranscriptionProvider: SilencePrecheckedTranscriber {
    func shouldSkip(peakLevel: Float, durationSeconds: Double?) -> Bool {
        AudioFormat.shouldSkipForSilence(peakLevel: peakLevel, durationSeconds: durationSeconds)
    }
}

enum CloudTranscriptionOutcome: Equatable, Sendable {
    case transcript(String)
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
    /// `.unchecked` when the provider has no pre-check or no level.
    var precheck = Precheck.unchecked
    var segments: [TranscriptSegment]? = nil
    /// Why the outcome was no speech.
    var noSpeechReason: NoSpeechReason? = nil

    /// Content-free line for the unified log: reason, clip loudness and pre-check level, segment scores. Never text.
    var noSpeechLogLine: String {
        func f(_ value: Float?) -> String { value.map { String(format: "%.4f", $0) } ?? "-" }
        let scores = (segments ?? []).compactMap(\.avgLogprob)
        let range = scores.isEmpty ? "-" : String(format: "%.2f/%.2f", scores.min()!, scores.max()!)
        return "no-speech reason=\(noSpeechReason?.rawValue ?? "unknown") clip=\(clipSeconds.map { String(format: "%.2f", $0) } ?? "-") p50=\(f(peakP50)) p90=\(f(peakP90)) "
            + "silence=\(f(silencePeak))/\(f(silencePeak == nil ? nil : AudioFormat.silencePeak)) segments=\(segments?.count ?? 0) logprob-min-max=\(range)"
    }
}

enum CloudTranscription {
    static let unreadableAudio = "The recording could not be read."
    static let unknownFailure = "Cloud transcription failed."

    /// Reads the recording off the main thread and runs `run(provider:audioData:)`.
    static func run(provider: TranscriptionProvider, audioFile: URL) async -> CloudTranscriptionOutcome {
        await runDetailed(provider: provider, audioFile: audioFile).outcome
    }

    static func run(provider: TranscriptionProvider, audioData: Data) async -> CloudTranscriptionOutcome {
        await runDetailed(provider: provider, audioData: audioData).outcome
    }

    static func runDetailed(provider: TranscriptionProvider, audioFile: URL) async -> (outcome: CloudTranscriptionOutcome, detail: CloudTranscriptionDetail) {
        guard let data = try? Data(contentsOf: audioFile) else { return (.failed(message: unreadableAudio, keepAudio: true), CloudTranscriptionDetail()) }
        return await runDetailed(provider: provider, audioData: data)
    }

    /// One provider call at most: silence pre-check, then one request, no retry, no fallback.
    /// No dictionary-derived prompt is ever attached (VOCABULARY.md).
    /// The detail (clip loudness, pre-check, segments) is for History; it never changes the outcome.
    static func runDetailed(provider: TranscriptionProvider, audioData: Data) async -> (outcome: CloudTranscriptionOutcome, detail: CloudTranscriptionDetail) {
        let prepared = CloudAudio.prepare(audioData)
        var detail = CloudTranscriptionDetail(clipSeconds: prepared.duration, silencePeak: prepared.peak)
        if let peaks = prepared.payload.levels.flatMap(OpenAITranscriptionFormat.peakPercentiles) { detail.peakP50 = peaks.p50; detail.peakP90 = peaks.p90 }
        if let checker = provider as? SilencePrecheckedTranscriber, let peak = prepared.peak {
            if checker.shouldSkip(peakLevel: peak, durationSeconds: prepared.duration) { detail.precheck = .skipped; detail.noSpeechReason = .precheck; return (.noSpeech(skipped: true), detail) }
            detail.precheck = .passed
        }
        if Task.isCancelled { return (.cancelled, detail) }
        do {
            let text: String
            if let detailed = provider as? DetailedTranscriber {
                let result = try await detailed.transcribeDetailed(prepared.payload)
                text = result.text; detail.segments = result.segments; detail.noSpeechReason = result.noSpeechReason
            } else {
                text = try await provider.transcribe(prepared.payload)
            }
            if Task.isCancelled { return (.cancelled, detail) }
            // A transcript of only punctuation (" .") is silence too: nothing is pasted.
            if !text.contains(where: { $0.isLetter || $0.isNumber }) {
                detail.noSpeechReason = detail.noSpeechReason ?? (!text.isEmpty ? .punctuationOnly : .empty)
                return (.noSpeech(skipped: false), detail)
            }
            return (.transcript(text), detail)
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
        case .noSpeech: return "No speech was detected. Check your microphone and try again."
        case .failed(let message, let keepAudio): return keepAudio ? message + " Audio is saved for retry for up to \(RetryWindow.text)." : message
        case .cancelled: return "Canceled. The recording was deleted."
        }
    }
}


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
