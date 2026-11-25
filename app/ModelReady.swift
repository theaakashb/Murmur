import Foundation

/// The "Murmur is ready" notice after a model download: when it posts and what it says.
enum ModelReady {
    enum Kind { case speech, cleanup }

    static let title = "Murmur is ready"

    static func body(_ kind: Kind, model: String, shortcut: String) -> String {
        switch kind {
        case .speech: return "\(model) finished downloading. Hold \(shortcut) to start dictating."
        case .cleanup: return "\(model) finished downloading. Cleanup is on."
        }
    }

    /// Only for a finished download, only with permission, and not while the user is looking at a Murmur window.
    static func shouldPost(succeeded: Bool, permitted: Bool, appActive: Bool, hasKeyWindow: Bool) -> Bool {
        succeeded && permitted && !(appActive && hasKeyWindow)
    }
}
