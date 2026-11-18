import Foundation

// The engine behind Murmur's optional AI features that are not dictation cleanup: writing modes (and voice edit).
// No setting: the engine follows the cleanup route (Routes.swift). Murmur Cloud and Custom Provider keep their own; the local routes
// use Apple Intelligence silently, only where it is available.

enum AIEngine: String, Sendable {
    case appleIntelligence = "apple-intelligence", customProvider = "custom-provider", murmurCloud = "murmur-cloud"

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

    /// The engine for a cleanup route; nil when the route has none (local routes on a Mac without Apple Intelligence).
    static func derive(cleanup: CleanupChoice) -> AIEngine? {
        switch cleanup {
        case .murmurCloud: return .murmurCloud
        case .customAPI: return .customProvider
        default: return AIEngine.appleIntelligence.unavailableReason(routes: RouteSettings(), hasKey: { _ in false }) == nil ? .appleIntelligence : nil
        }
    }
}
