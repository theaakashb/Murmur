// AppController: the changes chip and paste-failure cards.
import AppKit
import os

extension AppController {
    // MARK: Mode recovery

    /// Where verified inserted text sits, so "Use my words" can replace exactly it.
    struct ModeRecoveryAnchor { let element: AXUIElement; let pid: pid_t; let value: String; let range: NSRange }
    struct ModeRecovery {
        enum Stage: Equatable { case offer, copy(String), reverted }
        /// `changes` counts the words cleanup changed.
        let entryID: String, raw: String, inserted: String
        var changes = 0
        /// The text before cleanup but after dictionary spellings: what "Use my words" puts back.
        var mine = ""
        var anchor: ModeRecoveryAnchor?
        var stage = Stage.offer
        /// A writing mode shaped the text: the chip shows the mode icon instead of the cleanup sparkle.
        var usedMode = false
    }

    /// The field is replaced only when it still holds exactly the verified post-insertion value and
    /// the inserted text still sits at the recorded range.
    nonisolated static func recoveryRangeIsIntact(value: String, range: NSRange, inserted: String) -> Bool {
        let ns = value as NSString
        return range.location >= 0 && NSMaxRange(range) <= ns.length && ns.substring(with: range) == inserted
    }
    func clearModeRecovery() {
        recoveryRequest.cancel(); modeRecovery = nil
        bubble.hideRecovery()
    }
    /// Where the audio and the text went: "Local" or the route's name. Cleanup is judged by what happened, not
    /// by the configured route: a cloud call whose output the guard rejected still sent the text.
    nonisolated static func routeLabel(transcription: String?, cleanup: String?, outcome: CleanupPath?) -> String? {
        let onThisMac: Set<String> = ["local", "local-rules", ProviderID.localModel.rawValue, ProviderID.appleIntelligence.rawValue]
        func name(_ id: String) -> String { onThisMac.contains(id) ? "Local" : ProviderID(rawValue: id)?.displayName ?? id }
        let heard = transcription.map(name)
        var cleaned: String?
        if let cleanup, outcome != .off {
            let n = name(cleanup)
            cleaned = n != "Local" && (outcome == .localRules || outcome == .modelPunctuationOnLocal) ? "sent to \(n), local rules used" : n
        }
        switch (heard, cleaned) {
        case (nil, nil): return nil
        case (let h?, nil): return "Heard: \(h)"
        case (nil, let c?): return "Cleaned: \(c)"
        case (let h?, let c?): return h == c ? "Heard and cleaned: \(h)" : "Heard: \(h) · Cleaned: \(c)"
        }
    }
    /// Offered after any insertion that changed the transcript.
    func offerModeRecovery(entry: HistoryEntry, inserted: String, insertion: InsertionResult, target: FocusSnapshot?) {
        let mine = entry.cleanup?.input ?? entry.rawText
        let changes = WordDiff.changeCount(raw: mine, final: inserted)
        guard changes > 0 else { return }
        var anchor: ModeRecoveryAnchor?
        if insertion.status == .verified, let after = insertion.snapshot, let value = after.value, let selection = target?.selection {
            let range = NSRange(location: selection.location, length: inserted.utf16.count)
            if Self.recoveryRangeIsIntact(value: value, range: range, inserted: inserted) {
                anchor = ModeRecoveryAnchor(element: after.element, pid: after.pid, value: value, range: range)
            }
        }
        modeRecovery = ModeRecovery(entryID: entry.id, raw: entry.rawText, inserted: inserted, changes: changes, anchor: anchor)
        modeRecovery?.mine = mine; modeRecovery?.usedMode = entry.writingModeID != nil
        bubble.showRecovery(screen: destinationScreen)
    }
    /// ⌃⌘Z and the card's "Use raw": puts the raw transcript back over exactly the inserted text. Anything
    /// unreadable or changed since insertion leaves the field alone.
    func revertToRaw() {
        guard var recovery = modeRecovery, recovery.stage == .offer, !isBusy else { return }
        let text = WordDiff.revertText(raw: recovery.mine, inserted: recovery.inserted)
        if replaceInserted(recovery, with: text) {
            recovery.stage = .reverted
            updateRecoveredHistory(entryID: recovery.entryID, text: text)
            let id = recovery.entryID
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in if self?.modeRecovery?.entryID == id { self?.clearModeRecovery() } }
        } else { recovery.stage = .copy(text); bubble.restartRecoveryTimer() }
        modeRecovery = recovery
    }
    /// The chip's click: shows this dictation in History.
    func openRecoveryEntry() {
        guard let id = modeRecovery?.entryID else { return }
        clearModeRecovery()
        screenRequest = .historyEntry(id); openSettings?()
    }
    /// Whether ⌃⌘Z is live: the tap swallows it only while the offer is open.
    var revertOffered: Bool { modeRecovery?.stage == .offer }
    /// An empty selection at the end of the span: nothing selected for the next keystroke to delete.
    nonisolated static func caretAfter(_ range: CFRange) -> CFRange { CFRange(location: range.location + range.length, length: 0) }
    nonisolated static func replacementCounts(_ status: InsertionResult.Status) -> Bool { status == .verified }
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
        // Only a verified paste counts as replaced. A failed one must not leave the dictated span selected,
        // where the next keystroke would delete it.
        if result.status == .failed {
            var caret = Self.caretAfter(range)
            if let collapsed = AXValueCreate(.cfRange, &caret) { AXUIElementSetAttributeValue(anchor.element, kAXSelectedTextRangeAttribute as CFString, collapsed) }
        }
        if result.status != .failed { lastDictationText = text }
        return Self.replacementCounts(result.status)
    }
    private func updateRecoveredHistory(entryID: String, text: String) {
        guard let index = history.firstIndex(where: { $0.id == entryID }) else { return }
        var entry = history[index]; entry.finalText = text
        try? records?.update(entry); history[index] = entry
    }
    func copyRecovery() {
        guard let recovery = modeRecovery, case .copy(let text) = recovery.stage, !text.isEmpty else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
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
    /// Murmur deliberately did not paste: the card shows the text with Copy.
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
