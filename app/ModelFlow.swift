import AppKit

// The download, cancel and remove flow every downloadable model shares: the owner supplies the model's ModelSlot
// and the few steps that differ (refusals, what to refresh before and after).

@MainActor struct ModelFlow<Owner: AnyObject> {
    weak var owner: Owner?
    let slot: ReferenceWritableKeyPath<Owner, ModelSlot>
    /// Why a download or removal may not start now (`ModelGate`); nil means no gate for this model.
    var downloadGate: () -> String? = { nil }
    var removeGate: () -> String? = { nil }
    /// After the state shows the download starting.
    var willDownload: () -> Void = {}
    /// Also show the downloader's byte progress as it arrives (otherwise the owner samples it).
    var pushProgress = false
    var didDownload: (Result<Void, ModelDownloadError>) -> Void = { _ in }
    /// Runs the removal after the user confirmed; release what holds the files first, then call `go`.
    var release: (_ go: @escaping () -> Void) -> Void = { $0() }
    var didRemove: () -> Void = {}

    private var state: ModelSlot? { owner.map { $0[keyPath: slot] } }
    private func update(_ change: (inout ModelSlot) -> Void) { if let owner { change(&owner[keyPath: slot]) } }

    func download() {
        guard let state else { return }
        guard let downloader = state.downloader else { update { $0.message = ModelDownloadError.invalidManifest.userMessage }; return }
        if let refusal = downloadGate() { update { $0.message = refusal }; return }
        if state.inFlight { return }
        // The license and size are shown before any byte is fetched.
        let text = state.facts.confirmation(downloadSize: downloader.downloadSize, license: downloader.license, folder: downloader.target)
        let alert = NSAlert(); alert.messageText = text.title; alert.informativeText = text.body
        alert.addButton(withTitle: "Download"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        // A dictation may have started while the alert was open.
        if let refusal = downloadGate() { update { $0.message = refusal }; return }
        update { $0.inFlight = true; $0.message = "" }
        willDownload()
        update { $0.status = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: downloader.downloadSize)) }
        let facts = state.facts, this = self
        downloader.start(progress: pushProgress ? { progress in
            DispatchQueue.main.async { MainActor.assumeIsolated { if this.state?.inFlight == true { this.update { $0.status = .downloading(progress) } } } }
        } : nil) { result in
            DispatchQueue.main.async { MainActor.assumeIsolated {
                this.update { $0.inFlight = false; $0.status = downloader.status(); $0.message = facts.completionMessage(result) }
                if this.owner != nil { this.didDownload(result) }
            } }
        }
    }

    func cancel() { state?.downloader?.cancel() }

    func remove() {
        guard let state, let downloader = state.downloader else { return }
        if let refusal = removeGate() { update { $0.message = refusal }; return }
        if state.inFlight { return }
        let alert = NSAlert(); alert.messageText = state.facts.removeTitle
        alert.informativeText = state.facts.removeBody(downloader.target, downloader.downloadSize)
        alert.addButton(withTitle: "Remove"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        if let refusal = removeGate() { update { $0.message = refusal }; return }
        let facts = state.facts, this = self
        release {
            do { try downloader.remove(); this.update { $0.message = facts.removed } }
            catch { this.update { $0.message = (error as? ModelDownloadError)?.userMessage ?? ModelDownloadError.fileSystem.userMessage } }
            this.update { $0.status = downloader.status() }
            this.didRemove()
        }
    }
}
