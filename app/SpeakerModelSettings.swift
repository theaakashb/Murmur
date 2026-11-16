import AppKit

// Speaker-model download controller for Settings, and the NSAlert helper for destructive prompts.

/// Settings → Meetings → speaker separation (W7d): the switch and the optional pinned download.
/// It tells the recorder which verified model folder to use; nil while off, missing or unverified.
@MainActor final class SpeakerModelSettings: ObservableObject {
    @Published var enabled: Bool { didSet { defaults.set(enabled, forKey: SpeakerFacts.key); refresh() } }
    @Published var separateRoom: Bool { didSet { defaults.set(separateRoom, forKey: "meetingRoomSpeakers"); refresh() } }
    @Published private(set) var status: ModelStatus = .notDownloaded
    @Published var message = ""
    let downloader: ModelDownloader?
    var row: ModelRowPresentation { ModelRowPresentation.make(downloader == nil && !fixture ? nil : status) }
    var summary: String { Self.summary(downloadSize: downloader?.downloadSize ?? 21_599_417) }
    private let defaults: UserDefaults
    private let manifest = Bundle.main.url(forResource: "artifact-pins", withExtension: "json")
    private var inFlight = false, verified = false, verifying = false, fixture = false
    private weak var recorder: MeetingRecorder?
    private var isBusy: () -> Bool = { false }
    private var preview = false

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        enabled = defaults.bool(forKey: SpeakerFacts.key)
        separateRoom = defaults.bool(forKey: "meetingRoomSpeakers")
        if let manifest, let pins = try? ModelManifest(contentsOf: manifest, repository: SpeakerFacts.repository) {
            downloader = ModelDownloader(manifest: pins, target: SpeakerFacts.directory(home: FileManager.default.homeDirectoryForCurrentUser))
        } else { downloader = nil }
        status = downloader?.status() ?? .notDownloaded
    }

    func attach(_ recorder: MeetingRecorder, preview: Bool, isBusy: @escaping () -> Bool) {
        self.recorder = recorder; self.preview = preview; self.isBusy = isBusy
        let manifest = manifest, folder = downloader?.target
        recorder.verifySpeakerModel = {
            guard let manifest, let folder else { return false }
            return (try? ModelVerificationCache.shared.verify(folder, manifest: manifest, repository: SpeakerFacts.repository)) != nil
        }
        refresh()
    }

    /// Hashes the folder once (off the main thread), then hands it to the recorder while enabled.
    func refresh() {
        guard let recorder, !preview else { return }
        recorder.speakerModel = enabled && verified ? downloader?.target : nil
        recorder.separateRoomSpeakers = enabled && separateRoom
        let wanted = enabled && (row.isReady || inFlight)
        if recorder.speakersWanted != wanted { recorder.speakersWanted = wanted; if !wanted { recorder.reload() } }
        guard enabled, !verified, !verifying, !inFlight, row.isReady else { return }
        verifying = true
        let check = recorder.verifySpeakerModel
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let ok = check()
            DispatchQueue.main.async {
                guard let self else { return }
                self.verifying = false; self.verified = ok
                if !ok { self.message = "Speaker separation failed verification. Remove it and download it again." }
                self.refresh(); recorder.reload()
            }
        }
    }

    func download() {
        guard let downloader else { message = ModelDownloadError.invalidManifest.userMessage; return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy(), preview: preview, downloading: inFlight) { message = refusal; return }
        let text = Self.confirmation(downloadSize: downloader.downloadSize, folder: downloader.target)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        inFlight = true; message = ""; verified = false; refresh()
        status = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: downloader.downloadSize))
        downloader.start(progress: { [weak self] progress in
            DispatchQueue.main.async { if self?.inFlight == true { self?.status = .downloading(progress) } }
        }) { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.inFlight = false
                self.status = downloader.status()
                self.message = Self.completionMessage(result)
                self.refresh()
            }
        }
    }

    func cancelDownload() { downloader?.cancel() }

    func remove() {
        guard let downloader else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy(), preview: preview, downloading: inFlight) { message = refusal; return }
        if recorder?.separating.isEmpty == false { message = "Speakers are being separated now. Remove the model when that finishes, or Skip it."; return }
        let alert = NSAlert(); alert.messageText = Self.removeTitle
        alert.informativeText = Self.removeBody(folder: downloader.target)
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        verified = false; refresh()
        do { try downloader.remove(); message = "Speaker separation was removed." }
        catch { message = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage }
        status = downloader.status(); refresh()
    }

    // Download text; the credits come from SpeakerFacts.
    nonisolated static func summary(downloadSize: Int64) -> String { "pyannote community-1 · \(ModelFacts.formatBytes(downloadSize)) download · \(ModelFacts.location)" }
    nonisolated static func confirmation(downloadSize: Int64, folder: URL) -> (title: String, body: String) {
        ("Download speaker separation (\(ModelFacts.formatBytes(downloadSize)))?",
         "Murmur downloads \(ModelFacts.formatBytes(downloadSize)) from Hugging Face and checks every file against its pinned checksum. It is saved in \(folder.path).\n\n\(SpeakerFacts.credits)\n\nWith speaker separation on, the call’s audio from each meeting is kept until separation finishes after Stop, at most 24 hours.")
    }
    nonisolated static let removeTitle = "Remove speaker separation?"
    nonisolated static func removeBody(folder: URL) -> String { "New meetings label the call’s side as Them until you download it again. This deletes \(folder.path)." }
    nonisolated static func completionMessage(_ result: Result<Void, ModelDownloadError>) -> String {
        switch result {
        case .success: return "Speaker separation is downloaded and verified."
        case .failure(.cancelled): return "The download was cancelled. Retry continues from the files already downloaded."
        case .failure(let error): return error.userMessage
        }
    }
    /// UI fixture only (check_ui.swift).
    func showFixture(enabled: Bool, status: ModelStatus) { fixture = true; self.enabled = enabled; self.status = status }
}

extension NSAlert {
    /// Destructive first button, Cancel as the Return default so a stray keypress deletes nothing.
    func runDestructive() -> NSApplication.ModalResponse {
        buttons.first?.hasDestructiveAction = true
        if buttons.count > 1 { buttons[0].keyEquivalent = ""; buttons[1].keyEquivalent = "\r" }
        return runModal()
    }
}
