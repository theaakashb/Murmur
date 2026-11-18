import SwiftUI

extension AppController {
    /// Why the dictation shortcut can't be this key: it is the separate voice edit shortcut.
    func voiceEditConflict(keyCode: Int64, modifiers: CGEventFlags) -> String? {
        ShortcutBinding(stored: voiceEditShortcut).matches(keyCode: keyCode, modifiers: modifiers) && voiceEditShortcut != EditTrigger.sameKey
            ? "That is your voice edit shortcut. Pick another key." : nil
    }
}

/// Settings → Dictation: how voice edit is started. Default is the dictation key itself, when text is selected.
struct VoiceEditShortcutSettings: View {
    @ObservedObject var controller: AppController
    @State private var separate = false
    private var isSeparate: Bool { separate || controller.voiceEditShortcut != EditTrigger.sameKey }
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                Text("Voice edit shortcut").font(.system(size: 13))
                Spacer()
                Picker("Voice edit shortcut", selection: Binding(
                    get: { isSeparate },
                    set: { separate = $0; if !$0 { controller.voiceEditShortcut = EditTrigger.sameKey } })) {
                    Text("Same as dictation key, when text is selected").tag(false)
                    Text("A separate shortcut").tag(true)
                }.labelsHidden().frame(minWidth: 340, maxWidth: 360)
            }
            if isSeparate {
                ShortcutRecorder(controller: controller, target: \.voiceEditShortcut, resetTo: EditTrigger.sameKey)
                Text("A key that also edits text, such as ⌃E, is used only when text is selected. Otherwise it works as usual.")
                    .font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
            }
            Text("Select text, press the shortcut and say how to change it. To replace selected text by dictation, delete it first, or pick a separate voice edit shortcut. Works with Apple Intelligence (macOS 26) or your own API key; pasting the result makes rich text plain.")
                .font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
        }.padding(.bottom, 12)
    }
}
