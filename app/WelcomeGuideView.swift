import AppKit
import SwiftUI

// The welcome guide: permission, model and test-dictation steps.

extension SettingsView {
    // MARK: Welcome guide (gap #21)

    var shortcutName: String { controller.shortcutName }

    /// The guide's permission and model steps; the last step is the test dictation.
    private var guideSteps: [(title: String, detail: String, done: Bool, action: String, run: () -> Void)] {
        [
            ("Microphone", "So Murmur can hear you while the shortcut is held. Audio stays on this Mac unless you choose a cloud route.", controller.microphoneAuthorized, "Allow", controller.requestMicrophone),
            ("Accessibility", "So Murmur can paste into the field you are using. It never presses Return or sends a message.", controller.accessibilityAuthorized, "Open Settings…", { controller.openPermissionSettings("accessibility") }),
            ("Input Monitoring", "So the shortcut works in every app. Murmur listens only for the shortcut, Escape and its two ⌃⌘ chords.", controller.inputMonitoringAuthorized, "Open Settings…", { controller.openPermissionSettings("input-monitoring") }),
            ("Transcription", "Choose where dictation audio is transcribed. Cloud routes send audio to the provider shown below.", WelcomeGuide.isConfirmed(.transcription, .standard), "", {}),
            ("Cleanup", "Choose where cleanup runs and how much it changes your words. Faithful keeps cleanup off.", WelcomeGuide.isConfirmed(.cleanup, .standard), "", {}),
            ("Meetings", "Choose meeting transcription separately from dictation, then choose how meeting notes are written. None keeps only the transcript.", WelcomeGuide.isConfirmed(.meetings, .standard), "", {}),
            ("Speech model", controller.transcriptionRoute == .cloud && controller.meetingTranscription == .cloud && !controller.modelRow.isReady ? "Not needed while dictation and meeting transcription both run in the cloud." : ModelFacts.displayName, controller.modelRow.isReady || (controller.transcriptionRoute == .cloud && controller.meetingTranscription == .cloud),
             controller.modelRow.actions.contains(.retry) ? "Retry…" : "Download…", controller.downloadModel),
            ("Try it", "Press \(controller.shortcutName) and say a sentence. With this window in front, your words land in the field below instead of another app.", !controller.setupTestTranscript.isEmpty, "", {})
        ]
    }

    /// Test dictation uses the routes and cleanup style chosen earlier in the guide.
    var tryIt: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !setupComplete { help("Finish the earlier steps first; dictation needs all of them.", color: Palette.ink).padding(.bottom, 12) }
            TextEditor(text: $controller.setupTestTranscript)
                .font(WindowType.body(16)).scrollContentBackground(.hidden)
                .frame(minHeight: 96).padding(8)
                .overlay(alignment: .topLeading) {
                    if controller.setupTestTranscript.isEmpty {
                        Text("Waiting for your voice…").font(WindowType.body(16)).foregroundStyle(Palette.ink64)
                            .padding(.top, 8).padding(.leading, 13).allowsHitTesting(false).accessibilityHidden(true)
                    }
                }
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
                .accessibilityLabel("Test dictation")
        }
        .onAppear { controller.setupShowing = true }
        .onDisappear { controller.setupShowing = false }
    }

    private func finishGuide() {
        if let pending = WelcomeGuide.nextUnconfirmed(.standard) { guideStep = pending; return }
        WelcomeGuide.complete(.standard); guideStep = .microphone; screen = .home
    }

    var setup: some View {
        let steps = guideSteps
        let index = WelcomeGuide.Step.allCases.firstIndex(of: guideStep)!, step = steps[index]
        let last = guideStep == .tryIt
        return GeometryReader { geometry in
            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    eyebrow("Welcome guide · Step \(index + 1) of \(steps.count)")
                    VStack(alignment: .leading, spacing: -8) {
                        Text("Set up").font(.system(size: 42, weight: .regular, design: .serif)).tracking(-1.28)
                        wordmark("Murmur", size: 48)
                    }
                    .accessibilityElement(children: .combine).padding(.top, 16)
                    PaperVoice().padding(.top, 16)
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(steps.indices, id: \.self) { i in
                                let on = i == index
                                Button { guideStep = WelcomeGuide.Step.allCases[i] } label: {
                                    HStack(spacing: 12) {
                                        mono(String(format: "%02d", i + 1), size: 12, color: EditorialPalette.ink70)
                                        mono(steps[i].title, size: 12, color: EditorialPalette.ink)
                                        Spacer()
                                        if steps[i].done { mono("✓", size: 12, color: EditorialPalette.accentInk) }
                                    }
                                    .frame(height: 40)
                                    .overlay(alignment: .bottom) { Rectangle().fill(on ? EditorialPalette.accent : Color.clear).frame(height: 2) }
                                    .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain).accessibilityAddTraits(on ? .isSelected : [])
                            }
                        }
                    }
                    .padding(.top, 28)
                    Spacer()
                    Logo.path(in: CGSize(width: 48, height: 48)).fill(EditorialPalette.ink)
                        .frame(width: 48, height: 48).accessibilityHidden(true)
                }
                .padding(24).frame(width: geometry.size.width * 5 / 12, alignment: .leading).frame(maxHeight: .infinity, alignment: .top)
                .background(EditorialPalette.sidebar)

                VStack(alignment: .leading, spacing: 0) {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            mono(String(format: "%02d", index + 1), size: 13, color: EditorialPalette.accentInk)
                            Text(step.title).font(.system(size: 42, weight: .regular, design: .serif)).tracking(-1.28).padding(.top, 8)
                            HStack(alignment: .top, spacing: 4) {
                                help(step.detail)
                                if guideStep == .model {
                                    ModelInfoIcons(license: controller.modelLicense.map(ModelFacts.licenseText), details: controller.modelSummary)
                                }
                            }.padding(.top, 10)
                            Group {
                                if guideStep == .transcription { transcriptionRoutes }
                                else if guideStep == .cleanup {
                                    cleanupChoices
                                    if cleanupStyle.wrappedValue.needsModel, controller.cleanupChoice == .customAPI { customCleanupSettings }
                                    if cleanupStyle.wrappedValue.needsModel, controller.cleanupChoice == .appleIntelligence, let reason = AppleSystemModel.status() { help(reason.userMessage).padding(.top, 12) }
                                    if cleanupStyle.wrappedValue.needsModel, controller.cleanupChoice == .gemini { keyRow(.gemini).padding(.top, 12) }
                                    disclosure(controller.routeDisclosure(.cleanup)).padding(.top, 12)
                                }
                                else if guideStep == .meetings {
                                    meetingRoutes
                                    if controller.meetingTranscription == .cloud {
                                        cloudTranscriptionSettings
                                        disclosure(controller.meetingCloudNote?.disclosure).padding(.top, 12)
                                    }
                                    help("Cloud meetings use the provider configured in the Transcription step. Notes use the cleanup provider configuration.").padding(.top, 12)
                                    if controller.meetingNotesRoute == .gemini { keyRow(.gemini).padding(.top, 12) }
                                    if controller.meetingNotesRoute == .customAPI { customCleanupSettings }
                                }
                                else if last { tryIt }
                                else {
                                    let working = guideStep == .model && controller.modelRow.isWorking
                                    VStack(alignment: .leading, spacing: 10) {
                                        HStack(spacing: 14) {
                                            if step.done { mono("Done ✓", color: EditorialPalette.accentInk) }
                                            else if working { Button("Cancel", action: controller.cancelModelDownload).buttonStyle(InkButtonStyle(kind: .outline)) }
                                            else { Button(step.action, action: step.run).buttonStyle(InkButtonStyle()) }
                                            Spacer()
                                        }
                                        if working {
                                            help(controller.modelRow.statusText)
                                            if let progress = controller.modelRow.progress { progressBar(progress) }
                                        }
                                        if guideStep == .model && !controller.modelMessage.isEmpty { help(controller.modelMessage) }
                                        if guideStep == .inputMonitoring || guideStep == .accessibility {
                                            help("After you allow Murmur in System Settings, come back here; this page updates on its own.", color: EditorialPalette.ink70)
                                        }
                                    }
                                }
                            }
                            .padding(.top, 24)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    HStack(spacing: 8) {
                        if index > 0 { Button("← Back") { guideStep = WelcomeGuide.Step.allCases[index - 1] }.buttonStyle(InkButtonStyle(kind: .outline)) }
                        Button("Not Now") { screen = .home }.buttonStyle(InkButtonStyle(kind: .plain))
                        Spacer()
                        if last {
                            Button(WelcomeGuide.nextUnconfirmed(.standard) == nil ? "Finish" : "Review Choices →") { finishGuide() }.buttonStyle(InkButtonStyle(kind: .filled))
                        } else {
                            Button(guideStep.isConfiguration ? "Use These Settings →" : step.done ? "Continue →" : "Skip for Now →") {
                                if guideStep == .meetings && controller.meetingNotesRoute == nil { controller.meetingNotesRoute = MeetingNotesRoute.none }
                                WelcomeGuide.confirm(guideStep, .standard)
                                controller.settingsChanged()
                                guideStep = WelcomeGuide.Step.allCases[index + 1]
                            }
                                .buttonStyle(InkButtonStyle(kind: step.done ? .filled : .outline))
                        }
                    }
                    .padding(.top, 24)
                }
                .padding(24)
                .background(EditorialPalette.paper)
            }
            .foregroundStyle(EditorialPalette.ink)
            .tint(EditorialPalette.accent)
        }
    }
}
