import ApplicationServices
import Carbon
import Foundation

// Spike-only. Also compiled into app/check_controller.swift (see app/check.sh), which is where these
// assertions cover the app's insertion helpers.

extension FocusSnapshot {
    var learningEligible: Bool { readableText && !browserTypeUnknown }
}

func runInsertionChecks() {
    let steps = typingSteps("ab\ncd\t😀e\r\nf", limit: 2)
    assert(steps == [.text("ab"), .key(kVK_Return), .text("cd"), .key(kVK_Tab), .text("😀"), .text("e"), .key(kVK_Return), .text("f")])
    let long = typingSteps(String(repeating: "é😀", count: 50))
    assert(long.allSatisfy { if case .text(let t) = $0 { return t.utf16.count <= 16 } else { return false } })
    assert(long.map { if case .text(let t) = $0 { return t } else { return "" } }.joined() == String(repeating: "é😀", count: 50))
    assert(typingFallbackAllowed(afterPasteRefusal: "Clipboard changed before staging; no paste attempted"))
    assert(!typingFallbackAllowed(afterPasteRefusal: "Destination changed after clipboard staging; no paste attempted"))
    let fixture = FileManager.default.temporaryDirectory.appendingPathComponent("chromium-check-\(UUID().uuidString).app")
    defer { try? FileManager.default.removeItem(at: fixture) }
    let helper = fixture.appendingPathComponent("Contents/Frameworks/X Framework.framework/Versions/1/Helpers/X (Renderer).app")
    try? FileManager.default.createDirectory(at: helper, withIntermediateDirectories: true)
    assert(bundleHasChromiumHelpers(fixture) && !bundleHasChromiumHelpers(fixture.appendingPathComponent("Contents")))
    assert(readbackMatches("Hi there.", "Hi\u{00A0}there.\n") && readbackMatches(" a b", "a\u{200B} b"))
    assert(!readbackMatches("Hi there.", "Hi there. Hi there.") && !readbackMatches("ab", nil) && !readbackMatches(nil, "ab"))
    let element = AXUIElementCreateApplication(123)
    func destination(pid: pid_t = 123, bundleID: String = "com.apple.TextEdit", value: String = "abc",
                     selection: CFRange = CFRange(location: 3, length: 0), secure: Bool = false) -> FocusSnapshot {
        FocusSnapshot(element: element, pid: pid, bundleID: bundleID, role: kAXTextFieldRole,
                      subrole: "unknown", value: value, selection: selection, secure: secure, browserTypeUnknown: false)
    }
    assert(sameDestination(destination(), destination()))
    assert(!sameDestination(destination(), destination(pid: 124)))
    assert(!sameDestination(destination(), destination(bundleID: "other")))
    assert(!sameDestination(destination(), destination(value: "abcd")))
    assert(!sameDestination(destination(), destination(selection: CFRange(location: 2, length: 0))))
    assert(!sameDestination(destination(), destination(secure: true)))
    assert(insertionTextRole(kAXTextFieldRole))
    assert(insertionTextRole(kAXTextAreaRole))
    assert(!insertionTextRole("AXWebArea"))
    assert(!insertionTextRole("AXDocument"))
    assert(!insertionTextRole("AXComboBox"))
    assert(clipboardOwnershipVerified(expectedCount: 4, beforeCount: 4, afterCount: 4, expectedToken: "ours", actualToken: "ours"))
    assert(!clipboardOwnershipVerified(expectedCount: 4, beforeCount: 4, afterCount: 4, expectedToken: "ours", actualToken: "other"))
    assert(!clipboardOwnershipVerified(expectedCount: 4, beforeCount: 4, afterCount: 4, expectedToken: "ours", actualToken: nil))
    assert(!clipboardOwnershipVerified(expectedCount: 4, beforeCount: 5, afterCount: 5, expectedToken: "ours", actualToken: "ours"))
    assert(!clipboardOwnershipVerified(expectedCount: 4, beforeCount: 4, afterCount: 5, expectedToken: "ours", actualToken: "ours"))
    assert(insertionReadbackVerified(expected: "text", actual: "text", sameField: true, browserTypeUnknown: false))
    assert(!insertionReadbackVerified(expected: "text", actual: "text", sameField: true, browserTypeUnknown: true))
    assert(!insertionReadbackVerified(expected: "text", actual: "text", sameField: false, browserTypeUnknown: false))
    assert(!insertionReadbackVerified(expected: "text", actual: "old", sameField: true, browserTypeUnknown: false))
    assert(!insertionReadbackVerified(expected: nil, actual: nil, sameField: true, browserTypeUnknown: false))
    assert(expectedInsertedValue("A😀B", selection: CFRange(location: 1, length: 2), text: "X") == "AXB")
    assert(expectedInsertedValue("A😀B", selection: CFRange(location: 2, length: 0), text: "X") == nil)
    assert(expectedInsertedValue("abc", selection: CFRange(location: 3, length: 0), text: "!") == "abc!")
    assert(expectedInsertedValue("abc", selection: CFRange(location: 4, length: 0), text: "x") == nil)
    assert(expectedInsertedValue("abc", selection: CFRange(location: 2, length: Int.max), text: "x") == nil)
    assert(expectedInsertedValue(nil, selection: CFRange(location: 0, length: 0), text: "x") == nil)
}
