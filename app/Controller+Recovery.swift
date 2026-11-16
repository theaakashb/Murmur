// AppController: mode recovery (the "Use without mode" pill) and paste-failure cards.
import AppKit
import os

private let timingLog = Logger(subsystem: "cc.clouddev.dictation", category: "timing")

extension AppController {
    // MARK: Mode recovery

    /// Where verified inserted text sits, so the pill's "Use without mode" can replace exactly it.
    struct ModeRecoveryAnchor { let element: AXUIElement; let pid: pid_t; let value: String; let range: NSRange }
    struct ModeRecovery {
        enum Stage: Equatable { case offer, working, replaced, copy(String) }
        let entryID: String, modeName: String, raw: String, inserted: String
        var anchor: ModeRecoveryAnchor?
        var stage = Stage.offer
    }

    /// The field is replaced only when it still holds exactly the verified post-insertion value and
    /// the inserted text still sits at the recorded range.
    nonisolated static func recoveryRangeIsIntact(value: String, range: NSRange, inserted: String) -> Bool {
        let ns = value as NSString
        return range.location >= 0 && NSMaxRange(range) <= ns.length && ns.substring(with: range) == inserted
    }
    /// Synthetic UI fixture only (check_ui.swift): shows the pill without a real insertion.
    func showModeRecoveryFixture(stage: ModeRecovery.Stage) {
        modeRecovery = ModeRecovery(entryID: "fixture", modeName: "AI Prompt", raw: "", inserted: "", anchor: nil, stage: stage)
        bubble.showRecovery(screen: nil)
        if let path = ProcessInfo.processInfo.environment["MURMUR_PILL_PNG"] { bubble.writeSnapshot(to: URL(fileURLWithPath: path)) }
    }
    func clearModeRecovery() {
        recoveryRequest.cancel(); modeRecovery = nil
        bubble.hideRecovery()
    }
    func offerModeRecovery(entry: HistoryEntry, inserted: String, insertion: InsertionResult, target: FocusSnapshot?) {
        guard let name = entry.writingModeName else { return }
        var anchor: ModeRecoveryAnchor?
        if insertion.status == .verified, let after = insertion.snapshot, let value = after.value, let selection = target?.selection {
            let range = NSRange(location: selection.location, length: inserted.utf16.count)
            if Self.recoveryRangeIsIntact(value: value, range: range, inserted: inserted) {
                anchor = ModeRecoveryAnchor(element: after.element, pid: after.pid, value: value, range: range)
            }
        }
        modeRecovery = ModeRecovery(entryID: entry.id, modeName: name, raw: entry.rawText, inserted: inserted, anchor: anchor)
        bubble.showRecovery(screen: destinationScreen)
    }
    /// Reprocesses the original transcript (trigger included) through ordinary Cleanup, with no mode matching.
    func useWithoutMode() {
        guard var recovery = modeRecovery, recovery.stage == .offer, !isBusy, let processing else { return }
        recovery.stage = .working; modeRecovery = recovery
        let style = DictationStyle(mode: nil, settings: processing.settings)
        let entryID = recovery.entryID
        recoveryRequest.start({ await processing.processAsync(recovery.raw, style: style) }) { [weak self] outcome in
            guard let self, var recovery = self.modeRecovery, recovery.entryID == entryID, !self.isBusy else { return }
            guard case .insert(let text) = outcome.outcome, !text.isEmpty else { recovery.stage = .offer; self.modeRecovery = recovery; return }
            let replacement = recovery.inserted.hasPrefix(" ") && !text.hasPrefix(" ") ? " " + text : text
            if self.replaceInserted(recovery, with: replacement) {
                recovery.stage = .replaced
                self.updateRecoveredHistory(entryID: entryID, text: replacement)
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in if self?.modeRecovery?.entryID == entryID { self?.clearModeRecovery() } }
            } else { recovery.stage = .copy(text) }
            self.modeRecovery = recovery
        }
    }
    /// Selects the recorded range and pastes over it, only if the same focused field still holds the
    /// verified value; nothing else is ever touched.
    private func replaceInserted(_ recovery: ModeRecovery, with text: String) -> Bool {
        guard let anchor = recovery.anchor, let current = try? captureFocus(), current.pid == anchor.pid, CFEqual(current.element, anchor.element),
              current.readableText, current.value == anchor.value else { return false }
        watchTick(ending: true)
        var range = CFRange(location: anchor.range.location, length: anchor.range.length)
        guard let value = AXValueCreate(.cfRange, &range),
              AXUIElementSetAttributeValue(anchor.element, kAXSelectedTextRangeAttribute as CFString, value) == .success,
              let selected = try? captureFocus(), selected.pid == anchor.pid, CFEqual(selected.element, anchor.element),
              selected.value == anchor.value, selected.selection?.location == range.location, selected.selection?.length == range.length else { return false }
        let result = insertText(text, into: selected)
        timingLog.notice("mode-recovery status=\(result.status.rawValue, privacy: .public) detail=\(result.detail, privacy: .public)")
        if result.status != .failed { lastDictationText = text }
        return result.status != .failed
    }
    private func updateRecoveredHistory(entryID: String, text: String) {
        guard let index = history.firstIndex(where: { $0.id == entryID }) else { return }
        var entry = history[index]; entry.finalText = text
        try? records?.update(entry); history[index] = entry
    }
    func copyRecovery() {
        guard let recovery = modeRecovery, case .copy(let text) = recovery.stage, !text.isEmpty else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
        clearModeRecovery()
    }

    /// ⌃⌘V explicitly pastes again; never triggered by a read-back mismatch.
    /// Keep the last usable text through canceled, empty, or failed recordings.
    func pasteLastDictation() {
        guard let text = lastDictationText, !isBusy else { return }
        watchTick(ending: true)
        let result = pasteIntoFrontmostApp(text)
        timingLog.notice("paste-chord status=\(result.status.rawValue, privacy: .public) detail=\(result.detail, privacy: .public)")
        showInsertionResult(result, entryID: nil, text: text)
    }
    /// Read-back is diagnostic evidence, not a reason to warn or paste twice.
    func showInsertionResult(_ result: InsertionResult, entryID: String?, text: String) {
        switch result.status {
        case .verified, .unverified: setPhase(.ready, idleMessage, announcement: "Dictation finished.")
        case .failed: setPhase(.error, "Could not paste the dictation. Copy it here, select a text field and press ⌃⌘V, or copy it from History. Check the field first to avoid a duplicate.",
                               pasteFailure: PasteFailure(entryID: entryID, text: text))
        }
    }
    enum NotPasted { case insertionOff, unsaved }
    /// Murmur deliberately did not paste: the card shows the text with Copy (gap #17, Oct 15 follow-up).
    /// Failed pastes go through `showInsertionResult`; a posted but unconfirmed paste stays quiet.
    func showNotPasted(_ why: NotPasted, saved: String, entryID: String?, text: String) {
        switch why {
        case .insertionOff:
            setPhase(.saved, "\(saved) Press ⌃⌘V to paste it where your cursor is.", pasteFailure: PasteFailure(entryID: entryID, text: text, title: "Saved, not pasted"))
        case .unsaved:
            setPhase(.error, "\(saved) Could not save the text for insertion, so nothing was pasted. Copy it here or from History.", pasteFailure: PasteFailure(entryID: entryID, text: text))
        }
    }
    /// The card's Copy button: the text goes to the clipboard and its History entry is marked copied.
    func copyPasteFailure() {
        guard let failure = pasteFailure else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(failure.text, forType: .string)
        if let id = failure.entryID, var entry = history.first(where: { $0.id == id }) {
            entry.insertionStatus = InsertionStatus.copied.rawValue; try? records?.save(entry); refreshHistory()
        }
        setPhase(.ready, "Copied. Press ⌘V to paste it.", announcement: "Dictation copied.")
    }
}

// Probe inputs belong to the caller; only the private copy follows retry cleanup.
func copyProbeAudio(_ audio: URL, into retry: RetryAudio) throws -> URL {
    let ownedAudio = try retry.create()
    do {
        let input = try FileHandle(forReadingFrom: audio)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: ownedAudio)
        defer { try? output.close() }
        while let data = try input.read(upToCount: 1024 * 1024), !data.isEmpty { try output.write(contentsOf: data) }
        try output.synchronize()
        return ownedAudio
    } catch { try? retry.delete(ownedAudio); throw error }
}
