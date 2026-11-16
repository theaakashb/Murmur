// AppController: voice cue editing. Edits apply immediately; cueMessage reports the last edit error.
import Foundation

extension AppController {
    /// Re-reads the list the pane shows; the store itself is read at each dictation.
    func refreshCues() {
        cues = processing?.cues.cues ?? []
        cueMessage = processing?.cues.unreadable == true ? "Cues could not be read. They are off until the file is repaired or deleted." : ""
    }
    @discardableResult
    func addCue(phrase: String, text: String) -> Bool { change { try $0.add(phrase: phrase, text: text) } }
    @discardableResult
    func updateCue(_ cue: VoiceCue, phrase: String, text: String) -> Bool { change { try $0.update(id: cue.id, phrase: phrase, text: text) } }
    func deleteCue(_ cue: VoiceCue) { change { try $0.delete(id: cue.id) } }

    @discardableResult
    private func change(_ edit: (VoiceCueStore) throws -> Void) -> Bool {
        guard let store = processing?.cues else { cueMessage = "Cues are unavailable."; return false }
        defer { cues = store.cues }
        do { try edit(store); cueMessage = ""; return true }
        catch { cueMessage = (error as? StoreError)?.description ?? "Could not save cues."; return false }
    }
}
