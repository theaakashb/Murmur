import Foundation

@main struct ModelReadyChecks {
    static func main() {
        precondition(ModelReady.title == "Murmur is ready")
        precondition(ModelReady.body(.speech, model: "Parakeet", shortcut: "Right ⌥") == "Parakeet finished downloading. Hold Right ⌥ to start dictating.")
        precondition(ModelReady.body(.cleanup, model: "Qwen", shortcut: "Right ⌥") == "Qwen finished downloading. Cleanup is on.")
        // (succeeded, permitted, appActive, hasKeyWindow) -> posts
        let cases: [(Bool, Bool, Bool, Bool, Bool)] = [
            (true, true, false, false, true), (true, true, false, true, true), (true, true, true, false, true),
            (true, true, true, true, false),   // the user is looking at Murmur
            (false, true, false, false, false), // failed or cancelled
            (true, false, false, false, false)  // permission denied or not granted
        ]
        for c in cases { precondition(ModelReady.shouldPost(succeeded: c.0, permitted: c.1, appActive: c.2, hasKeyWindow: c.3) == c.4, "\(c)") }
        print("Model ready checks passed: 2 texts and \(cases.count) posting decisions.")
    }
}
