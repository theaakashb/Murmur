// AppController: learning from corrections (the post-paste watch, its key tap and undo).
import AppKit
import ApplicationServices
import Carbon
import os

extension AppController {
    // Learning from corrections (PRD R10): after an insertion, watch the dictated span until focus
    // leaves, 60 seconds pass, attribution is lost or the next insertion; each change goes to the
    // ledger, which settles when the watch ends or once the edited span has been stable for
    // `stableSettleDelay` with an accepted reading (gap #9); the watch then goes on, and a later
    // edit or undo replaces or withdraws what that early settle learned. A field that reads back the dictated text is read
    // through AX; any other field (VS Code's stub, web canvases) is followed by the keys typed in
    // that app during the window, which are never stored or logged.
    struct Watch {
        var observation: DictationObservation
        let pid: pid_t
        let field: FocusSnapshot?
        var keyFocus: LearningFocusIdentity? = nil
        var keys: KeystrokeSpan?
        var current: String
        /// When `current` last changed (system uptime), and whether its reading is unsettled ledger evidence.
        var changedAt: TimeInterval = ProcessInfo.processInfo.systemUptime
        var pending = false
        var settledEarly = false
        var entryID = "", route = ""
    }
    private static let stableSettleDelay: TimeInterval = 2
    /// Gap #9: an accepted reading unchanged for `stableSettleDelay` settles before the window closes.
    static func settlesEarly(pending: Bool, changedAt: TimeInterval, now: TimeInterval) -> Bool {
        pending && now - changedAt >= stableSettleDelay
    }
    func watchCorrections(_ insertion: InsertionResult, text: String, entryID: String, at offset: Int?, pid: pid_t?) {
        // With learning off no span is observed, so the entry gets no after-paste text.
        guard processing?.settings.learningEnabled == true else { timingLog.notice("watch route=none why=learning-off"); return }
        guard insertion.status != .failed else { timingLog.notice("watch route=none why=insert-failed"); return }
        let now = ProcessInfo.processInfo.systemUptime
        // A snapshot exists only when the same field read back the dictated text (verified, or a
        // browser paste whose read-back is not trusted for delivery but is for observation).
        // Why the field route was not taken, content-free, for the log.
        let length = (text as NSString).length
        let axMiss: String
        if let field = insertion.snapshot, field.readableText, let value = field.value, let offset {
            if NSMaxRange(NSRange(location: offset, length: length)) <= (value as NSString).length,
               (value as NSString).substring(with: NSRange(location: offset, length: length)) == text {
                axMiss = DictationObservation(value: value, insertedRange: NSRange(location: offset, length: length), now: now) == nil ? "span-cuts-word" : ""
            } else { axMiss = "text-not-at-offset" }
        } else { axMiss = insertion.snapshot == nil ? "no-readback" : insertion.snapshot!.readableText ? "no-offset" : "unreadable" }
        if axMiss.isEmpty, let field = insertion.snapshot, let value = field.value, let offset,
           let observation = DictationObservation(value: value, insertedRange: NSRange(location: offset, length: length), now: now) {
            watch = Watch(observation: observation, pid: field.pid, field: field, keys: nil, current: text, entryID: entryID, route: "ax")
            timingLog.notice("watch route=ax")
        } else if !learningRequiresReadableField, !IsSecureEventInputEnabled(),
                  let pid, CGPreflightListenEventAccess(), startKeyTap(),
                  let observation = DictationObservation(value: text, insertedRange: NSRange(location: 0, length: length), now: now) {
            var keys = KeystrokeSpan(text)
            let lexicon = processing?.dictionary.isCommonWord
            keys.resolve = { CorrectionFilter.plausibleEdit(original: text, candidates: $0, isCommonWord: lexicon) }
            let identity = LearningFocusIdentity.capture(pid: pid)
            guard !identity.secure else { stopWatching(); return }
            watch = Watch(observation: observation, pid: pid, field: nil, keyFocus: identity, keys: keys, current: text, entryID: entryID, route: "keys")
            timingLog.notice("watch route=keys ax-miss=\(axMiss, privacy: .public)")
        } else {
            timingLog.notice("watch route=none ax-miss=\(axMiss, privacy: .public) input-monitoring=\(CGPreflightListenEventAccess(), privacy: .public)")
            stopWatching(); return
        }
        watchTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.watchTick() }
        }
    }
    func watchTick(ending: Bool = false) {
        guard var w = watch else { return }
        let same: Bool, readable: Bool, value: String, selection: NSRange
        if var keys = w.keys {
            keys.refresh(); w.keys = keys; watch?.keys = keys
            let identity = LearningFocusIdentity.capture(pid: w.pid)
            same = keys.state != .departed && NSWorkspace.shared.frontmostApplication?.processIdentifier == w.pid && w.keyFocus?.matches(identity) == true
            readable = keys.state != .lost && !IsSecureEventInputEnabled() && !identity.secure
            value = keys.value; selection = keys.selection
        } else if let field = w.field {
            let focus = try? captureFocus()
            // captureFocus spins the run loop; a shortcut handled there may already have ended this watch.
            guard watch?.observation.sessionID == w.observation.sessionID else { return }
            same = focus.map { $0.pid == field.pid && CFEqual($0.element, field.element) } ?? false
            // Access is about the dictated field; another app's unreadable focus is a departure, not lost access.
            // Chromium turns Secure Event Input on in password fields, a backstop for unflagged ones.
            readable = AXIsProcessTrusted() && !IsSecureEventInputEnabled() && (same ? focus?.readableText == true : true)
            value = focus?.value ?? ""
            selection = focus?.selection.map { NSRange(location: $0.location, length: $0.length) } ?? NSRange(location: 0, length: 0)
        } else { return }
        let now = ProcessInfo.processInfo.systemUptime
        _ = w.observation.observe(value: value, selection: selection, now: now,
                                  sameFocusedField: same, readableNonsecure: readable)
        if ending { w.observation.finish() }
        let session = w.observation.sessionID, current = w.observation.edited ?? w.observation.initial
        do {
            if current != w.current {
                let outcome = try processing?.observeSpan(sessionID: session, original: w.observation.initial, current: current)
                // Shape only (word and letter counts, similarity), never the words themselves.
                let shape: String
                switch CorrectionFilter.replacements(w.observation.initial, current) {
                case .failure(let why): shape = "\(why)"
                case .success(let changes): shape = changes.map { change in
                    let from = CorrectionFilter.letters(change.source), to = CorrectionFilter.letters(change.targets[0])
                    return "\(TermText.tokens(change.source).count)w\(from.count)l->\(TermText.tokens(change.targets[0]).count)w\(to.count)l sim=\(String(format: "%.2f", CleanupText.similarity(from, to)))"
                }.joined(separator: ",")
                }
                timingLog.notice("watch edit outcome=\(outcome.map { "\($0)" } ?? "reverted", privacy: .public) changes=\(shape, privacy: .public)")
                w.current = current; w.changedAt = now; w.pending = outcome == .provisional
                // After an early settle, an edit or undo may have withdrawn what it learned.
                if w.settledEarly && outcome != .sessionClosed { refreshDictionary() }
            }
            watch = w
            if w.observation.stopped {
                stopWatching()
                recordAfterPaste(w)
                // Settling can also delete learned entries (reversal, conflict), so always refresh.
                defer { refreshDictionary() }
                let learned = try processing?.settle(sessionID: session) ?? []
                timingLog.notice("watch end reason=\(w.observation.stopReason?.rawValue ?? "unknown", privacy: .public) keys=\(w.keys.map { "\($0.state)" } ?? "-", privacy: .public) learned=\(learned.count, privacy: .public)")
                if let entry = learned.last { announceLearned(entry) }
            } else if Self.settlesEarly(pending: w.pending, changedAt: w.changedAt, now: now) {
                w.pending = false; w.settledEarly = true; watch = w
                defer { refreshDictionary() }
                let learned = try processing?.settle(sessionID: session) ?? []
                timingLog.notice("watch early-settle learned=\(learned.count, privacy: .public)")
                if let entry = learned.last { announceLearned(entry) }
            }
        } catch { stopWatching(); try? processing?.ledger.revert(sessionID: session) }
    }
    /// The span as last observed when the window closed, whatever the learning ledger made of it.
    /// Deferred: the next dictation can end the watch just before its paste, which should not wait on a disk write.
    private func recordAfterPaste(_ w: Watch) {
        let seen = w.observation.edited.map { AfterPaste.trimmingAppended(initial: w.observation.initial, edited: $0) }
        let after = AfterPaste(text: seen?.text, changed: seen?.changed, route: w.route, endReason: w.observation.stopReason?.rawValue)
        DispatchQueue.main.async { [weak self] in
            guard let self, !w.entryID.isEmpty, let index = self.history.firstIndex(where: { $0.id == w.entryID }) else { return }
            var entry = self.history[index]
            entry.afterPaste = after
            try? self.records?.save(entry); self.history[index] = entry
        }
    }
    func announceLearned(_ entry: DictionaryEntry) {
        bubble.showLearned(entry.preferred, entryID: entry.id, screen: destinationScreen)
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [.announcement: "Added \(entry.preferred) to your dictionary.", .priority: NSAccessibilityPriorityLevel.medium.rawValue])
    }
    /// The learned notice's Undo: the replacement stops and is tombstoned (see `CorrectionLedger.undoLearned`).
    func undoLearned(_ entryID: UUID) {
        bubble.hide()
        do { try processing?.ledger.undoLearned(entryID: entryID) } catch { message = "Could not undo the learned word." }
        refreshDictionary()
    }
    func stopWatching() {
        watchTimer?.invalidate(); watchTimer = nil; watch = nil
        if let keyTap { CGEvent.tapEnable(tap: keyTap, enable: false); CFMachPortInvalidate(keyTap) }
        keyTap = nil
    }
    /// Listen-only (Input Monitoring is enough); runs on the main run loop only while a keystroke watch is open.
    private func startKeyTap() -> Bool {
        let mask = [CGEventType.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown].reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly, eventsOfInterest: mask,
            callback: { _, type, event, context in
                if let context {
                    let controller = Unmanaged<AppController>.fromOpaque(context).takeUnretainedValue()
                    MainActor.assumeIsolated { controller.watchKey(type, event) }
                }
                return Unmanaged.passUnretained(event)
            }, userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return false }
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(nil, tap, 0), .commonModes)
        keyTap = tap
        return true
    }
    private func watchKey(_ type: CGEventType, _ event: CGEvent) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput { if let keyTap { CGEvent.tapEnable(tap: keyTap, enable: true) }; return }
        guard let w = watch, var keys = w.keys else { return }
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == w.pid, !IsSecureEventInputEnabled(),
              w.keyFocus?.matches(LearningFocusIdentity.capture(pid: w.pid)) == true else { watchTick(ending: true); return }
        if type == .keyDown { keys.apply(Self.watchedKey(event)) } else { keys.click() }
        watch?.keys = keys
    }
    nonisolated static func watchedKey(_ event: CGEvent) -> KeystrokeSpan.Key {
        let flags = event.flags
        if flags.contains(.maskCommand) || flags.contains(.maskControl) { return .other }
        let word = flags.contains(.maskAlternate), extend = flags.contains(.maskShift)
        switch event.getIntegerValueField(.keyboardEventKeycode) {
        case Int64(kVK_Delete): return .backspace(word: word)
        case Int64(kVK_ForwardDelete): return .forwardDelete
        case Int64(kVK_LeftArrow): return .left(word: word, extend: extend)
        case Int64(kVK_RightArrow): return .right(word: word, extend: extend)
        default:
            var length = 0
            var units = [UniChar](repeating: 0, count: 8)
            event.keyboardGetUnicodeString(maxStringLength: 8, actualStringLength: &length, unicodeString: &units)
            let text = String(utf16CodeUnits: units, count: length)
            // Hardware events carry the shifted character; synthetic ones may carry only the Shift flag.
            let shifted = extend || flags.contains(.maskAlphaShift)
            return .text(shifted && text.allSatisfy(\.isLetter) ? text.uppercased() : text)
        }
    }
}
