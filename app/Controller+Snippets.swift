// AppController: voice snippet editing. Edits apply immediately; snippetMessage reports the last edit error.
import Foundation

extension AppController {
    /// Re-reads the list the pane shows; the store itself is read at each dictation.
    func refreshSnippets() {
        snippets = processing?.snippets.snippets ?? []
        snippetMessage = processing?.snippets.unreadable == true ? "Snippets could not be read. They are off until the file is repaired or deleted." : ""
    }
    @discardableResult
    func addSnippet(cue: String, text: String) -> Bool { change { try $0.add(cue: cue, text: text) } }
    @discardableResult
    func updateSnippet(_ snippet: Snippet, cue: String, text: String) -> Bool { change { try $0.update(id: snippet.id, cue: cue, text: text) } }
    func deleteSnippet(_ snippet: Snippet) { change { try $0.delete(id: snippet.id) } }

    @discardableResult
    private func change(_ edit: (SnippetStore) throws -> Void) -> Bool {
        guard let store = processing?.snippets else { snippetMessage = "Snippets are unavailable."; return false }
        defer { snippets = store.snippets }
        do { try edit(store); snippetMessage = ""; return true }
        catch { snippetMessage = (error as? DictionaryError)?.description ?? "Could not save snippets."; return false }
    }
}
