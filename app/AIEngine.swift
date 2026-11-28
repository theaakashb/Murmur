import Foundation

// The engine behind Murmur's optional AI features that are not dictation cleanup: writing modes (and voice edit).
// No setting: the engine follows the cleanup route (Routes.swift). Custom Provider keeps its own; the local routes
// use Apple Intelligence silently, only where it is available.

enum AIEngine: String, Sendable {
    case appleIntelligence = "apple-intelligence", customProvider = "custom-provider"

    /// Why this engine cannot run now, in one line, or nil when it can.
    func unavailableReason(routes: RouteSettings) -> String? {
        switch self {
        case .appleIntelligence:
            guard #available(macOS 26, *) else { return "Apple Intelligence needs macOS 26." }
            return AppleSystemModel.status()?.userMessage
        case .customProvider:
            return makeCleanupProvider(.customAPI, key: nil, apiBaseURL: routes.customAPIBaseURL, model: routes.customAPIModel) == nil
                ? "Set up a Custom Provider in Settings → Providers." : nil
        }
    }

    /// The provider for this engine, or nil when it cannot run. Reuses the existing providers; the caller keeps the guard.
    func provider(routes: RouteSettings, key: (ProviderID) -> APIKey?) -> CleanupProvider? {
        guard unavailableReason(routes: routes) == nil else { return nil }
        switch self {
        case .appleIntelligence: if #available(macOS 26, *) { return AppleCleanupProvider() } else { return nil }
        case .customProvider: return makeCleanupProvider(.customAPI, key: key(.customCleanup), apiBaseURL: routes.customAPIBaseURL, model: routes.customAPIModel, reasoningEffort: routes.customAPIReasoningEffort)
        }
    }

    /// The engine for a cleanup route; nil when the route has none (local routes on a Mac without Apple Intelligence).
    static func derive(cleanup: CleanupChoice) -> AIEngine? {
        switch cleanup {
        case .customAPI: return .customProvider
        default: return AIEngine.appleIntelligence.unavailableReason(routes: RouteSettings()) == nil ? .appleIntelligence : nil
        }
    }
}
