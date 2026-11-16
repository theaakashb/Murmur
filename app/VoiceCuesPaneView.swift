import SwiftUI

// Settings → Writing → Cues (extension SettingsView): the list, add and edit rows, laid out like the Dictionary page.

extension SettingsView {
    var cuesPane: some View {
        VStack(alignment: .leading, spacing: 0) {
            help("Say a little. Insert the rest.").padding(.top, 12)
            help("Say a cue on its own, or say “insert <cue> here” inside a sentence. The text is inserted exactly as you saved it.").padding(.top, 6)
            VStack(alignment: .leading, spacing: 8) {
                field("When I say…", text: $newPhrase).onSubmit(addCue)
                cueEditor($newCueText, label: "Insert…")
                HStack {
                    help("\(newCueText.count) of \(VoiceCueStore.maxTextLength) characters")
                    Spacer()
                    Button("Create cue", action: addCue)
                        .buttonStyle(InkButtonStyle(kind: cueDraftIsEmpty ? .outline : .filled)).fixedSize().disabled(cueDraftIsEmpty)
                }
            }
            .padding(.top, 20)
            if !controller.cueMessage.isEmpty {
                Text(controller.cueMessage).font(Font.system(size: 14, weight: .semibold)).padding(.top, 10)
            }
            VStack(alignment: .leading, spacing: 0) {
                if controller.cues.isEmpty { help("No cues yet. Save a link, an address or a sign-off you say often.").padding(.vertical, 16) }
                ForEach(controller.cues) { cue in
                    Group { if editingCueID == cue.id { cueEditRow(cue) } else { cueRow(cue) } }
                        .frame(minHeight: 56).padding(.vertical, 4).overlay(alignment: .bottom) { softRule }
                }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 20)
            help("Cues stay on this Mac and are never sent to a cleanup model. The dictation in History keeps your words, and its final text has the inserted text.").padding(.top, 16)
        }
    }

    private func cueIsIncomplete(phrase: String, text: String) -> Bool { phrase.isBlank() || text.isBlank(multiline: true) }

    private var cueDraftIsEmpty: Bool { cueIsIncomplete(phrase: newPhrase, text: newCueText) }

    private func addCue() {
        guard !cueDraftIsEmpty else { return }
        if controller.addCue(phrase: newPhrase, text: newCueText) { newPhrase = ""; newCueText = "" }
    }

    private func cueEditor(_ text: Binding<String>, label: String) -> some View {
        TextEditor(text: text).font(WindowType.body(15)).scrollContentBackground(.hidden)
            .frame(height: 88).padding(8).overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
            .accessibilityLabel(label)
            .overlay(alignment: .topLeading) {
                if text.wrappedValue.isEmpty { Text(label).font(WindowType.body(15)).foregroundStyle(Palette.ink64).padding(.horizontal, 13).padding(.vertical, 8).allowsHitTesting(false).accessibilityHidden(true) }
            }
    }

    private func cueRow(_ cue: VoiceCue) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(cue.phrase).font(Font.system(size: 16, weight: .semibold)).lineLimit(1)
                Text(cue.text.replacingOccurrences(of: "\n", with: " ")).font(Font.system(size: 13)).foregroundStyle(Palette.ink70).lineLimit(1)
            }
            Spacer()
            Button("Edit") {
                editCuePhrase = cue.phrase; editCueText = cue.text
                controller.cueMessage = ""; editingCueID = cue.id
            }
            .buttonStyle(.plain).font(Font.system(size: 14, weight: .semibold)).frame(height: 44).accessibilityLabel("Edit \(cue.phrase)")
            Button { controller.deleteCue(cue) } label: {
                Text("×").font(Font.system(size: 18)).frame(width: 44, height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain).help("Delete \(cue.phrase)").accessibilityLabel("Delete \(cue.phrase)")
        }
    }

    private func cueEditRow(_ cue: VoiceCue) -> some View {
        let save = { if controller.updateCue(cue, phrase: editCuePhrase, text: editCueText) { editingCueID = nil } }
        return VStack(alignment: .leading, spacing: 8) {
            field("When I say…", text: $editCuePhrase).onSubmit(save)
            cueEditor($editCueText, label: "Insert…")
            HStack(spacing: 8) {
                help("\(editCueText.count) of \(VoiceCueStore.maxTextLength) characters")
                Spacer()
                Button("Save", action: save).buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                    .disabled(cueIsIncomplete(phrase: editCuePhrase, text: editCueText))
                Button("Cancel") { controller.cueMessage = ""; editingCueID = nil }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(.vertical, 8)
    }
}
