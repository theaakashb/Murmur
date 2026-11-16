// "Report an Issue": the content-free facts a bug report carries, their text and the prefilled GitHub URL. Foundation only.
import Foundation

/// Every field is a setting, a version or an enum name; there is nowhere to put dictated text, a key, a term or a URL.
struct Diagnostics {
    /// Where a step runs. A user-typed endpoint is `.custom`, which carries nothing to print.
    enum Route {
        case thisMac(model: String?)
        case service(name: String, model: String?)
        case custom

        var text: String {
            switch self {
            case .thisMac(let model): return "this Mac" + (model.map { " (\($0))" } ?? "")
            case .service(let name, let model): return name + (model.map { " (\($0))" } ?? "")
            case .custom: return "custom"
            }
        }
    }

    /// The newest History entry, as enum names and a total time only.
    struct LastResult {
        var insertionStatus: String
        var cleanupFailure: String?
        var cleanupPath: String?
        var totalSeconds: Double?
    }

    var appVersion: String, build: String, macOS: String, macModel: String
    var microphone: String
    var accessibilityAllowed: Bool, inputMonitoringAllowed: Bool
    var transcription: Route
    var cleanup: Route, cleanupStyle: String
    var historyOn: Bool, retention: String
    var models: [(name: String, installed: Bool)]
    /// Only read while History is on.
    var lastResult: LastResult?

    /// The editable block the report sheet shows and the issue body starts from.
    var text: String {
        func allowed(_ on: Bool) -> String { on ? "allowed" : "needed" }
        let last: String
        if !historyOn { last = "not recorded (History is off)" }
        else if let result = lastResult {
            let details = [result.cleanupFailure.map { "cleanup fallback \($0)" }, result.cleanupPath.map { "cleanup \($0)" }, result.totalSeconds.map { String(format: "%.1f s", $0) }]
            last = ([result.insertionStatus] + details.compactMap { $0 }).joined(separator: ", ")
        } else { last = "none yet" }
        return """
        What happened:


        ---
        Murmur diagnostics (no dictated text, audio, keys, dictionary, cues or app names)
        Murmur: \(appVersion) (\(build))
        macOS: \(macOS)
        Mac: \(macModel)
        Permissions: microphone \(microphone), accessibility \(allowed(accessibilityAllowed)), input monitoring \(allowed(inputMonitoringAllowed))
        Transcription: \(transcription.text)
        Cleanup: \(cleanup.text), style \(cleanupStyle)
        History: \(historyOn ? "on, keeping \(retention)" : "off (this session only)")
        Models: \(models.map { "\($0.name) \($0.installed ? "installed" : "not installed")" }.joined(separator: "; "))
        Newest dictation: \(last)
        """
    }
}

enum IssueReport {
    static let newIssueURL = "https://github.com/theaakashb/Murmur/issues/new"
    static let title = "Bug: "
    /// GitHub and mail apps document no limit; this is a conservative assumption for the encoded URL's length.
    static let maxURLLength = 4000
    static let truncationNote = "\n\n(Cut off here. Paste the rest from Copy Diagnostics.)"

    private static let unreserved = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
    private static func encode(_ text: String) -> String { text.addingPercentEncoding(withAllowedCharacters: unreserved) ?? "" }

    /// Where reports go for people without a GitHub account.
    static let email = "bugs-murmur@clouddev.cc"
    static let emailSubject = "Murmur bug report"

    /// The new-issue URL carrying `body`; a body that would exceed `maxURLLength` is cut at a character boundary and ends with `truncationNote`.
    static func url(body: String) -> URL { capped("\(newIssueURL)?title=\(encode(title))&body=", body) }

    /// A mailto link to `email` carrying `body`, capped like `url(body:)`.
    static func emailURL(body: String) -> URL { capped("mailto:\(email)?subject=\(encode(emailSubject))&body=", body) }

    private static func capped(_ prefix: String, _ body: String) -> URL {
        let room = maxURLLength - prefix.count
        let whole = encode(body)
        if whole.count <= room { return URL(string: prefix + whole)! }
        var left = room - encode(truncationNote).count
        var kept = ""
        for character in body {
            let piece = encode(String(character)).count
            guard left >= piece else { break }
            kept.append(character); left -= piece
        }
        return URL(string: prefix + encode(kept + truncationNote))!
    }
}
