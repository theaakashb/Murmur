import Foundation
import Carbon.HIToolbox

// Which keys can be voice edit's separate shortcut. Needs only Shortcut.swift, so it is checked on its own.

extension CustomShortcut {
    /// Why a key can't be the separate voice edit shortcut: whatever can't be a dictation shortcut, the dictation key itself,
    /// and the chords Murmur already keeps (⌃⌘C, ⌃⌘V, ⌃⌘Z). A plain text-editing key such as ⌃E is allowed: it is captured only on a selection.
    static func editRefusal(keyCode: Int64, modifiers: CGEventFlags, dictation: String) -> String? {
        if let reason = refusal(keyCode: keyCode, modifiers: modifiers) { return reason }
        let flags = modifiers.intersection(relevant)
        if keyCode == Int64(kVK_ANSI_Z) && flags == [.maskControl, .maskCommand] { return "⌃⌘Z undoes a canceled dictation." }
        if ShortcutBinding(stored: dictation).matches(keyCode: keyCode, modifiers: flags) { return "That is your dictation shortcut. Pick another key." }
        return nil
    }
}

extension ShortcutBinding {
    /// This binding is the same key with the same modifiers.
    func matches(keyCode code: Int64, modifiers flags: CGEventFlags) -> Bool {
        switch self {
        case .rightOption, .fn: return false // Modifier keys on their own can't be recorded as a voice edit shortcut.
        case .controlOptionSpace: return code == keyCode && flags == [.maskControl, .maskAlternate]
        case .custom(let shortcut): return code == shortcut.keyCode && flags.intersection(CustomShortcut.relevant) == shortcut.modifiers
        }
    }
}
