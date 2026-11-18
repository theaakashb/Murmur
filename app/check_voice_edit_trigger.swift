import Foundation
import ApplicationServices
import Carbon.HIToolbox

// Voice edit's trigger: the selection rule, the separate shortcut's tap behaviour on synthetic key events (nothing is posted),
// and which keys it may use. macOS only.

private var failures = 0
private func check(_ condition: @autoclosure () -> Bool, _ label: String, line: Int = #line) {
    if !condition() { failures += 1; print("FAIL [line \(line)]: \(label)") }
}

private func key(_ code: Int, down: Bool, flags: CGEventFlags, stamp: UInt64, repeats: Bool = false) -> CGEvent {
    let e = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(code), keyDown: down)!
    e.flags = flags; e.timestamp = stamp
    e.setIntegerValueField(.keyboardEventAutorepeat, value: repeats ? 1 : 0)
    return e
}

@main enum Main {
    static func main() {
        // The selection rule shared by the dictation key and a separate shortcut.
        check(EditTrigger.capturesKey(secureInput: false, selection: "some words"), "a readable selection starts voice edit")
        check(!EditTrigger.capturesKey(secureInput: false, selection: nil), "an unreadable selection is left alone: the key dictates or passes through")
        check(!EditTrigger.capturesKey(secureInput: false, selection: ""), "an empty selection is left alone")
        check(!EditTrigger.capturesKey(secureInput: false, selection: " \n\t"), "a whitespace selection is left alone")
        check(!EditTrigger.capturesKey(secureInput: true, selection: "some words"), "Secure Input is left alone even with a selection")

        // ⌃E as the separate shortcut: end-of-line must keep working without a selection.
        let ctrlE = CustomShortcut(keyCode: Int64(kVK_ANSI_E), modifiers: .maskControl, key: "E")
        var selected = false
        let listener = ShortcutListener(binding: .rightOption, processingSeconds: 0, onAction: nil)
        listener.editBinding = ctrlE
        listener.editSelectionProbe = { EditTrigger.capturesKey(secureInput: false, selection: selected ? "text" : nil) }
        var presses = 0, releases = 0
        listener.onEditPress = { presses += 1 }; listener.onEditRelease = { releases += 1 }
        let E = kVK_ANSI_E

        check(!listener.consumes(.keyDown, key(E, down: true, flags: .maskControl, stamp: 1)), "⌃E with no selection passes through")
        check(!listener.consumes(.keyUp, key(E, down: false, flags: .maskControl, stamp: 2)), "and so does its release")
        listener.receive(type: .keyDown, event: key(E, down: true, flags: .maskControl, stamp: 1))
        check(presses == 0, "a passed-through ⌃E never reaches voice edit")

        selected = true
        check(listener.consumes(.keyDown, key(E, down: true, flags: .maskControl, stamp: 10)), "⌃E with a selection is captured")
        check(listener.consumes(.keyDown, key(E, down: true, flags: .maskControl, stamp: 11, repeats: true)), "its autorepeat is captured")
        check(listener.consumes(.keyUp, key(E, down: false, flags: [], stamp: 12)), "its release is captured even after Control is let go")
        listener.receive(type: .keyDown, event: key(E, down: true, flags: .maskControl, stamp: 10))
        listener.receive(type: .keyDown, event: key(E, down: true, flags: .maskControl, stamp: 11, repeats: true))
        listener.receive(type: .keyUp, event: key(E, down: false, flags: [], stamp: 12))
        check(presses == 1 && releases == 1, "voice edit sees one press and one release: \(presses) \(releases)")
        check(!listener.consumes(.keyUp, key(E, down: false, flags: [], stamp: 13)), "a later release with nothing captured passes")

        check(!listener.consumes(.keyDown, key(E, down: true, flags: [.maskControl, .maskAlternate], stamp: 20)), "⌃⌥E is not ⌃E and passes through")
        check(!listener.consumes(.keyDown, key(kVK_ANSI_R, down: true, flags: .maskControl, stamp: 21)), "another key passes through")

        listener.editSelectionProbe = { EditTrigger.capturesKey(secureInput: true, selection: "text") }
        check(!listener.consumes(.keyDown, key(E, down: true, flags: .maskControl, stamp: 30)), "under Secure Input ⌃E passes through even with a selection")

        // Which keys can be the separate shortcut.
        let dictation = CustomShortcut(keyCode: Int64(kVK_ANSI_D), modifiers: [.maskControl, .maskAlternate], key: "D").stored
        func refusal(_ code: Int, _ flags: CGEventFlags, _ dictation: String = dictation) -> String? { CustomShortcut.editRefusal(keyCode: Int64(code), modifiers: flags, dictation: dictation) }
        check(refusal(kVK_ANSI_E, .maskControl) == nil, "⌃E is allowed")
        check(refusal(kVK_ANSI_D, [.maskControl, .maskAlternate]) != nil, "the dictation shortcut is refused")
        check(refusal(kVK_Space, [.maskControl, .maskAlternate], ShortcutBinding.controlOptionSpace == .controlOptionSpace ? "ctrl-option-space" : "") != nil, "the ⌃⌥Space preset is refused")
        check(refusal(kVK_ANSI_Z, [.maskControl, .maskCommand]) != nil, "⌃⌘Z is refused")
        check(refusal(kVK_ANSI_V, [.maskControl, .maskCommand]) != nil && refusal(kVK_ANSI_C, [.maskControl, .maskCommand]) != nil, "⌃⌘V and ⌃⌘C are refused")
        check(refusal(kVK_RightOption, []) != nil && refusal(kVK_ANSI_E, []) != nil, "a modifier alone or a bare letter is refused")

    if failures == 0 { print("voice edit trigger checks passed") } else { print("\(failures) failed"); exit(1) }

    }
}
