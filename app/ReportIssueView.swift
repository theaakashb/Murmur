import AppKit
import SwiftUI

// "Report an Issue": the sheet that shows the diagnostics text for editing, and the window that holds it. The app makes no network request; the mail app or browser does.

struct ReportIssueView: View {
    @State var text: String
    var open: (URL) -> Void
    var close: () -> Void
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Report an Issue").font(WindowType.body(20, weight: .bold))
            Text("This text is everything in the report: no dictated text, audio, API keys, dictionary entries, snippets, endpoint addresses or app names. Edit it as you like. “Email Report” opens your mail app with it addressed to \(IssueReport.email); nothing is sent until you send that email. No mail app? Copy the text and send it to that address. “Open GitHub Issue” needs a GitHub account and is public; GitHub receives the text as soon as the page loads. Murmur itself sends nothing.")
                .font(WindowType.body(14)).lineSpacing(2).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
            TextEditor(text: $text).font(.system(size: 12, design: .monospaced)).scrollContentBackground(.hidden)
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
    }
}

@MainActor final class ReportIssuePresenter {
    private(set) var window: NSWindow?

    func show(_ diagnostics: Diagnostics) {
        window?.close()
        let next = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        next.title = "Report an Issue"; next.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: ReportIssueView(text: diagnostics.text, open: { NSWorkspace.shared.open($0) }, close: { [weak next] in next?.close() }))
        next.contentView = host
        next.setContentSize(host.fittingSize); next.center()
        window = next
        NSApp.activate(ignoringOtherApps: true); next.makeKeyAndOrderFront(nil)
    }
}
