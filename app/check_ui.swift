// Explicit visual fixture: temporary storage, synthetic text, no capture or insertion.
import AppKit
import SwiftUI
import Combine

/// Keeps the fixture's live meeting panel alive.
@MainActor var fixtureMeetingPanel: MeetingPanelPresenter?
/// Keeps the fixture's Report an Issue window alive.
@MainActor let fixtureReportIssue = ReportIssuePresenter()

/// Fixture-only setters: they show a state with no download, insertion or preview, and are not part of the app.
extension AppController {
    func showLocalModelFixture(_ status: ModelStatus, message: String = "") { localSlot.status = status; localSlot.message = message }
    /// The pill without a real insertion.
    func showModeRecoveryFixture(stage: ModeRecovery.Stage, mode: Bool) {
        modeRecovery = ModeRecovery(entryID: "fixture", raw: "", inserted: "", changes: 3, anchor: nil, stage: stage, usedMode: mode)
        bubble.showRecovery(screen: nil)
    }
}

@MainActor final class FixtureDelegate: NSObject, NSApplicationDelegate {
    var controller: AppController?
    @objc func quit() { NSApp.terminate(nil) }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { !CommandLine.arguments.contains("--bubble-only") }
    func applicationWillTerminate(_ notification: Notification) { controller?.shutdown() }
}

@main struct UIFixture {
    @MainActor static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        let delegate = FixtureDelegate()
        application.delegate = delegate
        let menu = NSMenu(); let item = NSMenuItem(); let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit UI Check", action: #selector(FixtureDelegate.quit), keyEquivalent: "q").target = delegate
        item.submenu = appMenu; menu.addItem(item); menu.addItem(standardEditMenuItem()); application.mainMenu = menu
        let light = CommandLine.arguments.contains("--light")
        let args = CommandLine.arguments
        UserDefaults.standard[Prefs.sidebarCollapsed] = args.contains("--sidebar-collapsed")
        // --system-appearance leaves the app's appearance alone, so the bubble shows it follows macOS.
        if !args.contains("--system-appearance") { application.appearance = NSAppearance(named: light ? .aqua : .darkAqua) }
        // The fixture's defaults persist between runs: set the guide and History mode every time.
        UserDefaults.standard[Prefs.historyMode] = args.contains("--history-off") ? .off : .on
        if let flag = args.firstIndex(of: "--guide") {
            let step = args.indices.contains(flag + 1) ? Int(args[flag + 1]) ?? 0 : 0
            UserDefaults.standard.set(0, forKey: WelcomeGuide.completedKey); UserDefaults.standard.set(step, forKey: WelcomeGuide.stepKey)
        } else {
            WelcomeGuide.complete(.standard)
        }
        // Catch missing bundled typography instead of silently accepting a system-font fallback.
        _ = WindowType.display(64); _ = WindowType.body(14)
        precondition(NSFont(name: "InstrumentSerif-Regular", size: 64) != nil)
        precondition(NSFont(name: "DMSans-9ptRegular", size: 14) != nil)
        let controller = AppController(preview: true)
        controller.dictationLanguages = ["en"]
        if let flag = args.firstIndex(of: "--dictation-languages"), args.indices.contains(flag + 1) {
            controller.dictationLanguages = args[flag + 1].split(separator: ",").map(String.init)
        }
        if args.contains("--local-transcription") { controller.transcriptionRoute = .local }
        if CommandLine.arguments.contains("--custom-transcription") {
            controller.transcriptionRoute = .cloud
        }
        // Applying an unrelated switch must not reload History on the main thread.
        controller.applySettings()
        var historyReloads = 0
        let historyObservation = controller.$history.dropFirst().sink { _ in historyReloads += 1 }
        controller.recordingSounds.toggle()
        controller.applySettings()
        controller.recordingSounds.toggle()
        controller.applySettings()
        precondition(historyReloads == 0, "Unrelated switches reloaded History")
        historyObservation.cancel()
        delegate.controller = controller
        if args.contains("--mode-editor") { controller.writingModes = WritingMode.presets }
        // Exercise the style binding without capture, providers, or saved user data.
        let styleView = SettingsView(controller: controller)
        let savedChoice = controller.cleanupChoice
        controller.cleanupChoice = .localRules
        styleView.cleanupStyle.wrappedValue = .faithful
        precondition(styleView.cleanupStyle.wrappedValue == .faithful)
        precondition(!controller.cleanupFillers && !controller.cleanupCorrections && !controller.cleanupLists && !controller.cleanupPunctuation && !controller.cleanupSpoken)
        styleView.cleanupStyle.wrappedValue = .strict
        precondition(styleView.cleanupStyle.wrappedValue == .strict && controller.cleanupFillers && controller.cleanupPunctuation)
        controller.cleanupChoice = .customAPI
        styleView.cleanupStyle.wrappedValue = .professional
        precondition(styleView.cleanupStyle.wrappedValue == .professional && controller.cleanupPolish && controller.cleanupProfessional)
        styleView.cleanupStyle.wrappedValue = .faithful
        precondition(styleView.cleanupStyle.wrappedValue == .faithful)
        styleView.cleanupStyle.wrappedValue = .polish
        precondition(styleView.cleanupStyle.wrappedValue == .polish && controller.cleanupPolish && !controller.cleanupProfessional && controller.cleanupSpoken)
        styleView.cleanupStyle.wrappedValue = .strict
        controller.cleanupChoice = savedChoice
        let compact = CommandLine.arguments.contains("--compact")
        let opaque = CommandLine.arguments.contains("--opaque")
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: compact ? 880 : 1000, height: compact ? 680 : args.contains("--tall") ? 1100 : 680), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "Murmur UI Check"; window.titleVisibility = .hidden; window.titlebarAppearsTransparent = true; window.backgroundColor = Palette.paperNS
        window.contentView = NSHostingView(rootView: SettingsView(controller: controller,
            editingModeID: args.contains("--mode-editor") ? WritingMode.presets[0].id : nil).environment(\.utilityOpaquePreview, opaque))
        window.contentMinSize = NSSize(width: 880, height: 680)
        window.isReleasedWhenClosed = false
        window.center(); window.makeKeyAndOrderFront(nil)
        var entries = [
            HistoryEntry(id: UUID().uuidString, recordedAt: Date(), rawText: "Synthetic transcript pasted into an unreadable field. Long text wraps for visual inspection.\nSecond synthetic paragraph keeps this fixture free of real transcript content.", finalText: "Synthetic transcript pasted into an unreadable field. Long text wraps for visual inspection.\nSecond synthetic paragraph keeps this fixture free of real transcript content.", insertionStatus: InsertionStatus.unverified.rawValue),
            { var e = HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-1800), rawText: "um so the synthetic meeting is at three pm on tuesday we should uh bring the slides", finalText: "So the synthetic meeting is at 3 PM on Tuesday. We should bring the slides.", insertionStatus: (args.contains("--history-diff") ? InsertionStatus.savedNotInserted : InsertionStatus.verified).rawValue)
              e.transcriptionProvider = "local"; e.cleanupProvider = "local-model"; e.cleanupOutcome = .modelAccepted; return e }(),
            HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-3600), rawText: "Synthetic verified transcript.", finalText: "Synthetic verified transcript.", insertionStatus: InsertionStatus.verified.rawValue)
        ]
        entries[0].cleanupOptions = CleanupOptions(professional: true); entries[0].cleanupFallback = CleanupFailureReason.unexplainedDeletion.rawValue
        entries[0].cleanupOutcome = .localRules
        if CommandLine.arguments.contains("--paste-failed") { entries[0].insertionStatus = InsertionStatus.failed.rawValue }
        // --history-edit: a voice edit entry (synthetic selection, instruction and result), opened in History.
        if args.contains("--history-edit") {
            var e = HistoryEntry(id: UUID().uuidString, recordedAt: Date(), rawText: "make this friendlier and fix the typo", finalText: "Hi Sam, thanks for sending the draft. I made a few small fixes.", insertionStatus: InsertionStatus.edited.rawValue)
            e.writingModeName = "Voice edit"; e.editOriginal = "hi sam, thx for sending the draft. i made a few small fixs"; entries.insert(e, at: 0)
        }
        if CommandLine.arguments.contains("--empty") { entries = [] }
        // Insights: timed dictations into a few apps on most of the last 24 days.
        if args.contains("--insights") && !args.contains("--empty") {
            let apps = ["com.apple.TextEdit", "com.apple.mail", "com.apple.Notes", "com.apple.Safari"]
            let text = "Synthetic dictation about the quarterly plan with um enough words to measure a speaking pace."
            for day in 0..<24 where day % 7 != 5 && day != 9 {
                for n in 0..<(1 + day % 3) {
                    var entry = HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-Double(day) * 86400 - Double(n) * 1800),
                                             rawText: text, finalText: text.replacingOccurrences(of: "um ", with: ""), insertionStatus: InsertionStatus.unverified.rawValue)
                    entry.seconds = 6.5 + Double(n); entry.app = apps[(day + n) % apps.count]
                    entries.append(entry)
                }
            }
        }
        if CommandLine.arguments.contains("--matrix") && !CommandLine.arguments.contains("--empty") {
            entries += [
                HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-7200), rawText: "Synthetic failed paste before cleanup.", finalText: "Synthetic failed paste.", insertionStatus: InsertionStatus.failed.rawValue),
                HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-10800), rawText: "Synthetic pending delivery. Check the target before recovering this text.\nSecond line for wrapping.", finalText: "Synthetic pending delivery. Check the target before recovering this text.\nSecond line for wrapping.", insertionStatus: InsertionStatus.insertionPending.rawValue),
                HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-14400), rawText: "Synthetic saved output.", finalText: "Synthetic saved output.", insertionStatus: InsertionStatus.savedNotInserted.rawValue),
                HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-18000), rawText: "Synthetic cleanup hold.", finalText: "", insertionStatus: InsertionStatus.heldCleanup.rawValue),
                HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-21600), rawText: "Synthetic transcript hold.", finalText: "", insertionStatus: InsertionStatus.heldTranscript.rawValue)
            ]
        }
        // --professional-hint: an open Professional offer on a synthetic run-on dictation.
        let hintText = "so I wanted to tell you about the plan for the launch and we should get the designs done and then the copy and then the review and then ship it on Thursday no I mean Friday and also tell the team about the new pricing page and the docs and the changelog and make sure support knows"
        let hintEntry = HistoryEntry(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-300), rawText: hintText, finalText: hintText, insertionStatus: InsertionStatus.verified.rawValue)
        if CommandLine.arguments.contains("--professional-hint") { entries.insert(hintEntry, at: 0) }
        let sessionOnlyID = entries.first(where: { $0.status == .savedNotInserted })?.id
        do {
            guard let dataDirectory = controller.dataDirectory else { fatalError("Missing isolated fixture storage.") }
            let store = try HistoryStore(directory: dataDirectory.appendingPathComponent("history"))
            for entry in entries { try store.save(entry) }
        } catch { fatalError("Could not seed isolated History fixture.") }
        DispatchQueue.main.async {
            controller.refreshHistory()
            if args.contains("--professional-hint") {
                controller.professionalHint.offer(hintEntry.id)
            }
            controller.recoveryEntryID = args.contains("--history-diff") ? entries.first { $0.status == .savedNotInserted }?.id : entries.first?.id
            if args.contains("--history-diff") || args.contains("--history-edit") { DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { controller.screenRequest = .history } }
            controller.sessionOnlyIDs = sessionOnlyID.map { Set([$0]) } ?? []
            if CommandLine.arguments.contains("--dictionary") {
                controller.addDictionaryEntry(preferred: "Kubernetes", variants: "")
                controller.addDictionaryEntry(preferred: "kubectl", variants: "cube cuddle, cube control")
                controller.addDictionaryEntry(preferred: "Siobhan", variants: "shivon")
                if let entry = controller.dictionaryEntries.last { controller.updateDictionaryEntry(entry, preferred: entry.preferred, variants: "shivon", enabled: false) }
                controller.blockedWords = [.heard("versel"), .spelling("Akme")]
                // After the page's own refresh (fixture History has none), before the 2 s snapshot.
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    controller.dictionarySuggestions = [DictionarySuggestion(preferred: "Cerebras", heard: ["Serbrass", "Serebrass", "Serobrass"], dictations: 7),
                                                        DictionarySuggestion(preferred: "Proxmox", heard: ["Proxmox", "Proxmonk"], dictations: 6)]
                }
            }
            if args.contains("--cues") {
                controller.addCue(phrase: "my website", text: "https://example.com/sample")
                controller.addCue(phrase: "my address", text: "1 Sample Street\nExample Town, EX 00000")
            }
            if args.contains("--insights") || args.contains("--history-off") { controller.screenRequest = .insights }
            if args.contains("--history") { controller.screenRequest = .history }
            if args.contains("--settings") { controller.screenRequest = .settings }
            if args.contains("--dictionary") { controller.screenRequest = .settingsPane(.dictionary) }
            if args.contains("--cues") { controller.screenRequest = .settingsPane(.cues) }
            if args.contains("--cleanup-pane") { controller.screenRequest = .settingsPane(.cleanup) }
            if args.contains("--modes-pane") { controller.screenRequest = .settingsPane(.modes) }
            if args.contains("--transcription-pane") { controller.screenRequest = .settingsPane(.transcription) }
            // --cleanup-style -1|0|1|2: Faithful, Strict, Polish or Professional, with an AI model stored.
            if let flag = args.firstIndex(of: "--cleanup-style"), args.indices.contains(flag + 1), let style = Int(args[flag + 1]) {
                controller.cleanupChoice = args.contains("--custom-cleanup") ? .customAPI : .localModel; controller.cleanupPolish = style >= 1; controller.cleanupProfessional = style == 2
                if style == -1 { controller.useFaithfulTranscription() }
            }
            // --local-model ready|downloading|failed|notice|update: the managed local cleanup row in its states (no download runs).
            if let flag = args.firstIndex(of: "--local-model"), args.indices.contains(flag + 1) {
                switch args[flag + 1] {
                case "ready": controller.showLocalModelFixture(.ready)
                case "downloading": controller.showLocalModelFixture(.downloading(ModelDownloadProgress(bytesDone: 214_000_000, totalBytes: 560_000_000)))
                case "failed": controller.showLocalModelFixture(.failed(.diskFull))
                case "notice": controller.showLocalModelFixture(.notDownloaded)
                case "update": controller.showLocalModelFixture(.notDownloaded, message: ModelReady.updateRowMessage)
                default: controller.showLocalModelFixture(.notDownloaded)
                }
            }
            if let flag = args.firstIndex(of: "--section"), args.indices.contains(flag + 1) {
                if let pane = Int(args[flag + 1]).flatMap(SettingsPane.init(rawValue:)) { controller.screenRequest = .settingsPane(pane) }
            }
            if args.contains("--speaker-settings") { controller.speakerSettings.showFixture(enabled: true, status: args.contains("--ready") ? .ready : .notDownloaded) }
            // --meeting-setup shows the Meetings page's first-time setup; --meeting-languages a,b saves meeting languages.
            controller.meetingLanguages = args.contains("--meeting-setup") ? [] : args.firstIndex(of: "--meeting-languages").map { args[$0 + 1].split(separator: ",").map(String.init) } ?? ["en"]
            if args.contains("--meeting-setup") { controller.screenRequest = .meetings }
            if args.contains("--meeting-settings") { controller.meetingsUseCustomProvider = args.contains("--cloud"); controller.screenRequest = .settingsPane(.meetings) }
            if args.contains("--settings-search") { controller.screenRequest = .settingsSearch }
            controller.voiceLevel = 0.6
            controller.phase = CommandLine.arguments.contains("--processing") ? .processing : CommandLine.arguments.contains("--listening") ? .listening : CommandLine.arguments.contains("--saved") ? .saved : .error
            controller.message = CommandLine.arguments.contains("--credential-warning")
                ? "Save the API key again for this server address."
                : "Model verification failed. Choose the pinned model folder."
            if args.contains("--quiet") { controller.dismissStatus() }
            if CommandLine.arguments.contains("--recovery") { controller.hasRecoveryAudio = true }
            if CommandLine.arguments.contains("--live-words") {
                controller.liveWordsFixture = true; controller.liveText = ("Synthetic live words keep the newest tail of a long dictation on two lines, with only the last word dimm")
            }
            controller.bubble.show(phase: controller.phase, message: controller.message)
            if args.contains("--loop-bubble") {
                var visible = true
                Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in
                    MainActor.assumeIsolated {
                        visible.toggle()
                        if visible { controller.bubble.show(phase: .listening, message: "") }
                        else { controller.bubble.hide() }
                    }
                }
            }
            if CommandLine.arguments.contains("--paste-failed") {
                controller.bubble.hide() // As the controller does before inserting: the card must not follow the hide's slide to the notch.
                controller.showInsertionResult(InsertionResult(status: .failed, detail: "fixture", snapshot: nil), entryID: entries.first?.id,
                                               text: "Synthetic dictation that could not be pasted because the target app closed. It wraps to show the card's three-line limit and keeps going past it.")
            }
            if CommandLine.arguments.contains("--not-pasted") {
                controller.showNotPasted(.insertionOff, saved: "Saved to History.", entryID: entries.first?.id, text: "Synthetic dictation saved with Insert at the cursor off.")
            }
            // --notch / --no-notch: a synthetic notched or notch-less display for the bubble cards.
            if args.contains("--notch") || args.contains("--no-notch") {
                let notch = args.contains("--notch")
                controller.bubble.geometryOverride = BubbleGeometry(screenFrame: NSRect(x: 0, y: 0, width: 1512, height: 982), visibleFrame: NSRect(x: 0, y: 0, width: 1512, height: notch ? 950 : 982),
                                                                    left: notch ? NSRect(x: 0, y: 950, width: 656, height: 32) : nil, right: notch ? NSRect(x: 856, y: 950, width: 656, height: 32) : nil, notchHeight: notch ? 32 : 0)
            }
            if let i = args.firstIndex(of: "--mode-recovery") {
                let name = args.indices.contains(i + 1) ? args[i + 1] : ""
                controller.showModeRecoveryFixture(stage: name == "copy" ? .copy("x") : name == "reverted" ? .reverted : .offer, mode: args.contains("--used-mode"))
                if let path = ProcessInfo.processInfo.environment["MURMUR_PILL_PNG"] { controller.bubble.writeSnapshot(to: URL(fileURLWithPath: path)) }
            }
            if let i = args.firstIndex(of: "--voice-edit") {
                controller.showVoiceEditFixture(args.indices.contains(i + 1) ? args[i + 1] : "")
                if let path = ProcessInfo.processInfo.environment["MURMUR_PILL_PNG"] { controller.bubble.writeSnapshot(to: URL(fileURLWithPath: path)) }
            }
            if CommandLine.arguments.contains("--undo") {
                controller.bubble.showUndo(screen: nil)
                if let path = ProcessInfo.processInfo.environment["MURMUR_PILL_PNG"] { controller.bubble.writeSnapshot(to: URL(fileURLWithPath: path)) }
            }
            if CommandLine.arguments.contains("--learned") { controller.bubble.showLearned("Aakash", entryID: UUID(), screen: nil) }
            // Meeting detection: --call-offer shows Zoom's offer, --call-offer-browser Arc's. The fixture's
            // defaults persist, so the switch and Never list are reset every run; --never seeds the list.
            UserDefaults.standard.removeObject(forKey: CallDetectionSettings.enabledKey)
            UserDefaults.standard.set(args.contains("--never") ? ["us.zoom.xos", "company.thebrowser.Browser"] : [], forKey: CallDetectionSettings.neverKey)
            controller.callOffers.enabled = true; controller.callOffers.removeNever("")
            if let id = args.contains("--call-offer") ? "us.zoom.xos" : args.contains("--call-offer-browser") ? "company.thebrowser.Browser" : nil,
               let app = CallDetector.app(for: id) { controller.callOffers.show(CallOffer(app: app)) }
            // Tips: --tip-milestone / --tip-voice-edit show the floating card (MURMUR_TIP_PNG writes it),
            // --home-milestone the Home card, --explore-dots the Cues dot and intro. Tip state is reset every run.
            Tips.allKeys.forEach(UserDefaults.standard.removeObject(forKey:))
            if args.contains("--home-milestone") {
                UserDefaults.standard.set(10_240, forKey: Tips.wordsKey); UserDefaults.standard.set(9_000.0, forKey: Tips.savedKey)
                UserDefaults.standard.set(10_000, forKey: Tips.rungKey); controller.screenRequest = .home
            }
            if args.contains("--explore-dots") {
                UserDefaults.standard.set(Date().addingTimeInterval(-10 * 86_400), forKey: Tips.firstUseKey)
                controller.screenRequest = .settingsPane(args.contains("--tab") ? .cues : .general)
            }
            controller.tips.changed() // seeded after the first render
            if args.contains("--tip-milestone") || args.contains("--tip-voice-edit") {
                controller.tips.show(args.contains("--tip-milestone") ? .milestone(.init(words: 10_000, secondsSaved: 9_000)) : .voiceEdit)
                if let path = ProcessInfo.processInfo.environment["MURMUR_TIP_PNG"] { controller.tips.writeSnapshot(to: URL(fileURLWithPath: path)) }
            }
            if CommandLine.arguments.contains("--keep-notice") {
                Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { _ in
                    MainActor.assumeIsolated { controller.bubble.showLearned("Aakash", entryID: UUID(), screen: nil) }
                }
            }
            // --report-issue opens the Report an Issue window with synthetic values (--report-details with the details section open); quit it from the menu, since --snapshot does not close it.
            if args.contains("--report-issue") {
                let synthetic = Diagnostics(
                    appVersion: "0.3.0", build: "12", macOS: "Version 26.1 (Build 25B1)", macModel: "Mac15,6", microphone: "authorized",
                    accessibilityAllowed: true, inputMonitoringAllowed: false, transcription: .thisMac(model: "FluidInference/parakeet-unified-en-0.6b-coreml"),
                    cleanup: .service(name: "Local model (this Mac)", model: "clouddevaakash/murmur-cleanup-2b@555400a"), cleanupStyle: "Polish", historyOn: true, retention: "30 days",
                    models: [("Parakeet Unified English 0.6B", true), ("Local cleanup", false)],
                    lastResult: .init(insertionStatus: "verified", cleanupFailure: "lengthRatio", cleanupPath: "modelAccepted", totalSeconds: 2.3))
                print("Report details:\n\(synthetic.details)\nReport URL length: \(IssueReport.url(body: IssueReport.body(description: "", details: synthetic.details)).absoluteString.count)")
                fixtureReportIssue.show(synthetic, shortcut: controller.shortcutName, expanded: args.contains("--report-details"))
            }
            // Meeting notes: --meetings shows the page, --meeting-live the live panel, --consent the sheet.
            // The fixture's defaults persist: set the panel's minimized state every time (--collapsed).
            UserDefaults.standard[Prefs.meetingPanelCollapsed] = args.contains("--collapsed")
            UserDefaults.standard.removeObject(forKey: Prefs.meetingPanelTopRight.key)
            if args.contains("--meetings") || args.contains("--meeting-live") || args.contains("--consent") {
                var call = Meeting(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-7200), source: MeetingSource(name: "Zoom", bundleID: "us.zoom.xos"))
                call.insert(stream: .them, start: 2, end: 16, text: "Thanks for joining. Let's go over the synthetic launch plan for next week.")
                call.insert(stream: .me, start: 3, end: 17, text: "Sure. let's go over the synthetic launch plan for next week")
                call.insert(stream: .me, start: 18, end: 31, text: "I can draft the checklist by Thursday if that works for everyone.")
                call.insert(stream: .them, start: 30, end: 44, text: "That works. Send it to the team channel when it's ready.")
                call.myNotes = "Synthetic note: checklist due Thursday."
                call.seconds = 1834; call.finished = true
                // Speaker separation: --speakers shows separated, renamed speakers; --separating the progress line with Skip.
                if args.contains("--speakers") || args.contains("--separating") {
                    call.speakers = SpeakerSeparation(state: .pending, threshold: MeetingSpeakers.threshold)
                    call.insert(stream: .them, start: 45, end: 58, text: "I can review it on Friday before the synthetic launch.")
                    if args.contains("--speakers") {
                        MeetingSpeakers.apply([SpeakerSegment(speaker: "a", start: 2, end: 16), SpeakerSegment(speaker: "b", start: 30, end: 44),
                                               SpeakerSegment(speaker: "c", start: 45, end: 58)], to: &call)
                    }
                }
                // --meeting-notes: synthetic notes, as the notes route would write them.
                if args.contains("--meeting-notes") {
                    let ids = call.lines.map(\.id)
                    // With --speakers the notes name the speaker ("Speaker 2"), and the rename below relabels them.
                    let them = MeetingSpeakers.label(call.lines[3], in: call)
                    var notes = MeetingNotes(title: "Synthetic launch checklist plan", summary: "The call went over the synthetic launch plan for next week. Me offered to draft the checklist by Thursday, and \(them) asked for it in the team channel.",
                                             route: "Apple Intelligence (on-device)", writtenAt: Date())
                    notes.keyPoints = [NoteItem(text: "The synthetic launch plan is for next week", lines: [ids[0]])]
                    notes.decisions = [NoteItem(text: "Share the checklist in the team channel", lines: [ids[2], ids[3]])]
                    notes.actions = [NoteItem(text: "Draft the checklist", lines: [ids[2], ids[3]], owner: "Me", due: "Thursday", done: false),
                                     NoteItem(text: "Send it to the team channel when ready", lines: [ids[3]], owner: "Me", done: true)]
                    notes.dropped = 1
                    notes.outdated = args.contains("--outdated") ? true : nil
                    call.notes = notes
                }
                if args.contains("--speakers") { call.renameSpeaker(2, to: "Priya") }
                var solo = Meeting(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-86400 * 2), source: .microphoneOnly)
                solo.insert(stream: .me, start: 0, end: 12, text: "Synthetic voice memo recorded with only the microphone.")
                solo.finished = true
                var live = Meeting(id: UUID().uuidString, recordedAt: Date(), source: MeetingSource(name: "Google Chrome", bundleID: "com.google.Chrome"))
                live.insert(stream: .them, start: 0, end: 14, text: "Synthetic participant line in a live meeting.")
                live.insert(stream: .me, start: 14, end: 28, text: "Synthetic reply that wraps onto a second line in the narrow live panel.")
                live.seconds = 754
                let recorder = controller.meetingRecorder
                let panel = MeetingPanelPresenter(recorder: recorder) { fixtureGeometry(notch: args.contains("--fake-notch")) }
                recorder.onLiveChange = { panel.update() }
                recorder.showFixture([call, solo], live: args.contains("--meeting-live") ? live : nil, leftovers: [solo.id: 2], separating: args.contains("--separating") ? [call.id] : [], unsaved: args.contains("--unsaved") ? [solo.id] : [])
                if args.contains("--consent") { recorder.consentPending = .everything }
                if args.contains("--notes-choice") { recorder.notesChoicePending = call.id }
                if args.contains("--transcript") { MeetingsPage.initialTab = .transcript }
                if args.contains("--lecture-notes") { MeetingsPage.initialTab = .lecture }
                controller.screenRequest = .meetings
                fixtureMeetingPanel = panel
            }
            if args.contains("--diff-only") {
                window.contentView = NSHostingView(rootView: VStack(alignment: .leading, spacing: 8) {
                    Text("WHAT CHANGED").font(.system(size: 11, design: .monospaced)).foregroundStyle(Palette.ink70)
                    WordDiffText(raw: "um so the synthetic meeting is at three pm on tuesday we should uh bring the slides", final: "So the synthetic meeting is at 3 PM on Tuesday. We should bring the slides.")
                        .font(WindowType.body(15)).lineSpacing(3)
                }.padding(24).frame(width: 760, height: 180, alignment: .topLeading).background(Palette.paper).preferredColorScheme(.dark))
            }
            if CommandLine.arguments.contains("--bubble-only") { window.orderOut(nil) }
            // --snapshot PATH renders the window offscreen to a PNG and quits; works with the screen locked.
            if let flag = args.firstIndex(of: "--snapshot"), args.indices.contains(flag + 1) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
                    if let view = args.contains("--report-issue") ? fixtureReportIssue.window?.contentView : args.contains("--meeting-live") ? fixtureMeetingPanel?.view : window.contentView, let image = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
                        view.cacheDisplay(in: view.bounds, to: image)
                        try? image.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: args[flag + 1]))
                    }
                    application.terminate(nil)
                }
            }
            print("Fixture content: \(window.contentLayoutRect.size), full window: \(window.frame.size)"); fflush(stdout)
        }
        controller.openSettings = { application.activate(ignoringOtherApps: true); window.makeKeyAndOrderFront(nil) }
        controller.updateMenu = {
            if controller.phase == .ready {
                precondition(controller.history.count == entries.count && controller.recoveryEntryID == entries.first?.id)
                print("UI dismissal preserved synthetic recovery history."); fflush(stdout)
            }
        }
        application.activate(ignoringOtherApps: true)
        withExtendedLifetime((controller, window, delegate)) { application.run() }
        controller.shutdown()
    }
}

/// The live panel fixture ignores the real display: no notch, or with --fake-notch a synthetic 14-inch one on the main screen's frame.
@MainActor func fixtureGeometry(notch: Bool) -> BubbleGeometry {
    let screen = NSScreen.main ?? NSScreen.screens[0], full = screen.frame, band: CGFloat = 32, side = (full.width - 200) / 2
    return BubbleGeometry(screen: screen, screenFrame: full, visibleFrame: notch ? NSRect(x: full.minX, y: full.minY, width: full.width, height: full.height - band) : screen.visibleFrame,
                          left: notch ? NSRect(x: full.minX, y: full.maxY - band, width: side, height: band) : nil,
                          right: notch ? NSRect(x: full.maxX - side, y: full.maxY - band, width: side, height: band) : nil, notchHeight: band)
}
