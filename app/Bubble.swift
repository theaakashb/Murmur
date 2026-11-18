import AppKit
import SwiftUI

// Floating overlay cards, the dictation panel and the presenter that shows them.

func showsFloatingIndicator(phase: Phase) -> Bool {
    [.listening, .processing, .error].contains(phase)
}

private struct VoiceBubbles: View {
    @ObservedObject var controller: AppController
    let phase: Phase
    var ink: Color? = nil
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    private static let weights = [0.6, 0.85, 1, 0.85, 0.6]
    var body: some View {
        // Every display frame: a 0.15 s floor stepped the pulse visibly after release.
        TimelineView(.animation(minimumInterval: nil, paused: phase == .error || reduceMotion)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            GlassGroup {
                HStack(spacing: 0) {
                    ForEach(0..<5) { index in
                        let wobble = reduceMotion ? 1 : 0.65 + 0.35 * sin(t * 9 + Double(index) * 1.7)
                        // Read the microphone every frame (the published level updates every 0.15 s);
                        // the square root lifts quiet speech so the bars react to it.
                        let voice = reduceMotion ? controller.voiceLevel : controller.liveVoiceLevel()
                        let level = phase == .listening ? voice.squareRoot() * wobble
                            : phase == .processing ? (reduceMotion ? 0.35 : (sin(t * 4 - Double(index)) + 1) / 2 * 0.45) : 0.15
                        Color.clear.frame(width: 9, height: 13 + 27 * level * Self.weights[index])
                            .modifier(VoiceDotGlass(opaque: reduceTransparency || contrast == .increased, ink: ink))
                            .frame(width: 14, height: 40)
                    }
                }
            }
            .frame(width: 80, height: 40)
        }
        // The indicator's panel never becomes key; render its glass as active so it refracts
        // instead of falling back to the flat inactive look.
        .environment(\.appearsActive, true)
        .accessibilityElement(children: .ignore).accessibilityLabel(phase.rawValue)
        .help(phase == .listening ? "Release, or press the shortcut again after a double-press, to finish. Escape cancels." : phase.rawValue)
    }
}

private struct VoiceDotGlass: ViewModifier {
    let opaque: Bool
    var ink: Color? = nil
    @ViewBuilder func body(content: Content) -> some View {
        if let ink { content.overlay(Capsule().fill(ink)) }
        else if opaque { content.overlay(Capsule().fill(Color.primary.opacity(0.65))) }
        // A royal-blue fill under clear glass: glass tint alone rendered gray and vanished on white.
        else {
            let tinted = content.background(Capsule().fill(Palette.accent.opacity(0.55)))
            if #available(macOS 26.0, *) { tinted.glassEffect(.clear, in: Capsule()) }
            else { tinted.background(.ultraThinMaterial, in: Capsule()) }
        }
    }
}

private struct BubbleView: View {
    @ObservedObject var controller: AppController
    let message: String

    private var summary: String {
        message.components(separatedBy: ". ").first ?? "Dictation failed."
    }
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle").font(.system(size: 12)).foregroundStyle(.orange).accessibilityHidden(true).allowsHitTesting(false)
            Text(summary).font(.system(size: 12, weight: .medium)).fixedSize(horizontal: false, vertical: true)
                .help(message).allowsHitTesting(false).accessibilityHidden(true)
            Spacer(minLength: 4)
        }
        .padding(8).frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(RoundedRectangle(cornerRadius: 18))
        .onTapGesture { openError() }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(summary)
        .accessibilityHint("Open Murmur to view the full error.")
        .accessibilityAction { openError() }
        .modifier(UtilityGlass(radius: 18))
    }

    private func openError() {
        controller.screenRequest = .settings
        controller.showSettings()
    }
}

/// The dictation could not be pasted, so the bubble shows it with Copy. History and ⌃⌘V
/// still work. The panel never becomes key, so clicking leaves the user's app in front.
private struct PasteFailureCard: View {
    @ObservedObject var controller: AppController
    let failure: AppController.PasteFailure
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "doc.on.clipboard").font(.system(size: 12)).foregroundStyle(.orange).accessibilityHidden(true)
                Text(failure.title).font(.system(size: 12, weight: .semibold))
                Spacer(minLength: 4)
                Button { controller.dismissStatus() } label: { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)) }
                    .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Dismiss")
            }
            Text(failure.text).font(.system(size: 12)).lineLimit(3).truncationMode(.tail).fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading).accessibilityLabel("Dictation: \(failure.text)")
            HStack {
                Text("⌃⌘V pastes it too").font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer(minLength: 4)
                Button("Copy") { controller.copyPasteFailure() }.buttonStyle(.borderedProminent).controlSize(.small)
            }
        }
        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .modifier(UtilityGlass(radius: 18))
    }
}

/// Voice edit's processing bubble: the dots plus where the text is going, so it is seen as well as announced.
private struct EditingBubble: View {
    @ObservedObject var controller: AppController
    let label: String
    var body: some View {
        HStack(spacing: 6) {
            VoiceBubbles(controller: controller, phase: .processing).scaleEffect(0.6).frame(width: 48, height: 28)
            Text(label).font(.system(size: 12, weight: .medium)).lineLimit(2).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
        }
        .padding(8).frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .ignore).accessibilityLabel(label)
        .modifier(UtilityGlass(radius: 18))
    }
}

/// The level meter plus the words heard so far, at most two lines (the newest tail).
/// Settled words are bright; the last word, which can still grow, is dimmed. Display only.
private struct LiveWordsBubble: View {
    @ObservedObject var controller: AppController
    var body: some View {
        let parts = LiveWordsFacts.split(controller.liveText)
        HStack(spacing: 6) {
            VoiceBubbles(controller: controller, phase: .listening)
            Group {
                if parts.settled.isEmpty && parts.guess.isEmpty { Text("Listening…").foregroundStyle(.secondary) }
                else { Text(parts.settled).foregroundStyle(.primary) + Text(parts.guess).foregroundStyle(.secondary) }
            }
            .font(.system(size: 13, weight: .medium)).lineLimit(2).truncationMode(.head)
            .frame(maxWidth: .infinity, minHeight: 34, alignment: .leading)
            .accessibilityHidden(true)
        }
        .padding(.trailing, 12).padding(.vertical, 2)
        .modifier(UtilityGlass(radius: 22))
    }
}

/// Shown for a few seconds after Escape cancels a recording. Undo processes it as normal.
private struct UndoCancelCard: View {
    @ObservedObject var controller: AppController
    var inNotch = false
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "xmark.circle").font(.system(size: 12)).foregroundStyle(.secondary).accessibilityHidden(true)
            Text("Recording canceled").font(.system(size: 12, weight: .medium)).lineLimit(1)
            Spacer(minLength: 4)
            CardAction(title: "Undo", hint: "Transcribes and inserts the canceled recording.", inNotch: inNotch) { controller.undoCancel() }
        }
        .padding(inNotch ? 4 : 8).padding(.trailing, inNotch ? 8 : 0)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .modifier(RecoveryBackground(inNotch: inNotch))
    }
}

/// A short notice with Undo, which removes what was learned and blocks it; learned entries can
/// also be removed in Settings → Dictionary.
private struct LearnedNotice: View {
    let controller: AppController
    let word: String
    let entryID: UUID
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "character.book.closed").font(.system(size: 12)).foregroundStyle(.secondary).accessibilityHidden(true)
            Text("Added “\(word)” to dictionary").font(.system(size: 12, weight: .medium)).lineLimit(1)
            Spacer(minLength: 4)
            Button("Undo") { controller.undoLearned(entryID) }.buttonStyle(.borderedProminent).controlSize(.small)
                .accessibilityHint("Stops replacing this word and does not learn it again.")
        }
        .padding(8).frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .contain)
        .modifier(UtilityGlass(radius: 18))
    }
}

private struct ModeRecoveryCard: View {
    @ObservedObject var controller: AppController
    var inNotch = false
    var body: some View {
        if let recovery = controller.modeRecovery {
            HStack(spacing: 8) {
                Image(systemName: "sparkles").font(.system(size: 12)).foregroundStyle(Palette.accent).accessibilityHidden(true)
                // The card is measured once when shown: the hidden widest label keeps a later stage from being cut off.
                ZStack(alignment: .leading) {
                Text("Copy original").font(.system(size: 12, weight: .medium)).fixedSize().hidden().accessibilityHidden(true)
                switch recovery.stage {
                case .offer:
                    // A chip that reverts to the raw transcript when clicked (⌃⌘Z too).
                    Button { controller.revertToRaw() } label: {
                        Text("\(recovery.changes) change\(recovery.changes == 1 ? "" : "s")").font(.system(size: 12, weight: .medium)).lineLimit(1).fixedSize()
                            .frame(maxWidth: .infinity, minHeight: inNotch ? 24 : 0).contentShape(Rectangle())
                    }.buttonStyle(.plain)
                        .accessibilityLabel("\(recovery.changes) changes by cleanup").accessibilityHint("Replaces the inserted text with your words before cleanup if it is unchanged. Shortcut: Control Command Z.")
                case .reverted:
                    Text("Undone").font(.system(size: 12, weight: .medium)).lineLimit(1).fixedSize()
                        .accessibilityLabel("Replaced with your words")
                    Spacer(minLength: 0)
                case .copy:
                    // The field changed since insertion, so it is left alone; the original words go to the clipboard instead.
                    Button { controller.copyRecovery() } label: {
                        Text("Copy original").font(.system(size: 12, weight: .medium)).lineLimit(1).fixedSize()
                            .frame(maxWidth: .infinity, minHeight: inNotch ? 24 : 0).contentShape(Rectangle())
                    }.buttonStyle(.plain)
                        .accessibilityLabel("Could not replace safely. Copy your original words").accessibilityHint("Copies your words before cleanup.")
                }
                }
                if !inNotch && recovery.stage != .reverted {
                    Button { controller.clearModeRecovery() } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Dismiss")
                }
            }
            .padding(inNotch ? 4 : 8).frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .contain)
            .modifier(RecoveryBackground(inNotch: inNotch))
        }
    }
}

private struct CardAction: View {
    let title: String
    let hint: String
    let inNotch: Bool
    let action: () -> Void
    var body: some View {
        if inNotch {
            Rectangle().fill(Color.white.opacity(0.3)).frame(width: 1, height: 16)
            Spacer(minLength: 4)
            Button(action: action) {
                Text(title).font(.system(size: 12, weight: .medium))
                    .frame(height: 24).contentShape(Rectangle())
            }.buttonStyle(.plain).accessibilityHint(hint)
        } else {
            Button(title, action: action).buttonStyle(.borderedProminent).controlSize(.small).accessibilityHint(hint)
        }
    }
}

private struct RecoveryBackground: ViewModifier {
    let inNotch: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if inNotch { content.preferredColorScheme(.dark) }
        else { content.modifier(UtilityGlass(radius: 18)) }
    }
}

struct BubbleGeometry {
    let screen: NSScreen?
    let screenFrame: NSRect
    let frame: NSRect
    let left: NSRect?
    let right: NSRect?
    let notchHeight: CGFloat
    var hasNotch: Bool { left != nil && right != nil }
    var notchWidth: CGFloat { (right?.minX ?? 0) - (left?.maxX ?? 0) }
    var notchInset: CGFloat { notchWidth / 2 + 10 }
    // The menu bar is a point taller than the notch (33 vs 32 on a 15-inch Air), so the height comes from the area beside the notch.
    init(screen: NSScreen?) {
        let target = screen ?? NSScreen.main ?? NSScreen.screens.first
        let fallback = NSRect(x: 0, y: 0, width: 1280, height: 800)
        self.init(screen: target, screenFrame: target?.frame ?? fallback, visibleFrame: target?.visibleFrame ?? fallback,
                  left: target?.auxiliaryTopLeftArea, right: target?.auxiliaryTopRightArea, notchHeight: target.map { $0.auxiliaryTopLeftArea?.height ?? ($0.frame.maxY - $0.visibleFrame.maxY) } ?? 32)
    }
    /// Synthetic rectangles for checks and the fixture; real displays go through `init(screen:)`.
    init(screen: NSScreen? = nil, screenFrame: NSRect, visibleFrame: NSRect, left: NSRect?, right: NSRect?, notchHeight: CGFloat) {
        (self.screen, self.screenFrame, frame, self.left, self.right, self.notchHeight) = (screen, screenFrame, visibleFrame, left, right, notchHeight)
    }
    /// The dictation indicator beside the notch: `visible` points left of it, extended under the notch by `notchInset`.
    func leftDockFrame(visible: CGFloat, height: CGFloat) -> NSRect? {
        guard hasNotch, let left else { return nil }
        return NSRect(x: left.maxX - visible, y: screenFrame.maxY - height, width: visible + notchInset, height: height)
    }
    /// The meeting indicator, its mirror: `width` points right of the notch, extended under it up to where the dictation indicator ends, so both read as part of the notch.
    func rightDockFrame(width: CGFloat) -> NSRect? {
        guard hasNotch, let right else { return nil }
        let under = notchWidth - notchInset
        return NSRect(x: right.minX - under, y: screenFrame.maxY - notchHeight, width: width + under, height: notchHeight)
    }
}

@MainActor
final class BubblePresenter {
    private let controller: AppController
    private let panel = FloatingPanel(title: "Murmur status")
    /// Synthetic notch for the UI fixture only (check_ui --notch); real displays leave it nil.
    var geometryOverride: BubbleGeometry?
    private var transition = UUID()
    private var atNotch = false

    init(controller: AppController) {
        self.controller = controller
    }

    func show(phase: Phase, message: String, screen: NSScreen? = nil) {
        // An idle status change leaves a learned-word notice up; anything shown replaces it.
        // A card holding text not pasted shows in any phase (Saved included).
        guard showsFloatingIndicator(phase: phase) || controller.pasteFailure != nil else { if notice == nil { hide() }; return }
        notice = nil
        let geo = BubbleGeometry(screen: screen)
        let frame = geo.frame, notchWidth = geo.notchWidth, notchHeight = geo.notchHeight
        let editLabel = phase == .processing ? controller.processingLabel : nil
        let minimal = phase != .error && controller.pasteFailure == nil && editLabel == nil
        panel.hasShadow = !minimal
        let card = controller.pasteFailure
        let live = phase == .listening && controller.liveWordsActive
        let notch = minimal && !live && geo.hasNotch
        let entering = notch && (!panel.isVisible || !atNotch)
        transition = UUID()
        atNotch = notch
        panel.alphaValue = 1
        let width = editLabel != nil ? min(300, max(200, frame.width - 32)) : notch ? 72 + geo.notchInset : live ? min(420, max(240, frame.width - 32)) : minimal ? 80 : card != nil ? min(320, max(200, frame.width - 32)) : min(240, max(180, frame.width - 32))
        let content = editLabel.map { AnyView(EditingBubble(controller: controller, label: $0)) } ?? (notch ? AnyView(HStack(spacing: 0) {
            VoiceBubbles(controller: controller, phase: phase, ink: Palette.accent).scaleEffect(0.75).frame(width: 72, height: notchHeight)
            Color.clear.frame(width: geo.notchInset, height: notchHeight)
        }.frame(height: notchHeight).background(Color(red: 0, green: 0, blue: 0), in: UnevenRoundedRectangle(bottomLeadingRadius: 12)))
            : live ? AnyView(LiveWordsBubble(controller: controller))
            : minimal ? AnyView(VoiceBubbles(controller: controller, phase: phase))
            : card.map { AnyView(PasteFailureCard(controller: controller, failure: $0)) } ?? AnyView(BubbleView(controller: controller, message: message)))
        let host = NSHostingView(rootView: content.frame(width: width))
        panel.contentView = host
        let height = host.fittingSize.height
        panel.level = notch ? .statusBar : .floating
        present((notch ? geo.leftDockFrame(visible: 72, height: height) : nil) ?? NSRect(x: frame.midX - width / 2, y: frame.minY + 24, width: width, height: height), slideFrom: entering ? 72 : nil)
    }

    private func present(_ destination: NSRect, slideFrom: CGFloat?) {
        let slide = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? nil : slideFrom
        place(slide.map { destination.offsetBy(dx: $0, dy: 0) } ?? destination)
        panel.orderFrontRegardless()
        if slide != nil {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.2
                panel.animator().setFrame(destination, display: true)
            }
        }
    }

    /// Places the panel now. A plain setFrame loses to a hide() slide still pending or in flight (the
    /// indicator is hidden just before a blocking insertion), which then parks the next card at the notch.
    private func place(_ frame: NSRect) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0
            panel.animator().setFrame(frame, display: true)
            panel.animator().alphaValue = 1
        }
    }

    func hide() {
        notice = nil; recoveryNotice = nil
        let current = UUID()
        transition = current
        guard atNotch && panel.isVisible && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion else {
            atNotch = false; panel.orderOut(nil); return
        }
        atNotch = false
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.2
            panel.animator().setFrame(panel.frame.offsetBy(dx: 72, dy: 0), display: true)
            panel.animator().alphaValue = 0
        } completionHandler: { [weak self] in
            guard let self, self.transition == current else { return }
            self.panel.orderOut(nil)
            self.panel.alphaValue = 1
        }
    }
    /// Whether the bubble shows anything (a status, Undo, a card); the meeting offer waits for it.
    var isVisible: Bool { panel.isVisible }

    private var notice: UUID?
    /// A card beside the notch, or centred above the bottom edge on a screen without one; `notchMax` and `plain` are its widths.
    private func showCard<Card: View>(screen: NSScreen?, notchMax: CGFloat, plain: ClosedRange<CGFloat>, fitsContent: Bool = false, _ card: (Bool) -> Card) {
        transition = UUID(); atNotch = false; panel.alphaValue = 1
        let geo = geometryOverride ?? BubbleGeometry(screen: screen)
        let frame = geo.frame, left = geo.left, hasNotch = geo.hasNotch, notchHeight = geo.notchHeight
        var width = hasNotch ? min(notchMax, left!.width) : min(plain.upperBound, max(plain.lowerBound, frame.width - 32))
        // A card that fits its content: the widest the content wants, never more than the width above.
        if fitsContent { width = min(width, ceil(NSHostingView(rootView: card(hasNotch).fixedSize(horizontal: true, vertical: false)).fittingSize.width) + 12) }
        let content = hasNotch ? AnyView(HStack(spacing: 0) {
            card(true).frame(width: width, height: notchHeight)
            Color.clear.frame(width: geo.notchInset, height: notchHeight)
        }.background(Color.black, in: UnevenRoundedRectangle(bottomLeadingRadius: 12)))
            : AnyView(card(false).frame(width: width))
        let host = NSHostingView(rootView: content)
        panel.contentView = host
        panel.hasShadow = !hasNotch
        panel.level = hasNotch ? .statusBar : .floating
        let height = host.fittingSize.height
        present(NSRect(x: hasNotch ? left!.maxX - width : frame.midX - width / 2,
                       y: hasNotch ? geo.screenFrame.maxY - height : frame.minY + 24,
                       width: hasNotch ? width + geo.notchInset : width, height: height), slideFrom: hasNotch ? width : nil)
    }
    /// The Undo card; it leaves with the "Recording canceled" status (5 s) or when anything else is shown.
    func showUndo(screen: NSScreen?) {
        showCard(screen: screen, notchMax: 225, plain: 200...260) { UndoCancelCard(controller: controller, inNotch: $0) }
        notice = UUID()
    }
    private var recoveryNotice: UUID?
    /// The mode recovery pill (12 s), or the quiet changes chip (5 s); unless something else took the panel first.
    func showRecovery(screen: NSScreen?) {
        showCard(screen: screen, notchMax: 150, plain: 110...150, fitsContent: true) { ModeRecoveryCard(controller: controller, inNotch: $0) }
        let shown = UUID()
        notice = shown; recoveryNotice = shown
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self, self.recoveryNotice == shown else { return }
            self.controller.clearModeRecovery()
        }
    }
    /// Synthetic UI fixture only: writes the panel's current content as a PNG (screen capture needs a permission).
    func writeSnapshot(to url: URL) {
        guard let view = panel.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
    func hideRecovery() { if recoveryNotice != nil, notice == recoveryNotice { hide() } }
    /// The learned-word notice; leaves after 5 s unless something else took the panel first.
    func showLearned(_ word: String, entryID: UUID, screen: NSScreen?) {
        transition = UUID(); atNotch = false; panel.alphaValue = 1
        panel.level = .floating
        let frame = BubbleGeometry(screen: screen).frame
        let width = min(320, max(200, frame.width - 32))
        let shown = UUID()
        let host = NSHostingView(rootView: LearnedNotice(controller: controller, word: word, entryID: entryID).frame(width: width))
        panel.contentView = host
        panel.hasShadow = true
        place(NSRect(x: frame.midX - width / 2, y: frame.minY + 24, width: width, height: host.fittingSize.height))
        panel.orderFrontRegardless()
        notice = shown
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self, self.notice == shown else { return }
            self.hide()
        }
    }
}

/// Liquid Glass groups its children on macOS 26; older systems draw them as they are.
private struct GlassGroup<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        if #available(macOS 26.0, *) { GlassEffectContainer(spacing: 2) { content } } else { content }
    }
}
