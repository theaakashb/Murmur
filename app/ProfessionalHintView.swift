import SwiftUI

// The History row's Professional hint. It appears after enough use, on a dictation Professional would visibly
// improve (ProfessionalHint.swift), and offers to turn Professional on. Nothing is sent.

@MainActor final class ProfessionalHint: ObservableObject {
    static let stateKey = "professionalHintState", enabledKey = "professionalHints"

    @Published private(set) var state: ProfessionalHintState
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
    /// The user turned Professional on: the hint has done its job.
    func finish() { state.acted = true; close() }
    private func close() { state.offerID = nil; save() }
    /// Fixture only: opens an offer without the gate.
    func offer(_ id: String) { state.offerID = id }
}

struct ProfessionalHintLine: View {
    @ObservedObject var hint: ProfessionalHint
    @ObservedObject var controller: AppController
    let entry: HistoryEntry

    var body: some View {
        if hint.state.offerID == entry.id { card }
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(ProfessionalSignal.sentence(ProfessionalSignal.reasons(entry.rawText))).font(WindowType.body(14)).foregroundStyle(Palette.ink80)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Button("Use Professional") { controller.cleanupStyle = .professional; controller.settingsChanged(); hint.finish() }.buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                Button("Not now") { hint.dismiss() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
            }
            if controller.cleanupChoice != .customAPI { Text("Professional runs on this Mac with the Local model.").font(WindowType.body(13)).foregroundStyle(Palette.ink64).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(.horizontal, 14).padding(.vertical, 12).frame(maxWidth: .infinity, alignment: .leading)
        .background(Palette.accent16).padding(.bottom, 12)
    }
}
