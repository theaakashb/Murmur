import AppKit
import SwiftUI

// "Report an Issue": the sheet that shows the diagnostics text for editing, and the window that holds it. The app makes no network request; the mail app or browser does.

struct ReportIssueView: View {
    @State var text: String
    /// The display name of the dictation shortcut.
    var shortcut: String
    var open: (URL) -> Void
    var close: () -> Void
    @State private var copied = false
    @State private var selection: TextSelection?
    @FocusState private var editing: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Report an Issue").font(WindowType.body(20, weight: .bold))
            Text("Describe what happened. Dictate it with your Murmur shortcut (\(shortcut)) or type it. Only the text is added to the report, never audio, and you can read and edit it here.")
                .font(WindowType.body(14, weight: .semibold)).fixedSize(horizontal: false, vertical: true)
            Text("Apart from what you write, this text is everything in the report: no API keys, dictionary entries, cues, endpoint addresses or app names. Edit it as you like. “Email Report” opens your mail app with it addressed to \(IssueReport.email); nothing is sent until you send that email. No mail app? Copy the text and send it to that address. “Open GitHub Issue” needs a GitHub account and is public; GitHub receives the text as soon as the page loads. Murmur itself sends nothing.")
                .font(WindowType.body(14)).lineSpacing(2).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            TextEditor(text: $text, selection: $selection).focused($editing).font(.system(size: 12, design: .monospaced)).scrollContentBackground(.hidden)
                .padding(8).frame(height: 330).background(Palette.ink20.opacity(0.25), in: RoundedRectangle(cornerRadius: 6))
            HStack {
                Button(copied ? "Copied" : "Copy Diagnostics") {
                    NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string); copied = true
                }.buttonStyle(InkButtonStyle(kind: .outline))
                Spacer()
                Button("Close", action: close).buttonStyle(InkButtonStyle(kind: .plain)).keyboardShortcut(.cancelAction)
                Button("Open GitHub Issue") { open(IssueReport.url(body: text)) }.buttonStyle(InkButtonStyle(kind: .plain))
                Button("Email Report") { open(IssueReport.emailURL(body: text)) }.buttonStyle(InkButtonStyle(kind: .filled)).keyboardShortcut(.defaultAction)
            }
        }
        .padding(24).frame(width: 560).foregroundStyle(Palette.ink).background(Palette.paper)
        .onChange(of: text) { copied = false }
        // The empty line after "What happened:", where a dictation lands.
        .onAppear {
            if let newline = text.firstIndex(of: "\n") { selection = TextSelection(insertionPoint: text.index(after: newline)) }
            editing = true
        }
    }
}

@MainActor final class ReportIssuePresenter {
    private(set) var window: NSWindow?

    func show(_ diagnostics: Diagnostics, shortcut: String) {
        window?.close()
        let next = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        next.title = "Report an Issue"; next.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: ReportIssueView(text: diagnostics.text, shortcut: shortcut, open: { NSWorkspace.shared.open($0) }, close: { [weak next] in next?.close() }))
        next.contentView = host
        next.setContentSize(host.fittingSize); next.center()
        window = next
        NSApp.activate(ignoringOtherApps: true); next.makeKeyAndOrderFront(nil)
    }
}
