// AppController: downloading and removing the transcription, dictionary, live-words and local cleanup models.
import AppKit

extension AppController {
    // MARK: Model download

    /// The shared flow for one model; the transcription, dictionary and live-words models refuse while dictating.
    private func flow(_ slot: ReferenceWritableKeyPath<AppController, ModelSlot>, gated: Bool = true) -> ModelFlow<AppController> {
        var flow = ModelFlow(owner: self, slot: slot)
        if gated {
            flow.downloadGate = { [weak self] in self.flatMap { ModelGate.downloadRefusal(isBusy: $0.isBusy, preview: $0.preview, downloading: $0[keyPath: slot].inFlight) } }
            flow.removeGate = { [weak self] in self.flatMap { ModelGate.removeRefusal(isBusy: $0.isBusy, preview: $0.preview, downloading: $0[keyPath: slot].inFlight) } }
        }
        return flow
    }

    func downloadModel() {
        var flow = flow(\.modelSlot)
        flow.didDownload = { [weak self] result in
            if case .success = result, let self, let target = self.modelSlot.downloader?.target {
                self.modelDirectory = target.path
                UserDefaults.standard[Prefs.modelDirectory] = target.path
            }
        }
        flow.download()
    }
    func cancelModelDownload() { modelSlot.downloader?.cancel() }
    func removeModel() {
        var flow = flow(\.modelSlot)
        flow.release = { [weak self] go in self?.warm.stopNow(); go() } // Release the helper's open model files before deleting them.
        flow.remove()
    }
    func downloadDictionaryModel() {
        var flow = flow(\.dictionarySlot)
        flow.willDownload = { [weak self] in self?.dictionarySupportFailed = false }
        flow.didDownload = { [weak self] _ in self?.prewarm() } // Compiles the new model now, not on the next dictation.
        flow.download()
    }
    func cancelDictionaryModelDownload() { dictionarySlot.downloader?.cancel() }
    func removeDictionaryModel() {
        var flow = flow(\.dictionarySlot)
        flow.release = { [weak self] go in self?.warm.stopNow(); go() }
        flow.didRemove = { [weak self] in self?.dictionarySupportFailed = false; self?.prewarm() }
        flow.remove()
    }
    func downloadLocalModel() {
        var flow = flow(\.localSlot, gated: false)
        flow.didDownload = { _ in Task { await LocalCleanupRunner.shared.shutdown() } }
        flow.download()
    }
    func cancelLocalModelDownload() { localSlot.downloader?.cancel() }
    func removeLocalModel() {
        var flow = flow(\.localSlot, gated: false)
        flow.release = { go in
            Task {
                await LocalCleanupRunner.shared.shutdown() // Release the model files before deleting them.
                await MainActor.run { go() }
            }
        }
        flow.remove()
    }
    // MARK: Live words

    /// Checks the live folder's hashes once per launch, then loads the model once with no audio, so
    /// its first compile (about 10 s) happens now rather than in a recording. Off the main thread.
    func prepareLiveWords() {
        guard !preview, !liveWordsVerified, !preparingLiveWords, liveWordsSlot.row.isReady, let folder = liveWordsSlot.downloader?.target,
              let helper = BundledModels.helper, let manifest = BundledModels.pins else { return }
        preparingLiveWords = true
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard (try? ModelVerificationCache.shared.verify(folder, manifest: manifest, model: .liveWords)) != nil else {
                DispatchQueue.main.async { self?.preparingLiveWords = false; self?.liveWordsSlot.message = "Live words failed verification. Remove them and download again." }
                return
            }
            let compile = Process(); compile.executableURL = helper; compile.arguments = [folder.path, "--stream", String(AudioFormat.sampleRate)]
            compile.standardInput = FileHandle.nullDevice; compile.standardOutput = FileHandle.nullDevice; compile.standardError = FileHandle.nullDevice
            if (try? compile.run()) != nil { compile.waitUntilExit() }
            DispatchQueue.main.async { self?.preparingLiveWords = false; self?.liveWordsVerified = true }
        }
    }
    func startLiveWords() {
        stopLiveWords()
        guard LiveWordsFacts.starts(enabled: liveWords, ready: liveWordsVerified, cloudTranscription: sessionTranscriber != nil, onCloud: liveWordsOnCloud),
              !preview, let capture, let rate = capture.recordingFormat?.sampleRate,
              let helper = BundledModels.helper, let folder = liveWordsSlot.downloader?.target else { return }
        let token = generation
        liveText = ""
        guard let stream = LiveStream(helper: helper, folder: folder, sampleRate: rate, onText: { [weak self] text in
            guard let self, self.generation == token, self.live != nil else { return }
            self.liveText = text
        }) else { return }
        live = stream
        capture.setTee { stream.feed($0) }
        bubble.show(phase: phase, message: message, screen: destinationScreen)
    }
    /// Keeps the last text up until the Processing bubble replaces it; the next start clears it.
    func stopLiveWords() {
        capture?.setTee(nil)
        live?.stop(); live = nil
    }
    func downloadLiveWords() {
        var flow = flow(\.liveWordsSlot)
        flow.willDownload = { [weak self] in self?.liveWordsVerified = false }
        flow.didDownload = { [weak self] _ in self?.prepareLiveWords() }
        flow.download()
    }
    func cancelLiveWordsDownload() { liveWordsSlot.downloader?.cancel() }
    func removeLiveWords() {
        var flow = flow(\.liveWordsSlot)
        flow.didRemove = { [weak self] in self?.liveWordsVerified = false }
        flow.remove()
    }
    func useDefaultModelDirectory() { if let downloader = modelSlot.downloader { modelDirectory = downloader.target.path } }
}
