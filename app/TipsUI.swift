import AppKit
import SwiftUI

// Milestones and the voice-edit tip in their own floating panel at the bubble's spot, in the call offer's
// card with an accent glow; the Home milestone card; the settings pane intros. Rules live in Tips.swift.

@MainActor final class TipPresenter: ObservableObject {
    enum Card: Equatable { case milestone(Tips.Milestone), voiceEdit
        var id: Tips.ID { if case .milestone = self { return .milestone }; return .voiceEdit }
        /// Milestones are news and leave on their own; the voice-edit tip waits like the call offer.
        var lifetime: TimeInterval { if case .milestone = self { return 8 }; return CallDetector.offerLifetime }
    }
    @Published private(set) var card: Card?
    @Published var enabled = Tips.enabled() {
        didSet { Tips.setEnabled(enabled); if !enabled { pending = nil; hide() }; changed() }
    }
    private var pending: Card?
    private var shownAt: TimeInterval = 0
    private(set) weak var controller: AppController?
    private let panel = FloatingPanel(title: "Murmur tip")

    init(controller: AppController) { self.controller = controller }

    /// Settings re-renders on every voice-level tick, so dots are computed here on change, not per render.
    @Published private(set) var dots: Set<SettingsPane> = []
    private var intros: [SettingsPane] = []
    /// Settings and Home observe the controller, so a ledger change re-renders them through it.
    func changed() {
        let usage = usage
        intros = Tips.eligible(usage); dots = Tips.dots(usage)
        objectWillChange.send(); controller?.objectWillChange.send()
    }
    /// Delete All Data: nothing queued from before survives, and the switch shows the default again.
    func reset() { pending = nil; hide(); enabled = true; changed() }

    /// One inserted dictation: counts its words and queues a milestone when it crosses one.
    func recordDictation(final: String, seconds: Double?) {
        guard let controller else { return }
        let seed: () -> (words: Int, secondsSaved: Double, firstUse: Date?) = {
            if case .stats(let s) = controller.insights { return (s.words, s.secondsSaved ?? 0, s.days.min()) }
            return (0, 0, nil)
        }
        let milestone = Tips.record(words: Insights.words(final).count, secondsSaved: Tips.secondsSaved(final: final, seconds: seconds), now: Date(), seed: seed)
        changed()
        guard let milestone else { return }
        queue(.milestone(milestone))
    }
    /// The correction watch ended: a hand rewrite, twice in a week, by someone who has not used voice edit.
    func noteAfterPaste(initial: String, edited: String) {
        guard let controller, !Tips.isDone(.voiceEdit), !controller.history.contains(where: { $0.editOriginal != nil }),
              Tips.isRewrite(initial: initial, edited: edited), Tips.noteRewrite(now: Date()) else { return }
        queue(.voiceEdit)
    }
    func voiceEditUsed() { Tips.completed(.voiceEdit); if card == .voiceEdit || pending == .voiceEdit { pending = nil; hide() } }

    private func queue(_ next: Card) { if Tips.canShow(next.id, now: Date()) { pending = next } }

    /// From the controller's timer: a queued tip waits for a pause, never over dictation, the bubble, the call offer or a meeting.
    func tick() {
        guard let controller else { return }
        let now = ProcessInfo.processInfo.systemUptime
        let busy = controller.isBusy || controller.bubble.isVisible || controller.callOffers.offer != nil || controller.meetingRecorder.live != nil
        // The tip shares the bubble's spot: it gives way to anything else there, without counting as dismissed.
        if let card, busy || now - shownAt >= card.lifetime { hide() }
        guard let next = pending, card == nil, !busy else { return }
        guard Tips.canShow(next.id, now: Date()) else { pending = nil; return }
        show(next)
    }

    func show(_ next: Card) {
        pending = nil; card = next; shownAt = ProcessInfo.processInfo.systemUptime
        Tips.shown(now: Date())
        let frame = BubbleGeometry(screen: nil).frame
        let width = min(340, max(220, frame.width - 32))
        let host = NSHostingView(rootView: TipCard(card: next, presenter: self).frame(width: width))
        panel.contentView = host
        panel.setFrame(NSRect(x: frame.midX - width / 2, y: frame.minY + 24, width: width, height: host.fittingSize.height), display: true)
        panel.orderFrontRegardless()
        announce(Self.spoken(next, controller: controller), priority: .medium)
    }
    func hide() { card = nil; panel.orderOut(nil) }
    /// Milestones are news, not a nudge: closing one never stops the next. Only the voice-edit tip retires.
    func dismiss() { if card == .voiceEdit { Tips.dismissed(.voiceEdit) }; hide() }
    func never() { if card == .voiceEdit { Tips.completed(.voiceEdit) }; hide() }
    func act() {
        guard let card else { return }
        if card == .voiceEdit { Tips.completed(.voiceEdit) }
        hide()
        controller?.screenRequest = card == .voiceEdit ? .settingsPane(.recording) : .home
        controller?.openSettings?()
    }

    func writeSnapshot(to url: URL) {
        guard let view = panel.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    static func spoken(_ card: Card, controller: AppController?) -> String {
        switch card {
        case .milestone(let m): "Milestone: \(m.words.formatted()) words. About \(Tips.duration(m.secondsSaved)) of typing saved."
        case .voiceEdit: "Tip: \(voiceEditHow(controller))"
        }
    }
    static func voiceEditHow(_ controller: AppController?) -> String {
        guard let controller else { return "" }
        let key = controller.voiceEditShortcut == EditTrigger.sameKey ? controller.shortcutName : ShortcutBinding(stored: controller.voiceEditShortcut).displayName
        return "Select the text, press \(key) and say what to change, like “make it shorter”."
    }

    // MARK: Explore dots

    var usage: Tips.Usage {
        guard let controller else { return .init(dictations: 0, apps: 0, learnedWords: 0, daysUsed: 0) }
        var dictations = 0, apps = 0
        if case .stats(let s) = controller.insights { dictations = s.dictations; apps = s.apps.count }
        return .init(dictations: dictations, apps: apps, learnedWords: controller.dictionaryEntries.filter { $0.source == .learned }.count,
                     daysUsed: Tips.daysUsed(now: Date()))
    }
    func hasIntro(_ pane: SettingsPane) -> Bool { intros.contains(pane) }
    func opened(_ pane: SettingsPane) { if dots.contains(pane) { Tips.opened(pane); changed() } }
    func finishIntro(_ pane: SettingsPane) { if let id = Tips.id(for: pane) { Tips.completed(id) }; Tips.opened(pane); changed() }
    func dismissHome() { Tips.dismissHome(); changed() }
    func showAgain() { Tips.reset(); enabled = true; changed() }
}

/// The call offer's card, with the accent glow for good news.
private struct TipCard: View {
    let card: TipPresenter.Card
    @ObservedObject var presenter: TipPresenter
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "sparkles").font(.system(size: 12)).foregroundStyle(Palette.accent).accessibilityHidden(true)
                Text(title).font(.system(size: 12, weight: .semibold))
                Spacer(minLength: 4)
                Button { presenter.dismiss() } label: { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)) }
                    .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Not now")
            }
            switch card {
            case .milestone(let m):
                Text("\(m.words.formatted()) words").font(.system(size: 22, weight: .bold, design: .rounded)).foregroundStyle(Palette.accent)
                Text(m.secondsSaved >= 60 ? "About \(Tips.duration(m.secondsSaved)) of typing saved." : "Dictated with Murmur.").font(.system(size: 12))
                HStack(spacing: 6) {
                    if let next = Tips.next(after: m.words) { Text("Next: \(next.formatted())").font(.system(size: 11)).foregroundStyle(.secondary) }
                    Spacer(minLength: 4)
                    Button("See Home") { presenter.act() }.buttonStyle(.borderedProminent).controlSize(.small)
                }
            case .voiceEdit:
                Text("You rewrote a dictation by hand. \(TipPresenter.voiceEditHow(presenter.controller))").font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 6) {
                    Button("Don’t suggest this") { presenter.never() }.buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer(minLength: 4)
                    Button("Not now") { presenter.dismiss() }.controlSize(.small)
                    Button("Show Me") { presenter.act() }.buttonStyle(.borderedProminent).controlSize(.small)
                        .accessibilityHint("Opens the voice edit setting.")
                }
            }
        }
        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
        .tint(Palette.accent)
        .accessibilityElement(children: .contain)
        .modifier(AccentGlass(radius: 18))
    }
    private var title: String { if case .milestone = card { return "Milestone" }; return "Tip: fix it by voice" }
}

/// Home: the latest milestone, the rungs reached and the next, until dismissed.
struct HomeMilestoneCard: View {
    @ObservedObject var tips: TipPresenter
    var body: some View {
        if let reached = Tips.homeMilestone() {
            VStack(alignment: .leading, spacing: 6) {
                Text("Milestone").font(WindowType.body(12, weight: .semibold)).foregroundStyle(Palette.accentInk).textCase(.uppercase)
                Text("\(reached.formatted()) words").font(WindowType.body(30, weight: .bold)).tracking(-0.8).foregroundStyle(Palette.accent)
                Text("About \(Tips.duration(Tips.lifetimeSecondsSaved())) of typing saved so far.").font(WindowType.body(14)).foregroundStyle(Palette.ink70)
                HStack(spacing: 6) {
                    ForEach(Self.ladder(reached), id: \.self) { MilestoneRung(rung: $0, reached: reached) }
                    Spacer(minLength: 8)
                    Button("Dismiss") { tips.dismissHome() }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }.padding(.top, 4)
            }
            .padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background { AccentWash().clipShape(RoundedRectangle(cornerRadius: 10)) }
            .overlay(alignment: .leading) { UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(Palette.accent).frame(width: 3) }
            .accessibilityElement(children: .contain)
        }
    }
}

extension HomeMilestoneCard {
    static func ladder(_ reached: Int) -> [Int] { Tips.rungs.filter { $0 <= (Tips.next(after: reached) ?? reached) } }
}

private struct MilestoneRung: View {
    let rung: Int, reached: Int
    var body: some View {
        let done = rung <= reached
        Text(rung >= 1000 ? "\(rung / 1000)k" : "\(rung)").font(WindowType.body(12, weight: .medium))
            .padding(.horizontal, 9).padding(.vertical, 3)
            .foregroundStyle(done ? Color.white : Palette.accent)
            .background { if done { Capsule().fill(Palette.accent) } else { Capsule().strokeBorder(Palette.accent, style: StrokeStyle(lineWidth: 1, dash: [3])) } }
            .accessibilityLabel(done ? "\(rung.formatted()) words reached" : "Next: \(rung.formatted()) words")
    }
}

/// Settings: one line on a pane the user has not explored, with its reason drawn from their own use.
struct PaneIntro: View {
    let pane: SettingsPane
    @ObservedObject var tips: TipPresenter
    var body: some View {
        if tips.hasIntro(pane), let text = Self.text(pane, tips.usage) {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: "sparkles").foregroundStyle(Palette.accent).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(text.title).font(WindowType.body(14, weight: .semibold)).foregroundStyle(Palette.ink)
                    Text(text.body).font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Button("Got It") { tips.finishIntro(pane) }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
            }
            .padding(14).frame(maxWidth: .infinity, alignment: .leading)
            .background { AccentWash().clipShape(RoundedRectangle(cornerRadius: 10)) }
            .overlay(alignment: .leading) { UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10).fill(Palette.accent).frame(width: 3) }
            .padding(.bottom, 20)
            .accessibilityElement(children: .contain)
        }
    }
    static func text(_ pane: SettingsPane, _ usage: Tips.Usage) -> (title: String, body: String)? {
        switch pane {
        case .modes: ("Different apps, different voice.", "You dictate in \(usage.apps) apps. A mode can keep Mail formal and chat casual, and Murmur picks it by app.")
        case .dictionary: ("Murmur is learning your words.", "It learned \(usage.learnedWords == 1 ? "1 word" : "\(usage.learnedWords) words") from your corrections. Check the spellings below.")
        case .cues: ("Stop retyping what you repeat.", "Save a link, an address or a sign-off you repeat, then say its cue and Murmur inserts it exactly.")
        default: nil
        }
    }
}

/// The settings sidebar and tabs mark a pane with something new to the user.
struct ExploreDot: View {
    var body: some View { Circle().fill(Palette.accent).frame(width: 7, height: 7).accessibilityHidden(true) }
}

/// The accent wash behind in-window tips: the Professional hint's surface, lit from the leading edge.
private struct AccentWash: View {
    var body: some View {
        ZStack { Palette.accent16; LinearGradient(colors: [Palette.accent.opacity(0.14), .clear], startPoint: .leading, endPoint: .trailing) }
    }
}
