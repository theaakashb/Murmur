import AppKit
import SwiftUI

// The welcome guide: permission, model and test-dictation steps.

extension SettingsView {
    private func guideInfo(_ step: WelcomeGuide.Step) -> (title: String, detail: String, done: Bool, action: String, run: () -> Void) {
        switch step {
        case .welcome, .meetings: return ("A little room for thought.", "Speak freely. Your voice becomes text, right where you’re writing.", false, "", {})
        case .microphone: return (Permission.microphone.title, "A little access, a clear reason. So Murmur can hear you while the shortcut is held. Audio stays on this Mac unless you choose a cloud route.", isAllowed(.microphone), permissionActionTitle(.microphone), { grant(.microphone) })
        case .accessibility: return (Permission.accessibility.title, "So Murmur can paste into the field you are using. It never presses Return or sends a message.", isAllowed(.accessibility), permissionActionTitle(.accessibility), { grant(.accessibility) })
        case .inputMonitoring: return (Permission.inputMonitoring.title, "So the shortcut works in every app. Murmur listens only for the shortcut, Escape and its two ⌃⌘ chords.", isAllowed(.inputMonitoring), permissionActionTitle(.inputMonitoring), { grant(.inputMonitoring) })
        case .transcription: return ("Transcription", "Choose where dictation audio is transcribed. Cloud routes send audio to the provider shown below.", WelcomeGuide.isConfirmed(.transcription), "", {})
        case .cleanup: return ("Cleanup", "Choose where cleanup runs and how much it changes your words. Faithful keeps cleanup off.", WelcomeGuide.isConfirmed(.cleanup), "", {})
        case .model: return ("Speech model", controller.transcriptionRoute != .local && controller.meetingTranscription == .cloud && !controller.modelSlot.row.isReady ? "Not needed while dictation and meeting transcription both run in the cloud." : ModelFacts.batch.displayName, controller.modelSlot.row.isReady || (controller.transcriptionRoute != .local && controller.meetingTranscription == .cloud),
                             controller.modelSlot.row.actions.contains(.retry) ? "Retry…" : "Download…", controller.downloadModel)
        case .tryIt: return ("Find your words.", "Press \(controller.shortcutName) and say a sentence. With this window in front, your words land in the field below instead of another app.", !controller.setupTestTranscript.isEmpty, "", {})
        }
    }

    /// The site's drawing in two layers, tinted so it follows light and dark: blue ribbon, ink paper.
    var welcome: some View {
        VStack(alignment: .leading, spacing: 16) {
            help("Audio stays on this Mac unless you choose a cloud route. You see what a cloud route sends before you confirm it.")
            ZStack {
                ForEach(["hero-ink", "hero-ribbon"], id: \.self) { name in
                    if let url = Bundle.main.url(forResource: name, withExtension: "png", subdirectory: "Images"), let art = NSImage(contentsOf: url) {
                        Image(nsImage: art).renderingMode(.template).resizable().scaledToFit()
                            .foregroundStyle(name == "hero-ribbon" ? SetupPalette.blue : SetupPalette.ink)
                    }
                }
            }
            .frame(maxWidth: 380, maxHeight: 380).frame(maxWidth: .infinity, alignment: .center)
            .accessibilityElement(children: .ignore).accessibilityLabel("A blue voice ribbon becomes ink lines on a curling sheet of paper.")
        }
    }

    /// Test dictation uses the routes and cleanup style chosen earlier in the guide.
    var tryIt: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !setupComplete { help("Finish the earlier steps first; dictation needs all of them.", color: Palette.ink).padding(.bottom, 12) }
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 12) {
                    Text("Your shortcut").font(WindowType.body(14, weight: .medium))
                    ShortcutPresetPicker(controller: controller)
                }
                ShortcutRecorder(controller: controller)
            }
            .disabled(controller.isBusy).padding(.bottom, 16)
            HStack(spacing: 10) {
                let recording = controller.phase == .listening
                Button(recording ? "Stop and Transcribe" : "Start Dictating") { controller.toggleSetupDictation() }
                    .buttonStyle(InkButtonStyle()).disabled(!setupComplete || (controller.isBusy && !recording))
                if controller.isBusy { Button("Cancel") { controller.cancelRecording() }.buttonStyle(InkButtonStyle(kind: .outline)) }
                Text(!(controller.isBusy || controller.phase == .error) ? "Or hold \(controller.shortcutName) and speak." : controller.message)
                    .font(WindowType.body(13)).foregroundStyle(SetupPalette.muted).fixedSize(horizontal: false, vertical: true)
            }.padding(.bottom, 12)
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
            help(controller.learningEnabled
                 ? "Correct a misheard word within a minute of dictating to teach Murmur its spelling. You can also add names and terms in Dictionary."
                 : "Add names and terms in Dictionary, where you can also turn on learning from your spelling corrections.")
                .padding(.top, 16)
        }
        .onChange(of: controller.shortcut) { controller.settingsChanged() }
        .onAppear { controller.setupShowing = true }
        .onDisappear { controller.setupShowing = false }
    }

    func replayGuide() { WelcomeGuide.replay(); guideStep = .welcome; screen = .setup }

    private func finishGuide() {
        if let pending = WelcomeGuide.nextUnconfirmed() { guideStep = pending; return }
        WelcomeGuide.complete(); guideStep = .welcome; screen = .home
    }

    var setup: some View {
        let index = WelcomeGuide.Step.allCases.firstIndex(of: guideStep)!, step = guideInfo(guideStep)
        let last = guideStep == .tryIt
        return GeometryReader { geometry in
            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(guideStep.phase == .getReady ? "Get ready · \(index) of \(WelcomeGuide.Step.allCases.filter { $0.phase == .getReady }.count)" : guideStep.phase.title).font(WindowType.body(12, weight: .medium)).foregroundStyle(SetupPalette.muted).id(index)
                    VStack(alignment: .leading, spacing: 6) {
                        Text("murmur").font(WindowType.body(40, weight: .medium)).tracking(-1.6)
                    }
                    .accessibilityElement(children: .combine).padding(.top, 16)
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(WelcomeGuide.Step.Phase.allCases, id: \.self) { phase in
                                let current = phase == guideStep.phase
                                let first = WelcomeGuide.Step.allCases.first { $0.phase == phase }!
                                Button { guideStep = first } label: {
                                    HStack(spacing: 12) {
                                        Text(String(format: "%02d", phase.rawValue + 1)).font(WindowType.body(13)).foregroundStyle(SetupPalette.muted)
                                        Text(phase.title).font(WindowType.body(14, weight: current ? .semibold : .regular)).foregroundStyle(SetupPalette.ink)
                                        Spacer()
                                    }
                                    .frame(height: 40)
                                    .overlay(alignment: .bottom) { Rectangle().fill(current ? SetupPalette.blue : Color.clear).frame(height: 2) }
                                    .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain).accessibilityAddTraits(current ? .isSelected : [])
                                if phase == .getReady && current {
                                    ForEach(WelcomeGuide.Step.allCases.filter { $0.phase == .getReady }, id: \.self) { sub in
                                        let info = guideInfo(sub)
                                        Button { guideStep = sub } label: {
                                            HStack(spacing: 8) {
                                                Text(info.title).font(WindowType.body(13, weight: sub == guideStep ? .semibold : .regular)).foregroundStyle(SetupPalette.ink)
                                                Spacer()
                                                if info.done { Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundStyle(SetupPalette.blue).accessibilityLabel("Ready") }
                                            }
                                            .padding(.leading, 28).frame(minHeight: 30).contentShape(Rectangle())
                                        }
                                        .buttonStyle(.plain).accessibilityAddTraits(sub == guideStep ? .isSelected : [])
                                    }
                                }
                            }
                        }
                    }
                    .padding(.top, 28)
                    Spacer()
                    Logo.path(in: CGSize(width: 48, height: 48)).fill(SetupPalette.ink)
                        .frame(width: 48, height: 48).accessibilityHidden(true)
                }
                .padding(24).frame(width: geometry.size.width * 5 / 12, alignment: .leading).frame(maxHeight: .infinity, alignment: .top)
                .background(SetupPalette.pane)

                VStack(alignment: .leading, spacing: 0) {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 0) {
                            Text(step.title).font(WindowType.body(40, weight: .medium)).tracking(-1.2).fixedSize(horizontal: false, vertical: true)
                            HStack(alignment: .top, spacing: 4) {
                                help(step.detail)
                                if guideStep == .model {
                                    ModelInfoIcons(license: controller.modelSlot.license.map(ModelFacts.licenseText), details: controller.modelSlot.summary)
                                }
                            }.padding(.top, 10)
                            Group {
                                if guideStep == .transcription { transcriptionRoutes }
                                else if guideStep == .cleanup {
                                    cleanupChoices
                                    if cleanupStyle.wrappedValue != .faithful, controller.cleanupChoice == .customAPI { customCleanupSettings }
                                    if cleanupStyle.wrappedValue != .faithful, controller.cleanupChoice == .murmurCloud { keyRow(.murmurCloud).padding(.top, 16) }
                                    disclosure(controller.routeDisclosure(.cleanup)).padding(.top, 12)
                                }
                                else if guideStep == .welcome { welcome }
                                else if last { tryIt }
                                else {
                                    let working = guideStep == .model && controller.modelSlot.row.isWorking
                                    VStack(alignment: .leading, spacing: 10) {
                                        HStack(spacing: 14) {
                                            if step.done { HStack(spacing: 6) { Image(systemName: "checkmark").foregroundStyle(SetupPalette.blue); Text("Ready").foregroundStyle(SetupPalette.ink) }.font(WindowType.body(14, weight: .semibold)).accessibilityElement(children: .combine) }
                                            else if working { Button("Cancel", action: controller.cancelModelDownload).buttonStyle(InkButtonStyle(kind: .outline)) }
                                            else { Button(step.action, action: step.run).buttonStyle(InkButtonStyle()) }
                                            Spacer()
                                        }
                                        if working {
                                            help(controller.modelSlot.row.statusText)
                                            if let progress = controller.modelSlot.row.progress { progressBar(progress) }
                                        }
                                        if guideStep == .model && !controller.modelSlot.message.isEmpty { help(controller.modelSlot.message) }
                                        if !step.done && (guideStep == .inputMonitoring || guideStep == .accessibility) {
                                            help("In System Settings, turn on Murmur. If it’s missing, use + to add Murmur from Applications. Return here to check access.", color: SetupPalette.muted)
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
                            Button(WelcomeGuide.nextUnconfirmed() == nil ? "Finish" : "Review Choices →") { finishGuide() }.buttonStyle(InkButtonStyle(kind: .filled))
                        } else {
                            Button(guideStep == .welcome ? "Get started →" : guideStep.isConfiguration ? "Use These Settings →" : step.done ? "Continue →" : "Skip for Now →") {
                                WelcomeGuide.confirm(guideStep)
                                controller.settingsChanged()
                                guideStep = WelcomeGuide.Step.allCases[index + 1]
                            }
                                .buttonStyle(InkButtonStyle(kind: step.done || guideStep == .welcome ? .filled : .outline))
                        }
                    }
                    .padding(.top, 24)
                }
                .padding(24)
                .background(SetupPalette.ground)
            }
            .foregroundStyle(SetupPalette.ink)
            .tint(SetupPalette.blue)
        }
    }
}
