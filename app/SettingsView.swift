import AppKit
import SwiftUI

// The main window: SettingsView state, body, sidebar, search overlay, banners and shared form helpers.

/// How much cleanup does: Faithful leaves the words, Strict runs rules on this Mac, Polish and Professional need an AI model.
enum CleanupStyle: CaseIterable {
    case faithful, strict, polish, professional
    var label: String {
        switch self { case .faithful: "Faithful"; case .strict: "Natural"; case .polish: "Polish"; case .professional: "Professional" }
    }
    var needsModel: Bool { self == .polish || self == .professional }
    init(_ o: CleanupOptions) { self = !o.anyEnabled ? .faithful : o.professional ? .professional : o.polish ? .polish : .strict }
    var help: String {
        switch self {
        case .faithful: "Keeps the words as dictated, apart from dictionary spellings: “Um, let’s meet Tuesday, no, I mean, Thursday, at the cafe.”"
        case .strict: "Removes fillers and applies clear spoken corrections, otherwise keeping your wording: “Let’s meet Thursday at the cafe.”"
        case .polish: "Also resolves backtracks and false starts, and fixes small grammar slips, keeping your voice: “Let’s meet Thursday at the cafe.”"
        case .professional: "Rewrites into clear, professional writing of what you meant: “Let’s meet on Thursday at the café.”"
        }
    }
}

/// The five cleanup switches, shown in Settings → Cleanup and in each writing mode's editor.
enum CleanupToggle: CaseIterable {
    case fillers, corrections, lists, punctuation, spoken
    var title: String {
        switch self {
        case .fillers: "Remove filler words"
        case .corrections: "Apply spoken corrections"
        case .lists: "Format spoken lists"
        case .punctuation: "Fix punctuation and capitalization"
        case .spoken: "Spoken commands and numbers"
        }
    }
    /// The mode editor words the last one more briefly.
    func help(inMode: Bool) -> String {
        switch self {
        case .fillers: "Takes out “um”, “uh” and similar."
        case .corrections: "Uses your final wording for clear corrections, including “Monday. Actually, Thursday.” Ambiguous corrections stay."
        case .lists: "Turns “one, two, three” into a numbered list."
        case .punctuation: "Adds commas, periods and capitals."
        case .spoken: inMode ? "“comma”, “new paragraph”, numbers as digits, “at the rate” → @."
            : "“comma”, “new paragraph”, “two hundred thousand” → 200,000, “at the rate” → @, and “write an email to…” layout."
        }
    }
    var global: ReferenceWritableKeyPath<AppController, Bool> {
        switch self { case .fillers: \.cleanupFillers; case .corrections: \.cleanupCorrections; case .lists: \.cleanupLists; case .punctuation: \.cleanupPunctuation; case .spoken: \.cleanupSpoken }
    }
    var option: WritableKeyPath<CleanupOptions, Bool> {
        switch self { case .fillers: \.fillers; case .corrections: \.corrections; case .lists: \.lists; case .punctuation: \.punctuation; case .spoken: \.spoken }
    }
}

/// The three macOS permissions dictation needs; Settings, Home and the welcome guide list them the same way.
enum Permission: CaseIterable {
    case microphone, accessibility, inputMonitoring
    var title: String {
        switch self { case .microphone: "Microphone"; case .accessibility: "Accessibility"; case .inputMonitoring: "Input Monitoring" }
    }
}

struct SettingsView: View {
    @ObservedObject var controller: AppController
    enum Screen { case home, insights, meetings, settings, history, setup }
    @State var screen = Screen.home
    /// The welcome guide's current step, saved as it changes so the guide resumes there.
    @State var guideStep = WelcomeGuide.Step.welcome
    @State private var requestedPermissions: Set<Permission> = []
    /// The last settings pane, restored by ⌘, and on relaunch.
    @AppStorage(Prefs.settingsSection.key) var section = SettingsPane.general
    @State var showAdvanced = false
    /// A Polish or Professional tap on a local route: the offer shown instead of switching routes silently.
    @State var levelOffer: CleanupStyle? = CommandLine.arguments.contains("--level-offer") ? .polish : nil // the flag is for check_ui snapshots
    @State var showLiveModel = false
    @State var selectedID: String?
    /// An entry History should scroll into view once it is showing.
    @State var scrollTarget: String?
    @State var copiedID: String?
    @State var editingTextID: String?
    @State var textDraft = ""
    @State var searchText = ""
    @State private var settingsQuery = ""
    @State private var settingsSearchOpen = false
    @State private var settingsTarget = "top"
    @State private var settingsNavigationID = UUID()
    @FocusState private var settingsSearchFocused: Bool
    @State var newPreferred = ""
    @State var newVariants = ""
    @State var editingEntryID: UUID?
    @State var suggestionSpellings: [String: String] = [:]
    @State var editPreferred = ""
    @State var newPhrase = ""
    @State var newCueText = ""
    @State var editingCueID: UUID?
    @State var editCuePhrase = ""
    @State var editCueText = ""
    @State var editVariants = ""
    @State var openKey: ProviderID?
    @State var keyDraft = ""
    @State private var checkedSetup = false
    @State var editingModeID: UUID?
    @AppStorage(Prefs.sidebarCollapsed.key) var sidebarCollapsed = Prefs.sidebarCollapsed.fallback
    @AppStorage(Prefs.showDetailedSettingsInfo.key) var showDetailedSettingsInfo = Prefs.showDetailedSettingsInfo.fallback
    /// API address and model apply when editing ends (Return or leaving the field), not per keystroke.
    @FocusState var apiFieldFocused: Bool

    private var windowTitle: String {
        switch screen {
        case .home: "Murmur"
        case .insights: "Murmur: Insights"
        case .meetings: "Murmur: Meetings"
        case .history: "Murmur: History"
        case .setup: "Murmur: Welcome"
        case .settings: "Murmur Settings: " + section.title
        }
    }

    var body: some View {
        Group {
            if screen == .setup { setup.environment(\.editorialSurface, true) } else {
                VStack(spacing: 0) {
                    HStack(spacing: 0) {
                        sidebar
                        VStack(spacing: 0) {
                            banners
                            switch screen {
                            case .home: home
                            case .insights: insightsView
                            case .meetings: MeetingsPage(controller: controller, recorder: controller.meetingRecorder)
                            case .settings: settings.environment(\.editorialSurface, true).environment(\.settingsSurface, true)
                            default: history
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
            }
        }
        .disabled(settingsSearchOpen).accessibilityHidden(settingsSearchOpen)
        .foregroundStyle(Palette.ink)
        .background(Palette.paper)
        .background(WindowTitle(title: windowTitle))
        .overlay { if settingsSearchOpen { settingsSearchOverlay } }
        .onChange(of: sidebarCollapsed) { settingsSearchOpen = false; settingsQuery = "" }
        .frame(minWidth: 880, minHeight: 680)
        // Every setting applies as soon as it changes; attached here so hidden sections still apply.
        .modifier(AutoApplySettings(controller: controller, apiFieldFocused: apiFieldFocused))
        .onAppear {
            controller.refreshPermissions(); controller.refreshHistory(); controller.refreshInputDevices()
            if !checkedSetup {
                checkedSetup = true
                guideStep = WelcomeGuide.resumeStep()
                if WelcomeGuide.opensAtLaunch(alreadySetUp: setupComplete) { screen = .setup }
            }
            showRequestedScreen()
        }
        .onChange(of: controller.screenRequest) { showRequestedScreen() }
        .onChange(of: guideStep) { WelcomeGuide.save(guideStep) }
        // Permissions are granted in System Settings; re-read them on return.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in controller.refreshPermissions() }
    }

    /// A menu item (Welcome Guide…) or the UI fixture asked for a screen.
    private func showRequestedScreen() {
        guard let request = controller.screenRequest else { return }
        controller.screenRequest = nil
        switch request {
        case .settingsSearch:
            screen = .settings; section = .general
            settingsQuery = "meeting"; settingsSearchOpen = true
        case .guide: replayGuide()
        case .insights: screen = .insights
        case .meetings: screen = .meetings
        case .settings: screen = .settings
        case .settingsPane(let pane): settingsTarget = "top"; section = pane; screen = .settings
        case .history:
            screen = .history
            if let id = HistoryEntry.recoveryEntryToOpen(controller.recoveryEntryID, in: controller.history) { searchText = ""; selectedID = id; scrollTarget = id }
        case .historyEntry(let id):
            screen = .history
            if controller.history.contains(where: { $0.id == id }) { searchText = ""; selectedID = id; scrollTarget = id }
        case .home: screen = .home
        }
    }

    func mono(_ text: String, size: CGFloat = 12, color: Color = Palette.ink) -> some View {
        monoLabel(text, size: size, color: color, plain: screen == .settings)
    }

    @ViewBuilder func eyebrow(_ text: String) -> some View {
        if screen == .settings {
            Text(text).font(.system(size: 14, weight: .medium)).foregroundStyle(Palette.ink).id(text)
        } else { mono(text, size: 11, color: Palette.accentInk).id(text) }
    }

    var rule: some View { Rectangle().fill(Palette.ink20).frame(height: 1) }
    var softRule: some View { Rectangle().fill(Palette.ink.opacity(screen == .settings ? 0.1 : 0.2)).frame(height: 1) }

    func help(_ text: String, color: Color = Palette.ink70) -> some View {
        helpText(text, size: screen == .settings ? 12 : 13, color: color, system: screen == .settings)
    }

    func titled(_ title: String, _ detail: String?) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Text(title).font(screen == .settings ? .system(size: 13) : WindowType.body(15, weight: .semibold))
                if let detail, !showDetailedSettingsInfo && section != .privacy && section != .insertion { SettingInfoButton(text: detail) }
            }
            if let detail, showDetailedSettingsInfo || (screen == .settings && (section == .privacy || section == .insertion)) { help(detail) }
        }
    }

    var microphoneInfo: String {
        (controller.inputDeviceUID == MicrophoneChoice.automatic
            ? "Uses the Mac’s built-in microphone instead of a Bluetooth headset’s while available, so AirPods keep full playback quality. Otherwise uses the system default.\n\n" : "")
            + "If the microphone changes while you dictate, recording continues on the new one and keeps what was already recorded."
    }

    var microphonePicker: some View {
        let devices = controller.inputDevices
        let saved = controller.inputDeviceUID
        return Picker("Microphone", selection: $controller.inputDeviceUID) {
            Text("System default").tag("")
            Text("Automatic").tag(MicrophoneChoice.automatic)
            ForEach(devices, id: \.uid) { device in Text(device.name).tag(device.uid) }
            if !saved.isEmpty && saved != MicrophoneChoice.automatic && !devices.contains(where: { $0.uid == saved }) { Text("Chosen microphone (not connected)").tag(saved) }
        }
        .labelsHidden()
    }

    func toggleRow(_ title: String, _ detail: String, isOn: Binding<Bool>) -> some View {
        Toggle(isOn: isOn) { titled(title, detail) }.toggleStyle(MurmurToggleStyle())
            .padding(.vertical, screen == .settings ? 12 : 10).id(title)
    }

    func short(_ name: String) -> String {
        name.components(separatedBy: CharacterSet(charactersIn: "(,")).first?.trimmingCharacters(in: .whitespaces) ?? name
    }

    func field(_ title: String, text: Binding<String>, secure: Bool = false) -> some View {
        Group {
            if secure { SecureField("", text: text) } else { TextField("", text: text) }
        }
        .textFieldStyle(.plain).font(WindowType.body(15)).accessibilityLabel(title)
        .overlay(alignment: .leading) {
            if text.wrappedValue.isEmpty {
                Text(title).font(WindowType.body(15)).foregroundStyle(Palette.ink64).allowsHitTesting(false).accessibilityHidden(true)
            }
        }
        .padding(.horizontal, 12).frame(height: 44)
        .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
    }

    func progressBar(_ fraction: Double) -> some View {
        GeometryReader { geometry in
            Rectangle().fill(Palette.accent).frame(width: geometry.size.width * fraction)
        }
        .frame(height: 8).overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Palette.ink20, lineWidth: 1))
        .accessibilityElement().accessibilityLabel("Download progress").accessibilityValue("\(Int(fraction * 100)) percent")
    }

    func isAllowed(_ permission: Permission) -> Bool {
        switch permission { case .microphone: controller.microphoneAuthorized; case .accessibility: controller.accessibilityAuthorized; case .inputMonitoring: controller.inputMonitoringAuthorized }
    }

    func permissionActionTitle(_ permission: Permission) -> String {
        requestedPermissions.contains(permission) ? "Open System Settings…" : "Allow"
    }

    func grant(_ permission: Permission) {
        if requestedPermissions.contains(permission) {
            controller.openPermissionSettings(permission)
        } else {
            requestedPermissions.insert(permission)
            controller.requestPermission(permission)
        }
    }

    var setupComplete: Bool {
        controller.microphoneAuthorized && controller.accessibilityAuthorized && controller.inputMonitoringAuthorized && modelSatisfied
    }

    var modelSatisfied: Bool { controller.modelSlot.row.isReady || controller.transcriptionRoute != .local }

    @ViewBuilder var sidebar: some View {
        if screen == .settings { settingsSidebar } else { appSidebar }
    }

    private var appSidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button { sidebarCollapsed.toggle() } label: {
                Image(systemName: "sidebar.left")
                    .font(.system(size: 16)).frame(width: 36, height: 36)
                    .foregroundStyle(Palette.ink70).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar")
            .accessibilityLabel(sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar")
            .padding(.bottom, 8)
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    navigationRow("Home", icon: "house", selected: screen == .home) { screen = .home }
                    navigationRow("Insights", icon: "chart.bar.xaxis", selected: screen == .insights) { screen = .insights }
                    navigationRow("Meetings", icon: "person.2", selected: screen == .meetings) { screen = .meetings }
                    navigationRow("History", icon: "clock.arrow.circlepath", selected: screen == .history) { screen = .history }
                    navigationRow("Settings", icon: "gearshape", selected: false, dot: !controller.tips.dots.isEmpty) { screen = .settings }

                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack(spacing: 7) {
                Circle().fill(controller.phase == .error ? Palette.ink : Palette.accent).frame(width: 6, height: 6)
                if !sidebarCollapsed { mono(controller.phase.rawValue, size: 11, color: Palette.ink70) }
            }
            .accessibilityElement(children: .ignore).accessibilityLabel(controller.phase.rawValue)
            .help(controller.phase.rawValue + " · " + (controller.appliedTranscriptionIsLocal ? "Local transcription" : "Cloud transcription"))
            .padding(.leading, sidebarCollapsed ? 15 : 0)
            .padding(.top, 12).padding(.bottom, 16)
        }
        .padding(.horizontal, sidebarCollapsed ? 14 : 20).padding(.top, 16)
        .frame(width: sidebarCollapsed ? 64 : 216).frame(maxHeight: .infinity)
        .background(screen == .settings ? EditorialPalette.sidebar : Palette.paper)
    }

    private var settingsSidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button { screen = .home } label: {
                Label("Back to Murmur", systemImage: "chevron.left")
                    .font(.system(size: 13)).foregroundStyle(Palette.ink70)
                    .padding(.horizontal, 10).frame(maxWidth: .infinity, minHeight: 36, alignment: .leading)
                    .contentShape(Rectangle())
            }.buttonStyle(BackRowStyle()).accessibilityLabel("Back to Murmur").padding(.bottom, 20)
            Text("Settings").font(.system(size: 14, weight: .medium)).padding(.bottom, 12)
            Button { settingsSearchOpen = true } label: {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass")
                    Text("Search settings")
                    Spacer(minLength: 0)
                    Text("⌘K").accessibilityHidden(true)
                }.font(.system(size: 12)).foregroundStyle(Palette.ink70)
                    .padding(8).overlay(RoundedRectangle(cornerRadius: 5).stroke(Palette.ink20))
            }.buttonStyle(.plain).keyboardShortcut("k", modifiers: .command)
                .help("Search all settings (⌘K)").padding(.bottom, 24)
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(SettingsPane.groups, id: \.self) { pane in
                        if pane == .privacy { Divider().padding(.vertical, 12) }
                        navigationRow(pane.title, icon: pane.icon, selected: section.group == pane, dot: controller.tips.dots.contains { $0.group == pane }) {
                            section = pane; settingsTarget = "top"
                        }
                    }
                }
            }
        }
        .padding(.horizontal, 16).padding(.top, 20)
        .frame(width: 216).frame(maxHeight: .infinity)
        .background(EditorialPalette.sidebar)
        .overlay(alignment: .trailing) { Rectangle().fill(Palette.ink.opacity(0.08)).frame(width: 1) }
    }

    private var settingsSearchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(Palette.ink70).accessibilityHidden(true)
            TextField("Search settings", text: $settingsQuery)
                .textFieldStyle(.plain).font(WindowType.body(14))
                .accessibilityLabel("Search settings").focused($settingsSearchFocused)
                .onSubmit { if let result = SettingsSearch.matches(settingsQuery).first { openSetting(result) } }
                .onExitCommand { settingsSearchOpen = false; settingsQuery = "" }
        }
        .padding(8).background(Palette.ink.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
    }

    private var settingsSearchResults: some View {
        let results = SettingsSearch.matches(settingsQuery)
        return ScrollView {
            VStack(alignment: .leading, spacing: 4) {
                if results.isEmpty {
                    Text(settingsQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "Type to find a setting." : "No matching settings.")
                        .font(WindowType.body(13)).foregroundStyle(Palette.ink70).padding(8)
                }
                ForEach(results) { result in
                    Button { openSetting(result) } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(result.title).font(WindowType.body(14, weight: .medium)).fixedSize(horizontal: false, vertical: true)
                            Text(result.sectionName).font(WindowType.body(12)).foregroundStyle(Palette.ink70)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading).padding(8).contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).accessibilityLabel("\(result.title), \(result.sectionName)")
                }
            }
        }
        .frame(maxHeight: 300)
    }

    private var settingsSearchOverlay: some View {
        ZStack {
            Color.black.opacity(0.3).ignoresSafeArea().onTapGesture { settingsSearchOpen = false }
                .accessibilityHidden(true)
            VStack(spacing: 12) {
                HStack(spacing: 12) {
                    settingsSearchField
                    Button { settingsSearchOpen = false } label: { Image(systemName: "xmark") }
                        .buttonStyle(.plain).accessibilityLabel("Close settings search")
                }
                settingsSearchResults
            }
            .padding(20).frame(width: 440)
            .background(Palette.paper, in: RoundedRectangle(cornerRadius: 12))
            .shadow(color: .black.opacity(0.2), radius: 20, y: 8)
            .onAppear { DispatchQueue.main.async { settingsSearchFocused = true } }
            .onExitCommand { settingsSearchOpen = false }
            .accessibilityElement(children: .contain).accessibilityLabel("Settings search")
        }
    }

    private func openSetting(_ result: SettingsSearch.Result) {
        if result.target == "model-folder" { showAdvanced = true }
        if result.target == "live-words" { showLiveModel = true }
        settingsTarget = result.target; settingsNavigationID = UUID()
        section = result.section; screen = .settings
        settingsSearchOpen = false; settingsQuery = ""; settingsSearchFocused = false
    }

    private func navigationRow(_ title: String, icon: String, selected: Bool, dot: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.system(size: 15)).frame(width: 20)
                    .overlay(alignment: .topTrailing) { if dot && sidebarCollapsed && screen != .settings { ExploreDot().offset(x: 3, y: -2) } }
                if screen == .settings { Text(title).font(.system(size: 13, weight: selected ? .medium : .regular)).lineLimit(1) }
                else if !sidebarCollapsed { mono(title, size: 11, color: selected ? Palette.ink : Palette.ink70).lineLimit(1) }
                if dot && (screen == .settings || !sidebarCollapsed) { Spacer(minLength: 4); ExploreDot() }
            }
            .foregroundStyle(selected ? Palette.ink : Palette.ink70)
            .padding(.horizontal, screen == .settings ? 10 : 0)
            .frame(maxWidth: .infinity, minHeight: 36, alignment: sidebarCollapsed && screen != .settings ? .center : .leading)
            .background(selected ? Palette.accent16 : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain).help(title).accessibilityLabel(dot ? "\(title), new" : title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    @ViewBuilder private var banners: some View {
        if controller.hasStatusToShow && !controller.isBusy {
            HStack(alignment: .top, spacing: 12) {
                if controller.phase == .error { mono("Error", color: Palette.paper).padding(.top, 2) }
                Text(controller.message).font(WindowType.body(14)).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                Spacer(minLength: 8)
                Button(action: controller.dismissStatus) { Image(systemName: "xmark") }
                    .buttonStyle(.plain).help("Dismiss status").accessibilityLabel("Dismiss status")
            }
            .foregroundStyle(controller.phase == .error ? Palette.paper : Color.white).padding(.horizontal, 24).padding(.vertical, 12)
            .background(controller.phase == .error ? Palette.ink : Palette.accent)
        }
        if !setupComplete {
            HStack {
                Text(controller.accessibilityAuthorized && controller.inputMonitoringAuthorized
                     ? (modelSatisfied ? "Murmur needs the microphone before you can dictate." : "Murmur needs the microphone and its speech model before you can dictate.")
                     : "Accessibility and Input Monitoring are needed before Murmur can type for you.")
                    .font(WindowType.body(14))
                Spacer(minLength: 12)
                Button { screen = .setup } label: {
                    Text("Finish Setup →").font(WindowType.body(14, weight: .semibold))
                        .underline(color: Palette.accent).frame(height: 44)
                }
                .buttonStyle(.plain)
            }
            .foregroundStyle(Palette.paper).padding(.horizontal, 24)
            .background(Palette.ink)
        }
    }

    var settings: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Text(section.title).id("top")
                        .font(.system(size: 25, weight: .medium)).tracking(-0.5)
                    Text(section.lede)
                        .font(.system(size: 13)).foregroundStyle(EditorialPalette.ink70)
                        .padding(.top, 6).padding(.bottom, 24)
                    let tabs = section.tabs
                    if !tabs.isEmpty {
                        HStack(spacing: 0) {
                            ForEach(tabs, id: \.self) { value in
                                Button { section = value; settingsTarget = "top" } label: {
                                    HStack(spacing: 5) {
                                        Text(value.name).font(.system(size: 13, weight: .medium))
                                        if controller.tips.dots.contains(value) { ExploreDot() }
                                    }
                                        .foregroundStyle(section == value ? Palette.ink : Palette.ink70)
                                        .padding(.vertical, 12)
                                        .overlay(alignment: .bottom) { Rectangle().fill(section == value ? Palette.accent : .clear).frame(height: 2) }
                                        .padding(.horizontal, 12).contentShape(Rectangle()) // whole tab cell is clickable, not just the glyphs
                                }.buttonStyle(.plain).accessibilityAddTraits(section == value ? .isSelected : [])
                                    .accessibilityLabel(controller.tips.dots.contains(value) ? "\(value.name), new" : value.name)
                            }
                            Spacer(minLength: 0)
                        }.padding(.leading, -12).overlay(alignment: .bottom) { softRule }.padding(.bottom, 24)
                    }
                    PaneIntro(pane: section, tips: controller.tips)
                    sectionBody.disabled(controller.isBusy)
                    if controller.isBusy { help("Finish dictating to change settings.", color: EditorialPalette.ink70).padding(.top, 12) }
                }
                .padding(.vertical, 32)
                .frame(maxWidth: 760, alignment: .leading)
                .padding(.horizontal, 36)
                .frame(maxWidth: .infinity, alignment: .center)
            }
            .onAppear { DispatchQueue.main.async { proxy.scrollTo(settingsTarget, anchor: .top) } }
            .onChange(of: settingsNavigationID) { DispatchQueue.main.async { proxy.scrollTo(settingsTarget, anchor: .top) } }
            .background(EditorialPalette.paper)
            .tint(EditorialPalette.accent)
            .onAppear { controller.tips.opened(section) }
            .id(section)
        }
    }

    @ViewBuilder private var sectionBody: some View {
        switch section {
        case .general: general
        case .transcription: transcription
        case .cleanup: cleanup
        case .modes: modes
        case .dictionary: dictionary
        case .cues: cuesPane
        case .privacy: privacy
        case .apiKeys: apiKeys
        case .recording: recordingSettings
        case .insertion: insertionSettings
        case .meetings: meetingSettings
        case .updates: updates
        }
    }
}

/// Text fields receive ⌘X/⌘C/⌘V/⌘A/⌘Z through the Edit menu; without it, pasting an API key does nothing.
func standardEditMenuItem() -> NSMenuItem {
    let edit = NSMenu(title: "Edit")
    edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
    edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
    edit.addItem(.separator())
    edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
    edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
    edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
    edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
    let item = NSMenuItem(title: "Edit", action: nil, keyEquivalent: "")
    item.submenu = edit
    return item
}

/// The sidebar's back row: the same accent tint as a selected row, on hover and while pressed.
private struct BackRowStyle: ButtonStyle {
    @State private var hovering = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(hovering || configuration.isPressed ? Palette.accent16 : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            .onHover { hovering = $0 }
    }
}
