// AppController: History operations, the history size cap and Delete All Data.
import AppKit

extension AppController {
    func refreshHistory() {
        do { history = try records?.entries() ?? []; sessionOnlyIDs = records?.sessionOnlyIDs ?? [] }
        catch { message = "Could not read saved history." }
        // Once per change, not per render: Settings re-renders with every voice-level tick.
        insights = Insights.compute(history.filter { !sessionOnlyIDs.contains($0.id) }.map {
            InsightsItem(recordedAt: $0.recordedAt, rawText: $0.rawText, finalText: $0.finalText, seconds: $0.seconds, app: $0.app, expanded: $0.snippets != nil)
        }, historyOn: appliedHistoryMode == .on, now: Date(), calendar: .current)
    }
    func copyEntry(_ entry: HistoryEntry) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(entry.finalText, forType: .string) }
    /// ⌃⌘C anywhere: the latest dictation's raw transcript, so a held or uninserted one needs no
    /// trip to History. Does nothing when this launch has no transcript yet.
    func copyLatestRawTranscript() {
        guard let id = recoveryEntryID, let entry = history.first(where: { $0.id == id }) else { return }
        copyRawEntry(entry)
        if !isBusy { setPhase(.ready, "Raw transcript copied.") }
    }
    func copyRawEntry(_ entry: HistoryEntry) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(entry.rawText, forType: .string) }

    private func deleteTranscript(_ entry: HistoryEntry) throws {
        try records?.delete(entry)
        if entry.recordingFile != nil, let history = store?.directory {
            try? FileManager.default.removeItem(at: DiagnosticRecordings.directory(inHistory: history).appendingPathComponent(entry.id + ".wav"))
        }
        if lastDictationText == entry.finalText { lastDictationText = nil }
        if setupTestTranscript == entry.finalText { setupTestTranscript = "" }
        if recoveryEntryID == entry.id { recoveryEntryID = nil }
    }
    /// Saves an edited final text (the raw transcript is kept) and learns from the edit
    /// through the same filter and ledger as automatic observation. Returns false if it could not save.
    @discardableResult
    func editEntryText(_ entry: HistoryEntry, to text: String) -> Bool {
        guard text != entry.finalText else { return true }
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { message = "The final text cannot be empty."; return false }
        var edited = entry; edited.finalText = text; edited.edited = true
        do { try records?.update(edited) } catch { message = "Could not save this transcript."; return false }
        if lastDictationText == entry.finalText { lastDictationText = text }
        refreshHistory()
        if let processing, entry.snippets == nil {
            let learned = (try? processing.ledger.learnFromEdit(original: entry.finalText, edited: text)) ?? []
            refreshDictionary()
            if let learned = learned.last { announceLearned(learned) }
        }
        return true
    }
    func deleteEntry(_ entry: HistoryEntry) { do { try deleteTranscript(entry); refreshHistory() } catch { message = "Could not delete this transcript." } }
    func clearHistory() {
        let alert = NSAlert(); alert.messageText = "Delete all saved transcripts?"; alert.informativeText = "This cannot be undone."
        alert.addButton(withTitle: "Delete All"); alert.addButton(withTitle: "Cancel")
        if alert.runDestructive() == .alertFirstButtonReturn {
            do { for entry in history { try deleteTranscript(entry) } }
            catch { message = "Could not delete all transcripts. Remaining entries are shown in History." }
            refreshHistory()
        }
    }

    // MARK: History cap and Delete All Data

    /// The owner-only folder for chunk files; chunking is on only once it exists.
    func makeChunkFolder(_ chunks: URL) throws {
        try FileManager.default.createDirectory(at: chunks, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        Chunker.directory = chunks
    }
    func makeHistoryStore(_ directory: URL) throws -> HistoryStore {
        try HistoryStore(directory: directory) { [weak self] moved in DispatchQueue.main.async { self?.historyNotice = HistoryStore.quarantineNotice(moved) } }
    }
    func historyDisk(_ store: HistoryStore) -> HistoryDisk<HistoryEntry> {
        HistoryDisk(save: store.save, entries: store.entries, delete: store.delete)
    }
    /// Drops oldest disk entries over the 50 MB cap for finite retention; warns for Forever.
    func enforceHistoryCap() throws {
        guard let store else { historyCapWarning = nil; return }
        // Meetings live inside the History folder, so they share the cap's oldest-first list.
        let entries = try store.entries()
        // The recorder's list mirrors the saved meetings (the live one excluded); no files are re-read.
        let meetings = meetingRecorder.persists ? meetingRecorder.meetings : []
        let result: HistoryCapResult<CappedHistoryItem>
        if let meetingStore = meetingRecorder.store {
            result = try MeetingCap.enforce(entries: entries.map { ($0.id, $0.recordedAt) }, meetings: meetings, historyDirectory: store.directory, meetingStore: meetingStore,
                                            retention: appliedRetention) { id in
                if let entry = entries.first(where: { $0.id == id }) { try store.delete(entry) }
            }
            if result.dropped.contains(where: \.isMeeting) { meetingRecorder.forget(Set(result.dropped.filter(\.isMeeting).map(\.id))) }
        } else {
            let plain = try HistoryCap.enforce(entries: entries, directory: store.directory, retention: appliedRetention, delete: store.delete)
            result = HistoryCapResult(status: plain.status, dropped: [])
        }
        historyCapWarning = appliedRetention == .forever ? result.status.warning : nil
    }
    /// Called once a dictation's last history save is done.
    func historySaved() {
        do { try enforceHistoryCap() } catch { historyCapWarning = "Could not check the history size limit. Open History to review saved data." }
        refreshHistory()
    }
    func deleteAllData() {
        guard !isBusy else { message = "Finish the current dictation before deleting data."; return }
        let alert = NSAlert(); alert.messageText = DeleteAllDataText.title; alert.informativeText = DeleteAllDataText.body
        alert.addButton(withTitle: "Delete All Data"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        deleteConfirmedData()
    }
    /// Confirmation is kept outside the wipe so checks exercise the same reset as the UI.
    func deleteConfirmedData() {
        guard !isBusy else { message = "Finish the current dictation before deleting data."; return }
        guard let base = dataDirectory else { return }
        stopWatching()
        meetingRecorder.discardAll()
        if let retry { prestart.discard(store: retry) }
        chunkJobs.values.forEach { $0.discard() }; chunkJobs.removeAll()
        warm.stop()
        generation = UUID()
        lastDictationText = nil; setupTestTranscript = ""
        destination = nil; sessionTranscriber = nil
        let locations = MurmurDataLocations.standard(base: base)
        // Preview and the developer probe never touch the login Keychain.
        let removeKeys: () throws -> [String] = preview ? { [] } : {
            try KeychainCredentialStore().deleteAll()
            return ProviderID.allCases.map(\.rawValue)
        }
        let report = DataWipe.deleteAllData(locations, removeKeychainItems: removeKeys)
        // The old stores point at deleted directories or hold deleted data in memory: recreate them empty.
        let failed = PostWipeStep.run { step in
            switch step {
            case .discardInMemoryState:
                records?.clearSessionOnly(); records = nil; store = nil; retry = nil; processing = nil; stopWatching()
                // Words from dismissed suggestions come from dictations, so they go too.
                UserDefaults.standard.removeObject(forKey: Prefs.dismissedSuggestions.key); dictionarySuggestions = []
                recoveryEntryID = nil; recoveryAudio = nil; historyCapWarning = nil; historyNotice = nil
            case .recreateHistory:
                let next = try makeHistoryStore(locations.history)
                store = next; records = HistoryRouter(disk: historyDisk(next), persists: appliedHistoryMode.persistsTranscripts)
                configureMeetings(locations)
            case .recreateRetryAudio:
                retry = try RetryAudio(directory: locations.retryAudio)
                try makeChunkFolder(locations.chunks)
            case .reloadDictionary:
                do { processing = try ProcessingCoordinator.inApp(directory: locations.dictionary); dictionaryMessage = "" }
                catch { dictionaryMessage = "The dictionary could not be read. Dictations are held in History until it is repaired."; throw error }
            case .refreshViews:
                refreshHistory(); refreshRecoveryAudio(); refreshDictionary(); refreshSnippets(); refreshKeys()
            }
        }
        let outcome = PostWipeStep.message(report: report, failedSteps: failed)
        if !sessionAvailable { phase = .error; message = outcome.text + " Private storage is unavailable. Restart Murmur."; updateMenu?(); return }
        setPhase(outcome.succeeded ? .ready : .error, outcome.text)
    }
}
