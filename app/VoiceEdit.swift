// AppController: voice edit. Select text, press ⌃⌥E, say how to change it, and the selection is replaced.
// Explicit action only: the selection and the instruction go to an engine only from this chord, never from dictation.
import AppKit
import ApplicationServices
import Carbon
import os

/// What one voice edit holds from the chord to the result: the engine, the field and the selection as it was.
struct VoiceEditSession {
    let engineName: String
    /// Where the text goes: "this Mac" or a host. Shown in the bubble.
    let destination: String
    let onDevice: Bool
    let target: FocusSnapshot
    let selection: String
    let run: @Sendable (_ selection: String, _ instruction: String) async throws -> String
    var generation = UUID()
}

extension AppController {
    /// Flip when `POST /v1/edit` is deployed on Murmur Cloud (Murmur-Cloud-Service). Until then Murmur Cloud is not an edit engine,
    /// and a selection never falls through to another engine the user did not choose.
    static let cloudEditDeployed = false

    /// ⌃⌥E: starts a voice edit, or finishes the one that is listening.
    func voiceEditChord() {
        guard sessionAvailable, let listener else { return }
        if voiceEdit != nil, listener.machine.state == .listening { stopRecording(); return }
        guard listener.machine.state == .idle, !isBusy else { return }
        switch voiceEditSession() {
        case .failure(let refusal): setPhase(.error, refusal.message)
        case .success(let edit):
            pendingVoiceEdit = edit
            listener.emit(listener.machine.startToggled())
            pendingVoiceEdit = nil
            if listener.machine.state == .listening {
                setPhase(.listening, "Say how to change the selection. \(edit.onDevice ? "Editing on this Mac." : "The selection and your words go to \(edit.destination).") Press ⌃⌥E to finish.")
            } else { voiceEdit = nil }
        }
    }

    struct VoiceEditRefusal: Error { let message: String }

    /// The chosen engine and the current selection, or the one line that says why not. Makes no call and records nothing.
    func voiceEditSession() -> Result<VoiceEditSession, VoiceEditRefusal> {
        func no(_ message: String) -> Result<VoiceEditSession, VoiceEditRefusal> { .failure(VoiceEditRefusal(message: message)) }
        guard !IsSecureEventInputEnabled() else { return no("Voice edit is unavailable while Secure Input is active.") }
        let routes = routeSettings, engine = AIEngine.resolve(from: .standard, cleanup: cleanupChoice)
        let key: (ProviderID) -> APIKey? = { try? CustomAPIKeyBinding.readKey(for: $0, routes: routes, defaults: .standard, credentials: self.credentials) }
        if engine == .murmurCloud && !Self.cloudEditDeployed {
            return no("Voice edit is not on Murmur Cloud yet. Choose Apple Intelligence or a Custom Provider for AI features.")
        }
        if let reason = engine.unavailableReason(routes: routes, hasKey: { key($0) != nil }) {
            return no("\(reason) Voice edit works with Apple Intelligence (macOS 26) or your own API key.")
        }
        guard let target = try? captureFocus() else { return no("Select some text first.") }
        if target.secure { return no("Voice edit does not work in password fields.") }
        if target.pid == getpid() { return no("Voice edit works on text in other apps.") }
        guard let selection = focusedSelectedText(target.element), !selection.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return no("Select some text first. Murmur could not read a selection in this app.")
        }
        let name = engine == .appleIntelligence ? "Apple Intelligence" : engine == .customProvider ? "your Custom Provider" : "Murmur Cloud"
        let cap = engine == .appleIntelligence ? EditPrompt.appleSelectionLimit : EditPrompt.remoteSelectionLimit
        guard selection.count <= cap else { return no("The selection is too long for \(name) (\(cap) characters at most). Select less.") }
        if engine == .murmurCloud, let apiKey = key(.murmurCloud) {
            let client = MurmurCloudEditProvider(apiKey: apiKey)
            return .success(VoiceEditSession(engineName: name, destination: MurmurCloud.host, onDevice: false, target: target, selection: selection, run: { try await client.edit(text: $0, instruction: $1) }))
        }
        guard let provider = engine.provider(routes: routes, key: key) else { return no("Voice edit is not set up. Check AI features in Settings.") }
        let onDevice = provider.disclosure.staysOnThisMac
        return .success(VoiceEditSession(engineName: name, destination: onDevice ? "this Mac" : provider.disclosure.destinationHost, onDevice: onDevice, target: target, selection: selection,
                                         run: { try await provider.cleanup(EditPrompt.build(selection: $0, instruction: $1)) }))
    }

    /// The instruction transcript is in, saved like a dictation: one engine call, then a verified replace or a Copy card.
    func runVoiceEdit(_ edit: VoiceEditSession, instruction: String, entry: HistoryEntry, store: HistoryRouter<HistoryEntry>, done: @escaping () -> Void) {
        let words = instruction.trimmingCharacters(in: .whitespacesAndNewlines)
        func stop(_ message: String) { voiceEdit = nil; setPhase(.error, message); done() }
        guard !words.isEmpty else { stop("No instruction was heard. The selection was not changed."); return }
        guard words.count <= EditPrompt.instructionLimit else { stop("That instruction is too long. The selection was not changed."); return }
        var saved = entry; saved.writingModeName = "Voice edit"; try? store.save(saved)
        let token = edit.generation, selection = edit.selection, run = edit.run
        cloudStage = .cleanup; cloudDone = done
        cloudRequest.start({ () async -> Result<String, Error> in
            do { return .success(try await run(selection, words)) } catch { return .failure(error) }
        }) { [weak self] result in
            guard let self, self.generation == token else { return }
            self.cloudStage = nil; self.cloudDone = nil; self.voiceEdit = nil
            self.finishVoiceEdit(edit, instruction: words, result: result)
            done()
        }
        setPhase(.processing, edit.onDevice ? "Editing on this Mac…" : "Editing with \(edit.engineName) (\(edit.destination))…")
    }

    /// The card's "⌃⌘V pastes it too" line is true because the card's text is the last dictation.
    private func showEditCard(_ phase: Phase, _ message: String, text: String, title: String) {
        lastDictationText = text
        setPhase(phase, message, pasteFailure: PasteFailure(entryID: nil, text: text, title: title))
    }

    private func finishVoiceEdit(_ edit: VoiceEditSession, instruction: String, result: Result<String, Error>) {
        switch result {
        case .failure(let error):
            let failure = error as? ProviderError
            let reason: String
            switch failure {
            case .contentBlocked?: reason = "\(edit.engineName) declined this text."
            case .payloadTooLarge?: reason = "The selection is too long for \(edit.engineName)."
            case .cancelled?: return
            case let other?: reason = other.userMessage
            case nil: reason = "The edit failed."
            }
            setPhase(.error, "\(reason) The selection was not changed.")
        case .success(let output):
            switch EditCheck.verdict(selection: edit.selection, instruction: instruction, output: output) {
            case .unchanged: setPhase(.ready, "No change. The selection already fits that.", quick: true)
            case .refused(let why):
                showEditCard(.error, "\(why) The selection was not changed.", text: output, title: "Edit not applied")
            case .apply(let text):
                if replaceVoiceEditSelection(edit, with: text) {
                    // The original stays in memory only: Copy here, or ⌘Z in the app.
                    showEditCard(.ready, "Edited with \(edit.engineName). ⌘Z in the app undoes it.", text: edit.selection, title: "Original selection")
                } else {
                    showEditCard(.error, "The text changed while editing, so nothing was replaced.", text: text, title: "Edit not applied")
                }
            }
        }
    }

    /// Replaces only if the same field still holds the same value with the same text selected; never types over anything else.
    private func replaceVoiceEditSelection(_ edit: VoiceEditSession, with text: String) -> Bool {
        guard let current = try? captureFocus(), current.pid == edit.target.pid, CFEqual(current.element, edit.target.element), current.readableText,
              current.value == edit.target.value, current.selection?.location == edit.target.selection?.location, current.selection?.length == edit.target.selection?.length,
              focusedSelectedText(current.element) == edit.selection else { return false }
        watchTick(ending: true)
        let result = insertText(text, into: current)
        timingLog.notice("voice-edit status=\(result.status.rawValue, privacy: .public) detail=\(result.detail, privacy: .public)")
        return result.status != .failed
    }

    /// Synthetic UI fixture only (check_ui.sh --voice-edit): the bubble for each state, with no capture, no engine call and no text.
    func showVoiceEditFixture(_ state: String) {
        switch state {
        case "custom": setPhase(.processing, "Editing with your Custom Provider (api.example.com)…")
        case "no-engine": setPhase(.error, "Apple Intelligence needs macOS 26. Voice edit works with Apple Intelligence (macOS 26) or your own API key.")
        case "declined": setPhase(.error, "Apple Intelligence declined this text. The selection was not changed.")
        case "too-long": setPhase(.error, "The selection is too long for Apple Intelligence (3000 characters at most). Select less.")
        case "applied": setPhase(.ready, "Edited with Apple Intelligence. ⌘Z in the app undoes it.", pasteFailure: PasteFailure(entryID: nil, text: "Synthetic original selection.", title: "Original selection"))
        default: setPhase(.processing, "Editing on this Mac…")
        }
    }
}
