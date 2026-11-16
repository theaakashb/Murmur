import AppKit
import SwiftUI

// "Report an Issue": the sheet with a box for the description and the diagnostics in an editable details section, and the window that holds it. The app makes no network request; the mail app or browser does.

struct ReportIssueView: View {
    @State var details: String
    /// The display name of the dictation shortcut.
    var shortcut: String
    var open: (URL) -> Void
    var close: () -> Void
    /// Asks the window to fit the content again after the details section opens or closes.
    var refit: () -> Void = {}
    @State var expanded = false
    @State private var description = ""
    @State private var copied = false
    @FocusState private var editing: Bool

    private var report: String { IssueReport.body(description: description, details: details) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Report an Issue").font(WindowType.body(20, weight: .bold))
            Text("Describe what happened below, by dictating or typing. Only text is added to the report, never audio.")
                .font(WindowType.body(14, weight: .semibold)).fixedSize(horizontal: false, vertical: true)
            Text("“Email Report” opens your mail app addressed to \(IssueReport.email); nothing is sent until you send it. “Open GitHub Issue” needs a GitHub account and is public.")
                .font(WindowType.body(14)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
            TextEditor(text: $description).focused($editing).font(WindowType.body(14)).scrollContentBackground(.hidden)
                .padding(8).frame(height: 130).background(Palette.ink20.opacity(0.25), in: RoundedRectangle(cornerRadius: 6))
                .overlay(alignment: .topLeading) {
                    if description.isEmpty { Text("What happened? Dictate with \(shortcut) or type.").font(WindowType.body(14)).foregroundStyle(Palette.ink70).padding(.horizontal, 13).padding(.vertical, 8).allowsHitTesting(false) }
                }
            DisclosureGroup(isExpanded: $expanded) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("This text is added below your description. Edit it or delete a line before sending. No mail app? Copy the report and send it to \(IssueReport.email). GitHub receives the report as soon as the page loads. Murmur itself sends nothing.")
                        .font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
                    TextEditor(text: $details).font(.system(size: 12, design: .monospaced)).scrollContentBackground(.hidden)
                        .padding(8).frame(height: 190).background(Palette.ink20.opacity(0.25), in: RoundedRectangle(cornerRadius: 6))
                }.padding(.top, 8)
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Details sent with your report").font(WindowType.body(14, weight: .semibold))
                    Text("Murmur version, macOS version, permissions, routes and models; no dictated text, audio, keys, dictionary, cues or app names").font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack {
                Button(copied ? "Copied" : "Copy Report") {
                    NSPasteboard.general.clearContents(); NSPasteboard.general.setString(report, forType: .string); copied = true
                }.buttonStyle(InkButtonStyle(kind: .outline))
                Spacer()
                Button("Close", action: close).buttonStyle(InkButtonStyle(kind: .plain)).keyboardShortcut(.cancelAction)
                Button("Open GitHub Issue") { open(IssueReport.url(body: report)) }.buttonStyle(InkButtonStyle(kind: .plain))
                Button("Email Report") { open(IssueReport.emailURL(body: report)) }.buttonStyle(InkButtonStyle(kind: .filled)).keyboardShortcut(.defaultAction)
            }
        }
        .padding(24).frame(width: 560).foregroundStyle(Palette.ink).background(Palette.paper)
        .onChange(of: description) { copied = false }
        .onChange(of: details) { copied = false }
        .onChange(of: expanded) { refit() }
        .onAppear { editing = true }
    }
}

@MainActor final class ReportIssuePresenter {
    private(set) var window: NSWindow?

    func show(_ diagnostics: Diagnostics, shortcut: String, expanded: Bool = false) {
        window?.close()
        let next = NSWindow(contentRect: .zero, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        next.title = "Report an Issue"; next.isReleasedWhenClosed = false
        var host: NSHostingView<ReportIssueView>!
        let refit = { [weak next] in
            // After the layout pass: grow or shrink downward, keeping the top edge.
            DispatchQueue.main.async {
                guard let next else { return }
                var frame = next.frame
                let top = frame.maxY
                frame.size.height += host.fittingSize.height - next.contentLayoutRect.height; frame.origin.y = top - frame.height
                if let visible = next.screen?.visibleFrame, frame.minY < visible.minY { frame.origin.y = visible.minY }
                next.setFrame(frame, display: true, animate: false)
            }
        }
        host = NSHostingView(rootView: ReportIssueView(details: diagnostics.details, shortcut: shortcut, open: { NSWorkspace.shared.open($0) }, close: { [weak next] in next?.close() }, refit: refit, expanded: expanded))
        next.contentView = host
        next.setContentSize(host.fittingSize); next.center()
        window = next
        NSApp.activate(ignoringOtherApps: true); next.makeKeyAndOrderFront(nil)
    }
}
