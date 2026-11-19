// deps: Digests ModelManifest ModelDownload ModelText
import Foundation

// Golden outputs of the pure parts of ModelText.swift: byte formatting, the model row's presentation for every status, the download/remove/start gates,
// live words' bubble split and start rule, license text, and the summary and confirmation layouts for a synthetic model. One case per line.
// The summary and confirmation carry "Runs on this Mac"/"this Mac" wording; the C# port says "this PC" and its test replaces the words. Run from the repository root.

let encoder: JSONEncoder = { let e = JSONEncoder(); e.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]; return e }()
func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

struct BytesCase: Encodable { var bytes: Int64, text: String }
struct StatusCase: Encodable {
    var kind: String, done: Int64?, total: Int64?, reason: String?, error: String?, errorStatus: Int?
    var statusText: String, actions: [String], progress: Double?, isWorking: Bool, isReady: Bool
}
struct GateCase: Encodable { var gate: String, a: Bool, b: Bool, c: Bool, refusal: String? }
struct SplitCase: Encodable { var text: String, limit: Int, settled: String, guess: String }
struct StartCase: Encodable { var enabled: Bool, ready: Bool, cloudTranscription: Bool, onCloud: Bool, starts: Bool }
struct FailureCase: Encodable { var error: String, message: String }
struct LayoutCase: Encodable { var label: String, downloadSize: Int64, folder: String, summary: String, title: String, body: String, completed: String, failed: String, removeBody: String }

func action(_ a: ModelRowAction) -> String {
    switch a { case .download: return "download"; case .cancel: return "cancel"; case .retry: return "retry"; case .remove: return "remove" }
}

func statusCase(_ kind: String, _ status: ModelStatus?, done: Int64? = nil, total: Int64? = nil, reason: String? = nil, error: String? = nil, errorStatus: Int? = nil) -> StatusCase {
    let row = ModelRowPresentation.make(status)
    return StatusCase(kind: kind, done: done, total: total, reason: reason, error: error, errorStatus: errorStatus, statusText: row.statusText, actions: row.actions.map(action),
                      progress: row.progress, isWorking: row.isWorking, isReady: row.isReady)
}

@main struct Gen {
    static func main() throws {
        let sizes: [Int64] = [-5, 0, 1, 999, 1_000, 1_499, 1_500, 2_500, 999_499, 999_500, 1_000_000, 2_500_000, 12_345_678, 999_999_999, 1_000_000_000, 1_801_783_964, 1_005_000_000, 1_015_000_000, 1_516_744_416]
        let bytes = sizes.map { BytesCase(bytes: $0, text: ModelFacts.formatBytes($0)) }

        var statuses: [StatusCase] = [statusCase("missing", nil), statusCase("notDownloaded", .notDownloaded), statusCase("verifying", .verifying), statusCase("ready", .ready)]
        for (done, total) in [(0, 0), (0, 100), (1, 3), (50, 100), (99, 100), (100, 100), (150, 100), (1_000_000, 1_801_783_964), (1_801_783_964, 1_801_783_964)] as [(Int64, Int64)] {
            statuses.append(statusCase("downloading", .downloading(ModelDownloadProgress(bytesDone: done, totalBytes: total)), done: done, total: total))
        }
        statuses.append(statusCase("corrupt", .corrupt(ModelDownloader.corruptReason), reason: ModelDownloader.corruptReason))
        statuses.append(statusCase("corrupt", .corrupt(ModelDownloader.notDirectoryReason), reason: ModelDownloader.notDirectoryReason))
        let errors: [(String, ModelDownloadError, Int?)] = [("network", .network, nil), ("diskFull", .diskFull, nil), ("checksumMismatch", .checksumMismatch, nil), ("httpStatus", .httpStatus(404), 404),
                                                            ("cancelled", .cancelled, nil), ("invalidManifest", .invalidManifest, nil), ("unsafePath", .unsafePath, nil), ("fileSystem", .fileSystem, nil), ("busy", .busy, nil)]
        for (name, error, code) in errors { statuses.append(statusCase("failed", .failed(error), error: name, errorStatus: code)) }
        let failures = errors.map { FailureCase(error: $0.0, message: ModelFacts.failureMessage($0.1)) }

        var gates: [GateCase] = []
        for bits in 0..<8 {
            let a = bits & 1 != 0, b = bits & 2 != 0, c = bits & 4 != 0
            gates.append(GateCase(gate: "start", a: a, b: b, c: c, refusal: ModelGate.startRefusal(modelAvailable: a, runtimeAvailable: b, downloadingActiveModel: c)))
            gates.append(GateCase(gate: "download", a: a, b: b, c: c, refusal: ModelGate.downloadRefusal(isBusy: a, preview: b, downloading: c)))
            gates.append(GateCase(gate: "remove", a: a, b: b, c: c, refusal: ModelGate.removeRefusal(isBusy: a, preview: b, downloading: c)))
        }

        let long = String(repeating: "alpha beta gamma ", count: 20)
        let splits: [(String, Int)] = [("", 140), ("hello", 140), ("hello world", 140), ("  hello world  ", 140), ("hello world ", 140), ("one two three four five", 10), ("one two three four five", 12),
                                       ("abcdefghijklmnopqrstuvwxyz", 10), (long, 140), (long, 30), ("naïve café résumé today", 12), ("👩‍👩‍👧 family of emoji here now", 15), ("line one\nline two", 140), ("a b", 1), ("onboard", 140)]
        let splitCases = splits.map { text, limit -> SplitCase in let r = LiveWordsFacts.split(text, limit: limit); return SplitCase(text: text, limit: limit, settled: r.settled, guess: r.guess) }
        var starts: [StartCase] = []
        for bits in 0..<16 {
            let e = bits & 1 != 0, r = bits & 2 != 0, c = bits & 4 != 0, o = bits & 8 != 0
            starts.append(StartCase(enabled: e, ready: r, cloudTranscription: c, onCloud: o, starts: LiveWordsFacts.starts(enabled: e, ready: r, cloudTranscription: c, onCloud: o)))
        }

        // Layouts for synthetic models (the Mac's own models are in the C# tests, where the wording is the Windows wording).
        let pinned = ModelLicenseInfo(isPinned: true, declarations: [ModelLicenseDeclaration(source: "Pinned model manifest", name: "Example License", url: URL(string: "https://example.com/license")!)], note: "This license is recorded in Murmur's pinned model manifest.")
        let unpinned = ModelLicenseInfo(isPinned: false, declarations: [ModelLicenseDeclaration(source: "Card A", name: "License A", url: URL(string: "https://a.example/l")!), ModelLicenseDeclaration(source: "Card B", name: "License B", url: URL(string: "https://b.example/l")!)], note: "Not recorded.")
        let none = ModelLicenseInfo(isPinned: false, declarations: [], note: "The license for this model is not recorded in Murmur's pinned model manifest.")
        func facts(lead: String?, tail: [String], notice: String?, closing: String?) -> ModelFacts {
            ModelFacts(displayName: "Example", repository: "Example/model", directory: { $0.appendingPathComponent("m") }, confirmName: "the example", summaryLead: lead, summaryTail: tail,
                       placement: { "Saved at \($0.path)." }, licenseNotice: notice, closing: closing, removeTitle: "Remove?", removeBody: { folder, size in "Deletes \(folder.path) (\(ModelFacts.formatBytes(size)))." },
                       completed: "Done.", removed: "Gone.")
        }
        var layouts: [LayoutCase] = []
        for (label, f, license, size) in [("lead and tail, pinned", facts(lead: "Example", tail: ["English"], notice: nil, closing: nil), pinned, Int64(1_801_783_964)),
                                          ("no lead, unpinned", facts(lead: nil, tail: [], notice: nil, closing: nil), unpinned, Int64(560_833_591)),
                                          ("notice and closing, none", facts(lead: "X", tail: ["a", "b"], notice: "Credits: Example.", closing: "Closing paragraph."), none, Int64(4_321)),
                                          ("small", facts(lead: nil, tail: [], notice: nil, closing: nil), pinned, Int64(0))] {
            let text = f.confirmation(downloadSize: size, license: license, folder: URL(fileURLWithPath: "/models/example"))
            layouts.append(LayoutCase(label: label, downloadSize: size, folder: "/models/example", summary: f.summary(downloadSize: size), title: text.title, body: text.body,
                                      completed: f.completionMessage(.success(())), failed: f.completionMessage(.failure(.cancelled)), removeBody: f.removeBody(URL(fileURLWithPath: "/models/example"), size)))
        }
        let licenseText = [ModelFacts.licenseText(pinned), ModelFacts.licenseText(unpinned), ModelFacts.licenseText(none)]
        let licenseLine = String(decoding: try encoder.encode(licenseText), as: UTF8.self)

        print("{\n" + [lines("bytes", bytes), lines("statuses", statuses), lines("failures", failures), lines("gates", gates), lines("splits", splitCases), lines("starts", starts),
                       lines("layouts", layouts), "\"licenseText\": \(licenseLine)"].joined(separator: ",\n") + "\n}")
    }
}
