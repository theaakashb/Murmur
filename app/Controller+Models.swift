// AppController: downloading and removing the transcription, dictionary and live-words models.
import AppKit

extension AppController {
    // MARK: Model download (R2)

    func downloadModel() {
        guard let downloader else { modelMessage = ModelDownloadError.invalidManifest.userMessage; return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: modelDownloadInFlight) { modelMessage = refusal; return }
        // The license and size are shown before any byte is fetched.
        let text = ModelFacts.confirmation(downloadSize: downloader.downloadSize, license: downloader.license)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        // A dictation may have started while the alert was open.
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: modelDownloadInFlight) { modelMessage = refusal; return }
        modelDownloadInFlight = true; modelMessage = ""
        modelStatus = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: downloader.downloadSize))
        downloader.start { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.modelDownloadInFlight = false
                self.modelStatus = downloader.status()
                self.modelMessage = ModelFacts.completionMessage(result)
                // Point dictation at the verified download right away (an explicit user action, like Apply).
                if case .success = result {
                    self.modelDirectory = downloader.target.path
                    UserDefaults.standard.set(downloader.target.path, forKey: "modelDirectory")
                }
            }
        }
    }
    func cancelModelDownload() { downloader?.cancel() }
    func removeModel() {
        guard let downloader else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: modelDownloadInFlight) { modelMessage = refusal; return }
        let alert = NSAlert(); alert.messageText = "Remove the downloaded model?"
        alert.informativeText = "Murmur cannot dictate until you download it again (\(ModelFacts.formatBytes(downloader.downloadSize))) or choose another model folder."
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: modelDownloadInFlight) { modelMessage = refusal; return }
        warm.stopNow() // Release the helper's open model files before deleting them.
        do { try downloader.remove(); modelMessage = "The model was removed." }
        catch { modelMessage = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage }
        modelStatus = downloader.status()
    }
    func downloadDictionaryModel() {
        guard let dictionaryDownloader else { dictionaryModelMessage = ModelDownloadError.invalidManifest.userMessage; return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: dictionaryDownloadInFlight) { dictionaryModelMessage = refusal; return }
        let text = DictionaryModelFacts.confirmation(downloadSize: dictionaryDownloader.downloadSize, license: dictionaryDownloader.license, folder: dictionaryDownloader.target)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: dictionaryDownloadInFlight) { dictionaryModelMessage = refusal; return }
        dictionaryDownloadInFlight = true; dictionaryModelMessage = ""; dictionarySupportFailed = false
        dictionaryModelStatus = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: dictionaryDownloader.downloadSize))
        dictionaryDownloader.start { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.dictionaryDownloadInFlight = false
                self.dictionaryModelStatus = dictionaryDownloader.status()
                self.dictionaryModelMessage = DictionaryModelFacts.completionMessage(result)
                self.prewarm() // Compiles the new model now, not on the next dictation.
            }
        }
    }
    func cancelDictionaryModelDownload() { dictionaryDownloader?.cancel() }
    func removeDictionaryModel() {
        guard let dictionaryDownloader else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: dictionaryDownloadInFlight) { dictionaryModelMessage = refusal; return }
        let alert = NSAlert(); alert.messageText = DictionaryModelFacts.removeTitle
        alert.informativeText = DictionaryModelFacts.removeBody(folder: dictionaryDownloader.target)
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: dictionaryDownloadInFlight) { dictionaryModelMessage = refusal; return }
        warm.stopNow()
        do { try dictionaryDownloader.remove(); dictionaryModelMessage = "Dictionary support was removed." }
        catch { dictionaryModelMessage = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage }
        dictionaryModelStatus = dictionaryDownloader.status(); dictionarySupportFailed = false
        prewarm()
    }
    func downloadLocalModel() {
        guard let localCleanupDownloader else { localModelMessage = ModelDownloadError.invalidManifest.userMessage; return }
        if localDownloadInFlight { return }
        let text = LocalCleanupFacts.confirmation(downloadSize: localCleanupDownloader.downloadSize, license: localCleanupDownloader.license)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        localDownloadInFlight = true; localModelMessage = ""
        localModelStatus = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: localCleanupDownloader.downloadSize))
        localCleanupDownloader.start { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.localDownloadInFlight = false
                self.localModelStatus = localCleanupDownloader.status()
                Task { await LocalCleanupRunner.shared.shutdown() }
                self.localModelMessage = { if case .failure(.cancelled) = result { return "The download was cancelled. Retry continues from the files already downloaded." }; if case .failure(let error) = result { return error.userMessage }; return "The model is downloaded and verified." }()
            }
        }
    }
    func cancelLocalModelDownload() { localCleanupDownloader?.cancel() }
    func removeLocalModel() {
        guard let localCleanupDownloader, !localDownloadInFlight else { return }
        let alert = NSAlert(); alert.messageText = LocalCleanupFacts.removeTitle; alert.informativeText = LocalCleanupFacts.removeBody
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        Task {
            await LocalCleanupRunner.shared.shutdown() // Release the model files before deleting them.
            await MainActor.run {
                do { try localCleanupDownloader.remove(); self.localModelMessage = "The local cleanup model was removed." }
                catch { self.localModelMessage = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage }
                self.localModelStatus = localCleanupDownloader.status()
            }
        }
    }
    // MARK: Live words (gap #13)

    /// Checks the live folder's hashes once per launch, then loads the model once with no audio, so
    /// its first compile (about 10 s) happens now rather than in a recording. Off the main thread.
    func prepareLiveWords() {
        guard !preview, !liveWordsVerified, !preparingLiveWords, liveWordsRow.isReady, let folder = liveWordsDownloader?.target,
              let helper = Bundle.main.url(forAuxiliaryExecutable: "ModelBench"),
              let manifest = Bundle.main.url(forResource: "artifact-pins", withExtension: "json") else { return }
        preparingLiveWords = true
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard (try? ModelVerificationCache.shared.verify(folder, manifest: manifest, list: liveWordsFileList)) != nil else {
                DispatchQueue.main.async { self?.preparingLiveWords = false; self?.liveWordsMessage = "Live words failed verification. Remove them and download again." }
                return
            }
            let compile = Process(); compile.executableURL = helper; compile.arguments = [folder.path, "--stream", "16000"]
            compile.standardInput = FileHandle.nullDevice; compile.standardOutput = FileHandle.nullDevice; compile.standardError = FileHandle.nullDevice
            if (try? compile.run()) != nil { compile.waitUntilExit() }
            DispatchQueue.main.async { self?.preparingLiveWords = false; self?.liveWordsVerified = true }
        }
    }
    func startLiveWords() {
        stopLiveWords()
        guard LiveWordsFacts.starts(enabled: liveWords, ready: liveWordsVerified, cloudTranscription: sessionTranscriber != nil, onCloud: liveWordsOnCloud),
              !preview, let capture, let rate = capture.recordingFormat?.sampleRate,
              let helper = Bundle.main.url(forAuxiliaryExecutable: "ModelBench"), let folder = liveWordsDownloader?.target else { return }
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
        guard let liveWordsDownloader else { liveWordsMessage = ModelDownloadError.invalidManifest.userMessage; return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: liveWordsDownloadInFlight) { liveWordsMessage = refusal; return }
        let text = LiveWordsFacts.confirmation(downloadSize: liveWordsDownloader.downloadSize, license: liveWordsDownloader.license, folder: liveWordsDownloader.target)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if let refusal = ModelGate.downloadRefusal(isBusy: isBusy, preview: preview, downloading: liveWordsDownloadInFlight) { liveWordsMessage = refusal; return }
        liveWordsDownloadInFlight = true; liveWordsMessage = ""; liveWordsVerified = false
        liveWordsStatus = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: liveWordsDownloader.downloadSize))
        liveWordsDownloader.start { [weak self] result in
            DispatchQueue.main.async {
                guard let self else { return }
                self.liveWordsDownloadInFlight = false
                self.liveWordsStatus = liveWordsDownloader.status()
                self.liveWordsMessage = LiveWordsFacts.completionMessage(result)
                self.prepareLiveWords()
            }
        }
    }
    func cancelLiveWordsDownload() { liveWordsDownloader?.cancel() }
    func removeLiveWords() {
        guard let liveWordsDownloader else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: liveWordsDownloadInFlight) { liveWordsMessage = refusal; return }
        let alert = NSAlert(); alert.messageText = LiveWordsFacts.removeTitle
        alert.informativeText = LiveWordsFacts.removeBody(folder: liveWordsDownloader.target)
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        if let refusal = ModelGate.removeRefusal(isBusy: isBusy, preview: preview, downloading: liveWordsDownloadInFlight) { liveWordsMessage = refusal; return }
        do { try liveWordsDownloader.remove(); liveWordsMessage = "Live words were removed." }
        catch { liveWordsMessage = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage }
        liveWordsStatus = liveWordsDownloader.status(); liveWordsVerified = false
    }
    func useDefaultModelDirectory() { if let downloader { modelDirectory = downloader.target.path } }
}
