// Reading the focused app through Accessibility for writing modes and screen context.
import AppKit
import ApplicationServices

private func axElement(_ element: AXUIElement, _ name: String) -> AXUIElement? {
    guard let raw = axAttribute(element, name), CFGetTypeID(raw) == AXUIElementGetTypeID() else { return nil }
    return unsafeBitCast(raw, to: AXUIElement.self)
}
private func axChildren(_ element: AXUIElement) -> [AXUIElement] {
    guard let raw = axAttribute(element, kAXChildrenAttribute), CFGetTypeID(raw) == CFArrayGetTypeID() else { return [] }
    return (raw as? [AnyObject] ?? []).compactMap { CFGetTypeID($0) == AXUIElementGetTypeID() ? unsafeBitCast($0, to: AXUIElement.self) : nil }
}

/// The page URL in a known browser, from its web area's AXURL: first up from the focused field,
/// then a short search of the focused window. Never forces accessibility modes on the browser.
/// The result is for mode matching only; callers never store or log it.
func focusedPageURL(pid: pid_t, from element: AXUIElement?) -> URL? {
    guard let bundle = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier, browserBundleIDs.contains(bundle) else { return nil }
    let app = AXUIElementCreateApplication(pid)
    AXUIElementSetMessagingTimeout(app, 0.25)
    func pageURL(_ e: AXUIElement) -> URL? {
        guard axAttribute(e, kAXRoleAttribute) as? String == "AXWebArea", let raw = axAttribute(e, kAXURLAttribute),
              CFGetTypeID(raw) == CFURLGetTypeID() else { return nil }
        return unsafeBitCast(raw, to: CFURL.self) as URL
    }
    // The messaging timeout covers only the app element, so a time budget bounds the whole search.
    let deadline = ProcessInfo.processInfo.systemUptime + 0.3
    var current = element
    for _ in 0..<40 {
        guard let e = current, ProcessInfo.processInfo.systemUptime < deadline else { break }
        if let url = pageURL(e) { return url }
        current = axElement(e, kAXParentAttribute)
    }
    // ponytail: breadth-first, 400 nodes; a page whose web area sits deeper is not matched.
    var queue = axElement(app, kAXFocusedWindowAttribute).map { [$0] } ?? [], seen = 0
    while !queue.isEmpty, seen < 400, ProcessInfo.processInfo.systemUptime < deadline {
        let e = queue.removeFirst(); seen += 1
        if let url = pageURL(e) { return url }
        queue += axChildren(e)
    }
    return nil
}

/// The focused field's selected text, unless it is a secure field.
func focusedSelectedText(_ element: AXUIElement) -> String? {
    axSecure(element) ? nil : axAttribute(element, kAXSelectedTextAttribute) as? String
}

/// Visible text of the app's focused window through Accessibility, up to `limit` characters,
/// skipping secure fields. Bounded by node count and time so a large page cannot stall a dictation.
func focusedWindowText(pid: pid_t, limit: Int) -> String {
    let app = AXUIElementCreateApplication(pid)
    AXUIElementSetMessagingTimeout(app, 0.25)
    guard let window = axElement(app, kAXFocusedWindowAttribute) else { return "" }
    let deadline = ProcessInfo.processInfo.systemUptime + 0.5
    var queue = [window], seen = 0, out = ""
    while !queue.isEmpty, seen < 2_000, out.count < limit, ProcessInfo.processInfo.systemUptime < deadline {
        let e = queue.removeFirst(); seen += 1
        if axSecure(e) { continue }
        let role = axAttribute(e, kAXRoleAttribute) as? String
        if ["AXStaticText", "AXTextField", "AXTextArea", "AXHeading"].contains(role ?? ""),
           let text = axAttribute(e, kAXValueAttribute) as? String, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            out += (out.isEmpty ? "" : "\n") + text
        }
        queue += axChildren(e)
    }
    return String(out.prefix(limit))
}
