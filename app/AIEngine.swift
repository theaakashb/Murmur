import Foundation

// The engine behind Murmur's optional AI features that are not dictation cleanup: writing modes (and voice edit).
// One setting, three engines. Dictation cleanup keeps its own route (Routes.swift).

enum AIEngine: String, CaseIterable, Identifiable, Sendable {
    case appleIntelligence = "apple-intelligence", customProvider = "custom-provider", murmurCloud = "murmur-cloud"

    static let defaultsKey = "aiEngine"
    var id: String { rawValue }

    var label: String {
        switch self {
        case .appleIntelligence: return "Apple Intelligence (this Mac, free)"
        case .customProvider: return "Custom Provider"
        case .murmurCloud: return "Murmur Cloud"
        }
    }

    /// Why this engine cannot run now, in one line, or nil when it can. `hasKey` only asks whether a key is stored.
    func unavailableReason(routes: RouteSettings, hasKey: (ProviderID) -> Bool) -> String? {
        switch self {
        case .appleIntelligence:
            guard #available(macOS 26, *) else { return "Apple Intelligence needs macOS 26." }
            return AppleSystemModel.status()?.userMessage
        case .customProvider:
            return makeCleanupProvider(.customAPI, key: nil, apiBaseURL: routes.customAPIBaseURL, model: routes.customAPIModel) == nil
                ? "Set up a Custom Provider in Settings → Providers." : nil
        case .murmurCloud:
            return hasKey(.murmurCloud) ? nil : "Add your Murmur Cloud key in Settings → Providers."
        }
    }

    /// The provider for this engine, or nil when it cannot run. Reuses the existing providers; the caller keeps the guard.
    func provider(routes: RouteSettings, key: (ProviderID) -> APIKey?) -> CleanupProvider? {
        guard unavailableReason(routes: routes, hasKey: { key($0) != nil }) == nil else { return nil }
        switch self {
        case .appleIntelligence: if #available(macOS 26, *) { return AppleCleanupProvider() } else { return nil }
        case .customProvider: return makeCleanupProvider(.customAPI, key: key(.customCleanup), apiBaseURL: routes.customAPIBaseURL, model: routes.customAPIModel, reasoningEffort: routes.customAPIReasoningEffort)
        case .murmurCloud: return key(.murmurCloud).map { MurmurCloudCleanupProvider(apiKey: $0) }
        }
    }

    /// The stored choice; before one is made, the engine matching the cleanup route (Murmur Cloud, Custom Provider), else Apple Intelligence.
    static func resolve(from defaults: UserDefaults, cleanup: CleanupChoice) -> AIEngine {
        if let stored = defaults.string(forKey: defaultsKey).flatMap(AIEngine.init(rawValue:)) { return stored }
        switch cleanup { case .murmurCloud: return .murmurCloud; case .customAPI: return .customProvider; default: return .appleIntelligence }
    }
    func save(to defaults: UserDefaults) { defaults.set(rawValue, forKey: Self.defaultsKey) }
}
