// Model row text and rules: every downloadable model's facts, the row's buttons and the download/remove gate. Foundation only.
import Foundation

/// One downloadable model: where it comes from, where it lives and what Settings says about it.
/// The text layouts (size, summary, confirmation, completion) are written once below; a model's own
/// wording is a field here. The speaker and local cleanup models are declared beside their features.
struct ModelFacts {
    /// The model's name where it is a setting or a step.
    let displayName: String
    /// The row's title when it differs from `displayName`.
    var rowTitle: String? = nil
    let repository: String
    var list = ModelManifest.batchList
    let directory: (_ home: URL) -> URL
    /// The license the model cards declare when the pins record none.
    var unpinnedLicense: ModelLicenseInfo? = nil
    /// The model's name in "Download …?".
    let confirmName: String
    /// Leads the summary line ahead of the size.
    var summaryLead: String? = nil
    /// Summary items between the size and where it runs.
    var summaryTail: [String] = []
    /// The sentence after the checksum sentence in the confirmation.
    let placement: (_ folder: URL) -> String
    /// Replaces the "License:" block (the speaker model's required credits).
    var licenseNotice: String? = nil
    /// A final paragraph of the confirmation.
    var closing: String? = nil
    let removeTitle: String
    let removeBody: (_ folder: URL, _ downloadSize: Int64) -> String
    let completed: String
    let removed: String
    var explanation = ""
    var unavailableMessage = ""

    static let location = "Runs on this Mac"

    var title: String { rowTitle ?? displayName }

    /// Decimal units, as Finder shows download sizes: 1,801,783,964 bytes is "1.80 GB".
    static func formatBytes(_ bytes: Int64) -> String {
        let value = Double(max(0, bytes))
        if value >= 1_000_000_000 { return String(format: "%.2f GB", value / 1_000_000_000) }
        if value >= 1_000_000 { return String(format: "%.0f MB", value / 1_000_000) }
        if value >= 1_000 { return String(format: "%.0f KB", value / 1_000) }
        return "\(Int64(value)) bytes"
    }

    /// "1.80 GB download · English · Runs on this Mac"
    func summary(downloadSize: Int64) -> String {
        ((summaryLead.map { [$0] } ?? []) + ["\(Self.formatBytes(downloadSize)) download"] + summaryTail + [Self.location]).joined(separator: " · ")
    }

    /// One line per declaration, with its link, then the note (which says when the license is unresolved).
    static func licenseText(_ license: ModelLicenseInfo) -> String {
        var lines = license.declarations.map { "\($0.name) (\($0.source)): \($0.url.absoluteString)" }
        lines.append(license.note)
        return lines.joined(separator: "\n")
    }

    /// Shown before any byte is downloaded.
    func confirmation(downloadSize: Int64, license: ModelLicenseInfo, folder: URL) -> (title: String, body: String) {
        let size = Self.formatBytes(downloadSize)
        return ("Download \(confirmName) (\(size))?",
                "Murmur downloads \(size) from Hugging Face and checks every file against its pinned checksum. \(placement(folder))\n\n\(licenseNotice ?? "License:\n\(Self.licenseText(license))")" + (closing.map { "\n\n" + $0 } ?? ""))
    }

    static func savedIn(_ folder: URL) -> String { "It is saved in \(folder.path)." }

    func completionMessage(_ result: Result<Void, ModelDownloadError>) -> String {
        switch result {
        case .success: return completed
        case .failure(let error): return Self.failureMessage(error)
        }
    }

    static func failureMessage(_ error: ModelDownloadError) -> String {
        error == .cancelled ? "The download was cancelled. Retry continues from the files already downloaded." : error.userMessage
    }

    /// Facts from the Hugging Face model APIs (checked 2026-10-03):
    /// nvidia/parakeet-unified-en-0.6b declares `license: other`, `license_name: nvidia-open-model-license`
    /// with the link below; the pinned conversion repository declares `license: cc-by-4.0`.
    static let parakeetUnpinnedLicense = ModelLicenseInfo(
        isPinned: false,
        declarations: [
            ModelLicenseDeclaration(source: "Upstream NVIDIA model card (nvidia/parakeet-unified-en-0.6b)",
                                    name: "NVIDIA Open Model License",
                                    url: URL(string: "https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/")!),
            ModelLicenseDeclaration(source: "Core ML conversion model card (FluidInference/parakeet-unified-en-0.6b-coreml)",
                                    name: "CC-BY-4.0",
                                    url: URL(string: "https://huggingface.co/FluidInference/parakeet-unified-en-0.6b-coreml")!),
        ],
        note: "The license is not recorded in Murmur's pinned model manifest. The upstream and conversion model cards declare different licenses, and Murmur has not resolved which terms apply. Review both before downloading.")

    /// The batch transcription model. The folder is the one existing installs already use, so a model placed there before is recognized.
    static let batch = ModelFacts(
        displayName: "Parakeet Unified English 0.6B",
        repository: "FluidInference/parakeet-unified-en-0.6b-coreml",
        directory: { $0.appendingPathComponent("Library/Caches/Dictation/module-one/unified-d32e972") },
        unpinnedLicense: parakeetUnpinnedLicense,
        confirmName: "Parakeet Unified English 0.6B",
        summaryTail: ["English"],
        placement: { _ in "After that, transcription runs on this Mac without the network." },
        removeTitle: "Remove the downloaded model?",
        removeBody: { _, size in "Murmur cannot dictate until you download it again (\(ModelFacts.formatBytes(size))) or choose another model folder." },
        completed: "The model is downloaded and verified. Murmur is ready to dictate.",
        removed: "The model was removed.")

    /// The CTC model that rescores on-device transcripts toward the dictionary (FluidAudio vocabulary
    /// boosting). It lives in FluidAudio's own cache folder, because FluidAudio's rescorer reads the CTC
    /// tokenizer only from there; that folder can be shared with other apps built on FluidAudio, so
    /// Murmur checks it against the pinned hashes before every use and runs without it otherwise.
    static let dictionary = ModelFacts(
        displayName: "Dictionary support for on-device transcription",
        repository: "FluidInference/parakeet-ctc-110m-coreml",
        directory: { $0.appendingPathComponent("Library/Application Support/FluidAudio/Models/parakeet-ctc-110m-coreml") },
        confirmName: "Parakeet CTC 110M",
        summaryLead: "Parakeet CTC 110M",
        placement: { "It is saved in FluidAudio's shared model folder (\($0.path)); a copy another app put there is replaced." },
        removeTitle: "Remove dictionary support?",
        removeBody: { folder, _ in "On-device transcription stops using your dictionary until you download it again. This deletes \(folder.path), which other apps built on FluidAudio may also use." },
        completed: "Dictionary support is downloaded and verified. On-device transcription now uses your dictionary.",
        removed: "Dictionary support was removed.",
        explanation: "Helps on-device transcription spell your dictionary words without touching everyday English. Your words stay on this Mac.",
        unavailableMessage: "Dictionary support could not start, so on-device transcription is running without your dictionary. Remove it and download it again.")

    /// Live words: the streaming encoder and the small models it loads with, in a folder of
    /// their own beside the batch model. Optional; dictation never needs it.
    static let liveWords = ModelFacts(
        displayName: "Live words while speaking",
        rowTitle: "Parakeet Unified streaming",
        repository: batch.repository,
        list: "live_files",
        directory: { $0.appendingPathComponent("Library/Caches/Dictation/module-one/unified-d32e972-live") },
        unpinnedLicense: parakeetUnpinnedLicense,
        confirmName: "live words",
        summaryLead: "Parakeet Unified streaming",
        placement: ModelFacts.savedIn,
        removeTitle: "Remove live words?",
        removeBody: { folder, _ in "The bubble goes back to the level meter until you download it again. This deletes \(folder.path)." },
        completed: "Live words are downloaded and verified.",
        removed: "Live words were removed.",
        explanation: "Shows your words in the bubble while you speak. Display only: the inserted text still comes from the whole recording. While you record, a second on-device model uses up to about 1 GB more memory; it quits when you stop.")
}

extension ModelManifest {
    init(contentsOf url: URL, _ model: ModelFacts) throws {
        try self.init(contentsOf: url, repository: model.repository, list: model.list, unpinnedLicense: model.unpinnedLicense)
    }
}

/// One model's download state beside its downloader (nil when the bundled pins are missing or invalid).
struct ModelSlot {
    let facts: ModelFacts
    let downloader: ModelDownloader?
    var status: ModelStatus = .notDownloaded
    var message = ""
    var inFlight = false

    init(_ facts: ModelFacts, pins: URL?, home: URL) {
        self.facts = facts
        downloader = pins.flatMap { try? ModelManifest(contentsOf: $0, facts) }.map { ModelDownloader(manifest: $0, target: facts.directory(home)) }
    }

    var row: ModelRowPresentation { ModelRowPresentation.make(downloader == nil ? nil : status) }
    var license: ModelLicenseInfo? { downloader?.license }
    var summary: String { downloader.map { facts.summary(downloadSize: $0.downloadSize) } ?? "" }
}

/// Live words: when a recording starts the helper, and the bubble's text.
enum LiveWordsFacts {
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
            return ModelRowPresentation(statusText: ModelFacts.failureMessage(error), actions: [.retry, .remove], progress: nil, isWorking: false, isReady: false)
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

extension DictationLanguages.LocalModel {
    var runnerFlag: String? {
        switch self {
        case .parakeetEnglish: return nil
        case .parakeetTDT: return "--parakeet-tdt"
        case .whisperTurbo: return "--whisper-turbo"
        }
    }

    var facts: ModelFacts {
        switch self {
        case .parakeetEnglish: return .batch
        case .parakeetTDT: return .multilingualTDT
        case .whisperTurbo: return .multilingualWhisper
        }
    }
}

extension ModelFacts {
    static let multilingualTDT = multilingual(
        "Parakeet TDT v3", repository: "FluidInference/parakeet-tdt-0.6b-v3-coreml", folder: "parakeet-tdt-v3",
        license: ModelLicenseInfo(isPinned: false, declarations: [
            .init(source: "NVIDIA upstream model card", name: "CC BY 4.0",
                  url: URL(string: "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3")!),
            .init(source: "FluidInference conversion model card", name: "CC BY 4.0 metadata, Apache 2.0 in card text",
                  url: URL(string: "https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v3-coreml/blob/7dd20fe6b1797d35f5e3307e8b1732d9a178edfe/README.md")!)
        ], note: "Review the upstream and conversion license declarations before downloading."))

    static let multilingualWhisper = multilingual(
        "Whisper large-v3-turbo", repository: "DictionLabs/whisperkit-coreml", folder: "whisper-large-v3-turbo")

    private static func multilingual(_ name: String, repository: String, folder: String, license: ModelLicenseInfo? = nil) -> ModelFacts {
        ModelFacts(displayName: name, repository: repository,
                   directory: { $0.appendingPathComponent("Library/Caches/Dictation/module-one").appendingPathComponent(folder) },
                   unpinnedLicense: license, confirmName: name,
                   placement: { _ in "After that, transcription runs on this Mac without the network." },
                   removeTitle: "Remove the downloaded model?",
                   removeBody: { _, size in "Download it again (\(formatBytes(size))) to dictate with these languages." },
                   completed: "The model is downloaded and verified.", removed: "The model was removed.")
    }
}
