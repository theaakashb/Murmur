import AppKit
import SwiftUI

// The History row's Professional hint. It appears after enough use, on a dictation Professional would visibly
// improve (ProfessionalHint.swift). Without a Murmur Cloud key it explains and offers a trial; with one, a
// preview is a single request the user starts after the disclosure. Nothing is ever sent in the background.

@MainActor final class ProfessionalHint: ObservableObject {
    enum Preview: Equatable { case idle, asking, working, ready(String), unchanged, unavailable(String) }

    static let stateKey = "professionalHintState", enabledKey = "professionalHints"
    /// Professional is in both plans; the trial starts on the lowest-cost one.
    static let trialPlan = "basic"
    static var checkoutURL: URL { MurmurCloud.baseURL.appendingPathComponent("billing/checkout").appending(queryItems: [URLQueryItem(name: "plan", value: trialPlan)]) }

    @Published private(set) var state: ProfessionalHintState
    @Published var preview = Preview.idle
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        state = defaults.data(forKey: Self.stateKey).flatMap { try? JSONDecoder().decode(ProfessionalHintState.self, from: $0) } ?? ProfessionalHintState()
    }

    var enabled: Bool { defaults.object(forKey: Self.enabledKey) as? Bool ?? true }

    private func save() { defaults.set(try? JSONEncoder().encode(state), forKey: Self.stateKey) }

    func consider(entryID: String, raw: String, professionalActive: Bool) {
        state.consider(entryID: entryID, raw: raw, now: Date(), enabled: enabled, professionalActive: professionalActive); save()
    }
    func dismiss() { state.dismissals += 1; close() }
    /// The user previewed or started a trial: the hint has done its job.
    func finish() { state.acted = true; close() }
    private func close() { state.offerID = nil; preview = .idle; save() }
    /// Fixture only: opens an offer without the gate.
    func offer(_ id: String) { state.offerID = id }

    /// One request for one dictation's raw text, only after the user pressed Preview. No screen context, no dictionary terms.
    func requestPreview(raw: String, key: APIKey, options: CleanupOptions) {
        preview = .working
        var o = options; o.polish = true; o.professional = true; o.trustModel = true
        Task { [weak self] in
            let attempt = await ProcessingCoordinator.cloudClean(raw, provider: MurmurCloudCleanupProvider(apiKey: key), options: o, terms: [])
            guard let text = attempt.text, attempt.reason == nil else {
                self?.preview = .unavailable(attempt.error?.userMessage ?? "No preview available for this dictation."); return
            }
            self?.preview = text.trimmingCharacters(in: .whitespacesAndNewlines) == raw.trimmingCharacters(in: .whitespacesAndNewlines) ? .unchanged : .ready(text)
        }
    }
}

struct ProfessionalHintLine: View {
    @ObservedObject var hint: ProfessionalHint
    @ObservedObject var controller: AppController
    let entry: HistoryEntry

    var body: some View {
        if hint.state.offerID == entry.id { card }
    }

    private var key: APIKey? { controller.savedKeys.contains(.murmurCloud) ? ((try? controller.credentials.read(.murmurCloud)) ?? nil) : nil }

    private func note(_ text: String) -> some View {
        Text(text).font(WindowType.body(13)).foregroundStyle(Palette.ink64).fixedSize(horizontal: false, vertical: true)
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(ProfessionalSignal.sentence(ProfessionalSignal.reasons(entry.rawText))).font(WindowType.body(14)).foregroundStyle(Palette.ink80)
                .fixedSize(horizontal: false, vertical: true)
            switch hint.preview {
            case .idle:
                HStack(spacing: 8) {
                    if key == nil {
                        Button(MurmurCloud.getTitle) { NSWorkspace.shared.open(MurmurCloud.getURL); hint.finish() }.buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                    } else {
                        Button("See it in Professional") { hint.preview = .asking }.buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                    }
                    Button("Not now") { hint.dismiss() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }
                if key == nil { note(MurmurCloud.purchaseAvailable ? "Professional runs on Murmur Cloud. A card is required to start a trial; your key arrives by email." : "Professional runs on Murmur Cloud.") }
            case .asking:
                note("This sends this dictation's text to \(MurmurCloud.host) so Murmur Cloud can write the Professional version. Nothing is sent until you press Preview.")
                HStack(spacing: 8) {
                    Button("Preview") { if let key { hint.requestPreview(raw: entry.rawText, key: key, options: controller.processing?.settings.options ?? .standard) } }
                        .buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                    Button("Cancel") { hint.preview = .idle }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }
            case .working:
                note("Asking Murmur Cloud…")
            case .ready(let text):
                note("Professional version")
                Text(text).font(WindowType.body(15)).lineSpacing(3).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                HStack(spacing: 8) {
                    Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string) }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                    Button("Done") { hint.finish() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }
                note("Your history is unchanged. Copy it to use it.")
            case .unchanged:
                note("Professional would leave this one as it is.")
                Button("Close") { hint.finish() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
            case .unavailable(let message):
                note(message)
                Button("Close") { hint.finish() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 12).frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.accent16).padding(.bottom, 12)
    }
}

extension MurmurCloud {
    /// The one place the "get Murmur Cloud" button is defined; the false branch is a placeholder pending the product decision.
    static var getTitle: String { purchaseAvailable ? "Start a trial" : "Get Murmur Cloud" }
    @MainActor static var getURL: URL { purchaseAvailable ? ProfessionalHint.checkoutURL : URL(string: "https://murmurdictation.com")! }
}
