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
    /// Centre bars swing most, like a synth's level meter.
    private static let weights = [0.6, 0.85, 1, 0.85, 0.6]
    var body: some View {
        // Every display frame: a 0.15 s floor stepped the pulse visibly after release.
        TimelineView(.animation(minimumInterval: nil, paused: phase == .error || reduceMotion)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            // Clear Liquid Glass capsules that stretch vertically with the voice.
            GlassEffectContainer(spacing: 2) {
                HStack(spacing: 0) {
                    ForEach(0..<5) { index in
                        let wobble = reduceMotion ? 1 : 0.65 + 0.35 * sin(t * 9 + Double(index) * 1.7)
                        // Read the microphone every frame (the published level updates every 0.15 s);
                        // the square root lifts quiet speech so the bars react to it.
                        let voice = reduceMotion ? controller.voiceLevel : controller.liveVoiceLevel()
                        let level = phase == .listening ? voice.squareRoot() * wobble
                            : phase == .processing ? (reduceMotion ? 0.35 : (sin(t * 4 - Double(index)) + 1) / 2 * 0.45) : 0.15
                        // Rests as a short pill (9 × 13 pt) and stretches up to the full 40 pt.
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
            content.background(Capsule().fill(Palette.accent.opacity(0.55)))
                .glassEffect(.clear, in: Capsule())
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
        controller.showSettings()
        DispatchQueue.main.async { NotificationCenter.default.post(name: .dictationShowError, object: nil) }
    }
}

/// Gap #17: the dictation could not be pasted, so the bubble shows it with Copy. History and ⌃⌘V
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

/// Gap #13: the level meter plus the words heard so far, at most two lines (the newest tail).
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

/// Gap #18: shown for a few seconds after Escape cancels a recording. Undo processes it as normal.
private struct UndoCancelCard: View {
    @ObservedObject var controller: AppController
    var inNotch = false
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "xmark.circle").font(.system(size: 12)).foregroundStyle(.secondary).accessibilityHidden(true)
            Text("Recording canceled").font(.system(size: 12, weight: .medium)).lineLimit(1)
            Spacer(minLength: 4)
            if inNotch {
                Rectangle().fill(Color.white.opacity(0.3)).frame(width: 1, height: 16)
                Spacer(minLength: 4)
                Button { controller.undoCancel() } label: {
                    Text("Undo").font(.system(size: 12, weight: .medium))
                        .frame(height: 24).contentShape(Rectangle())
                }.buttonStyle(.plain)
                    .accessibilityHint("Transcribes and inserts the canceled recording.")
            } else {
                Button("Undo") { controller.undoCancel() }.buttonStyle(.borderedProminent).controlSize(.small)
                    .accessibilityHint("Transcribes and inserts the canceled recording.")
            }
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

/// Shown after a dictation that used a mode: "Mode used: Email  [Use without mode]  [×]".
private struct ModeRecoveryCard: View {
    @ObservedObject var controller: AppController
    var inNotch = false
    var body: some View {
        if let recovery = controller.modeRecovery {
            HStack(spacing: 8) {
                Image(systemName: "slider.horizontal.3").font(.system(size: 12)).foregroundStyle(.secondary).accessibilityHidden(true)
                switch recovery.stage {
                case .offer:
                    Text("Mode used: \(recovery.modeName)").font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 4)
                    if inNotch {
                        Rectangle().fill(Color.white.opacity(0.3)).frame(width: 1, height: 16)
                        Spacer(minLength: 4)
                        Button { controller.useWithoutMode() } label: {
                            Text("Use without mode").font(.system(size: 12, weight: .medium))
                                .frame(height: 24).contentShape(Rectangle())
                        }.buttonStyle(.plain)
                            .accessibilityHint("Processes the original transcript with ordinary Cleanup and replaces the inserted text if it is unchanged.")
                    } else {
                        Button("Use without mode") { controller.useWithoutMode() }.buttonStyle(.borderedProminent).controlSize(.small)
                            .accessibilityHint("Processes the original transcript with ordinary Cleanup and replaces the inserted text if it is unchanged.")
                    }
                case .working:
                    Text("Reprocessing…").font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 4)
                case .replaced:
                    Text("Replaced without mode").font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 4)
                case .copy:
                    Text("Could not replace safely").font(.system(size: 12, weight: .medium)).lineLimit(1)
                    Spacer(minLength: 4)
                    Button("Copy") { controller.copyRecovery() }.buttonStyle(.borderedProminent).controlSize(.small)
                        .accessibilityHint("Copies the text processed without the mode.")
                }
                if !inNotch && recovery.stage != .replaced {
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

private struct RecoveryBackground: ViewModifier {
    let inNotch: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if inNotch { content.preferredColorScheme(.dark) }
        else { content.modifier(UtilityGlass(radius: 18)) }
    }
}

private final class DictationPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class BubblePresenter {
    private let controller: AppController
    private let panel = DictationPanel(contentRect: .zero, styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
    private var transition = UUID()
    private var atNotch = false

    init(controller: AppController) {
        self.controller = controller
        panel.title = "Murmur status"
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
    }

    func show(phase: Phase, message: String, screen: NSScreen? = nil) {
        // An idle status change leaves a learned-word notice up; anything shown replaces it.
        // A card holding text not pasted shows in any phase (Saved included).
        guard showsFloatingIndicator(phase: phase) || controller.pasteFailure != nil else { if notice == nil { hide() }; return }
        notice = nil
        let targetScreen = screen ?? NSScreen.main ?? NSScreen.screens.first
        let frame = targetScreen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let minimal = phase != .error && controller.pasteFailure == nil
        panel.hasShadow = !minimal
        let card = controller.pasteFailure
        let live = phase == .listening && controller.liveWordsActive
        let left = targetScreen?.auxiliaryTopLeftArea
        let right = targetScreen?.auxiliaryTopRightArea
        let notch = minimal && !live && left != nil && right != nil
        let entering = notch && (!panel.isVisible || !atNotch)
        transition = UUID()
        atNotch = notch
        panel.alphaValue = 1
        let notchWidth = (right?.minX ?? 0) - (left?.maxX ?? 0)
        let notchHeight = targetScreen.map { $0.frame.maxY - $0.visibleFrame.maxY } ?? 32
        let width = notch ? 72 + notchWidth / 2 + 10 : live ? min(420, max(240, frame.width - 32)) : minimal ? 80 : card != nil ? min(320, max(200, frame.width - 32)) : min(240, max(180, frame.width - 32))
        let content = notch ? AnyView(HStack(spacing: 0) {
            VoiceBubbles(controller: controller, phase: phase, ink: Palette.accent).scaleEffect(0.75).frame(width: 72, height: notchHeight)
            Color.clear.frame(width: notchWidth / 2 + 10, height: notchHeight)
        }.frame(height: notchHeight).background(Color(red: 0, green: 0, blue: 0), in: UnevenRoundedRectangle(bottomLeadingRadius: 12)))
            : live ? AnyView(LiveWordsBubble(controller: controller))
            : minimal ? AnyView(VoiceBubbles(controller: controller, phase: phase))
            : card.map { AnyView(PasteFailureCard(controller: controller, failure: $0)) } ?? AnyView(BubbleView(controller: controller, message: message))
        let host = NSHostingView(rootView: content.frame(width: width))
        panel.contentView = host
        let height = host.fittingSize.height
        panel.level = notch ? .statusBar : .floating
        let x = notch ? left!.maxX - 72 : frame.midX - width / 2
        let y = notch ? targetScreen!.frame.maxY - height : frame.minY + 24
        let destination = NSRect(x: x, y: y, width: width, height: height)
        place(entering && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? destination.offsetBy(dx: 72, dy: 0) : destination)
        panel.orderFrontRegardless()
        if entering && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
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
    /// The Undo card; it leaves with the "Recording canceled" status (5 s) or when anything else is shown.
    func showUndo(screen: NSScreen?) {
        transition = UUID(); atNotch = false; panel.alphaValue = 1
        let targetScreen = screen ?? NSScreen.main ?? NSScreen.screens.first
        let frame = targetScreen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let left = targetScreen?.auxiliaryTopLeftArea
        let right = targetScreen?.auxiliaryTopRightArea
        let hasNotch = left != nil && right != nil
        let width = hasNotch ? min(225, left!.width) : min(260, max(200, frame.width - 32))
        let notchWidth = (right?.minX ?? 0) - (left?.maxX ?? 0)
        let notchHeight = targetScreen.map { $0.frame.maxY - $0.visibleFrame.maxY } ?? 32
        let content = hasNotch ? AnyView(HStack(spacing: 0) {
            UndoCancelCard(controller: controller, inNotch: true).frame(width: width, height: notchHeight)
            Color.clear.frame(width: notchWidth / 2 + 10, height: notchHeight)
        }.background(Color.black, in: UnevenRoundedRectangle(bottomLeadingRadius: 12)))
            : AnyView(UndoCancelCard(controller: controller).frame(width: width))
        let host = NSHostingView(rootView: content)
        panel.contentView = host
        panel.hasShadow = !hasNotch
        panel.level = hasNotch ? .statusBar : .floating
        let height = host.fittingSize.height
        let destination = NSRect(x: hasNotch ? left!.maxX - width : frame.midX - width / 2,
                                 y: hasNotch ? targetScreen!.frame.maxY - height : frame.minY + 24,
                                 width: hasNotch ? width + notchWidth / 2 + 10 : width, height: height)
        let animate = hasNotch && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        place(animate ? destination.offsetBy(dx: width, dy: 0) : destination)
        panel.orderFrontRegardless()
        if animate {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.2
                panel.animator().setFrame(destination, display: true)
            }
        }
        notice = UUID()
    }
    private var recoveryNotice: UUID?
    /// The mode recovery pill; leaves after 12 s unless something else took the panel first.
    func showRecovery(screen: NSScreen?) {
        transition = UUID(); atNotch = false; panel.alphaValue = 1
        let targetScreen = screen ?? NSScreen.main ?? NSScreen.screens.first
        let frame = targetScreen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let left = targetScreen?.auxiliaryTopLeftArea
        let right = targetScreen?.auxiliaryTopRightArea
        let hasNotch = left != nil && right != nil
        let width = hasNotch ? min(320, left!.width) : min(380, max(260, frame.width - 32))
        let shown = UUID()
        let notchWidth = (right?.minX ?? 0) - (left?.maxX ?? 0)
        let notchHeight = targetScreen.map { $0.frame.maxY - $0.visibleFrame.maxY } ?? 32
        let content = hasNotch ? AnyView(HStack(spacing: 0) {
            ModeRecoveryCard(controller: controller, inNotch: true).frame(width: width, height: notchHeight)
            Color.clear.frame(width: notchWidth / 2 + 10, height: notchHeight)
        }.background(Color.black, in: UnevenRoundedRectangle(bottomLeadingRadius: 12)))
            : AnyView(ModeRecoveryCard(controller: controller).frame(width: width))
        let host = NSHostingView(rootView: content)
        panel.contentView = host
        panel.hasShadow = !hasNotch
        panel.level = hasNotch ? .statusBar : .floating
        let height = host.fittingSize.height
        let destination = NSRect(x: hasNotch ? left!.maxX - width : frame.midX - width / 2,
                                 y: hasNotch ? targetScreen!.frame.maxY - height : frame.minY + 24,
                                 width: hasNotch ? width + notchWidth / 2 + 10 : width, height: height)
        let animate = hasNotch && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        place(animate ? destination.offsetBy(dx: width, dy: 0) : destination)
        panel.orderFrontRegardless()
        if animate {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.2
                panel.animator().setFrame(destination, display: true)
            }
        }
        notice = shown; recoveryNotice = shown
        DispatchQueue.main.asyncAfter(deadline: .now() + 12) { [weak self] in
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
        let frame = (screen ?? NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
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
