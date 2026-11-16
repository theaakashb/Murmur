import Foundation

@main struct DiagnosticsChecks {
    static func sample(historyOn: Bool = true, cleanup: Diagnostics.Route = .service(name: "Local rules (this Mac)", model: nil)) -> Diagnostics {
        Diagnostics(appVersion: "0.3.0", build: "12", macOS: "Version 26.1 (Build 25B1)", macModel: "Mac15,6", microphone: "authorized",
                    accessibilityAllowed: true, inputMonitoringAllowed: false, transcription: .thisMac(model: "FluidInference/parakeet"),
                    cleanup: cleanup, cleanupStyle: "Strict", historyOn: historyOn, retention: "30 days",
                    models: [("Speech model", true), ("Local cleanup", false)],
                    lastResult: .init(insertionStatus: "verified", cleanupFailure: "lengthRatio", cleanupPath: "modelAccepted", totalSeconds: 2.34))
    }

    /// The URL's decoded body.
    static func body(_ url: URL) -> String { URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.first { $0.name == "body" }!.value! }

    static func main() {
        // What a user types or says has nowhere to go: a custom endpoint becomes "custom", and no field takes text.
        let sentinels = ["SENTINEL-TRANSCRIPT", "SENTINEL-KEY", "SENTINEL-TERM", "https://SENTINEL-ENDPOINT.example/v1", "SENTINEL-CUSTOM-MODEL", "SENTINEL-BUNDLE"]
        var custom = sample(cleanup: .custom); custom.transcription = .custom
        let text = custom.text, url = IssueReport.url(body: text).absoluteString.removingPercentEncoding!
        for marker in sentinels { assert(!text.contains(marker) && !url.contains(marker), "leaked \(marker)") }
        assert(text.contains("Transcription: custom") && text.contains("Cleanup: custom, style Strict"))

        let normal = sample().text
        for line in ["Murmur: 0.3.0 (12)", "Mac: Mac15,6", "Permissions: microphone authorized, accessibility allowed, input monitoring needed",
                     "Transcription: this Mac (FluidInference/parakeet)", "History: on, keeping 30 days", "Models: Speech model installed; Local cleanup not installed",
                     "Newest dictation: verified, cleanup fallback lengthRatio, cleanup modelAccepted, 2.3 s"] { assert(normal.contains(line), line) }

        let off = sample(historyOn: false).text
        assert(off.contains("History: off (this session only)") && off.contains("Newest dictation: not recorded (History is off)") && !off.contains("verified"))
        var empty = sample(); empty.lastResult = nil
        assert(empty.text.contains("Newest dictation: none yet"))

        // Round trip through the query, with characters that need escaping.
        let awkward = "a & b = c # d + e % f ? g\nline two é 🙂 日本語"
        let url1 = IssueReport.url(body: awkward)
        let parts = URLComponents(url: url1, resolvingAgainstBaseURL: false)!
        assert(parts.scheme == "https" && parts.host == "github.com" && parts.path == "/theaakashb/Murmur/issues/new")
        assert(parts.queryItems!.first { $0.name == "title" }!.value == IssueReport.title && body(url1) == awkward)

        // The cap: ASCII, and multi-byte characters landing on the boundary at every offset.
        for filler in ["x", "é", "日", "🙂", "👨‍👩‍👧", "%"] {
            for offset in 0..<12 {
                let long = String(repeating: "a", count: offset) + String(repeating: filler, count: 5000)
                let capped = IssueReport.url(body: long)
                let decoded = body(capped)
                assert(capped.absoluteString.count <= IssueReport.maxURLLength && decoded.hasSuffix(IssueReport.truncationNote), "cap \(filler) \(offset)")
                let kept = String(decoded.dropLast(IssueReport.truncationNote.count))
                assert(long.hasPrefix(kept) && kept.count < long.count && kept.count > 30, "boundary \(filler) \(offset)")
            }
        }
        let fits = String(repeating: "x", count: 500)
        assert(body(IssueReport.url(body: fits)) == fits)
        assert(IssueReport.url(body: "").absoluteString.count <= IssueReport.maxURLLength)
        print("Diagnostics checks passed")
    }
}
