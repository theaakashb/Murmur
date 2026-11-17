import AppKit
import SwiftUI

@main struct MeetingStartChecks {
    @MainActor static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let presenter = MeetingConsentPresenter()
        var answers: [Bool] = []
        presenter.show(source: .microphoneOnly) { answers.append($0) }
        func consent() -> (NSWindow, MeetingConsentSheet) {
            let window = app.windows.first { $0.isVisible && $0.title == "Start Meeting Notes" }!
            return (window, (window.contentView as! NSHostingView<MeetingConsentSheet>).rootView)
        }
        let (first, cancel) = consent()
        precondition(cancel.source == .microphoneOnly)
        precondition(app.windows.filter(\.isVisible).count == 1, "Consent needs no Settings window")
        cancel.done(false)
        precondition(answers == [false] && !first.isVisible)
        presenter.show(source: .microphoneOnly) { answers.append($0) }
        presenter.show(source: .everything) { answers.append($0) }
        let (second, accept) = consent()
        precondition(accept.source == .everything, "The latest selected source is used")
        precondition(app.windows.filter(\.isVisible).count == 1)
        accept.done(true)
        precondition(answers == [false, true] && !second.isVisible)
        // The minimized indicator: the pill beside the notch, or the menu bar item; never a floating pill.
        let notched = BubbleGeometry(screenFrame: NSRect(x: 0, y: 0, width: 1512, height: 982), visibleFrame: NSRect(x: 0, y: 0, width: 1512, height: 950),
                                     left: NSRect(x: 0, y: 950, width: 656, height: 32), right: NSRect(x: 856, y: 950, width: 656, height: 32), notchHeight: 32)
        let flat = BubbleGeometry(screenFrame: NSRect(x: 0, y: 0, width: 1920, height: 1080), visibleFrame: NSRect(x: 0, y: 0, width: 1920, height: 1055), left: nil, right: nil, notchHeight: 25)
        precondition(MeetingIndicator.notch.resolved(hasNotch: true) == .notch && MeetingIndicator.notch.resolved(hasNotch: false) == .menuBar
                     && MeetingIndicator.menuBar.resolved(hasNotch: true) == .menuBar, "the notch needs a notch; everything else is the menu bar")
        let recorder = MeetingRecorder()
        var geometry = notched, placements = 0
        let panel = MeetingPanelPresenter(recorder: recorder) { geometry }
        panel.onPlacement = { placements += 1 }
        UserDefaults.standard[Prefs.meetingPanelCollapsed] = true; UserDefaults.standard[Prefs.meetingIndicator] = MeetingIndicator.notch
        recorder.showFixture([], live: Meeting(id: UUID().uuidString, recordedAt: Date(), source: .microphoneOnly))
        panel.update()
        func shown() -> Bool { panel.view?.window?.isVisible == true }
        precondition(!panel.inMenuBar && shown() && panel.view?.window?.frame == notched.rightDockFrame(width: liveDockWidth), "minimized on a notched display: the pill beside the notch")
        UserDefaults.standard[Prefs.meetingIndicator] = MeetingIndicator.menuBar; panel.indicatorChanged()
        precondition(panel.inMenuBar && !shown(), "menu bar chosen: the panel hides and the menu bar item is the indicator")
        UserDefaults.standard[Prefs.meetingIndicator] = MeetingIndicator.notch; geometry = flat; panel.indicatorChanged()
        precondition(panel.inMenuBar && !shown(), "no notch: the menu bar is used, never a floating pill")
        let before = placements
        panel.indicatorChanged(); panel.update()
        precondition(placements == before && !shown(), "an unchanged setting and a recorder update leave the hidden panel alone")
        panel.expand()
        precondition(!panel.inMenuBar && shown() && panel.view?.window?.frame.size == NSSize(width: 340, height: 520) && !UserDefaults.standard[Prefs.meetingPanelCollapsed], "Show Meeting Panel brings the full panel back")
        recorder.showFixture([], live: nil); panel.update()
        precondition(!panel.inMenuBar && panel.view == nil, "the indicator goes when the meeting ends")
        print("Meeting start checks passed (standalone consent, cancel, accept, source replacement, minimized indicator in the notch or the menu bar).")
    }
}
