// Model row text and rules: the three models' facts, the row's buttons and the download/remove gate. Foundation only.
import Foundation

enum ModelFacts {
    static let displayName = "Parakeet Unified English 0.6B"
    static let language = "English"
    static let location = "Runs on this Mac"

    /// The folder existing installs already use, so a model placed there before is recognized.
    static func defaultDirectory(home: URL) -> URL {
        home.appendingPathComponent("Library/Caches/Dictation/module-one/unified-d32e972")
    }

    /// Decimal units, as Finder shows download sizes: 1,801,783,964 bytes is "1.80 GB".
    static func formatBytes(_ bytes: Int64) -> String {
        let value = Double(max(0, bytes))
        if value >= 1_000_000_000 { return String(format: "%.2f GB", value / 1_000_000_000) }
        if value >= 1_000_000 { return String(format: "%.0f MB", value / 1_000_000) }
        if value >= 1_000 { return String(format: "%.0f KB", value / 1_000) }
        return "\(Int64(value)) bytes"
    }

    /// "1.80 GB download · English · Runs on this Mac"
    static func summary(downloadSize: Int64) -> String {
        "\(formatBytes(downloadSize)) download · \(language) · \(location)"
    }

    /// One line per declaration, with its link, then the note (which says when the license is unresolved).
    static func licenseText(_ license: ModelLicenseInfo) -> String {
        var lines = license.declarations.map { "\($0.name) (\($0.source)): \($0.url.absoluteString)" }
        lines.append(license.note)
        return lines.joined(separator: "\n")
    }

    /// Shown before any byte is downloaded.
    static func confirmation(downloadSize: Int64, license: ModelLicenseInfo) -> (title: String, body: String) {
        ("Download \(displayName) (\(formatBytes(downloadSize)))?",
         "Murmur downloads \(formatBytes(downloadSize)) from Hugging Face and checks every file against its pinned checksum. After that, transcription runs on this Mac without the network.\n\nLicense:\n\(licenseText(license))")
    }

    static func completionMessage(_ result: Result<Void, ModelDownloadError>) -> String {
        switch result {
        case .success: return "The model is downloaded and verified. Murmur is ready to dictate."
        case .failure(.cancelled): return "The download was cancelled. Retry continues from the files already downloaded."
        case .failure(let error): return error.userMessage
        }
    }
}

/// The CTC model that rescores on-device transcripts toward the dictionary (FluidAudio vocabulary
/// boosting). It lives in FluidAudio's own cache folder, because FluidAudio's rescorer reads the CTC
/// tokenizer only from there; that folder can be shared with other apps built on FluidAudio, so
/// Murmur checks it against the pinned hashes before every use and runs without it otherwise.
enum DictionaryModelFacts {
    static let displayName = "Dictionary support for on-device transcription"
    static let modelName = "Parakeet CTC 110M"

    static func directory(home: URL) -> URL {
        home.appendingPathComponent("Library/Application Support/FluidAudio/Models/parakeet-ctc-110m-coreml")
    }

    /// "Parakeet CTC 110M · 103 MB download · Runs on this Mac"
    static func summary(downloadSize: Int64) -> String {
        "\(modelName) · \(ModelFacts.formatBytes(downloadSize)) download · \(ModelFacts.location)"
    }

    static let explanation = "Helps on-device transcription spell your dictionary words without touching everyday English. Your words stay on this Mac."

    static func confirmation(downloadSize: Int64, license: ModelLicenseInfo, folder: URL) -> (title: String, body: String) {
        ("Download \(modelName) (\(ModelFacts.formatBytes(downloadSize)))?",
         "Murmur downloads \(ModelFacts.formatBytes(downloadSize)) from Hugging Face and checks every file against its pinned checksum. It is saved in FluidAudio's shared model folder (\(folder.path)); a copy another app put there is replaced.\n\nLicense:\n\(ModelFacts.licenseText(license))")
    }

    static let removeTitle = "Remove dictionary support?"
    static func removeBody(folder: URL) -> String {
        "On-device transcription stops using your dictionary until you download it again. This deletes \(folder.path), which other apps built on FluidAudio may also use."
    }

    static func completionMessage(_ result: Result<Void, ModelDownloadError>) -> String {
        switch result {
        case .success: return "Dictionary support is downloaded and verified. On-device transcription now uses your dictionary."
        case .failure(.cancelled): return "The download was cancelled. Retry continues from the files already downloaded."
        case .failure(let error): return error.userMessage
        }
    }

    static let unavailableMessage = "Dictionary support could not start, so on-device transcription is running without your dictionary. Remove it and download it again."
}

/// Live words (gap #13): the streaming encoder and the small models it loads with, in a folder of
/// their own beside the batch model. Optional; dictation never needs it.
enum LiveWordsFacts {
    static let displayName = "Live words while speaking"
    static func directory(home: URL) -> URL {
        home.appendingPathComponent("Library/Caches/Dictation/module-one/unified-d32e972-live")
    }
    static func summary(downloadSize: Int64) -> String {
        "Parakeet Unified streaming · \(ModelFacts.formatBytes(downloadSize)) download · \(ModelFacts.location)"
    }
    static let explanation = "Shows your words in the bubble while you speak. Display only: the inserted text still comes from the whole recording. While you record, a second on-device model uses up to about 1 GB more memory; it quits when you stop."
    static func confirmation(downloadSize: Int64, license: ModelLicenseInfo, folder: URL) -> (title: String, body: String) {
        ("Download live words (\(ModelFacts.formatBytes(downloadSize)))?",
         "Murmur downloads \(ModelFacts.formatBytes(downloadSize)) from Hugging Face and checks every file against its pinned checksum. It is saved in \(folder.path).\n\nLicense:\n\(ModelFacts.licenseText(license))")
    }
    static let removeTitle = "Remove live words?"
    static func removeBody(folder: URL) -> String { "The bubble goes back to the level meter until you download it again. This deletes \(folder.path)." }
    static func completionMessage(_ result: Result<Void, ModelDownloadError>) -> String {
        switch result {
        case .success: return "Live words are downloaded and verified."
        case .failure(.cancelled): return "The download was cancelled. Retry continues from the files already downloaded."
        case .failure(let error): return error.userMessage
        }
    }
    /// Whether a recording starts the live-words helper. On a cloud transcription route it starts only
    /// with "Show live words on cloud routes" on (off by default); the preview itself is always on this Mac.
    static func starts(enabled: Bool, ready: Bool, cloudTranscription: Bool, onCloud: Bool) -> Bool {
        enabled && ready && (!cloudTranscription || onCloud)
    }
    /// The bubble's text: everything but the last word is settled; the last word may still grow
    /// ("onboard" → "onboarding"), so it is shown dimmed. Only the tail that fits two lines is kept.
    static func split(_ text: String, limit: Int = 140) -> (settled: String, guess: String) {
        var tail = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if tail.count > limit, let space = tail.dropFirst(tail.count - limit).firstIndex(of: " ") { tail = "…" + tail[space...] }
        guard let space = tail.lastIndex(of: " ") else { return ("", tail) }
        return (String(tail[...space]), String(tail[tail.index(after: space)...]))
    }
}

enum ModelRowAction: Equatable { case download, cancel, retry, remove }

struct ModelRowPresentation: Equatable {
    let statusText: String
    let actions: [ModelRowAction]
    /// 0...1 while bytes are arriving; nil otherwise.
    let progress: Double?
    let isWorking: Bool
    let isReady: Bool

    /// `status` is nil when the bundled manifest is missing or invalid.
    static func make(_ status: ModelStatus?) -> ModelRowPresentation {
        guard let status else {
            return ModelRowPresentation(statusText: ModelDownloadError.invalidManifest.userMessage, actions: [], progress: nil, isWorking: false, isReady: false)
        }
        switch status {
        case .notDownloaded:
            return ModelRowPresentation(statusText: "Not downloaded", actions: [.download], progress: nil, isWorking: false, isReady: false)
        case .downloading(let progress):
            let text = "Downloading… \(Int((progress.fraction * 100).rounded(.down)))% (\(ModelFacts.formatBytes(progress.bytesDone)) of \(ModelFacts.formatBytes(progress.totalBytes)))"
            return ModelRowPresentation(statusText: text, actions: [.cancel], progress: progress.fraction, isWorking: true, isReady: false)
        case .verifying:
            return ModelRowPresentation(statusText: "Verifying downloaded files…", actions: [.cancel], progress: nil, isWorking: true, isReady: false)
        case .ready:
            return ModelRowPresentation(statusText: "Downloaded", actions: [.remove], progress: nil, isWorking: false, isReady: true)
        case .corrupt(let reason):
            return ModelRowPresentation(statusText: reason, actions: [.retry, .remove], progress: nil, isWorking: false, isReady: false)
        case .failed(let error):
            return ModelRowPresentation(statusText: ModelFacts.completionMessage(.failure(error)), actions: [.retry, .remove], progress: nil, isWorking: false, isReady: false)
        }
    }
}

/// Content-free refusals. Each returns nil when the action may proceed.
enum ModelGate {
    static let downloadingMessage = "The model is still downloading. Wait for it to finish, or cancel it in Settings."
    static let missingRuntimeMessage = "The local model runtime is missing from this build. Reinstall Murmur."
    static let missingModelMessage = "The transcription model is not available. Download the model in Settings, or choose a downloaded model folder."

    /// `downloadingActiveModel`: a download is replacing the folder this dictation would read.
    static func startRefusal(modelAvailable: Bool, runtimeAvailable: Bool, downloadingActiveModel: Bool) -> String? {
        if downloadingActiveModel { return downloadingMessage }
        if !runtimeAvailable { return missingRuntimeMessage }
        if !modelAvailable { return missingModelMessage }
        return nil
    }

    static func downloadRefusal(isBusy: Bool, preview: Bool, downloading: Bool) -> String? {
        if preview { return "Model download is disabled in preview." }
        if downloading { return ModelDownloadError.busy.userMessage }
        if isBusy { return "Finish the current dictation before downloading the model." }
        return nil
    }

    static func removeRefusal(isBusy: Bool, preview: Bool, downloading: Bool) -> String? {
        if preview { return "Removing the model is disabled in preview." }
        if downloading { return "Cancel the download before removing the model." }
        if isBusy { return "Finish the current dictation before removing the model." }
        return nil
    }
}

extension ModelLicenseDeclaration: Identifiable {
    var id: String { name + "|" + url.absoluteString }
}
