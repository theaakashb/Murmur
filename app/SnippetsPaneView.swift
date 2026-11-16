import SwiftUI

// Settings → Writing → Snippets (extension SettingsView): the list, add and edit rows, laid out like the Dictionary page.

extension SettingsView {
    var snippetsPane: some View {
        VStack(alignment: .leading, spacing: 0) {
            help("Say the cue on its own, or say “insert <cue> here” inside a sentence. The text is inserted exactly as you saved it.").padding(.top, 12)
            VStack(alignment: .leading, spacing: 8) {
                field("When I say", text: $newCue).onSubmit(addSnippet)
                snippetEditor($newSnippetText, label: "Insert this text")
                HStack {
                    help("\(newSnippetText.count) of \(SnippetStore.maxTextLength) characters")
                    Spacer()
                    Button("Add", action: addSnippet)
                        .buttonStyle(InkButtonStyle(kind: snippetDraftIsEmpty ? .outline : .filled)).fixedSize().disabled(snippetDraftIsEmpty)
                }
            }
            .padding(.top, 20)
            if !controller.snippetMessage.isEmpty {
                Text(controller.snippetMessage).font(Font.system(size: 14, weight: .semibold)).padding(.top, 10)
            }
            VStack(alignment: .leading, spacing: 0) {
                if controller.snippets.isEmpty { help("No snippets yet. Save a link, an address or a sign-off you say often.").padding(.vertical, 16) }
                ForEach(controller.snippets) { snippet in
                    Group { if editingSnippetID == snippet.id { snippetEditRow(snippet) } else { snippetRow(snippet) } }
                        .frame(minHeight: 56).padding(.vertical, 4).overlay(alignment: .bottom) { softRule }
                }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 20)
            help("Snippets stay on this Mac and are never sent to a cleanup model. The dictation in History keeps your words, and its final text has the snippet.").padding(.top, 16)
        }
    }

    private func snippetIsIncomplete(cue: String, text: String) -> Bool { cue.isBlank() || text.isBlank(multiline: true) }

    private var snippetDraftIsEmpty: Bool { snippetIsIncomplete(cue: newCue, text: newSnippetText) }

    private func addSnippet() {
        guard !snippetDraftIsEmpty else { return }
        if controller.addSnippet(cue: newCue, text: newSnippetText) { newCue = ""; newSnippetText = "" }
    }

    private func snippetEditor(_ text: Binding<String>, label: String) -> some View {
        TextEditor(text: text).font(WindowType.body(15)).scrollContentBackground(.hidden)
            .frame(height: 88).padding(8).overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
            .accessibilityLabel(label)
            .overlay(alignment: .topLeading) {
                if text.wrappedValue.isEmpty { Text(label).font(WindowType.body(15)).foregroundStyle(Palette.ink64).padding(.horizontal, 13).padding(.vertical, 8).allowsHitTesting(false).accessibilityHidden(true) }
            }
    }

    private func snippetRow(_ snippet: Snippet) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(snippet.cue).font(Font.system(size: 16, weight: .semibold)).lineLimit(1)
                Text(snippet.text.replacingOccurrences(of: "\n", with: " ")).font(Font.system(size: 13)).foregroundStyle(Palette.ink70).lineLimit(1)
            }
            Spacer()
            Button("Edit") {
                editSnippetCue = snippet.cue; editSnippetText = snippet.text
                controller.snippetMessage = ""; editingSnippetID = snippet.id
            }
            .buttonStyle(.plain).font(Font.system(size: 14, weight: .semibold)).frame(height: 44).accessibilityLabel("Edit \(snippet.cue)")
            Button { controller.deleteSnippet(snippet) } label: {
                Text("×").font(Font.system(size: 18)).frame(width: 44, height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain).help("Delete \(snippet.cue)").accessibilityLabel("Delete \(snippet.cue)")
        }
    }

    private func snippetEditRow(_ snippet: Snippet) -> some View {
        let save = { if controller.updateSnippet(snippet, cue: editSnippetCue, text: editSnippetText) { editingSnippetID = nil } }
        return VStack(alignment: .leading, spacing: 8) {
            field("When I say", text: $editSnippetCue).onSubmit(save)
            snippetEditor($editSnippetText, label: "Insert this text")
            HStack(spacing: 8) {
                help("\(editSnippetText.count) of \(SnippetStore.maxTextLength) characters")
                Spacer()
                Button("Save", action: save).buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                    .disabled(snippetIsIncomplete(cue: editSnippetCue, text: editSnippetText))
                Button("Cancel") { controller.snippetMessage = ""; editingSnippetID = nil }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(.vertical, 8)
    }
}
