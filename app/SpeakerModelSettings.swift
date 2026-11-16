import AppKit

// Speaker-model download controller for Settings, and the NSAlert helper for destructive prompts.

extension ModelFacts {
    /// The meeting speaker model; the credits come from SpeakerFacts.
    static let speaker = ModelFacts(
        displayName: SpeakerFacts.displayName,
        rowTitle: "pyannote community-1",
        repository: "FluidInference/speaker-diarization-coreml",
        directory: { $0.appendingPathComponent("Library/Caches/Dictation/module-one/speaker-diarization-df2625a") },
        confirmName: "speaker separation",
        summaryLead: "pyannote community-1",
        placement: ModelFacts.savedIn,
        licenseNotice: SpeakerFacts.credits,
        closing: "With speaker separation on, the call’s audio from each meeting is kept until separation finishes after Stop, at most \(RetryWindow.text).",
        removeTitle: "Remove speaker separation?",
        removeBody: { folder, _ in "New meetings label the call’s side as Them until you download it again. This deletes \(folder.path)." },
        completed: "Speaker separation is downloaded and verified.",
        removed: "Speaker separation was removed.")
}

/// Settings → Meetings → speaker separation: the switch and the optional pinned download.
/// It tells the recorder which verified model folder to use; nil while off, missing or unverified.
@MainActor final class SpeakerModelSettings: ObservableObject {
    @Published var enabled: Bool { didSet { defaults.set(enabled, forKey: SpeakerFacts.key); refresh() } }
    @Published var separateRoom: Bool { didSet { defaults.set(separateRoom, forKey: SpeakerFacts.roomKey); refresh() } }
    @Published var slot: ModelSlot
    var row: ModelRowPresentation { ModelRowPresentation.make(slot.downloader == nil && !fixture ? nil : slot.status) }
    var summary: String { slot.facts.summary(downloadSize: slot.downloader?.downloadSize ?? 21_599_417) }
    private let defaults: UserDefaults
    private var verified = false, verifying = false, fixture = false
    private weak var recorder: MeetingRecorder?
    private var isBusy: () -> Bool = { false }
    private var preview = false

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        enabled = defaults.bool(forKey: SpeakerFacts.key)
        separateRoom = defaults.bool(forKey: SpeakerFacts.roomKey)
        slot = ModelSlot(.speaker, pins: BundledModels.pins, home: FileManager.default.homeDirectoryForCurrentUser)
        slot.status = slot.downloader?.status() ?? .notDownloaded
    }

    func attach(_ recorder: MeetingRecorder, preview: Bool, isBusy: @escaping () -> Bool) {
        self.recorder = recorder; self.preview = preview; self.isBusy = isBusy
        refresh()
    }

    /// Checks the model folder against its pins (cached by fingerprint); wired into the recorder, which runs it off the main thread.
    var verifyModel: () -> Bool {
        let manifest = BundledModels.pins, folder = slot.downloader?.target
        return {
            guard let manifest, let folder else { return false }
            return (try? ModelVerificationCache.shared.verify(folder, manifest: manifest, model: .speaker)) != nil
        }
    }

    /// Hashes the folder once (off the main thread), then hands it to the recorder while enabled.
    func refresh() {
        guard let recorder, !preview else { return }
        recorder.speakerModel = enabled && verified ? slot.downloader?.target : nil
        recorder.separateRoomSpeakers = enabled && separateRoom
        let wanted = enabled && (row.isReady || slot.inFlight)
        if recorder.speakersWanted != wanted { recorder.speakersWanted = wanted; if !wanted { recorder.reload() } }
        guard enabled, !verified, !verifying, !slot.inFlight, row.isReady else { return }
        verifying = true
        let check = verifyModel
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let ok = check()
            DispatchQueue.main.async {
                guard let self else { return }
                self.verifying = false; self.verified = ok
                if !ok { self.slot.message = "Speaker separation failed verification. Remove it and download it again." }
                self.refresh(); recorder.reload()
            }
        }
    }

    private var flow: ModelFlow<SpeakerModelSettings> {
        var flow = ModelFlow(owner: self, slot: \.slot)
        flow.downloadGate = { [weak self] in self.flatMap { ModelGate.downloadRefusal(isBusy: $0.isBusy(), preview: $0.preview, downloading: $0.slot.inFlight) } }
        flow.removeGate = { [weak self] in
            self.flatMap { ModelGate.removeRefusal(isBusy: $0.isBusy(), preview: $0.preview, downloading: $0.slot.inFlight)
                ?? ($0.recorder?.separating.isEmpty == false ? "Speakers are being separated now. Remove the model when that finishes, or Skip it." : nil) }
        }
        flow.willDownload = { [weak self] in self?.verified = false; self?.refresh() }
        flow.pushProgress = true
        flow.didDownload = { [weak self] _ in self?.refresh() }
        flow.release = { [weak self] go in self?.verified = false; self?.refresh(); go() }
        flow.didRemove = { [weak self] in self?.refresh() }
        return flow
    }
    func download() { flow.download() }
    func cancelDownload() { slot.downloader?.cancel() }
    func remove() { flow.remove() }

    /// UI fixture only (check_ui.swift).
    func showFixture(enabled: Bool, status: ModelStatus) { fixture = true; self.enabled = enabled; slot.status = status }
}

extension NSAlert {
    /// Destructive first button, Cancel as the Return default so a stray keypress deletes nothing.
    func runDestructive() -> NSApplication.ModalResponse {
        buttons.first?.hasDestructiveAction = true
        if buttons.count > 1 { buttons[0].keyEquivalent = ""; buttons[1].keyEquivalent = "\r" }
        return runModal()
    }
}
