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

    static let updateTitle = "Model update available"

    /// After an app update that pins new models the user had installed: they must download them again.
    static func updateBody(models: [String]) -> String {
        let names = models.count < 3 ? models.joined(separator: " and ") : models.dropLast().joined(separator: ", ") + " and " + models.last!
        return "This version of Murmur uses a new \(names). Click to download it in Settings."
    }

    /// Only for a finished download, only with permission, and not while the user is looking at a Murmur window.
    static func shouldPost(succeeded: Bool, permitted: Bool, appActive: Bool, hasKeyWindow: Bool) -> Bool {
        succeeded && permitted && !(appActive && hasKeyWindow)
    }
}
