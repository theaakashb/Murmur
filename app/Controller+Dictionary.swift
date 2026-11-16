// AppController: dictionary editing, suggestions and the helper vocabulary file.
import AppKit

extension AppController {
    /// Every dictionary change ends here: it rewrites the helper's vocabulary file and restarts
    /// the warm helper with it, so a wipe or edit never leaves old terms loaded.
    func refreshDictionary() {
        dictionaryEntries = processing?.dictionary.entries ?? []
        blockedWords = processing?.ledger.blocked ?? []
        writeHelperVocabulary()
        if started { prewarm() }
    }
    /// FluidAudio's vocabulary JSON: enabled entries (not command targets), preferred spelling plus "heard as" aliases.
    /// Owner-only, beside dictionary.json; absent when nothing is enabled.
    private func writeHelperVocabulary() {
        guard let vocabularyFile else { return }
        let terms = processing?.dictionary.helperVocabulary ?? []
        if terms.isEmpty { try? FileManager.default.removeItem(at: vocabularyFile); return }
        guard let data = try? JSONSerialization.data(withJSONObject: ["terms": terms], options: [.sortedKeys]) else { return }
        try? PrivateFiles.write(data, to: vocabularyFile)
    }

    @discardableResult
    func addDictionaryEntry(preferred: String, variants: String) -> Bool {
        guard let processing else { dictionaryMessage = "The dictionary is unavailable."; return false }
        do { try processing.addEntry(preferred: preferred, variants: variants); dictionaryMessage = ""; refreshDictionary(); return true }
        catch { dictionaryMessage = (error as? StoreError)?.description ?? "Could not save the dictionary."; return false }
    }
    @discardableResult
    func updateDictionaryEntry(_ entry: DictionaryEntry, preferred: String, variants: String, enabled: Bool) -> Bool {
        guard let processing else { dictionaryMessage = "The dictionary is unavailable."; return false }
        defer { refreshDictionary() }
        do { try processing.updateEntry(id: entry.id, preferred: preferred, variants: variants, enabled: enabled); dictionaryMessage = ""; return true }
        catch { dictionaryMessage = (error as? StoreError)?.description ?? "Could not save the dictionary."; return false }
    }
    private static let dismissedSuggestionsKey = "dictionarySuggestionsDismissed"
    /// Recomputed when the Dictionary page opens and after each answer; spell checks are cached for the launch.
    func refreshDictionarySuggestions() {
        let dismissed = Set(UserDefaults.standard.stringArray(forKey: Self.dismissedSuggestionsKey) ?? [])
        dictionarySuggestions = DictionarySuggestions.find(history.map { (raw: $0.rawText, final: $0.finalText) }, entries: dictionaryEntries,
                                                           dismissed: dismissed, isCommonWord: { [self] word in
            if let known = spellingCache[word] { return known }
            let common = isCommonEnglishWord(word); spellingCache[word] = common; return common
        })
    }
    /// Adds the heard spellings to the entry with this spelling if there is one, else makes a new entry.
    func acceptDictionarySuggestion(_ suggestion: DictionarySuggestion, preferred: String) {
        let spelling = preferred.trimmingCharacters(in: .whitespaces)
        let heard = suggestion.heard.filter { TermText.fold($0) != TermText.fold(spelling) }
        if let entry = dictionaryEntries.first(where: { TermText.fold($0.preferred) == TermText.fold(spelling) }) {
            let variants = entry.variants + heard.filter { word in !entry.variants.contains { TermText.fold($0) == TermText.fold(word) } }
            updateDictionaryEntry(entry, preferred: entry.preferred, variants: variants.joined(separator: ", "), enabled: entry.enabled)
        } else {
            addDictionaryEntry(preferred: spelling, variants: heard.joined(separator: ", "))
        }
        refreshDictionarySuggestions()
    }
    func dismissDictionarySuggestion(_ suggestion: DictionarySuggestion) {
        let dismissed = (UserDefaults.standard.stringArray(forKey: Self.dismissedSuggestionsKey) ?? []) + suggestion.heard.map(TermText.fold)
        UserDefaults.standard.set(dismissed, forKey: Self.dismissedSuggestionsKey)
        refreshDictionarySuggestions()
    }
    func deleteDictionaryEntry(_ entry: DictionaryEntry) {
        do { try processing?.deleteEntry(id: entry.id); dictionaryMessage = "" } catch { dictionaryMessage = "Could not delete this dictionary entry." }
        refreshDictionary()
    }
    func unblockWord(_ item: CorrectionLedger.Blocked) {
        do { try processing?.ledger.unblock(item); dictionaryMessage = "" } catch { dictionaryMessage = "Could not save the learning data." }
        refreshDictionary()
    }
    func resetLearnedEntries() {
        let alert = NSAlert(); alert.messageText = "Reset learned dictionary entries?"
        alert.informativeText = "This removes every learned entry and all learning evidence, and unblocks every word under Not learned. Entries you added stay."
        alert.addButton(withTitle: "Reset Learned"); alert.addButton(withTitle: "Cancel")
        guard alert.runDestructive() == .alertFirstButtonReturn else { return }
        do { try processing?.resetLearned(); dictionaryMessage = "" } catch { dictionaryMessage = "Could not reset learned entries." }
        refreshDictionary()
    }
}

/// The learning lexicon (PRD approved condition): a word is common when the English spell checker
/// finds no misspelling in it, so ordinary-word swaps (lunch→launch) are never learned.
/// CorrectionLedger calls it on the main thread only.
extension ProcessingCoordinator {
    /// The one place the app supplies the lexicon; it lives on the dictionary store from here on.
    static func inApp(directory: URL) throws -> ProcessingCoordinator {
        try ProcessingCoordinator(directory: directory, isCommonWord: isCommonEnglishWord)
    }
}

func isCommonEnglishWord(_ word: String) -> Bool {
    MainActor.assumeIsolated {
        NSSpellChecker.shared.checkSpelling(of: word, startingAt: 0, language: "en", wrap: false, inSpellDocumentWithTag: 0, wordCount: nil).location == NSNotFound
    }
}
