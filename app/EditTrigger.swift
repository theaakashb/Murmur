import Foundation

// Voice edit's trigger rule, kept pure so it is checked without a keyboard.

enum EditTrigger {
    /// Voice edit takes the key only for a non-empty, readable selection outside Secure Input. Anything else is left alone:
    /// the dictation key dictates as before, and a text-editing key such as ⌃E (end of line) passes through.
    static func capturesKey(secureInput: Bool, selection: String?) -> Bool {
        !secureInput && !(selection?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
    }

    /// The setting's value for "same key as dictation, when text is selected".
    static let sameKey = "same"
}
