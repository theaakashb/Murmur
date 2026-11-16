import AppKit
import SwiftUI

// The Settings pane bodies (extension SettingsView) and the controls only Settings uses.

extension SettingsView {
    /// Settings → Meetings (decision of October 15, 2026, amending Q2): the meeting transcription route.
    var meetingRoutes: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 4) {
                eyebrow("Meeting transcription")
                SettingInfoButton(text: "A change applies to meetings started afterwards; a meeting in progress keeps its route. Dictation’s route is set separately in Transcription.\n\n" + (controller.meetingTranscription == .local
                    ? "Meeting audio is transcribed on this Mac and never leaves it. This is the default."
                    : controller.meetingCloudNote.map { $0.note + "\n\n" + $0.disclosure } ?? "Choose a cloud transcriber in Settings → Transcription."))
            }.padding(.top, 20)
            segmented([(MeetingTranscription.local, "On this Mac (Parakeet)"), (MeetingTranscription.cloud, "Cloud (\(controller.cloudTranscriber.displayName))")], $controller.meetingTranscription)
            if controller.meetingTranscription == .cloud && controller.meetingCloudNote == nil {
                help("No cloud transcriber is set up. Choose one and save its key in Settings → Transcription and API Keys.", color: EditorialPalette.ink)
            }
            HStack(spacing: 4) {
                eyebrow("Meeting notes")
                SettingInfoButton(text: controller.meetingNotesRoute == nil ? "Not chosen yet: Murmur asks after your first meeting." : "Notes are written after Stop from the transcript and your own notes. Every item cites the lines it rests on; items the transcript does not support are left out.")
            }.padding(.top, 20)
            MeetingNotesRoutePicker(controller: controller, tooltipDetails: true, selection: Binding(get: { controller.meetingNotesRoute ?? .none }, set: controller.chooseNotesRoute))
        }
    }

    var meetingSettings: some View {
        VStack(alignment: .leading, spacing: 12) {
            meetingRoutes
            CallDetectionRows(presenter: controller.callOffers).id("call-detection")
            SpeakerSettingsSection(settings: controller.speakerSettings).id("speaker-separation")
        }
    }

    var recordingSettings: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                eyebrow("Shortcut")
                SettingInfoButton(text: controller.pressToToggle ? "Press the shortcut to start recording and press it again to stop, or hold it and release. Escape cancels." : "Hold to record until you release. Double-press to keep recording until you press again. Escape cancels.")
                    .accessibilityLabel("How to use the shortcut")
            }.padding(.top, 12)
            HStack(spacing: 12) {
                Text("Dictation shortcut").font(.system(size: 13))
                Spacer()
                ShortcutPresetPicker(controller: controller)
            }.padding(.vertical, 12)
            ShortcutRecorder(controller: controller).padding(.bottom, 12)
            toggleRow("Press once to start, again to stop", "Holding still works: release to stop. Off means hold to record.", isOn: $controller.pressToToggle)
            softRule.padding(.vertical, 12)
            eyebrow("Audio")
            HStack(alignment: .top, spacing: 16) {
                titled("Microphone", microphoneInfo)
                Spacer(minLength: 16)
                microphonePicker.frame(maxWidth: 280, alignment: .trailing)
            }.padding(.top, 16).id("Microphone")
            toggleRow("Start and stop sounds", "A short sound when recording starts and when it stops or is canceled. No sound when text is inserted.", isOn: $controller.recordingSounds)
            softRule.padding(.top, 12)
            eyebrow("Live preview").padding(.top, 24)
            toggleRow(ModelFacts.liveWords.displayName, ModelFacts.liveWords.explanation, isOn: $controller.liveWords).id("live-words")
            if controller.liveWords {
                DisclosureGroup("Live preview model", isExpanded: $showLiveModel) {
                    liveWordsModel.padding(.vertical, 10)
                }.font(.system(size: 13)).padding(.vertical, 12)
                toggleRow("Show live words on cloud routes", "Off: when transcription runs in the cloud, the bubble shows only the level meter and the extra model does not start. The preview always runs on this Mac.", isOn: $controller.liveWordsOnCloud)
            }

        }
    }

    var insertionSettings: some View {
        VStack(alignment: .leading, spacing: 0) {
            toggleRow("Insert at the cursor", "Types into whichever field has the cursor when dictation finishes. Press ⌃⌘V to paste the last dictation again if it is missing; check the field first to avoid a duplicate. The previous clipboard is restored unless you copy something newer. Automatic paste is marked transient; other software may still read or retain it. Murmur never sends a message.", isOn: $controller.automaticInsertion)
            toggleRow("Add a leading space when needed", "Separates dictation from existing text at the cursor. Existing whitespace, punctuation and selected text are left alone.", isOn: $controller.intelligentLeadingSpace)
        }
    }

    var general: some View {
        VStack(alignment: .leading, spacing: 0) {
            eyebrow("Visibility").padding(.top, 12)
            toggleRow("Show menu bar icon", "When hidden, reopen Murmur from Finder or Spotlight.", isOn: $controller.showMenuBar)
            toggleRow("Show in Dock", "Adds Murmur to the Dock and the app switcher. With both icons hidden, reopen Murmur from Finder or Spotlight.", isOn: $controller.showInDock)
            toggleRow("Open at login", "Starts Murmur in the background when you log in, so the shortcut is ready.", isOn: Binding(get: { controller.openAtLogin }, set: controller.setOpenAtLogin))

            Toggle("Show detailed info", isOn: $showDetailedSettingsInfo)
                .font(Font.system(size: 13)).toggleStyle(.checkbox).padding(.top, 12).id("detailed-info")

        }
    }

    func permissionRow(_ permission: Permission) -> some View {
        permissionRow(permission.title, allowed: isAllowed(permission), actionTitle: permission.actionTitle) { grant(permission) }
    }

    func permissionRow(_ title: String, allowed: Bool, actionTitle: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 14) {
            Text(title).font(Font.system(size: 15, weight: .semibold))
            Spacer()
            mono(allowed ? "Allowed ✓" : "Needed", color: allowed ? Palette.accentInk : Palette.ink)
            if !allowed { Button(actionTitle, action: action).buttonStyle(InkButtonStyle()) }
        }
        .frame(minHeight: 44).padding(.vertical, 18)
    }

    var transcription: some View {
        VStack(alignment: .leading, spacing: 0) {
            modelRow(controller.modelSlot, readyText: "Model ready ✓", download: controller.downloadModel, cancel: controller.cancelModelDownload, remove: controller.removeModel)
            .padding(.vertical, 18).padding(.top, 20)

            transcriptionRoutes

            Button { showAdvanced.toggle() } label: {
                mono((showAdvanced ? "▾" : "▸") + " Advanced", size: 13).frame(height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain).padding(.top, 28)
            if showAdvanced {
                HStack(spacing: 16) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Model folder").id("model-folder").font(Font.system(size: 16, weight: .semibold))
                        help((controller.usesCustomModelFolder ? "Custom folder" : "Default folder") + " · " + (controller.modelAvailable ? "model files found ✓" : "no model files in this folder"))
                        if !controller.modelDirectory.isEmpty {
                            Text(URL(fileURLWithPath: controller.modelDirectory).lastPathComponent)
                                .font(.system(size: 12, design: .monospaced)).foregroundStyle(EditorialPalette.ink70).lineLimit(1)
                                .textSelection(.enabled).help(controller.modelDirectory)
                        }
                    }
                    Spacer()
                    if controller.usesCustomModelFolder { Button("Use Default", action: controller.useDefaultModelDirectory).buttonStyle(InkButtonStyle(kind: .outline)) }
                    Button("Choose Folder…", action: controller.chooseModelDirectory).buttonStyle(InkButtonStyle(kind: .outline))
                }
                .padding(.vertical, 16).overlay(alignment: .top) { rule }
                help("A folder you choose takes effect at once.")
            }
        }
    }

    var transcriptionRoutes: some View {
        VStack(alignment: .leading, spacing: 0) {
            eyebrow("Where transcription runs").padding(.top, 28)
            segmented([(TranscriptionRoute.local, "On this Mac (Parakeet)"), (TranscriptionRoute.cloud, "Cloud")], $controller.transcriptionRoute).padding(.top, 8)
            if controller.transcriptionRoute == .local {
                help("Audio stays on this Mac. Downloading the model uses the network once; dictation does not.").padding(.top, 12)
            } else {
                cloudTranscriptionSettings
                disclosure(controller.routeDisclosure(.transcription)).padding(.top, 16)
            }
        }
    }

    var cloudTranscriptionSettings: some View {
        VStack(alignment: .leading, spacing: 0) {
            segmented(CloudTranscriber.allCases.map { ($0, $0.displayName) }, $controller.cloudTranscriber, size: 14).padding(.top, 16)
                .help(controller.cloudTranscriber.displayName)
            if controller.cloudTranscriber == .gemini {
                let modelHint = controller.geminiTranscriptionModel.trimmingCharacters(in: .whitespacesAndNewlines) == GeminiTranscribeProvider.defaultModel
                    ? "Recommended: Gemini 3.5 Transcribe, dedicated speech recognition."
                    : "Other Gemini models may follow spoken instructions, skip words, or invent text."
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 4) {
                        Text("Gemini transcription model").font(Font.system(size: 14, weight: .semibold))
                        settingInfo(modelHint)
                    }
                    field(GeminiTranscribeProvider.defaultModel, text: $controller.geminiTranscriptionModel)
                        .focused($apiFieldFocused).onSubmit(controller.settingsChanged)
                    keyRow(.gemini)
                }.padding(.top, 16)
            }
            if controller.cloudTranscriber == .customAPI {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Transcription API base URL").font(Font.system(size: 14, weight: .semibold))
                    field("https://api.example.com/v1", text: $controller.customTranscriptionAPIBaseURL)
                        .focused($apiFieldFocused).onSubmit(controller.settingsChanged)
                    Text("Transcription model name").font(Font.system(size: 14, weight: .semibold))
                    field("whisper-1", text: $controller.customTranscriptionModel)
                        .focused($apiFieldFocused).onSubmit(controller.settingsChanged)
                    keyRow(.customTranscription)
                    help("Needs an OpenAI-compatible /v1/audio/transcriptions endpoint. Separate from cleanup.")
                }.padding(.top, 16)
            }
        }
    }

    @ViewBuilder func disclosure(_ text: String?) -> some View {
        if let text {
            Text(text)
                .font(Font.system(size: 14)).lineSpacing(2).fixedSize(horizontal: false, vertical: true)
                .padding(16).frame(maxWidth: .infinity, alignment: .leading).background(EditorialPalette.accent16)
        }
    }

    /// The one view-side mapping between the cleanup style and the controller's switches; every view reads and writes it here.
    var cleanupStyle: Binding<CleanupStyle> {
        Binding(get: { controller.cleanupStyle }, set: { controller.cleanupStyle = $0 })
    }

    static let aiCleanupChoices = CleanupChoice.selectable.filter { $0 != .localRules }

    var cleanupChoices: some View {
        VStack(alignment: .leading, spacing: 0) {
            eyebrow("Cleanup style").padding(.top, 28)
            HStack(spacing: 4) {
                segmented(CleanupStyle.allCases.map { ($0, $0.label) }, cleanupStyle, size: 14)
                SettingInfoButton(text: CleanupStyle.allCases.map(\.info).joined(separator: "\n"))
                    .accessibilityLabel("About cleanup styles")
            }.padding(.top, 8)
            help(cleanupStyle.wrappedValue.help)
                .padding(.top, 8)
            if cleanupStyle.wrappedValue.needsModel {
                eyebrow("AI model").padding(.top, 20)
                segmented(Self.aiCleanupChoices.map { ($0, short($0.displayName)) }, $controller.cleanupChoice, size: 14)
                    .padding(.top, 8).help(controller.cleanupChoice.displayName)
                if controller.cleanupChoice == .localModel { localModelSection }
            }
            if controller.geminiCleanupNotice {
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    help(LocalCleanupFacts.removedGeminiNotice)
                    Button("Dismiss", action: controller.dismissGeminiCleanupNotice).buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                }.padding(.top, 12)
            }
        }
    }

    /// Download row and capabilities of the managed local cleanup model, shown in both the Settings and the setup flows.
    var localModelSection: some View {
        let slot = controller.localSlot, row = slot.row
        // Narrow (compact setup) windows stack the buttons under the name and status instead of squeezing them.
        let hint = !row.isReady && row.progress == nil && row.actions.contains(.download)
        return modelRow(slot, narrow: true, download: controller.downloadLocalModel, cancel: controller.cancelLocalModelDownload, remove: controller.removeLocalModel,
                        extras: hint ? [AnyView(help("Until it is downloaded, dictations are cleaned with Local rules."))] : [])
            .padding(.top, 16)
    }

    var cleanup: some View {
        VStack(alignment: .leading, spacing: 0) {
            cleanupChoices
            rule.padding(.top, 20)
            ForEach(CleanupToggle.allCases, id: \.self) { toggle in
                toggleRow(toggle.title, toggle.help(inMode: false), isOn: $controller[dynamicMember: toggle.global])
            }
            help("If an AI rewrite is rejected, clear spoken corrections can still apply. The raw transcript stays in History.").padding(.top, 16)

            if cleanupStyle.wrappedValue.needsModel, controller.cleanupChoice == .appleIntelligence, let reason = AppleSystemModel.status() {
                help(reason.userMessage + " Until then, local rules clean dictations.").padding(.top, 8)
            }
            toggleRow("Use screen context", "AI cleanup also gets the selected text, the clipboard and the focused window's text, as reference for spelling names and terms. Never read under Secure Input, in password fields, or from clipboard items marked as passwords. Not used by On this Mac or Local model. Modes can turn it on or off.", isOn: $controller.screenContext)
                .padding(.top, 12).disabled(!cleanupStyle.wrappedValue.needsModel)
            if cleanupStyle.wrappedValue.needsModel, controller.cleanupChoice == .customAPI {
                customCleanupSettings
            }
        }
    }

    var customCleanupSettings: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Cleanup API base URL").font(Font.system(size: 14, weight: .semibold))
            field("https://api.cerebras.ai/v1", text: $controller.customAPIBaseURL)
                .focused($apiFieldFocused).onSubmit(controller.settingsChanged)
            Text("Model name").font(Font.system(size: 14, weight: .semibold)).padding(.top, 4)
            field("gpt-oss-120b", text: $controller.customAPIModel)
                .focused($apiFieldFocused).onSubmit(controller.settingsChanged)
            keyRow(.customCleanup)
            help("This provider needs OpenAI-compatible POST /v1/chat/completions. Its address and API key are separate from transcription. Enter the provider's exact model ID. A key is sent only over HTTPS; local endpoints can be used without one.")
        }
        .padding(.top, 16)
    }

    // MARK: Writing modes (gaps #6-#8)

    var modes: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                Text("How modes are chosen").font(Font.system(size: 14, weight: .semibold))
                SettingInfoButton(text: "Preset trigger words work at the start of a dictation; other modes also accept them at the end. Spoken triggers take priority, then the website, then the app focused when you started. If two modes match the same way, the first in this list wins. Anything a mode leaves as “Same as Cleanup” follows the Cleanup settings.")
            }.padding(.top, 12)
            rule.padding(.top, 20)
            ForEach($controller.writingModes) { $mode in
                modeRow($mode).overlay(alignment: .bottom) { softRule }
            }
            if controller.writingModes.isEmpty { help("No modes yet.").padding(.vertical, 16) }
            Button("Add Mode") {
                let mode = WritingMode(); controller.writingModes.append(mode); editingModeID = mode.id
            }
            .buttonStyle(InkButtonStyle(kind: .outline)).fixedSize().padding(.top, 16)
        }
    }

    private func modeSummary(_ mode: WritingMode) -> String {
        var parts: [String] = []
        if !mode.triggers.isEmpty { parts.append("Said: " + mode.triggers.joined(separator: ", ")) }
        if !mode.websites.isEmpty { parts.append(mode.websites.joined(separator: ", ")) }
        if !mode.apps.isEmpty { parts.append(mode.apps.map(appName).joined(separator: ", ")) }
        return parts.isEmpty ? "Matches nothing yet" : parts.joined(separator: " · ")
    }

    func appName(_ bundleID: String) -> String {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID).map { FileManager.default.displayName(atPath: $0.path).replacingOccurrences(of: ".app", with: "") } ?? bundleID
    }

    /// Apps dictated into since launch, then running apps, without ones already in the mode.
    private func pickableApps(excluding chosen: [String]) -> [String] {
        let running = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular && $0.processIdentifier != getpid() }.compactMap(\.bundleIdentifier)
        var seen = Set(chosen)
        return (controller.recentApps + running.sorted { appName($0).localizedCaseInsensitiveCompare(appName($1)) == .orderedAscending }).filter { seen.insert($0).inserted }
    }

    /// nil is "Same as Cleanup".
    private func modeStyle(_ mode: Binding<WritingMode>) -> Binding<CleanupStyle?> {
        Binding(get: {
            guard let o = mode.wrappedValue.options else { return nil }
            return CleanupStyle(o)
        }, set: { value in
            guard let value else { mode.wrappedValue.options = nil; return }
            guard value != .faithful else { mode.wrappedValue.options = .faithful; return }
            let current = mode.wrappedValue.options.flatMap { $0.anyEnabled ? $0 : nil }
            var o = current ?? CleanupOptions(fillers: controller.cleanupFillers, corrections: controller.cleanupCorrections, lists: controller.cleanupLists,
                                              punctuation: controller.cleanupPunctuation, spoken: controller.cleanupSpoken)
            if !o.anyEnabled { o = .standard } // Over Faithful settings, start from the defaults.
            o.polish = value.needsModel; o.professional = value == .professional
            mode.wrappedValue.options = o
        })
    }

    private func modeFlag(_ mode: Binding<WritingMode>, _ key: WritableKeyPath<CleanupOptions, Bool>) -> Binding<Bool> {
        Binding(get: { mode.wrappedValue.options?[keyPath: key] ?? false }, set: { mode.wrappedValue.options?[keyPath: key] = $0 })
    }

    @ViewBuilder private func modeRow(_ mode: Binding<WritingMode>) -> some View {
        let m = mode.wrappedValue
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(m.name.isEmpty ? "Untitled mode" : m.name).font(Font.system(size: 16, weight: .semibold))
                    help(m.enabled ? modeSummary(m) : "Off · " + modeSummary(m)).lineLimit(2)
                }
                Spacer()
                Button(editingModeID == m.id ? "Done" : "Edit") { editingModeID = editingModeID == m.id ? nil : m.id }
                    .buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                Button("Delete", role: .destructive) {
                    controller.writingModes.removeAll { $0.id == m.id }
                    if editingModeID == m.id { editingModeID = nil }
                }
                .buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                .accessibilityLabel("Delete \(m.name.isEmpty ? "untitled mode" : m.name)")
            }
            .padding(.vertical, 12)
            if editingModeID == m.id { modeEditor(mode).padding(.bottom, 16) }
        }
    }

    private func modeEditor(_ mode: Binding<WritingMode>) -> some View {
        let m = mode.wrappedValue
        return VStack(alignment: .leading, spacing: 0) {
            field("Mode name", text: mode.name)
            toggleRow("Mode on", "When off, this mode never matches.", isOn: mode.enabled)

            eyebrow("Custom instructions").padding(.top, 20)
            TextEditor(text: Binding(get: { mode.wrappedValue.instructions }, set: { mode.wrappedValue.instructions = String($0.prefix(WritingModeLimits.instructions)) }))
                .font(Font.system(size: 15)).scrollContentBackground(.hidden)
                .frame(height: m.transform == nil ? 120 : 240).padding(8)
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(EditorialPalette.ink20, lineWidth: 1))
                .accessibilityLabel("Custom instructions").padding(.top, 8)
            help("\(m.instructions.count) of \(WritingModeLimits.instructions) characters. " + (m.transform == nil
                 ? "Sent only to AI cleanup routes. Cleanup still preserves what you said."
                 : "These are the rewriting instructions sent to AI cleanup. Edit them to make this mode your own. Murmur checks the result, but rewrites can still make mistakes."), color: EditorialPalette.ink70)
                .font(Font.system(size: 12)).padding(.top, 6)

            eyebrow("Apps").padding(.top, 20)
            ForEach(m.apps, id: \.self) { app in
                HStack {
                    Text(appName(app)).font(Font.system(size: 15)); help(app, color: EditorialPalette.ink70).font(Font.system(size: 12))
                    Spacer()
                    Button("Remove") { mode.wrappedValue.apps.removeAll { $0 == app } }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }
            }
            Menu("Add App") {
                ForEach(pickableApps(excluding: m.apps), id: \.self) { app in
                    Button(appName(app)) { mode.wrappedValue.apps.append(app) }
                }
            }
            .fixedSize().padding(.top, 8)

            eyebrow("Websites").padding(.top, 20)
            ModeListEditor(items: mode.websites, placeholder: "github.com, docs.google.com/document/*, *.slack.com", label: "Website")
            help("Matches the page open in the browser when recording starts. A site includes its subdomains; add a path to match only that part. The address is read through Accessibility, only while a mode lists a website, and is never saved or sent. A browser that does not show its page to Accessibility matches by app only.", color: EditorialPalette.ink70)
                .font(Font.system(size: 12)).padding(.top, 6)

            eyebrow("Trigger words").padding(.top, 20)
            ModeListEditor(items: mode.triggers, placeholder: "email mode", label: "Trigger")
            help(m.transform == nil ? "Say one at the start or end of a dictation to use this mode; it is removed from the text. Pick a phrase you would not otherwise say there."
                 : "Say one at the very start of a dictation to use this mode; it is removed from the text. This built-in mode rewrites the text on AI cleanup routes (not On this Mac); the custom instructions above control its rewrite.", color: EditorialPalette.ink70)
                .font(Font.system(size: 12)).padding(.top, 6)

            eyebrow("Cleanup").padding(.top, 20)
            segmented([(CleanupStyle?.none, "Same as Cleanup")] + CleanupStyle.allCases.map { ($0, $0.label) }, modeStyle(mode), size: 13).padding(.top, 8)
            if let o = m.options, o.anyEnabled {
                ForEach(CleanupToggle.allCases, id: \.self) { toggle in
                    toggleRow(toggle.title, toggle.help(inMode: true), isOn: modeFlag(mode, toggle.option))
                }
            }

            eyebrow("Tone").padding(.top, 20)
            segmented(Tone.allCases.map { ($0, $0.displayName) }, mode.tone, size: 13).padding(.top, 8)
            help(m.tone == .formal ? "Text stays as cleanup wrote it."
                 : m.tone == .casual ? "Drops commas and the final period. Commas inside numbers stay."
                 : m.tone == .veryCasual ? "Casual, and sentences start in lowercase. “I” and words like “iPhone” keep their capitals."
                 : "The final period becomes “!”.", color: EditorialPalette.ink70)
                .font(Font.system(size: 12)).padding(.top, 6)
            help("Applied by rules after cleanup, on every route. Never applied to an email laid out from “write an email to…”.", color: EditorialPalette.ink70)
                .font(Font.system(size: 12))

            eyebrow("Screen context").padding(.top, 20)
            segmented([(Bool?.none, "Same as Cleanup"), (true, "On"), (false, "Off")], mode.screenContext, size: 13).padding(.top, 8)


        }
    }

    /// Download row for live words (gap #13), shown while the setting is on.
    private var liveWordsModel: some View {
        modelRow(controller.liveWordsSlot, quiet: true, download: controller.downloadLiveWords, cancel: controller.cancelLiveWordsDownload, remove: controller.removeLiveWords)
    }

    /// Download row for dictionary support in on-device transcription (the CTC model).
    var dictionaryModel: some View {
        let failed = controller.dictionarySupportFailed
        return modelRow(controller.dictionarySlot, showReady: !failed, download: controller.downloadDictionaryModel, cancel: controller.cancelDictionaryModelDownload, remove: controller.removeDictionaryModel,
                        extras: [AnyView(help(ModelFacts.dictionary.explanation))]
                            + (failed ? [AnyView(Text(ModelFacts.dictionary.unavailableMessage).font(Font.system(size: 14, weight: .semibold)).fixedSize(horizontal: false, vertical: true))] : []))
            .padding(.vertical, 18).padding(.top, 20)
    }

    /// A Settings model row: the large row, or the quiet one beside the optional live-words switch.
    func modelRow(_ slot: ModelSlot, readyText: String = "Ready ✓", showReady: Bool = true, quiet: Bool = false, narrow: Bool = false,
                  download: @escaping () -> Void, cancel: @escaping () -> Void, remove: @escaping () -> Void, extras: [AnyView] = []) -> some View {
        let name = Text(slot.facts.title)
        return ModelRowView(
            slot: slot,
            title: quiet ? AnyView(name.font(Font.system(size: 14, weight: .medium)).fixedSize(horizontal: false, vertical: true))
                : narrow ? AnyView(name.font(Font.system(size: 18, weight: .semibold)).fixedSize()) : AnyView(name.font(Font.system(size: 18, weight: .semibold))),
            license: slot.license.map(ModelFacts.licenseText),
            ready: showReady ? AnyView(mono(readyText, color: EditorialPalette.accentInk)) : nil,
            status: { text in
                quiet ? AnyView(Text(text).font(Font.system(size: 13)).foregroundStyle(EditorialPalette.ink70).fixedSize(horizontal: false, vertical: true))
                    : AnyView(Text(text).font(Font.system(size: 14, weight: .semibold)).fixedSize(horizontal: false, vertical: true))
            },
            progress: { AnyView(progressBar($0)) },
            message: { AnyView(help($0)) },
            statusTop: quiet ? 0 : 5, spacing: quiet ? 8 : 12, narrow: narrow, extras: extras, download: download, cancel: cancel, remove: remove)
    }

    var dictionary: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                field("Preferred spelling", text: $newPreferred).frame(minWidth: 100, idealWidth: 180, maxWidth: 220).onSubmit(addEntry)
                field("Heard as (optional)", text: $newVariants).help("Separate alternate spellings with commas.").onSubmit(addEntry)
                Button("Add", action: addEntry)
                    .buttonStyle(InkButtonStyle(kind: newPreferred.isBlank() ? .outline : .filled)).fixedSize()
                    .disabled(newPreferred.isBlank())
            }
            .padding(.top, 20)
            if !controller.dictionaryMessage.isEmpty {
                Text(controller.dictionaryMessage).font(Font.system(size: 14, weight: .semibold)).padding(.top, 10)
            }
            VStack(alignment: .leading, spacing: 0) {
                if controller.dictionaryEntries.isEmpty {
                    help("No words yet. Add names and jargon Murmur gets wrong.").padding(.vertical, 16)
                }
                ForEach(controller.dictionaryEntries) { entry in
                    Group { if editingEntryID == entry.id { dictionaryEditRow(entry) } else { dictionaryRow(entry) } }
                        .frame(minHeight: 56).padding(.vertical, 4).overlay(alignment: .bottom) { softRule }
                }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 20)
            if !controller.dictionarySuggestions.isEmpty {
                eyebrow("Suggested").padding(.top, 24)
                help("Words Murmur keeps hearing that aren't in your dictionary. Fix the spelling if needed, then add it.").padding(.top, 4)
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(controller.dictionarySuggestions) { item in suggestionRow(item).overlay(alignment: .bottom) { softRule } }
                }
                .overlay(alignment: .top) { rule }.padding(.top, 12)
            }
            if !controller.blockedWords.isEmpty {
                eyebrow("Not learned").padding(.top, 24)
                help("Skipped because your fixes conflicted or were undone.").padding(.top, 4)
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(controller.blockedWords, id: \.self) { item in blockedRow(item).overlay(alignment: .bottom) { softRule } }
                }
                .overlay(alignment: .top) { rule }.padding(.top, 12)
            }
            HStack {
                help("“Heard as” forms are always replaced. With cleanup on, sound-alikes get fixed too. Edits save immediately.")
                Spacer()
                Button("Reset Learned…", role: .destructive, action: controller.resetLearnedEntries).buttonStyle(InkButtonStyle(kind: .outline))
                    .disabled(!controller.dictionaryEntries.contains { $0.source == .learned } && controller.blockedWords.isEmpty)
            }
            .padding(.top, 16)
            rule.padding(.top, 24)
            toggleRow("Learn from my corrections", "Fix a misheard word once, within a minute, and Murmur adds it to your dictionary and fixes it from your next dictation. Where an app hides its text, it follows your keys for that minute; raw keys stay in memory.", isOn: $controller.learningEnabled)
            toggleRow("Limit learning to readable fields", "No keystroke learning in editors Murmur can't read. Add those words by hand.", isOn: $controller.learningRequiresReadableField)
            eyebrow("On-device dictionary support").padding(.top, 16)
            dictionaryModel
        }
        .onAppear { controller.refreshDictionarySuggestions() }
    }

    func addEntry() {
        guard !newPreferred.isBlank() else { return }
        if controller.addDictionaryEntry(preferred: newPreferred, variants: newVariants) { newPreferred = ""; newVariants = "" }
    }

    private func dictionaryRow(_ entry: DictionaryEntry) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.preferred).font(Font.system(size: 16, weight: .semibold))
                if !entry.variants.isEmpty {
                    Text("Heard as " + entry.variants.joined(separator: ", ")).font(Font.system(size: 13)).foregroundStyle(EditorialPalette.ink70).lineLimit(1)
                }
            }
            .opacity(entry.enabled ? 1 : 0.45)
            Spacer()
            mono(entry.source == .learned ? "Learned" : "Manual", size: 11, color: EditorialPalette.accentInk)
                .padding(.horizontal, 8).padding(.vertical, 3).background(EditorialPalette.accent16)
            Toggle(isOn: Binding(get: { entry.enabled }, set: { on in
                controller.updateDictionaryEntry(entry, preferred: entry.preferred, variants: entry.variants.joined(separator: ", "), enabled: on)
            })) { EmptyView() }
                .toggleStyle(MurmurToggleStyle()).fixedSize().accessibilityLabel("Use \(entry.preferred)")
            Button("Edit") {
                editPreferred = entry.preferred; editVariants = entry.variants.joined(separator: ", ")
                controller.dictionaryMessage = ""; editingEntryID = entry.id
            }
            .buttonStyle(.plain).font(Font.system(size: 14, weight: .semibold)).frame(height: 44).accessibilityLabel("Edit \(entry.preferred)")
            Button { controller.deleteDictionaryEntry(entry) } label: {
                Text("×").font(Font.system(size: 18)).frame(width: 44, height: 44).contentShape(Rectangle())
            }
            .buttonStyle(.plain).help("Delete \(entry.preferred)").accessibilityLabel("Delete \(entry.preferred)")
        }
    }

    private func suggestionRow(_ item: DictionarySuggestion) -> some View {
        let spelling = Binding(get: { suggestionSpellings[item.id] ?? item.preferred }, set: { suggestionSpellings[item.id] = $0 })
        let empty = spelling.wrappedValue.isBlank()
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                field("Spelling", text: spelling).frame(minWidth: 100, idealWidth: 180, maxWidth: 220)
                Text("Heard as \(item.heard.joined(separator: ", ")) · \(item.dictations) dictations").font(Font.system(size: 13)).foregroundStyle(EditorialPalette.ink70)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button("Add") { controller.acceptDictionarySuggestion(item, preferred: spelling.wrappedValue) }
                .buttonStyle(InkButtonStyle()).fixedSize().disabled(empty).accessibilityLabel("Add \(spelling.wrappedValue)")
            Button("Dismiss") { controller.dismissDictionarySuggestion(item) }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                .accessibilityLabel("Dismiss \(item.heard[0])")
        }
        .frame(minHeight: 56).padding(.vertical, 4)
    }

    private func blockedRow(_ item: CorrectionLedger.Blocked) -> some View {
        let (word, why): (String, String) = switch item {
        case .heard(let source): (source, "Fixes for this heard form are not learned")
        case .spelling(let target): (target, "This spelling is not learned")
        }
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(word).font(Font.system(size: 16, weight: .semibold))
                Text(why).font(Font.system(size: 13)).foregroundStyle(EditorialPalette.ink70)
            }
            Spacer()
            Button("Unblock") { controller.unblockWord(item) }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                .accessibilityLabel("Unblock \(word)")
        }
        .frame(minHeight: 56).padding(.vertical, 4)
    }

    private func dictionaryEditRow(_ entry: DictionaryEntry) -> some View {
        let save = {
            if controller.updateDictionaryEntry(entry, preferred: editPreferred, variants: editVariants, enabled: entry.enabled) { editingEntryID = nil }
        }
        return HStack(spacing: 8) {
            field("Preferred spelling", text: $editPreferred).frame(minWidth: 100, idealWidth: 160, maxWidth: 220).onSubmit(save)
            field("Heard as, comma-separated (optional)", text: $editVariants).onSubmit(save)
            Button("Save", action: save).buttonStyle(InkButtonStyle(kind: .filled)).fixedSize()
                .disabled(editPreferred.isBlank())
            Button("Cancel") { controller.dictionaryMessage = ""; editingEntryID = nil }.buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                .keyboardShortcut(.cancelAction)
        }
    }

    var privacy: some View {
        VStack(alignment: .leading, spacing: 0) {
            eyebrow("Permissions").padding(.top, 20)
            VStack(spacing: 0) {
                ForEach(Permission.allCases, id: \.self) { permissionRow($0) }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 8)
            Button("Refresh Permissions", action: controller.refreshPermissionsAndShortcut)
                .buttonStyle(InkButtonStyle(kind: .outline)).padding(.top, 16)


            eyebrow("Transcript history").padding(.top, 20)
            segmented([(HistoryMode.on, "Save on this Mac"), (HistoryMode.off, "Off")], $controller.historyMode).padding(.top, 8)
            help(controller.historyMode == .on
                 ? "Completed transcripts are saved before insertion, so you can copy them if typing fails. No cloud sync."
                 : HistoryMode.offDisclosure).padding(.top, 12)
            eyebrow("Keep transcripts for").padding(.top, 28)
            segmented(HistoryRetention.allCases.map { ($0, $0.label) }, $controller.retention).padding(.top, 8)
            toggleRow("Keep recordings for diagnostics (7 days)", "Keeps the audio of each dictation on this Mac, beside its History entry, so a developer can replay what Murmur heard. Off by default. Recordings are deleted after 7 days, and are kept only while History is saved. Delete All Data removes them.", isOn: $controller.keepRecordings).padding(.top, 20)
            if let notice = controller.historyNotice {
                Text(notice).font(Font.system(size: 14, weight: .semibold)).fixedSize(horizontal: false, vertical: true).padding(.top, 12)
            }
            if let warning = controller.historyCapWarning {
                Text(warning).font(Font.system(size: 14, weight: .semibold)).fixedSize(horizontal: false, vertical: true).padding(.top, 12)
            }
            if controller.hasRecoveryAudio {
                Button("Show Retained Audio…", action: controller.revealRecoveryAudio)
                    .buttonStyle(InkButtonStyle(kind: .outline)).padding(.top, 20)
                help("Failed recordings stay on this Mac for up to \(RetryWindow.text).").padding(.top, 8)
            }
            HStack(spacing: 20) {
                titled("Delete all data", DeleteAllDataText.explanation)
                Spacer()
                Button("Delete All Data…", role: .destructive, action: controller.deleteAllData).buttonStyle(InkButtonStyle())
            }
            .padding(.vertical, 18).padding(.top, 36).id("delete-data")
        }
    }

    var apiKeys: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(spacing: 0) {
                ForEach(KeyText.providers, id: \.self) { provider in keyRow(provider) }
            }
            .overlay(alignment: .top) { rule }.padding(.top, 20)
            help("Keys are stored in your Keychain and never shown again after saving. Custom transcription and cleanup use separate server addresses and API keys.").padding(.top, 16)
        }
    }

    func keyRow(_ provider: ProviderID) -> some View {
        let has = controller.savedKeys.contains(provider), open = openKey == provider
        return VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(provider.displayName).font(Font.system(size: 16, weight: .semibold))
                    mono(has ? "Saved in Keychain ✓" : KeyText.none, color: has ? EditorialPalette.accentInk : EditorialPalette.ink70)
                }
                Spacer()
                if has {
                    Button("Validate") { controller.validateKey(provider) }.buttonStyle(InkButtonStyle(kind: .outline))
                }
                if has { Button("Remove", role: .destructive) { controller.removeKey(provider) }.buttonStyle(InkButtonStyle(kind: .outline)) }
                Button(open ? "Cancel" : has ? "Replace" : "Add Key") { keyDraft = ""; openKey = open ? nil : provider }
                    .buttonStyle(InkButtonStyle())
            }
            if open {
                HStack(spacing: 8) {
                    field("Paste your \(provider.displayName) API key", text: $keyDraft, secure: true)
                    Button("Save Key") { if controller.saveKey(provider, keyDraft) { keyDraft = ""; openKey = nil } }
                        .buttonStyle(InkButtonStyle(kind: .accent)).disabled(keyDraft.isEmpty)
                }
            }
            if let address = controller.routeSettings.apiBaseURL(for: provider) {
                help("Server address: \(address)")
                if controller.customAPIKeyNeedsConfirmation(provider) {
                    help("Save the API key again to confirm this server address. The existing key stays in Keychain and will not be sent until confirmed.", color: EditorialPalette.accentInk)
                }
            }
            if let message = controller.keyMessages[provider], !message.isEmpty { help(message) }
        }
        .padding(.vertical, 16).overlay(alignment: .bottom) { softRule }
    }

    var updates: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 16) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Murmur " + controller.currentVersion).font(Font.system(size: 18, weight: .semibold))
                    if !controller.updateStatus.isEmpty { help(controller.updateStatus) }
                }
                Spacer()
                Button("Check for Updates", action: controller.checkForUpdates).buttonStyle(InkButtonStyle())
            }
            .padding(.vertical, 18).padding(.top, 20)
            toggleRow("Check automatically", UpdateText.explanation, isOn: $controller.updateChecksEnabled)
            toggleRow("Install updates automatically", "Download verified updates in the background. Installation waits until dictation and meeting recording finish.", isOn: $controller.installUpdatesAutomatically)
        }
    }
}

/// Records a custom shortcut (gap #14). While it waits, the global tap is off so the current
/// shortcut can't start a dictation or be swallowed. Escape stops waiting; a refused key says why.
struct ShortcutPresetPicker: View {
    @ObservedObject var controller: AppController
    var body: some View {
        Picker("Dictation shortcut", selection: $controller.shortcut) {
            ForEach(ShortcutBinding.presets, id: \.id) { Text($0.name).tag($0.id) }
            if !ShortcutBinding.presets.contains(where: { $0.id == controller.shortcut }) {
                Text("Custom shortcut").tag(controller.shortcut)
            }
        }.labelsHidden().frame(maxWidth: 190)
    }
}

struct ShortcutRecorder: View {
    @ObservedObject var controller: AppController
    @State private var monitor: Any?
    @State private var refusal: String?
    private var custom: CustomShortcut? { if case .custom(let shortcut) = ShortcutBinding(stored: controller.shortcut) { return shortcut }; return nil }
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                Text(monitor != nil ? "Press the keys…" : custom.map { "Custom: \($0.label)" } ?? "Or record your own")
                    .font(WindowType.body(14, weight: .medium)).accessibilityAddTraits(.updatesFrequently)
                Spacer(minLength: 8)
                Button(monitor != nil ? "Stop" : "Record Shortcut…") { monitor != nil ? stop() : start() }.buttonStyle(InkButtonStyle(kind: .outline))
                if custom != nil && monitor == nil {
                    Button("Reset") { refusal = nil; controller.shortcut = ShortcutBinding.defaultID }.buttonStyle(InkButtonStyle(kind: .outline))
                }
            }
            if let refusal { Text(refusal).font(WindowType.body(13)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true) }
            else if monitor != nil {
                Text("Use ⌘, ⌥ or ⌃ with a key, or an F-key. Built-in F1–F12 work only with fn held or with “Use F1, F2, etc. keys as standard function keys” on. Escape stops.")
                    .font(WindowType.body(13)).foregroundStyle(Palette.ink70).fixedSize(horizontal: false, vertical: true)
            }
        }
        .onDisappear { stop() }
    }
    private func start() {
        guard monitor == nil, controller.pauseShortcutForRecorder() else { return }
        refusal = nil
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let flags = (event.cgEvent?.flags ?? []).intersection(CustomShortcut.relevant)
            let code = Int64(event.keyCode)
            if code == 53 && flags.isEmpty { stop(); return nil }
            if let reason = CustomShortcut.refusal(keyCode: code, modifiers: flags) { refusal = reason; return nil }
            controller.shortcut = CustomShortcut(keyCode: code, modifiers: flags, key: Self.keyName(event)).stored
            refusal = nil; stop(); return nil
        }
    }
    private func stop() {
        guard let monitor else { return }
        NSEvent.removeMonitor(monitor); self.monitor = nil
        controller.resumeShortcutAfterRecorder()
    }
    private static let named: [UInt16: String] = [49: "Space", 36: "Return", 48: "Tab", 51: "Delete", 117: "Forward Delete", 123: "←", 124: "→", 125: "↓", 126: "↑",
        115: "Home", 119: "End", 116: "Page Up", 121: "Page Down", 122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8",
        101: "F9", 109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16", 64: "F17", 79: "F18", 80: "F19", 90: "F20"]
    static func keyName(_ event: NSEvent) -> String {
        // Control characters (keypad Enter, Clear) have no printable name; CustomShortcut falls back to the key code.
        named[event.keyCode] ?? (event.charactersIgnoringModifiers ?? "").uppercased().filter { !$0.isWhitespace && !($0.asciiValue.map { $0 < 32 || $0 == 127 } ?? false) }
    }
}

/// A list of short strings with an add field and remove buttons (mode websites and trigger words).
private struct ModeListEditor: View {
    @Binding var items: [String]
    let placeholder: String
    let label: String
    @State private var draft = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(items, id: \.self) { item in
                HStack {
                    Text(item).font(WindowType.body(15))
                    Spacer()
                    Button("Remove") { items.removeAll { $0 == item } }.buttonStyle(InkButtonStyle(kind: .plain)).fixedSize()
                }
            }
            HStack(spacing: 8) {
                TextField(placeholder, text: $draft).textFieldStyle(.plain).font(WindowType.body(15)).accessibilityLabel(label)
                    .padding(.horizontal, 12).frame(height: 44).overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
                    .onSubmit(add)
                Button("Add", action: add).buttonStyle(InkButtonStyle(kind: .outline)).fixedSize()
                    .disabled(draft.isBlank())
            }
        }
        .padding(.top, 8)
    }
    private func add() {
        // Several at once, separated by commas.
        for item in draft.split(separator: ",").map({ $0.trimmingCharacters(in: .whitespaces) }) where !item.isEmpty && !items.contains(item) { items.append(item) }
        draft = ""
    }
}

/// Applies settings as soon as they change (README "Settings and recovery"). The settings are `AppController.persisted`
/// and `routeSettings`, not a list kept here; provider URL and model fields wait for Return or focus loss.
struct AutoApplySettings: ViewModifier {
    @ObservedObject var controller: AppController
    let apiFieldFocused: Bool
    func body(content: Content) -> some View {
        content
            .onChange(of: controller.immediateSettings) { controller.settingsChanged() }
            .onChange(of: controller.routeSettings) { old, new in
                if !apiFieldFocused || old.transcription != new.transcription || old.cloudTranscriber != new.cloudTranscriber || old.cleanup != new.cleanup { controller.settingsChanged() }
            }
            .onChange(of: apiFieldFocused) { _, focused in if !focused { controller.settingsChanged() } }
    }
}

/// Settings → Meetings: meeting detection's switch and "Never" list (PRD 11.7, Q6).
private struct CallDetectionRows: View {
    @ObservedObject var presenter: CallOfferPresenter
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            monoLabel("Call detection", size: 11, color: Palette.accentInk).padding(.top, 20)
            Toggle(isOn: $presenter.enabled) {
                HStack(spacing: 4) {
                    Text("Offer to take notes when a call starts").font(WindowType.body(16, weight: .semibold))
                    SettingInfoButton(text: "When a call app or a browser uses the microphone for a few seconds, Murmur offers meeting notes. It never starts recording on its own. It checks only which apps use the microphone, on this Mac; it reads no windows or web addresses.")
                }
            }
            .toggleStyle(MurmurToggleStyle())
            if !presenter.never.isEmpty {
                Text("Never offer for").font(WindowType.body(14, weight: .semibold))
                ForEach(presenter.never, id: \.self) { id in
                    HStack {
                        Text(CallOfferPresenter.name(id)).font(WindowType.body(14))
                        Spacer()
                        Button("Remove") { presenter.removeNever(id) }.buttonStyle(InkButtonStyle(kind: .outline))
                            .accessibilityLabel("Offer again for \(CallOfferPresenter.name(id))")
                    }
                }
            }
        }
    }
}
