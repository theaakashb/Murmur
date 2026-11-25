import AppKit
import SwiftUI
import ApplicationServices
import AVFoundation
import os
import Combine
import CryptoKit
import Darwin
import Carbon
import ServiceManagement
@preconcurrency import Dispatch

/// Content-free timing and stop reasons, alongside the capture and shortcut timing. Shared by Controller*.swift and
/// ModelHelper.swift; Capture.swift and Shortcut.swift keep their own so the capture probe and spike harness compile alone.
let timingLog = Logger(subsystem: "cc.clouddev.dictation", category: "timing")


extension InsertionStatus {
    init(_ result: InsertionResult.Status) {
        switch result { case .verified: self = .verified; case .unverified: self = .unverified; case .failed: self = .failed }
    }
}

/// The raw values are shown in the menu, the sidebar and the accessibility label.
enum Phase: String { case ready = "Ready", listening = "Listening", processing = "Processing", error = "Error", saved = "Saved" }

/// A screen the window should show, asked for by a menu item, a controller flow or the UI fixture.
enum ScreenRequest: Equatable {
    case home, guide, insights, meetings, history, settings, settingsSearch
    case settingsPane(SettingsPane)
    case historyEntry(String)
}

@MainActor final class AppController: ObservableObject {
    @Published var phase = Phase.ready
    @Published var message = "Hold your shortcut to dictate, or double-press it to dictate until you press it again."
    @Published var history: [HistoryEntry] = []
    @Published var recoveryEntryID: String?
    @Published var recoveryAudio: URL?
    @Published var modelDirectory: String
    @Published var dictationLanguages = Prefs.dictationLanguages.fallback
    @Published var shortcut = Prefs.shortcut.fallback
    @Published var voiceLevel = 0.0
    @Published var showMenuBar = Prefs.showMenuBar.fallback
    /// Off by default; applies as soon as it changes.
    @Published var showInDock = Prefs.showInDock.fallback
    @Published var dictationIndicator = Prefs.dictationIndicator.fallback
    @Published var meetingIndicator = Prefs.meetingIndicator.fallback
    /// A screen the window should switch to, set from a menu item or the UI fixture. The window clears it once shown.
    @Published var screenRequest: ScreenRequest?
    /// CoreAudio UID of the chosen microphone; empty means the system default.
    @Published var inputDeviceUID = Prefs.inputDeviceUID.fallback
    /// Connected microphones, cached: Settings re-renders with every voice-level tick.
    @Published private(set) var inputDevices = Capture.inputDevices()
    func refreshInputDevices() { inputDevices = Capture.inputDevices() }
    /// The system's login item state (SMAppService), not a stored setting; off until the user turns it on.
    @Published private(set) var openAtLogin = SMAppService.mainApp.status == .enabled
    @Published var permissionsSummary = ""
    @Published var hasRecoveryAudio = false
    var microphoneAuthorized: Bool { permission() == .authorized }
    var accessibilityAuthorized: Bool { AXIsProcessTrusted() }
    var inputMonitoringAuthorized: Bool { CGPreflightListenEventAccess() }
    var modelAvailable: Bool { modelFilesAvailable() }
    @Published var intelligentLeadingSpace = Prefs.intelligentLeadingSpace.fallback
    @Published var automaticInsertion = Prefs.automaticInsertion.fallback
    @Published var pressToToggle = Prefs.pressToToggle.fallback
    /// Short system sounds when recording starts and stops; none on insertion.
    @Published var recordingSounds = Prefs.recordingSounds.fallback
    /// Diagnostic recordings: removed from Settings, so always off; see `DiagnosticRecordings`.
    @Published var keepRecordings = Prefs.keepRecordings.fallback
    /// Live words in the bubble while recording; needs the optional download.
    @Published var liveWords = Prefs.liveWords.fallback
    /// Live words also while transcription runs in the cloud; off by default (user, Oct 15, 2026).
    @Published var liveWordsOnCloud = Prefs.liveWordsOnCloud.fallback
    /// The running preview transcript. Display only: never saved, logged, learned from or cleaned.
    @Published var liveText = ""
    @Published var liveWordsSlot: ModelSlot
    var liveWordsActive: Bool { live != nil || liveWordsFixture }
    /// The UI fixture's synthetic preview (check_ui.swift); never set in the app.
    var liveWordsFixture = false
    @Published var retention = Prefs.retention.fallback
    @Published var cleanupFillers = Prefs.cleanupFillers.fallback
    @Published var cleanupCorrections = Prefs.cleanupCorrections.fallback
    @Published var cleanupLists = Prefs.cleanupLists.fallback
    @Published var cleanupPunctuation = Prefs.cleanupPunctuation.fallback
    /// Polish instead of strict cleanup (`CleanupOptions.polish`).
    @Published var cleanupPolish = Prefs.cleanupPolish.fallback
    /// Professional above Polish (`CleanupOptions.professional`).
    @Published var cleanupProfessional = Prefs.cleanupProfessional.fallback
    @Published var cleanupSpoken = Prefs.cleanupSpoken.fallback
    /// Screen context for AI cleanup, opt-in; a writing mode can override it.
    @Published var screenContext = Prefs.screenContext.fallback
    /// Writing modes, saved as soon as they change and read at each recording start.
    /// The engine writing modes and voice edit run on (AIEngine.swift): derived from the cleanup route, never stored.
    var aiEngine: AIEngine? { AIEngine.derive(cleanup: appliedRoutes.cleanup) }
    @Published var writingModes: [WritingMode] { didSet { WritingModes.save(writingModes, to: .standard) } }
    /// Apps dictated into since launch, newest first, for the mode app picker. Memory only.
    @Published private(set) var recentApps: [String] = []
    @Published var learningEnabled = Prefs.learningEnabled.fallback
    @Published var learningRequiresReadableField = Prefs.readableLearning.fallback
    // Dictionary edits apply immediately; dictionaryMessage reports the last edit error.
    @Published var dictionaryEntries: [DictionaryEntry] = []
    /// Forms and spellings learning skips (conflicting, undone or deleted fixes), for Settings.
    @Published var blockedWords: [CorrectionLedger.Blocked] = []
    @Published var dictionaryMessage = ""
    /// Voice cues for Settings; `cueMessage` reports the last edit error.
    @Published var cues: [VoiceCue] = []
    @Published var cueMessage = ""
    @Published var dictionarySuggestions: [DictionarySuggestion] = []
    var spellingCache: [String: Bool] = [:]
    // Model downloads. Each slot's message reports the last download action, never interrupting dictation.
    @Published var modelSlot: ModelSlot
    @Published var tdtSlot: ModelSlot
    @Published var whisperSlot: ModelSlot
    @Published var dictionarySlot: ModelSlot
    /// The helper could not start dictionary support and is transcribing without it.
    @Published var dictionarySupportFailed = false
    /// The managed local cleanup model (Murmur Cleanup 2B): downloaded only after the user asks, in the app's own model folder.
    @Published var localSlot: ModelSlot
    var modelSlots: [ReferenceWritableKeyPath<AppController, ModelSlot>] { [\.modelSlot, \.tdtSlot, \.whisperSlot, \.dictionarySlot, \.liveWordsSlot, \.localSlot] }
    var usesCustomModelFolder: Bool { modelSlot.downloader.map { URL(fileURLWithPath: modelDirectory).standardizedFileURL.path != $0.target.path } ?? false }
    // History-off mode and the size cap. History-off and update checks apply as soon as they change.
    @Published var historyMode = Prefs.historyMode.fallback
    @Published var historyCapWarning: String?
    /// Set at launch when damaged history files were moved aside.
    @Published var historyNotice: String?
    /// While the first-run screen is open, a dictation made with Murmur in front lands here instead of being refused.
    var setupShowing = false
    @Published var setupTestTranscript = ""
    @Published var sessionOnlyIDs: Set<String> = []
    /// Home and Insights, from the History kept on disk only.
    @Published var insights: Insights = .empty
    var historySummary: String { HistoryText.summary(mode: appliedHistoryMode, count: history.count, sessionOnly: sessionOnlyIDs.count) }
    // Sparkle owns update dialogs, installation and the automatic schedule.
    @Published var updateChecksEnabled = Prefs.updateChecks.fallback
    @Published var updateStatus = ""
    @Published var installUpdatesAutomatically = false
    private lazy var sparkleUpdater = AppUpdater(isBusy: { [weak self] in
        guard let self else { return true }
        return self.isBusy || self.meetingRecorder.hasPendingUpdateWork
    }, preferencesChanged: { [weak self] checks, installs in
        self?.updateChecksEnabled = checks
        self?.installUpdatesAutomatically = installs
    })
    var currentVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0" }
    // Processing routes apply as soon as they change.
    // API keys save immediately to the Keychain; only their presence is published.
    @Published var transcriptionRoute: TranscriptionRoute
    @Published var cleanupChoice: CleanupChoice
    @Published var customAPIBaseURL: String
    @Published var customTranscriptionAPIBaseURL: String
    @Published var customAPIModel: String
    @Published var customTranscriptionModel: String
    @Published var customAPIReasoningEffort: String
    @Published var savedKeys: Set<ProviderID> = []
    @Published var keyMessages: [ProviderID: String] = [:]
    var routeSettings: RouteSettings { RouteSettings(transcription: transcriptionRoute, cleanup: cleanupChoice, customAPIBaseURL: customAPIBaseURL, customTranscriptionAPIBaseURL: customTranscriptionAPIBaseURL, customAPIModel: customAPIModel, customTranscriptionModel: customTranscriptionModel, customAPIReasoningEffort: customAPIReasoningEffort) }
    var appliedTranscriptionIsLocal: Bool { appliedRoutes.transcription == .local }
    /// What a stage would send off this Mac with the settings as edited, or nil when nothing leaves.
    /// Reads no key (SwiftUI calls it on every render); shows the configured route's disclosure.
    func routeDisclosure(_ stage: ProcessingStage) -> String? { DisclosureText.summary(RoutePlan.make(routeSettings, cleanupSendsText: true) { _ in nil }, stage: stage) }
    var canCancelRequest: Bool { cloudRequest.isActive }
    var isBusy: Bool { phase == .listening || phase == .processing || (sessionAvailable && session.state != .idle) }
    var openSettings: (() -> Void)?
    var updateMenu: (() -> Void)?
    lazy var bubble = BubblePresenter(controller: self)
    /// Meeting detection: offers meeting notes when a call app or browser uses the microphone.
    lazy var callOffers = CallOfferPresenter(controller: self)
    lazy var tips = TipPresenter(controller: self)
    /// Meeting notes: its own capture and store, sharing the warm helper.
    let meetingRecorder = MeetingRecorder()
    /// Settings → Meetings → speaker separation: the switch and its optional download.
    let speakerSettings = SpeakerModelSettings()
    var store: HistoryStore?
    /// All history reads and writes; it keeps entries in memory only when history is off.
    var records: HistoryRouter<HistoryEntry>?
    var appliedHistoryMode: HistoryMode
    var dataDirectory: URL? // Internal so the isolated UI fixture can seed only its temporary history.
    /// Set once the live folder's hashes were checked this launch (and the model compiled).
    var liveWordsVerified = false
    var live: LiveStream?
    /// The private vocabulary file the helper reads with dictionary support (the pinned CTC model, `dictionarySlot`).
    var vocabularyFile: URL?
    /// Set by start(); until then dictionary refreshes do not pre-warm, so launch hashes the model once.
    var started = false
    let dictionaryModelDirectory = ModelFacts.dictionary.directory(FileManager.default.homeDirectoryForCurrentUser)
    var retry: RetryAudio?
    var processing: ProcessingCoordinator?
    var appliedRoutes: RouteSettings
    // Preview and the developer probe never touch the login Keychain.
    let professionalHint = ProfessionalHint(defaults: .standard)
    lazy var credentials: CredentialStore = preview ? InMemoryCredentialStore() : KeychainCredentialStore()
    let cloudRequest = CloudRequest()
    var cloudDone: (() -> Void)?
    var cloudStage: ProcessingStage?
    /// Voice edit (VoiceEdit.swift): armed by the chord, adopted by the next start, spent by this recording's transcript.
    var pendingVoiceEdit: VoiceEditSession?
    var voiceEdit: VoiceEditSession?
    var voiceEditPressedAt: TimeInterval = 0
    /// `EditTrigger.sameKey`: the dictation key, when text is selected. Otherwise a recorded `CustomShortcut`.
    @Published var voiceEditShortcut = Prefs.voiceEditShortcut.fallback
    /// This session's cloud transcriber, chosen at start; nil means local Parakeet.
    var sessionTranscriber: TranscriptionProvider?
    private var sessionLanguages = DictationLanguages.english
    var capture: Capture?
    /// The field focused when recording started. Clearing it ends the session's mode and context too.
    var destination: FocusSnapshot? { didSet { if destination == nil { sessionMode = nil; sessionContext = nil } } }
    /// The app or website mode matched when recording started (a trigger word can still override it).
    /// Only the mode is kept: the page URL is read once at start and never stored or logged.
    private var sessionMode: WritingMode?
    /// Screen text captured at start, in memory only, for AI cleanup when the resolved mode allows it.
    private var sessionContext: ScreenContext?
    var destinationScreen: NSScreen?
    private var insertThisSession = true
    // Uptime marks for timing logs only: when the key was released, and when cleanup began.
    private var finishedAt = 0.0, cleanupStartedAt = 0.0, transcribeStartedAt = 0.0
    /// Where kept recordings go; nil while the setting is off or History is.
    private let recordings = DiagnosticRecordings()
    /// Kept only while History is saved: a diagnostic recording must not outlive History's own switch.
    private func applyRecordingSetting() {
        recordings.directory = keepRecordings && appliedHistoryMode.persistsTranscripts && !preview ? store.map { DiagnosticRecordings.directory(inHistory: $0.directory) } : nil
    }
    private var leadingSpaceThisSession = false
    private var shortcutGeneration = UUID()
    var listener: ShortcutListener?
    private var source: CFRunLoopSource?
    private var tapThread: TapThread?
    let prestart = PreStart()
    /// Recording continues this long after release so the last word survives.
    nonisolated private static let releaseTail: TimeInterval = 0.2
    /// Uptime of the last hold's key-release event, stamped on the tap thread and consumed by the main
    /// thread's finish, so timing and the tail start at the release, not when a busy main thread got to it.
    nonisolated private static let holdRelease = ReleaseStamp()
    private final class ReleaseStamp: @unchecked Sendable {
        private let lock = NSLock()
        private var value: TimeInterval?
        func set(_ uptime: TimeInterval) { lock.withLock { value = uptime } }
        func take() -> TimeInterval? { lock.withLock { defer { value = nil }; return value } }
    }
    let warm = WarmRunner()
    /// Chunk transcripts of local recordings, keyed by each recording's audio file.
    var chunkJobs: [URL: ChunkJob] = [:]
    private var noNap: NSObjectProtocol?
    private var monitor: Timer?
    private var expiry: Timer?
    private var runner: Process?
    private var recordingFailed = false
    var generation = UUID()
    var appliedRetention: HistoryRetention
    private var activeModelDirectory = ""
    private var listeningStarted = Date()
    private var phaseBeforeEarly: (Phase, String)?
    private var statusShown = UUID()
    private var applyScheduled = false
    private var folderWatch: AnyCancellable?
    var preview = false
    private var previewDirectory: URL?
    private lazy var session: CaptureSession = {
        let session = CaptureSession(store: retry!, start: { [weak self] url in
            guard let self else { throw CaptureError("Application stopped") }
            let recording = Capture(); self.capture = recording
            try recording.start(url: url)
            self.setPhase(.listening, self.listeningMessage)
        }, stop: { [weak self] in
            guard let self else { return false }
            self.capture?.stop()
            self.recordingFailed = self.recordingFailed || self.capture?.failed == true
            let hasAudio = (self.capture?.frames ?? 0) > 0
            self.capture = nil
            return hasAudio
        }, process: { [weak self] url, done in
            guard let self else { done(); return }
            if self.recordingFailed {
                self.chunkJobs.removeValue(forKey: url)?.discard()
                self.setPhase(.error, "Recording was interrupted. Partial audio is saved for retry.")
                done()
            } else { self.transcribe(url, done: done) }
        })
        // ponytail: fixed tail (`releaseTail`); measured: long dictations lost the last word with none.
        session.releaseTail = Self.releaseTail
        session.takePrestarted = { [weak self] in
            guard let self, let (capture, url) = self.prestart.take() else { return nil }
            self.capture = capture; return url
        }
        session.resumeCapture = { [weak self] in guard let self else { return }; self.setPhase(.listening, self.listeningMessage) }
        session.willDeleteCanceled = { [weak self] url in self?.holdForUndo(url) }
        session.finishRecording = { [weak self] in timingLog.notice("capture-cap reached"); guard let self, let listener = self.listener else { return }; listener.emit(listener.machine.finish()) }
        session.resetShortcut = { [weak self] in guard let self, let listener = self.listener else { return }; listener.emit(listener.machine.eventStreamLost()); listener.machine.processingFinished() }
        return session
    }()

    // Stored properties of the per-feature extensions (Controller+*.swift), which cannot declare their own.
    /// Settings → Meetings: where meeting audio is transcribed (decision of October 15, 2026). Applies
    /// to meetings started after it changes; a meeting keeps the route it started with.
    @Published var meetingTranscription = MeetingTranscription.load(.standard) {
        didSet { UserDefaults.standard.set(meetingTranscription.rawValue, forKey: MeetingTranscription.key) }
    }
    /// Settings → Meetings → Meeting notes. Nil until the first Stop asks, with None preselected.
    // Unset defaults to Apple Intelligence when it is ready on this Mac; otherwise the first Stop asks.
    @Published var meetingNotesRoute = MeetingNotesRoute.load(.standard) ?? (AppleSystemModel.status() == nil ? .appleIntelligence : nil) {
        didSet {
            if let route = meetingNotesRoute { UserDefaults.standard.set(route.rawValue, forKey: MeetingNotesRoute.key) }
            else { UserDefaults.standard.removeObject(forKey: MeetingNotesRoute.key) }
        }
    }
    @Published var modeRecovery: ModeRecovery?
    let recoveryRequest = CloudRequest()
    /// Latest insertable dictation, including verified deliveries, kept in memory for ⌃⌘V.
    var lastDictationText: String?
    var watch: Watch?
    var watchTimer: Timer?
    var keyTap: CFMachPort?
    var preparingLiveWords = false
    /// Shows or hides the live meeting panel; set by the app delegate.
    var meetingPanelChanged: (() -> Void)?
    lazy var meetingConsent = MeetingConsentPresenter()

    init(preview: Bool = false) {
        let defaults = UserDefaults.standard
        let home = FileManager.default.homeDirectoryForCurrentUser, pins = BundledModels.pins
        modelSlot = ModelSlot(.batch, pins: pins, home: home)
        tdtSlot = ModelSlot(.multilingualTDT, pins: pins, home: home)
        whisperSlot = ModelSlot(.multilingualWhisper, pins: pins, home: home)
        dictionarySlot = ModelSlot(.dictionary, pins: pins, home: home)
        liveWordsSlot = ModelSlot(.liveWords, pins: pins, home: home)
        let local = ModelSlot(.localCleanup, pins: pins, home: home)
        localSlot = local
        modelDirectory = defaults[Prefs.modelDirectory]
        if let downloader = local.downloader {
            // The first launch of the helper in a session checks every file's size and checksum against the pins.
            LocalCleanupRunner.verifier = { downloader.verifyInstalled() == .ready }
        }
        let inputDevice = defaults[Prefs.inputDeviceUID]
        Capture.preferredInputUID = inputDevice.isEmpty ? nil : inputDevice
        appliedRetention = defaults[Prefs.retention]; appliedHistoryMode = defaults[Prefs.historyMode]
        writingModes = WritingModes.load(from: defaults)
        let routes = RouteSettings.load(from: defaults)
        transcriptionRoute = routes.transcription; cleanupChoice = routes.cleanup
        customAPIBaseURL = routes.customAPIBaseURL; customTranscriptionAPIBaseURL = routes.customTranscriptionAPIBaseURL; customAPIModel = routes.customAPIModel; customTranscriptionModel = routes.customTranscriptionModel; customAPIReasoningEffort = routes.customAPIReasoningEffort; appliedRoutes = routes
        self.preview = preview
        loadSettings(from: defaults)
        do {
            let base = preview ? FileManager.default.temporaryDirectory.appendingPathComponent("Dictation-preview-" + UUID().uuidString) : MurmurDataLocations.applicationSupport
            if preview { previewDirectory = base }
            dataDirectory = base
            let locations = MurmurDataLocations.standard(base: base)
            store = try makeHistoryStore(locations.history)
            if let store { records = HistoryRouter(disk: historyDisk(store), persists: appliedHistoryMode.persistsTranscripts) }
            applyRecordingSetting()
            retry = try RetryAudio(directory: locations.retryAudio)
            // Chunk files live only while their recording is transcribed; any left by a crash go now.
            try? FileManager.default.removeItem(at: locations.chunks)
            try? makeChunkFolder(locations.chunks)
            // Dictionary/ledger corruption disables only cleanup and the dictionary: dictations are then held for review in History rather than blocking all dictation.
            vocabularyFile = locations.dictionary.appendingPathComponent("parakeet-vocabulary.json")
            do { processing = try ProcessingCoordinator.inApp(directory: locations.dictionary) }
            catch { processing = nil; dictionaryMessage = "The dictionary could not be read. Dictations are held in History until it is repaired." }
            if let processing, processing.settings.route != routes.cleanup.processingRoute {
                var next = processing.settings; next.route = routes.cleanup.processingRoute; try? processing.update(next)
            }
            if let retry {
                for file in try FileManager.default.contentsOfDirectory(at: retry.directory, includingPropertiesForKeys: nil) where file.lastPathComponent.hasPrefix(".result-") {
                    try FileManager.default.removeItem(at: file)
                }
            }
            // One damaged file must not stop Murmur: move it aside, say so, and still expire it on schedule.
            if let moved = try store?.quarantineUnreadable(), moved > 0 {
                historyNotice = HistoryStore.quarantineNotice(moved)
            }
            try store?.purge(retention: appliedRetention)
            configureMeetings(locations)
            historySaved(); refreshRecoveryAudio(); refreshDictionary(); refreshCues()
        } catch { store = nil; records = nil; retry = nil; processing = nil; phase = .error; message = "Private storage is unavailable. Murmur cannot start." }
        for slot in modelSlots { self[keyPath: slot].status = self[keyPath: slot].downloader?.status() ?? .notDownloaded }
        // An update that pins a new model leaves the old folder behind: delete it if the new model is ready, else say how to replace it.
        for slot in modelSlots where !self[keyPath: slot].retiredFolders(home: home).isEmpty {
            if self[keyPath: slot].status == .ready { self[keyPath: slot].retiredFolders(home: home).forEach { try? FileManager.default.removeItem(at: $0) } }
            else { self[keyPath: slot].message = "This update uses a newer model. Download it to replace the old one; the old one is deleted once the new one is verified." }
        }
        warm.onDictionaryStatus = { [weak self] failed in DispatchQueue.main.async { self?.dictionarySupportFailed = failed } }
        if !preview {
            do { try RouteSettings.migrateCustomAPI(in: defaults, credentials: credentials) }
            catch {
                keyMessages[.customTranscription] = KeyText.storeFailed
                keyMessages[.customCleanup] = KeyText.storeFailed
            }
        }
        refreshKeys()
        // Choose Folder, Use Default and a finished download all change the folder, even with
        // Settings closed; apply each so the folder survives a relaunch.
        folderWatch = $modelDirectory.dropFirst().removeDuplicates().sink { [weak self] _ in self?.settingsChanged() }
        if phase == .ready { message = idleMessage }
    }
    func start() {
        // Writing to a warm helper that died must fail with EPIPE, not kill Murmur.
        signal(SIGPIPE, SIG_IGN)
        // App Nap would delay the shortcut; idle system sleep stays allowed.
        // Verify the model now so the first dictation does not hash 1.8 GB.
        started = true; prewarm(); if liveWords { prepareLiveWords() }
        noNap = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep, reason: "Respond to the dictation shortcut without delay")
        refreshPermissions()
        if !preview { configureShortcut() }
        if !preview {
            updateStatus = sparkleUpdater.start(checksEnabled: updateChecksEnabled)
            if Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") != nil {
                updateChecksEnabled = sparkleUpdater.automaticallyChecksForUpdates
            }
            installUpdatesAutomatically = sparkleUpdater.automaticallyDownloadsUpdates
        }
        for name in [AVCaptureDevice.wasConnectedNotification, AVCaptureDevice.wasDisconnectedNotification] {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.refreshInputDevices() } }
        }
        // Returning from System Settings reactivates Murmur: re-check grants and start the shortcut once Input Monitoring is allowed.
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.refreshPermissions()
                if !self.preview && self.listener == nil && CGPreflightListenEventAccess() { self.configureShortcut(); if self.listener != nil { self.message = "Input Monitoring allowed. The shortcut is ready." } }
            }
        }
        monitor = Timer.scheduledTimer(withTimeInterval: MeetingLimits.tickInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.voiceLevel = self.capture?.currentLevel ?? 0
                self.meetingRecorder.tick()
                if !self.preview { self.callOffers.tick(); self.tips.tick() }
                for (audio, job) in self.chunkJobs where !FileManager.default.fileExists(atPath: audio.path) { job.discard(); self.chunkJobs[audio] = nil }
                for slot in self.modelSlots where self[keyPath: slot].inFlight {
                    if let status = self[keyPath: slot].downloader?.status(), status != self[keyPath: slot].status { self[keyPath: slot].status = status }
                }
                if self.sessionAvailable && self.session.state == .listening, let capture = self.capture,
                   // A device change stops the engine; it restarts on the current device and only
                   // an engine that cannot restart counts as an interruption.
                   (!capture.engine.isRunning && !capture.restart()) || capture.failed {
                    let running = capture.engine.isRunning, writeFailed = capture.failed
                    timingLog.notice("capture-interrupted engine-running=\(running, privacy: .public) write-error=\(writeFailed, privacy: .public)")
                    self.recordingFailed = true; self.stopRecording()
                }
            }
        }
        if let monitor { RunLoop.main.add(monitor, forMode: .common) }
        expiry = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                do {
                    try self.retry?.purge(); try self.store?.purge(retention: self.appliedRetention)
                    if let history = self.store?.directory { try DiagnosticRecordings.purge(history: history) }
                    if let cutoff = self.appliedRetention.cutoff() { self.records?.purgeSessionOnly(recordedAtOrBefore: cutoff) }
                    self.meetingRecorder.purge(cutoff: self.appliedRetention.cutoff())
                    try self.enforceHistoryCap(); self.refreshHistory(); self.refreshRecoveryAudio()
                }
                catch {
                    if self.isBusy { self.message = "Data expiry failed. Open History after this dictation to review saved data." }
                    else { self.setPhase(.error, "Data expiry failed. Open History to review saved data.") }
                }
            }
        }
    }
    var sessionAvailable: Bool { retry != nil && store != nil }
    func setOpenAtLogin(_ on: Bool) {
        guard !preview else { openAtLogin = on; return }
        do { if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
        catch { message = on ? "Murmur could not be added to Login Items. Add it in System Settings → General → Login Items." : "Murmur could not be removed from Login Items. Remove it in System Settings → General → Login Items." }
        openAtLogin = SMAppService.mainApp.status == .enabled
    }
    func refreshPermissions() {
        // Login Items can be changed in System Settings; this runs whenever Murmur is reactivated.
        if !preview { openAtLogin = SMAppService.mainApp.status == .enabled }
        permissionsSummary = "Microphone: \(permission().rawValue) · Accessibility: \(AXIsProcessTrusted() ? "allowed" : "needed") · Input Monitoring: \(CGPreflightListenEventAccess() ? "allowed" : "needed")"
    }
    func requestMicrophone() {
        AVCaptureDevice.requestAccess(for: .audio) { [weak self] _ in DispatchQueue.main.async { self?.refreshPermissions() } }
    }
    func requestPermission(_ permission: Permission) {
        guard !preview else { return }
        switch permission {
        case .microphone:
            if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined { requestMicrophone() }
            else { openPermissionSettings(.microphone) }
        case .accessibility:
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        case .inputMonitoring:
            if CGRequestListenEventAccess() { refreshPermissionsAndShortcut() }
        }
    }

    func openPermissionSettings(_ permission: Permission) {
        guard !preview else { return }
        let pane = switch permission { case .inputMonitoring: "Privacy_ListenEvent"; case .microphone: "Privacy_Microphone"; case .accessibility: "Privacy_Accessibility" }
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") { NSWorkspace.shared.open(url) }
    }
    func chooseModelDirectory() {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.allowsMultipleSelection = false; panel.message = "Choose the pinned Parakeet Unified model folder."
        if panel.runModal() == .OK, let url = panel.url { modelDirectory = url.path }
    }
    /// Settings apply as soon as they change; there is no Apply button. Changes made in the same
    /// moment (Faithful Transcription flips three toggles) apply once. Confirmations still appear,
    /// and Cancel puts the control back.
    func settingsChanged() {
        guard !applyScheduled else { return }
        applyScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.applyScheduled = false
            self.applySettings()
        }
    }
    func refreshPermissionsAndShortcut() {
        refreshPermissions()
        if !preview, listener == nil, !isBusy, CGPreflightListenEventAccess() { configureShortcut() }
    }
    func applySettings() {
        guard !isBusy else { message = "Finish the current dictation before changing settings."; return }
        let retentionChanged = retention != appliedRetention
        let historyChanged = historyMode != appliedHistoryMode
        let expired: [HistoryEntry]
        do { expired = retentionChanged ? try store?.expired(retention: retention) ?? [] : [] }
        catch { message = "Could not review history retention. Settings were not applied."; return }
        let expiredMeetings = retention.cutoff().map { cutoff in meetingRecorder.meetings.filter { $0.recordedAt <= cutoff }.count } ?? 0
        if retention != appliedRetention, !expired.isEmpty || expiredMeetings > 0 {
            let alert = NSAlert()
            alert.messageText = expiredMeetings == 0 ? "Delete \(expired.count) expired transcripts?" : "Delete \(expired.count) expired transcripts and \(expiredMeetings) \(expiredMeetings == 1 ? "meeting" : "meetings")?"
            alert.informativeText = "Applying this retention period permanently removes these older entries."
            alert.addButton(withTitle: "Apply and Delete"); alert.addButton(withTitle: "Cancel")
            guard alert.runDestructive() == .alertFirstButtonReturn else { retention = appliedRetention; return }
        }
        if historyMode == .off && appliedHistoryMode == .on {
            let alert = NSAlert(); alert.messageText = "Turn history off?"
            alert.informativeText = HistoryMode.offDisclosure
            alert.addButton(withTitle: "Turn Off History"); alert.addButton(withTitle: "Cancel")
            guard alert.runDestructive() == .alertFirstButtonReturn else { historyMode = appliedHistoryMode; return }
        }
        let routes = routeSettings
        if routes.cleanup == .localModel && appliedRoutes.cleanup != .localModel { LocalModelCleanupProvider().prewarm() }
        appliedRoutes = routes; routes.save(to: .standard)
        appliedRetention = retention
        appliedHistoryMode = historyMode; records?.persistsTranscripts = historyMode.persistsTranscripts
        if meetingRecorder.persists != historyMode.persistsTranscripts { meetingRecorder.persists = historyMode.persistsTranscripts; meetingRecorder.reload() }
        let defaults = UserDefaults.standard
        defaults[Prefs.modelDirectory] = modelDirectory; saveSettings(to: defaults)
        applyRecordingSetting(); if liveWords { prepareLiveWords() }
        Capture.preferredInputUID = inputDeviceUID.isEmpty ? nil : inputDeviceUID
        if let processing {
            if !learningEnabled || (learningRequiresReadableField && watch?.keys != nil) {
                if let watch { try? processing.ledger.revert(sessionID: watch.observation.sessionID) }
                stopWatching()
            }
            var next = processing.settings
            next.options = ProcessingSettings.options(fillers: cleanupFillers, corrections: cleanupCorrections, lists: cleanupLists, punctuation: cleanupPunctuation, polish: cleanupPolish, professional: cleanupProfessional, spoken: cleanupSpoken)
            next.learningEnabled = learningEnabled; next.route = routes.cleanup.processingRoute
            next.screenContext = screenContext
            do { try processing.update(next) } catch { setPhase(.error, "Could not save cleanup and learning settings.") }
        }
        if !preview { sparkleUpdater.setPreferences(checks: updateChecksEnabled, installs: installUpdatesAutomatically) }
        // Unrelated switches must not scan disk and recompute Insights during their animation.
        if retentionChanged {
            do { try store?.purge(retention: appliedRetention); meetingRecorder.purge(cutoff: appliedRetention.cutoff()); try enforceHistoryCap() }
            catch { setPhase(.error, "Could not apply history retention.") }
        }
        if retentionChanged || historyChanged { refreshHistory() }
        refreshPermissions(); if !preview { configureShortcut() }
        if phase == .ready { message = idleMessage }
        updateMenu?()
    }
    func routePlan(_ routes: RouteSettings, cleanupSendsText: Bool) -> RoutePlan {
        var refusal: String?
        let plan = RoutePlan.make(routes, cleanupSendsText: cleanupSendsText) { provider in
            do { return try CustomAPIKeyBinding.readKey(for: provider, routes: routes, defaults: .standard, credentials: self.credentials) } catch {
                refusal = (error as? CredentialError)?.description ?? KeyText.unreadable
                return nil
            }
        }
        return RoutePlan(transcriber: plan.transcriber, cleaner: refusal == nil ? plan.cleaner : nil, refusal: refusal ?? plan.refusal, stages: plan.stages)
    }
    private func configureShortcut() {
        removeShortcut()
        guard sessionAvailable else { return }
        guard CGPreflightListenEventAccess() else { message = "Allow Input Monitoring, then click Refresh Permissions to enable the shortcut."; return }
        let binding = ShortcutBinding(stored: shortcut)
        let token = shortcutGeneration
        let next = ShortcutListener(binding: binding, processingSeconds: 0, onAction: { [weak self] action, done in
            guard let self, self.shortcutGeneration == token else { done(); return }
            self.handle(action, done: done)
        })
        guard let tap = next.createTap(), let nextSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else { message = "The shortcut could not start. Check Input Monitoring and Accessibility."; return }
        next.onEscapeWhileProcessing = { [weak self] in self?.cancelCloudRequest() }
        next.onCopyChord = { [weak self] in self?.copyLatestRawTranscript() }
        next.onPasteChord = { [weak self] in self?.pasteLastDictation() }
        next.onRevertChord = { [weak self] in self?.revertToRaw() }
        next.revertOffered = { [weak self] in self?.revertOffered == true }
        if case .custom(let edit) = ShortcutBinding(stored: voiceEditShortcut), voiceEditShortcut != EditTrigger.sameKey {
            next.editBinding = edit; next.editSelectionProbe = editSelectionReadable
            next.onEditPress = { [weak self] in self?.voiceEditPressed() }
            next.onEditRelease = { [weak self] in self?.voiceEditReleased() }
        }
        next.pressToToggle = pressToToggle
        // A press while the previous dictation processes records right away; its events replay
        // into the machine when that dictation finishes, which adopts this capture.
        next.onQueuedPress = { [weak self] in
            guard let self, self.sessionAvailable, !IsSecureEventInputEnabled() else { return false }
            let shown = (self.phase, self.message)
            guard self.session.startEarly() else { return false }
            self.phaseBeforeEarly = shown; self.cue(.start); return true
        }
        next.onQueuedCancel = { [weak self] in
            guard let self else { return }
            self.session.cancelEarly()
            if self.session.state == .processing, let (phase, message) = self.phaseBeforeEarly { self.setPhase(phase, message) }
        }
        listener = next; source = nextSource
        // A stalled main thread must not keep recording after a hold's release.
        next.onHoldRelease = { released in
            Self.holdRelease.set(released)
            Capture.current?.cut(after: released + AppController.releaseTail)
        }
        if let retry {
            next.onPressFromIdle = { [prestart] in prestart.begin(store: retry) }
            next.onPressWithoutStart = { [prestart] in prestart.discard(store: retry) }
        }
        let thread = TapThread(source: nextSource); thread.startAndWait(); tapThread = thread
        CGEvent.tapEnable(tap: tap, enable: true)
    }
    private static let idleMessages = ["Press your shortcut to dictate and press it again to finish, or hold it and release.", "Hold your shortcut to dictate, or double-press it to dictate until you press it again."]
    var idleMessage: String { Self.idleMessages[pressToToggle ? 0 : 1] }
    /// False only for the plain idle hint; failures that leave the phase at Ready still show.
    var hasStatusToShow: Bool { phase != .ready || !Self.idleMessages.contains(message) }
    private var listeningMessage: String { "Listening… \(listener?.pressToToggle == true ? "Release or press the shortcut again" : "Release") to finish, or press Escape to cancel." }
    /// The shortcut recorder needs the keys itself: the tap would swallow the current chord, and
    /// pressing it would start a dictation. `resume` restores the tap with whatever is saved.
    func pauseShortcutForRecorder() -> Bool {
        guard !isBusy else { message = "Finish the current dictation before recording a shortcut."; return false }
        removeShortcut(); return true
    }
    func resumeShortcutAfterRecorder() { if !preview && listener == nil { configureShortcut() } }
    var shortcutName: String { ShortcutBinding(stored: shortcut).displayName }
    private func removeShortcut() {
        shortcutGeneration = UUID()
        tapThread?.stop(); tapThread = nil
        if let tap = listener?.tap { CFMachPortInvalidate(tap) }
        source = nil; listener = nil
    }
    private func refuse(_ message: String, done: () -> Void) { session.resetShortcut?(); setPhase(.error, message); done() }
    private func handle(_ action: ShortcutMachine.Action, done: @escaping () -> Void) {
        // A refused start never adopts the capture begun on the tap thread; delete it.
        defer { if action == .start, let retry { prestart.discard(store: retry) } }
        guard sessionAvailable else { done(); return }
        if action == .start {
            undo.discard()
            guard !IsSecureEventInputEnabled() else { refuse("Dictation is unavailable while Secure Input is active.", done: done); return }
            guard permission() == .authorized else { refuse("Allow Microphone access in Settings before dictating.", done: done); return }
            // An unconfigured cloud route refuses; it never falls back to local.
            // A mode may turn cleanup on even when the Cleanup settings have it all off.
            // With the dictation key and a readable selection this recording is a voice edit; with none, it dictates as before.
            if pendingVoiceEdit == nil, let edit = voiceEditForDictationKey() {
                switch edit {
                case .success(let session): pendingVoiceEdit = session
                case .failure(let refusal): refuse(refusal.message, done: done); return
                }
            }
            let editing = pendingVoiceEdit != nil // An edit sends no dictation cleanup and reads no screen context.
            guard let languages = DictationLanguages(codes: Set(dictationLanguages)) else {
                refuse("Choose at least one supported dictation language.", done: done); return
            }
            let sendsText = !editing && languages == .english && ((processing?.settings.options.anyEnabled ?? false) || writingModes.contains { $0.enabled && $0.options?.anyEnabled == true })
            let plan = routePlan(appliedRoutes, cleanupSendsText: sendsText)
            if let refusal = plan.refusal { refuse(refusal, done: done); return }
            let slot = self[keyPath: speechModelSlot(for: languages.localModel)]
            let modelPath = speechModelPath(for: languages.localModel)
            let available = languages.localModel == .parakeetEnglish ? modelFilesAvailable() : slot.row.isReady
            if plan.usesLocalModel, let refusal = ModelGate.startRefusal(modelAvailable: available, runtimeAvailable: BundledModels.helper != nil, downloadingActiveModel: slot.inFlight && (languages.localModel != .parakeetEnglish || !usesCustomModelFolder)) {
                refuse(refusal, done: done); return
            }
            sessionLanguages = languages
            sessionTranscriber = plan.transcriber; processing?.cloudCleaner = plan.cleaner; processing?.engineCleaner = engineProvider()
            (plan.cleaner as? LocalModelCleanupProvider)?.prewarm()
            destination = try? captureFocus()
            if destination?.secure == true { refuse("Password fields cannot receive dictation.", done: done); return }
            if destination?.pid == getpid() { destination = nil }
            insertThisSession = automaticInsertion; leadingSpaceThisSession = intelligentLeadingSpace; activeModelDirectory = modelPath ?? modelDirectory
            listeningStarted = Date(); generation = UUID(); recoveryEntryID = nil; recordingFailed = false
            voiceEdit = pendingVoiceEdit.map { var edit = $0; edit.generation = generation; return edit }; pendingVoiceEdit = nil
            destinationScreen = screenForDestination(destination)
            if languages == .english { captureModeAndContext(aiCleanup: plan.cleaner != nil && voiceEdit == nil) }
            else { sessionMode = nil; sessionContext = nil }
        }
        let wasListening = session.state == .listening
        if wasListening, action == .finish {
            let now = ProcessInfo.processInfo.systemUptime
            finishedAt = Self.holdRelease.take().flatMap { now - $0 < 3 ? $0 : nil } ?? now
            session.releasedAt = finishedAt
        }
        // An early capture already played its start sound when it began (onQueuedPress).
        let adoptsEarly = session.early != nil
        session.receive(action, completed: done)
        if action == .start, session.state == .listening, !adoptsEarly { cue(.start) }
        if action == .start, session.state == .listening, sessionLanguages == .english { startLiveWords() }
        if session.state != .listening { stopLiveWords() }
        if wasListening, action == .finish || (action == .cancel && listener?.cancelIsExplicit == true) { cue(.stop) }
        // Show processing at release, not after the release tail, so the stop feels immediate.
        if wasListening, action == .finish, session.state == .processing { setPhase(.processing, "Finishing recording…") }
        if action == .start, session.state == .listening, sessionTranscriber == nil, sessionLanguages.localModel == .parakeetEnglish, let capture, let audio = session.audio { attachChunks(capture, audio: audio) }
        if action == .cancel {
            destination = nil
            if undo.isOpen {
                setPhase(.ready, "Recording canceled. Undo is available for \(Int(undo.lifetime)) seconds.")
                bubble.showUndo(screen: destinationScreen)
                DispatchQueue.main.asyncAfter(deadline: .now() + undo.lifetime) { [weak self] in if self?.undo.isOpen == false { self?.undo.discard() } }
            } else { setPhase(.ready, "Recording canceled."); bubble.hide() }
        }
        else if action == .start && session.state == .idle { setPhase(.error, "The microphone could not start. Check the selected input device.") }
        else if action == .finish && session.state == .idle && phase == .listening {
            destination = nil; setPhase(.error, "No audio was captured. Check your microphone and try again.")
        }
    }
    /// Writing mode for the app (and page) focused at start, and screen context when an AI route will
    /// use it. The page URL is read through Accessibility (no Automation prompt), only when an enabled
    /// mode lists a website, and is used here and dropped. Screen text is read only with screen context
    /// on (globally or in a mode), never under Secure Input or in a secure field, and stays in memory.
    private func captureModeAndContext(aiCleanup: Bool) {
        sessionMode = nil; sessionContext = nil
        let front = NSWorkspace.shared.frontmostApplication
        let pid = destination?.pid ?? front?.processIdentifier
        guard let pid, pid != getpid() else { return }
        let modes = writingModes
        if modes.contains(where: \.enabled) {
            let url = WritingModes.needsURL(modes) ? focusedPageURL(pid: pid, from: destination?.element) : nil
            sessionMode = WritingModes.match(modes, bundleID: destination?.bundleID ?? front?.bundleIdentifier, url: url)
        }
        // Read only if this dictation could use it: the start mode (or the global setting) has it on,
        // or a mode that a trigger word could still pick does.
        // The managed local model never uses screen text, so none is read for it.
        guard aiCleanup || processing?.engineCleaner != nil, appliedRoutes.cleanup != .localModel || processing?.engineCleaner != nil, let settings = processing?.settings,
              DictationStyle.needsScreenContext(mode: sessionMode, modes: modes, settings: settings),
              !IsSecureEventInputEnabled(), destination?.secure != true else { return }
        let pasteboard = NSPasteboard.general
        let types = pasteboard.types?.map(\.rawValue) ?? []
        // Never read the string of an item marked as a password or otherwise private.
        let clipboard = types.contains(where: ScreenContext.privateTypes.contains) ? nil : pasteboard.string(forType: .string)
        let selected = destination.flatMap { focusedSelectedText($0.element) }
        let token = generation
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let window = focusedWindowText(pid: pid, limit: ScreenContext.windowLimit)
            DispatchQueue.main.async {
                guard let self, self.generation == token else { return }
                self.sessionContext = ScreenContext.make(selected: selected, clipboard: clipboard, clipboardTypes: types, window: window,
                                                         secureInput: IsSecureEventInputEnabled(), secureField: self.destination?.secure == true)
            }
        }
    }
    private func attachChunks(_ capture: Capture, audio: URL) {
        let models = URL(fileURLWithPath: activeModelDirectory)
        guard let runtime = helperRuntime else { return }
        let warm = warm
        let job = ChunkJob { url, boundary, done in
            runtime.launch(models: models, qos: .userInitiated) { launch in
                guard let launch else { done(nil); return }
                // Without dictionary support there is nothing to repair at a cut.
                if boundary && launch.dictionaryArguments.isEmpty { done(nil); return }
                warm.transcribe(url, helper: runtime.helper, launch: launch, boundary: boundary, completion: done)
            }
        }
        if capture.attachChunks({ job.receive($0) }) { chunkJobs[audio] = job }
    }
    private func modelFilesAvailable() -> Bool {
        guard let files = modelSlot.downloader?.manifest.files else { return false }
        let folder = URL(fileURLWithPath: modelDirectory)
        return files.allSatisfy { ((try? FileManager.default.attributesOfItem(atPath: folder.appendingPathComponent($0.rfilename).path))?[.size] as? NSNumber)?.int64Value == $0.size }
    }
    private func screenForDestination(_ destination: FocusSnapshot?) -> NSScreen? {
        if let destination {
            var rawPosition: CFTypeRef?, rawSize: CFTypeRef?
            if AXUIElementCopyAttributeValue(destination.element, kAXPositionAttribute as CFString, &rawPosition) == .success,
               AXUIElementCopyAttributeValue(destination.element, kAXSizeAttribute as CFString, &rawSize) == .success,
               let rawPosition, let rawSize,
               CFGetTypeID(rawPosition) == AXValueGetTypeID(), CFGetTypeID(rawSize) == AXValueGetTypeID() {
                var point = CGPoint.zero; var size = CGSize.zero
                if AXValueGetValue(unsafeBitCast(rawPosition, to: AXValue.self), .cgPoint, &point),
                   AXValueGetValue(unsafeBitCast(rawSize, to: AXValue.self), .cgSize, &size) {
                    let center = NSPoint(x: point.x + size.width / 2, y: (NSScreen.screens.first?.frame.maxY ?? 0) - point.y - size.height / 2)
                    if let screen = NSScreen.screens.first(where: { NSMouseInRect(center, $0.frame, false) }) { return screen }
                }
            }
        }
        return NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) }
    }
    func liveVoiceLevel() -> Double {
        capture?.currentLevel ?? 0
    }
    /// Home's Start button. Murmur hides first, so focus returns to the app you were in and
    /// the text pastes at its cursor on release, as with the shortcut; the next press or Finish ends it.
    func startDictationFromHome() {
        guard let listener, listener.machine.state == .idle, !isBusy else { return }
        NSApp.hide(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let listener = self?.listener else { return }
            listener.emit(listener.machine.startToggled())
        }
    }
    /// First-run Try it button: starts or ends a dictation without hiding Murmur, so the result lands in the guide.
    func toggleSetupDictation() {
        guard let listener else { return }
        if listener.machine.state == .idle { if !isBusy { listener.emit(listener.machine.startToggled()) } } else { stopRecording() }
    }
    func stopRecording() { guard let listener else { return }; listener.emit(listener.machine.finish()) }
    func cancelRecording() {
        if cloudRequest.isActive { cancelCloudRequest(); return }
        guard let listener else { return }; listener.cancelByUser()
    }
    /// A canceled recording kept for Undo: 16 kHz mono samples in memory, never on disk,
    /// plus what its start captured, so Undo processes it as if it had finished normally.
    private struct Canceled {
        let samples: [Float]
        let destination: FocusSnapshot?, mode: WritingMode?, context: ScreenContext?, screen: NSScreen?
        let transcriber: TranscriptionProvider?, cleaner: CleanupProvider?
        let languages: DictationLanguages
        let insert: Bool, leadingSpace: Bool, modelDirectory: String, started: Date
    }
    private let undo = UndoWindow<Canceled>()
    /// Only a cancel the user asked for (Escape, the menu) can be undone; a lone tap records nothing.
    private func holdForUndo(_ url: URL) {
        guard listener?.cancelIsExplicit == true, let samples = Self.samples16k(url), !samples.isEmpty else { return }
        undo.hold(Canceled(samples: samples, destination: destination, mode: sessionMode, context: sessionContext, screen: destinationScreen,
                           transcriber: sessionTranscriber, cleaner: processing?.cloudCleaner, languages: sessionLanguages, insert: insertThisSession,
                           leadingSpace: leadingSpaceThisSession, modelDirectory: activeModelDirectory, started: listeningStarted))
    }
    /// The recording as 16 kHz mono floats, the helper's input rate (a five-minute recording is about 19 MB).
    static func samples16k(_ url: URL) -> [Float]? {
        guard let file = try? AVAudioFile(forReading: url), file.length > 0,
              let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(AudioFormat.sampleRate), channels: 1, interleaved: false),
              let input = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
              (try? file.read(into: input)) != nil, let converter = AVAudioConverter(from: file.processingFormat, to: target) else { return nil }
        // One call with a buffer for the whole clip returned only part of it; drain in blocks.
        var samples: [Float] = [], fed = false
        while let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: 16_384) {
            let status = converter.convert(to: output, error: nil) { _, state in
                if fed { state.pointee = .endOfStream; return nil }
                fed = true; state.pointee = .haveData; return input
            }
            guard status != .error, let data = output.floatChannelData else { return nil }
            samples.append(contentsOf: UnsafeBufferPointer(start: data[0], count: Int(output.frameLength)))
            if status != .haveData { return samples }
        }
        return nil
    }
    func undoCancel() {
        guard let listener, listener.machine.state == .idle, sessionAvailable, !isBusy, let item = undo.take() else { bubble.hide(); return }
        _ = listener.runAsProcessing { [weak self] done in
            guard let self else { done(); return }
            self.destination = item.destination; self.sessionMode = item.mode; self.sessionContext = item.context
            self.destinationScreen = item.screen; self.sessionTranscriber = item.transcriber; self.processing?.cloudCleaner = item.cleaner; self.processing?.engineCleaner = self.engineProvider()
            self.insertThisSession = item.insert; self.leadingSpaceThisSession = item.leadingSpace
            self.sessionLanguages = item.languages
            self.activeModelDirectory = item.modelDirectory; self.listeningStarted = item.started
            self.generation = UUID(); self.recoveryEntryID = nil
            self.transcribe(nil, held: item.samples, done: done)
        }
    }
    private enum Cue: String { case start = "Tink", stop = "Pop" }
    /// Measured October 15, 2026: both cues mixed at full level into a recording added no words,
    /// and a recording of only the cues transcribed as empty, so the stop cue can play at release.
    private func cue(_ cue: Cue) { if recordingSounds && !preview { NSSound(named: cue.rawValue)?.play() } }
    /// Escape or Cancel during a cloud request cancels its Task (and HTTP request); nothing from
    /// this session is inserted. Audio is deleted, as for a canceled recording; a transcript that
    /// was already saved stays in History.
    func cancelCloudRequest() {
        guard cloudRequest.isActive, let done = cloudDone else { return }
        generation = UUID(); cloudRequest.cancel(); cloudDone = nil
        let stage = cloudStage; cloudStage = nil
        if stage == .transcription, let audio = session.audio { try? FileManager.default.removeItem(at: audio) }
        destination = nil
        let editing = voiceEdit != nil; voiceEdit = nil
        setPhase(.ready, editing && stage != .transcription ? "Edit canceled. Your text was not changed." : stage == .transcription ? CloudTranscription.message(.cancelled) ?? "Canceled." : "Canceled. The raw transcript is saved in History and was not inserted.")
        bubble.hide(); historySaved(); done()
    }
    /// A dictation that could not be pasted, shown in the bubble with Copy. Any other status replaces it.
    /// `title` names why: a failed paste, or a paste Murmur did not attempt.
    struct PasteFailure: Equatable { let entryID: String?; let text: String; var title = "Couldn’t paste here" }
    @Published private(set) var pasteFailure: PasteFailure?
    /// Voice edit's processing bubble shows where the text goes; other processing bubbles stay as dots.
    private(set) var processingLabel: String?
    /// `quick`: a status that needs no action ("No speech") goes after 2 s instead of 8.
    func setPhase(_ phase: Phase, _ message: String, announcement: String? = nil, pasteFailure: PasteFailure? = nil, quick: Bool = false, showsMessage: Bool = false) {
        processingLabel = showsMessage && phase == .processing ? message : nil
        self.phase = phase; self.message = message; self.pasteFailure = pasteFailure
        if phase == .listening || phase == .processing { clearModeRecovery() }
        // Every finished status leaves on its own unless replaced: text stays in History and retry audio
        // stays on disk. "No speech" needs no action (2 s); errors get longer to read (8 s); a text to
        // copy 30 s; the rest 5 s.
        let shown = UUID(); statusShown = shown
        let dismissAfter: TimeInterval? = phase == .listening || phase == .processing || !hasStatusToShow ? nil :
            pasteFailure != nil ? 30 : phase != .error ? 5 : quick ? 2 : 8
        if let dismissAfter {
            DispatchQueue.main.asyncAfter(deadline: .now() + dismissAfter) { [weak self] in
                guard let self, self.statusShown == shown else { return }
                self.dismissStatus()
            }
        }
        // Errors raised while processing leave that session's audio retained for retry.
        // Audio already deleted after a usable transcript (e.g. a held cleanup) is not offered.
        recoveryAudio = phase == .error && sessionAvailable && session.state == .processing ? session.audio.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil } : nil
        refreshRecoveryAudio()
        updateMenu?()
        bubble.show(phase: phase, message: message, screen: destinationScreen)
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [.announcement: announcement ?? message, .priority: NSAccessibilityPriorityLevel.medium.rawValue])
    }
    /// This dictation's recording length, kept with its History entry for Insights.
    private var sessionSeconds: Double?
    nonisolated private static func audioSeconds(_ url: URL) -> Double? {
        guard let file = try? AVAudioFile(forReading: url), file.fileFormat.sampleRate > 0 else { return nil }
        return Double(file.length) / file.fileFormat.sampleRate
    }
    /// `held`: an undone cancel's samples, in memory only. There is then no file: no chunks,
    /// no one-shot fallback and no retry audio.
    private func transcribe(_ audio: URL?, held: [Float]? = nil, done: @escaping () -> Void) {
        sessionSeconds = held.map { Double($0.count) / Double(AudioFormat.sampleRate) } ?? audio.flatMap(Self.audioSeconds)
        transcribeStartedAt = ProcessInfo.processInfo.systemUptime
        if let provider = sessionTranscriber { transcribeInCloud(audio, held: held, with: provider, done: done); return }
        setPhase(.processing, "Transcribing on this Mac…")
        let token = generation; let models = URL(fileURLWithPath: activeModelDirectory)
        guard let runtime = helperRuntime, let store = records else { setPhase(.error, "The model runtime is unavailable. Audio is saved for retry."); done(); return }
        let timestamp = listeningStarted
        let began = ProcessInfo.processInfo.systemUptime
        runtime.launch(models: models, model: sessionLanguages.localModel, qos: .userInitiated) { [weak self] launch in
            let helper = runtime.helper
            defer { timingLog.notice("model-verify took=\(ProcessInfo.processInfo.systemUptime - began, format: .fixed(precision: 3))") }
            guard let launch
            else { DispatchQueue.main.async { guard let self, self.generation == token else { return }; self.setPhase(.error, "Model verification failed. Choose the pinned model folder. \(audio == nil ? Self.heldDiscarded : "Audio is saved for retry.")"); done() }; return }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.generation == token else { return }
                if let audio { self.runTranscription(helper, launch: launch, audio: audio, store: store, timestamp: timestamp, token: token, done: done); return }
                self.warm.transcribe(samples: held ?? [], helper: helper, launch: launch) { [weak self] line in
                    self?.handleTranscript(line.map { .success($0) } ?? .failure(CaptureError("Transcription failed")), audio: nil, store: store, timestamp: timestamp, token: token, done: done)
                }
            }
        }
    }
    private struct NoSpeech: Error {}
    /// Warm helper first; if it cannot answer, the one-shot helper transcribes the same audio.
    private func runTranscription(_ helper: URL, launch: HelperLaunch, audio: URL, store: HistoryRouter<HistoryEntry>, timestamp: Date, token: UUID, done: @escaping () -> Void) {
        // Chunks transcribed during recording; if any failed, the full WAV goes through below.
        if let job = chunkJobs.removeValue(forKey: audio) {
            if job.isEnded {
                let began = ProcessInfo.processInfo.systemUptime
                job.result { [weak self] text in
                    timingLog.notice("chunks took=\(ProcessInfo.processInfo.systemUptime - began, format: .fixed(precision: 3)) answered=\(text != nil, privacy: .public)")
                    if let text, let line = try? JSONSerialization.data(withJSONObject: ["text": text]) {
                        self?.handleTranscript(.success(line), audio: audio, store: store, timestamp: timestamp, token: token, done: done); return
                    }
                    DispatchQueue.main.async { [weak self] in
                        guard let self, self.generation == token else { return }
                        self.runTranscription(helper, launch: launch, audio: audio, store: store, timestamp: timestamp, token: token, done: done)
                    }
                }
                return
            }
            job.discard()
        }
        let began = ProcessInfo.processInfo.systemUptime
        warm.transcribe(audio, helper: helper, launch: launch) { [weak self] line in
            timingLog.notice("warm-runner took=\(ProcessInfo.processInfo.systemUptime - began, format: .fixed(precision: 3)) answered=\(line != nil, privacy: .public)")
            if let line { self?.handleTranscript(.success(line), audio: audio, store: store, timestamp: timestamp, token: token, done: done); return }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.generation == token else { return }
                self.launchRunner(helper, launch: launch, audio: audio, store: store, timestamp: timestamp, token: token, done: done)
            }
        }
    }
    private func launchRunner(_ helper: URL, launch: HelperLaunch, audio: URL, store: HistoryRouter<HistoryEntry>, timestamp: Date, token: UUID, done: @escaping () -> Void) {
        let pipe = Pipe(), output = pipe.fileHandleForReading
        let task = Process(); task.executableURL = helper; task.arguments = [launch.models.path, audio.path, "--single"] + launch.arguments
        task.standardOutput = pipe; task.standardError = FileHandle.nullDevice
        do { try task.run(); runner = task; try? pipe.fileHandleForWriting.close() } catch { try? output.close(); try? pipe.fileHandleForWriting.close(); setPhase(.error, "Transcription could not start. Audio is saved for retry."); done(); return }
        let timeout = DispatchWorkItem { if task.isRunning { task.terminate(); DispatchQueue.global().asyncAfter(deadline: .now() + 2) { if task.isRunning { kill(task.processIdentifier, SIGKILL) } } } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 300, execute: timeout)
        let launched = ProcessInfo.processInfo.systemUptime
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            defer { timeout.cancel(); try? output.close() }
            let result = Result<Data, Error> {
                let data: Data
                do { data = try readHelperOutput(output) }
                catch {
                    if task.isRunning { kill(task.processIdentifier, SIGKILL) }
                    task.waitUntilExit()
                    throw error
                }
                task.waitUntilExit()
                timingLog.notice("runner took=\(ProcessInfo.processInfo.systemUptime - launched, format: .fixed(precision: 3)) status=\(task.terminationStatus)")
                guard task.terminationStatus == 0 else { throw CaptureError("Transcription failed") }
                return data
            }
            self?.handleTranscript(result, audio: audio, store: store, timestamp: timestamp, token: token, done: done)
        }
    }
    nonisolated private static let heldDiscarded = "The canceled recording was discarded."
    /// Shared by the warm and one-shot helpers: parse the last JSON line, save, then deliver. Runs off main.
    nonisolated private func handleTranscript(_ result: Result<Data, Error>, audio: URL?, store: HistoryRouter<HistoryEntry>, timestamp: Date, token: UUID, done: @escaping () -> Void) {
        do {
            let data = try result.get()
            guard let last = String(data: data, encoding: .utf8)?.split(separator: "\n").last,
                  let text = helperAnswer(Data(last.utf8))?["text"] as? String else { throw CaptureError("No usable transcript") }
            // The model ran and answered, but heard nothing; audio stays until the retry window ends.
            guard text.contains(where: { $0.isLetter || $0.isNumber }) else { throw NoSpeech() }
            var savedEntry = HistoryEntry(id: UUID().uuidString, recordedAt: timestamp, rawText: text, finalText: text, insertionStatus: InsertionStatus.savedNotInserted.rawValue)
            if let audio { savedEntry.recordingFile = recordings.keep(audio, entryID: savedEntry.id) }
            try store.save(savedEntry)
            if let audio { try? FileManager.default.removeItem(at: audio) }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.generation == token else { return }
                self.runner = nil
                self.deliver(savedEntry, store: store, token: token, done: done)
            }
        } catch {
            let message = error is NoSpeech ? "No speech was detected. Check your microphone and try again."
                : audio == nil ? "Transcription failed. \(Self.heldDiscarded)" : "Transcription failed. Audio is saved for retry for up to \(RetryWindow.text)."
            let quick = error is NoSpeech
            DispatchQueue.main.async { [weak self] in guard let self, self.generation == token else { return }; self.runner = nil; self.setPhase(.error, message, quick: quick); done() }
        }
    }
    /// Cloud transcription: one provider call in a cancellable Task, never the local model.
    /// The result then follows the local path: history save, processing, insertion.
    private func transcribeInCloud(_ audio: URL?, held: [Float]?, with provider: TranscriptionProvider, done: @escaping () -> Void) {
        guard let store = records else { setPhase(.error, "Private storage is unavailable. \(audio == nil ? Self.heldDiscarded : "Audio is saved for retry.")"); done(); return }
        let token = generation; let timestamp = listeningStarted
        let began = ProcessInfo.processInfo.systemUptime
        cloudStage = .transcription; cloudDone = done
        let heldWAV = held.map { WAVAudio.encodePCM16($0, sampleRate: AudioFormat.sampleRate) }
        let languages = sessionLanguages
        cloudRequest.start({
            if let heldWAV { return await CloudTranscription.runDetailed(provider: provider, audioData: heldWAV, languages: languages) }
            guard let audio else { return await CloudTranscription.runDetailed(provider: provider, audioData: Data(), languages: languages) }
            return await CloudTranscription.runDetailed(provider: provider, audioFile: audio, languages: languages)
        }) { [weak self] (result: (outcome: CloudTranscriptionOutcome, detail: CloudTranscriptionDetail)) in
            guard let self, self.generation == token else { return }
            let outcome = result.outcome
            self.cloudStage = nil; self.cloudDone = nil
            timingLog.notice("cloud-transcribe took=\(ProcessInfo.processInfo.systemUptime - began, format: .fixed(precision: 3)) outcome=\(Self.transcriptionOutcomeName(outcome), privacy: .public)")
            let text: String
            switch outcome {
            case .transcript(let value): text = value
            case .noSpeech, .failed, .cancelled:
                if case .noSpeech = outcome { timingLog.notice("\(result.detail.noSpeechLogLine, privacy: .public)") }
                // Audio stays for retry (until the retry window ends) unless the provider error discards it.
                if case .failed(_, false) = outcome, let audio { try? FileManager.default.removeItem(at: audio) }
                var message = CloudTranscription.message(outcome) ?? CloudTranscription.unknownFailure
                if audio == nil, case .failed(let reason, _) = outcome { message = reason + " " + Self.heldDiscarded }
                if case .noSpeech = outcome { self.setPhase(.error, message, quick: true) } else { self.setPhase(.error, message) }
                done(); return
            }
            var entry = HistoryEntry(id: UUID().uuidString, recordedAt: timestamp, rawText: text, finalText: text, insertionStatus: InsertionStatus.savedNotInserted.rawValue)
            entry.transcriptionProvider = provider.id.rawValue; entry.transcriptionModel = provider.modelID
            entry.transcription = Self.transcriptionDetail(result.detail)
            if let audio { entry.recordingFile = self.recordings.keep(audio, entryID: entry.id) }
            do { try store.save(entry) } catch { self.setPhase(.error, "Could not save the transcript. \(audio == nil ? Self.heldDiscarded : "Audio is saved for retry for up to \(RetryWindow.text).")"); done(); return }
            if let audio { try? FileManager.default.removeItem(at: audio) }
            self.deliver(entry, store: store, token: token, done: done)
        }
        setPhase(.processing, "Transcribing with \(provider.id.displayName)…")
    }
    /// Dictionary, then cleanup, for a transcript already saved to History. Local rules run here;
    /// any other cleanup route makes one provider call in a cancellable Task (no local fallback).
    private func deliver(_ savedEntry: HistoryEntry, store: HistoryRouter<HistoryEntry>, token: UUID, done: @escaping () -> Void) {
        if let edit = voiceEdit, edit.generation == token { runVoiceEdit(edit, instruction: savedEntry.rawText, entry: savedEntry, store: store, done: done); return }
        cleanupStartedAt = ProcessInfo.processInfo.systemUptime
        // Saved with the next write of this entry; no extra disk write before insertion.
        var savedEntry = savedEntry; savedEntry.seconds = sessionSeconds
        savedEntry.timing = StageTiming(transcription: cleanupStartedAt - transcribeStartedAt)
        // finishedAt is 0 until a key release is seen (a History retry, Undo): no release stage then.
        if finishedAt > 0 {
            let waited = max(0, transcribeStartedAt - finishedAt)
            savedEntry.timing?.capture = waited
            timingLog.notice("release-to-transcribe took=\(waited, format: .fixed(precision: 3))")
        }
        recoveryEntryID = savedEntry.id
        if sessionLanguages != .english {
            sessionContext = nil
            savedEntry.transcriptionProvider = sessionTranscriber?.id.rawValue ?? "local"
            savedEntry.transcriptionModel = sessionTranscriber?.modelID ?? sessionLanguages.localModel.facts.repository
            let text = processing?.dictionary.apply(to: savedEntry.rawText, manualVariantsOnly: true) ?? savedEntry.rawText
            complete(ProcessingResult(outcome: .insert(text), cleanupPath: .off), entry: savedEntry, store: store, done: done)
            return
        }
        // A trigger word beats the app or website matched at start; it is cut from the text, not from History's raw transcript.
        let trigger = WritingModes.trigger(writingModes, in: savedEntry.rawText)
        let text = trigger?.text ?? savedEntry.rawText
        let style = processing.map { DictationStyle(mode: trigger?.mode ?? sessionMode, settings: $0.settings, context: sessionContext) }
        sessionContext = nil
        // Provenance: identifiers and settings only, never text, context or keys.
        let mode = trigger?.mode ?? sessionMode, info = Bundle.main.infoDictionary
        savedEntry.cleanupOptions = style?.options; savedEntry.writingModeID = mode?.id.uuidString; savedEntry.writingModeName = mode?.name
        savedEntry.transcriptionProvider = sessionTranscriber?.id.rawValue ?? "local"
        savedEntry.transcriptionModel = sessionTranscriber == nil ? ModelFacts.batch.repository : sessionTranscriber?.modelID
        savedEntry.appVersion = (info?["CFBundleShortVersionString"] as? String).map { v in (info?["CFBundleVersion"] as? String).map { "\(v) (\($0))" } ?? v }
        // Counts and the app only, never the screen text; `complete` keeps them only when the result says the context was sent.
        var detail = CleanupDetail(contextUsed: false)
        if let context = style?.context {
            detail.contextApp = destination?.bundleID
            detail.contextChars = ["selected": context.selected.count, "clipboard": context.clipboard.count, "window": context.window.count]
        }
        savedEntry.cleanup = detail
        guard let processing, processing.settings.route != .local || style?.usesEngine == true else {
            complete(processing?.process(text, style: style) ?? ProcessingResult(outcome: .heldForRecovery(raw: text, reason: .cleanerFailed)), entry: savedEntry, store: store, done: done); return
        }
        cloudStage = .cleanup; cloudDone = done
        cloudRequest.start({ await processing.processAsync(text, style: style) }) { [weak self] outcome in
            guard let self, self.generation == token else { return }
            self.cloudStage = nil; self.cloudDone = nil
            self.complete(outcome, entry: savedEntry, store: store, done: done)
        }
        setPhase(.processing, "Cleaning up with \(processing.cloudCleaner?.id.displayName ?? "the cleanup provider")…")
    }
    private static func transcriptionDetail(_ detail: CloudTranscriptionDetail) -> TranscriptionDetail {
        TranscriptionDetail(
            segments: detail.segments?.map { SegmentDetail(text: $0.text, start: $0.start, end: $0.end, avgLogprob: $0.avgLogprob, noSpeechProb: $0.noSpeechProb,
                                                           compressionRatio: $0.compressionRatio, loudWindows: $0.loudWindows, dropped: $0.dropped) },
            clipSeconds: detail.clipSeconds, peakP50: detail.peakP50.map(Double.init), peakP90: detail.peakP90.map(Double.init),
            silencePeak: detail.silencePeak.map(Double.init), silenceThreshold: detail.silencePeak == nil ? nil : Double(AudioFormat.silencePeak),
            precheck: detail.precheck.rawValue)
    }
    /// Content-free outcome name for the timing log.
    private static func transcriptionOutcomeName(_ outcome: CloudTranscriptionOutcome) -> String {
        switch outcome {
        case .transcript: return "transcript"
        case .noSpeech: return "noSpeech"
        case .failed: return "failed"
        case .cancelled: return "cancelled"
        }
    }
    private func complete(_ result: ProcessingResult, entry savedEntry: HistoryEntry, store: HistoryRouter<HistoryEntry>, done: @escaping () -> Void) {
        let outcome = result.outcome
        var entry = savedEntry
        // Consumed here for every outcome, so a later retry never measures from an old key release.
        let released = finishedAt; finishedAt = 0
        entry.cleanupOutcome = result.cleanupPath
        entry.cues = result.cues > 0 ? result.cues : nil
        entry.timing?.cleanup = ProcessInfo.processInfo.systemUptime - cleanupStartedAt
        if processing != nil {
            var detail = entry.cleanup ?? CleanupDetail()
            detail.contextUsed = result.contextUsed
            if !result.contextUsed { detail.contextApp = nil; detail.contextChars = nil }
            detail.modelOutput = result.modelOutput; detail.guardVerdict = result.guardVerdict; detail.promptHash = result.promptHash
            detail.dictionaryTerms = result.terms.isEmpty ? nil : result.terms; detail.input = result.cleanupInput
            detail.providerError = result.providerError?.kind
            entry.cleanup = detail
        }
        timingLog.notice("cleanup took=\(ProcessInfo.processInfo.systemUptime - self.cleanupStartedAt, format: .fixed(precision: 3)) path=\(result.cleanupPath?.rawValue ?? "none", privacy: .public)")
        // A writing-mode dictation ran on the AI engine, whatever the cleanup route is.
        if sessionLanguages == .english {
            entry.cleanupProvider = result.engine?.rawValue ?? (processing?.settings.route == .local ? "local-rules" : processing?.cloudCleaner?.id.rawValue)
            entry.cleanupModel = result.engine != nil ? processing?.engineCleaner?.modelID : processing?.cloudCleaner?.modelID
        }
        entry.contextUsed = result.contextUsed
        let text = entry.rawText
        let saved = HistoryText.saved(persists: !store.sessionOnlyIDs.contains(entry.id))
        recordCleanup(outcome: result.cleanupPath, fallback: result.fallback, error: result.providerError)
        guard case .insert(let processed) = outcome else {
            let reason = result.providerError.map { " " + $0.userMessage } ?? ""
            // Content-free: the guard's category, never the text.
            if case .heldForRecovery(_, let why) = outcome {
                timingLog.notice("cleanup-held reason=\(why.rawValue, privacy: .public) provider-error=\(result.providerError?.kind ?? "none", privacy: .public)")
            }
            entry = entry.with(finalText: "", status: .heldCleanup)
            do { try store.save(entry); setPhase(.error, "Cleanup was held for review.\(reason) The raw transcript is in History. Press ⌃⌘C to copy it.") }
            catch { setPhase(.error, "Cleanup was held for review.\(reason) The raw transcript is in History; its status could not be saved.") }
            endDictation(done); return
        }
        // Content-free: why the full cleanup was not used (the punctuation transfer or local rules was).
        let fallback = result.fallback?.rawValue, rejected = result.rejectedOutput
        if let why = result.fallback {
            timingLog.notice("cleanup-fallback entry=\(savedEntry.id, privacy: .public) reason=\(why.rawValue, privacy: .public) provider-error=\(result.providerError?.kind ?? "none", privacy: .public)")
        }
        // Only filler words were heard: keep the raw transcript, insert nothing, show no error.
        if processed.isEmpty {
            entry = entry.with(finalText: "", status: .savedNotInserted, fallback: fallback, rejected: rejected)
            try? store.save(entry)
            setPhase(.ready, "Only filler words were heard, so nothing was inserted.")
            endDictation(done); return
        }
        if processed != text || fallback != nil {
            entry = entry.with(finalText: processed, fallback: fallback, rejected: rejected)
            do { try store.save(entry) } catch { setPhase(.error, "Could not save the processed transcript. The raw transcript is in History."); endDictation(done); return }
        }
        // First-run test dictation: Murmur never types into itself, so show the result on the setup screen.
        if setupShowing && NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() {
            // ⌃⌘V still works if this flag was left on with no setup window visible.
            setupTestTranscript = processed; lastDictationText = processed; setPhase(.ready, "Test dictation worked."); bubble.hide()
            endDictation(done); return
        }
        lastDictationText = processed
        professionalHint.consider(entryID: entry.id, raw: text, professionalActive: cleanupProfessional && cleanupChoice.runsModelLevels)
        if !preview { tips.recordDictation(final: processed, seconds: entry.seconds) }
        if insertThisSession {
            // Murmur never pastes into itself, but a text view in its own window (the Report an Issue box) takes the text directly.
            if NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid(), let editor = NSApp.keyWindow?.firstResponder as? NSTextView, editor.isEditable, !editor.isFieldEditor {
                editor.insertText(processed, replacementRange: editor.selectedRange())
                entry = entry.with(finalText: processed, status: .verified, fallback: fallback, rejected: rejected)
                try? store.save(entry)
                lastDictationText = processed
                showInsertionResult(InsertionResult(status: .verified, detail: "inserted into a text view in Murmur", snapshot: nil), entryID: entry.id, text: processed)
                endDictation(done); return
            }
            watchTick(ending: true)
            // Insert where the cursor is now, even if focus moved while recording or processing.
            let target = (try? captureFocus()).flatMap { $0.pid == getpid() ? nil : $0 }
            // Persist uncertainty before the one permitted delivery attempt.
            let insertionText = textWithLeadingSpace(processed, value: target?.value, selection: target?.selection, enabled: leadingSpaceThisSession)
            entry = entry.with(finalText: insertionText, fallback: fallback, rejected: rejected)
            let delivery = Delivery.deliver(&entry, status: \.insertionStatus, pending: InsertionStatus.insertionPending.rawValue, save: { try store.save($0) }) { entry in
                // The text appearing is the signal; insertion's verification waits block the main
                // thread, so take the indicator down first instead of freezing it.
                bubble.hide()
                let inserting = ProcessInfo.processInfo.systemUptime
                // A readable text field is pasted into and read back; anything else (a web editor, a
                // custom text view, no accessible focus) gets a plain paste.
                let readable = target.map { insertionTextRole($0.role) && $0.value != nil && $0.selection != nil } ?? false
                let cleanupTook = entry.timing?.cleanup ?? 0
                entry.timing?.handoff = inserting - (cleanupStartedAt + cleanupTook)
                let insertion = readable ? insertText(entry.finalText, into: target!) : pasteIntoFrontmostApp(entry.finalText)
                timingLog.notice("insert app=\(target?.bundleID ?? NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "none", privacy: .public) role=\(target?.role ?? "none", privacy: .public) status=\(insertion.status.rawValue, privacy: .public) took=\(ProcessInfo.processInfo.systemUptime - inserting, format: .fixed(precision: 3)) detail=\(insertion.detail, privacy: .public)")
                let inserted = ProcessInfo.processInfo.systemUptime
                // The text is visible when ⌘V is posted or the field first reads back matching; the
                // read-back's remaining wait and the clipboard restore come after and are not the user's wait.
                let visible = insertion.visibleAt ?? inserted
                entry.timing?.insertion = visible - inserting
                entry.timing?.insertionDone = inserted - inserting
                timingLog.notice("text-visible took=\(visible - inserting, format: .fixed(precision: 3))")
                // finishedAt is 0 until a key release is seen (a History retry, Undo); the line would then be uptime.
                if released > 0 {
                    entry.timing?.total = visible - released
                    timingLog.notice("dictation took=\(visible - released, format: .fixed(precision: 3))")
                }
                if let app = target?.bundleID, !app.isEmpty { recentApps = [app] + recentApps.filter { $0 != app }.prefix(9) }
                lastDictationText = entry.finalText
                if insertion.status != .failed { entry.app = target?.bundleID ?? NSWorkspace.shared.frontmostApplication?.bundleIdentifier }
                return (InsertionStatus(insertion.status).rawValue, insertion)
            }
            switch delivery {
            case .delivered(let insertion):
                // An expansion is the user's stored text, not a dictation to learn spellings from.
                if sessionLanguages == .english, entry.cues == nil { watchCorrections(insertion, text: entry.finalText, entryID: entry.id, at: target?.selection?.location, pid: target?.pid ?? NSWorkspace.shared.frontmostApplication?.processIdentifier) }
                showInsertionResult(insertion, entryID: entry.id, text: entry.finalText)
                if insertion.status != .failed { offerModeRecovery(entry: entry, inserted: entry.finalText, insertion: insertion, target: target) }
                // A provider failure is not silent: a bad key or quota would otherwise degrade every dictation.
                if insertion.status != .failed, let error = result.providerError {
                    setPhase(.ready, "Inserted with local cleanup. \(error.userMessage)")
                }
            case .statusSaveFailed:
                // After an attempt the paste may have landed, so no card offers the text again.
                setPhase(.error, Delivery.statusSaveFailureMessage(saved: saved))
            case .pendingSaveFailed:
                showNotPasted(.unsaved, saved: saved, entryID: entry.id, text: processed)
            }
        } else {
            try? store.save(entry) // Records the recording length, which only later saves would carry.
            showNotPasted(.insertionOff, saved: saved, entryID: entry.id, text: processed)
        }
        endDictation(done)
    }
    /// The end of every path through `complete`: nothing is pending for this dictation's field, History is current, the press is released.
    private func endDictation(_ done: () -> Void) { destination = nil; historySaved(); done() }
    // Keep the warning visible until a later dictation confirms recovery or uses an intentional local route.
    @Published private(set) var cleanupWarning = false
    func recordCleanup(outcome: CleanupPath?, fallback: CleanupFailureReason?, error: ProviderError? = nil) {
        let previous = cleanupWarning
        if outcome == .modelAccepted || outcome == .localRoute { cleanupWarning = false }
        else if fallback == .cleanerFailed || error != nil { cleanupWarning = true }
        if previous != cleanupWarning { updateMenu?() }
    }


    // Explicit developer check of the production pipeline, using a synthetic file
    // and an exact disposable destination. Never enabled during ordinary launch.
    func checkSavedAudio(_ audio: URL, expectedApp: String, completion: @escaping () -> Void) {
        // Canvas editors (Google Docs) expose no focused field; the frontmost app must still match.
        let focused = try? captureFocus()
        guard sessionAvailable, modelFilesAvailable(), let front = NSWorkspace.shared.frontmostApplication,
              front.bundleIdentifier == expectedApp, front.processIdentifier != getpid(), focused?.secure != true,
              focused == nil || focused?.pid == front.processIdentifier else {
            fputs("app-check=refused destination-or-model-unavailable\n", stderr); completion(); return
        }
        guard !isBusy, let retry else { completion(); return }
        let ownedAudio: URL
        do { ownedAudio = try copyProbeAudio(audio, into: retry) } catch {
            fputs("app-check=refused audio-copy-failed\n", stderr); completion(); return
        }
        // The probe is local only: it never sends audio or text to a provider.
        if let processing {
            var local = processing.settings; local.route = .local
            do { try processing.update(local, persist: false) }
            catch { fputs("app-check=refused local-settings-failed\n", stderr); completion(); return }
        }
        sessionLanguages = .english
        sessionTranscriber = nil; processing?.cloudCleaner = nil; processing?.engineCleaner = nil
        destination = focused; insertThisSession = true; leadingSpaceThisSession = intelligentLeadingSpace
        activeModelDirectory = modelDirectory; listeningStarted = Date(); generation = UUID()
        destinationScreen = screenForDestination(focused); recoveryEntryID = nil
        transcribe(ownedAudio) { [weak self] in
            print("app-check=complete state=\(self?.phase.rawValue ?? "unknown")")
            fflush(stdout)
            // Stay up through the correction window so the probe can exercise learning.
            @MainActor func waitForWatch() {
                guard let self, self.watch != nil else { print("app-check=watch-ended"); fflush(stdout); completion(); return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { MainActor.assumeIsolated { waitForWatch() } }
            }
            waitForWatch()
        }
    }
    func refreshRecoveryAudio() {
        guard let retry else { hasRecoveryAudio = false; return }
        hasRecoveryAudio = ((try? FileManager.default.contentsOfDirectory(at: retry.directory, includingPropertiesForKeys: nil)) ?? []).contains { $0.pathExtension == "wav" }
    }
    func useFaithfulTranscription() { cleanupFillers = false; cleanupCorrections = false; cleanupLists = false; cleanupPunctuation = false; cleanupSpoken = false }
    /// Starts (or restarts, when what it loads changed) the warm helper in the background, so the
    /// next dictation does not wait for a model load.
    func prewarm() {
        let model = requiredSpeechModel
        guard !preview, let runtime = helperRuntime, let path = speechModelPath(for: model),
              model == .parakeetEnglish || requiredSpeechSlot.row.isReady else { return }
        let warm = warm
        runtime.launch(models: URL(fileURLWithPath: path), model: model, qos: .utility) { launch in
            guard let launch else { return }
            warm.prepare(helper: runtime.helper, launch: launch)
        }
    }
    /// The helper and what launching it needs, or nil when this build has no helper or model pins.
    var helperRuntime: HelperRuntime? {
        guard let helper = BundledModels.helper, let manifest = BundledModels.pins else { return nil }
        return HelperRuntime(helper: helper, manifest: manifest, vocabulary: vocabularyFile, dictionaryModel: dictionaryModelDirectory)
    }
    func dismissStatus() {
        guard !isBusy, modeRecovery == nil else { return }
        if sessionAvailable {
            setPhase(.ready, idleMessage)
        } else {
            phase = .error; message = "Private storage is unavailable. Murmur cannot start."
            updateMenu?()
        }
        bubble.hide()
    }
    func showSettings() { openSettings?() }
    /// Selects this error's retained audio when known; otherwise opens the retry folder holding older retained audio.
    func revealRecoveryAudio() {
        if let recoveryAudio, FileManager.default.fileExists(atPath: recoveryAudio.path) { NSWorkspace.shared.activateFileViewerSelecting([recoveryAudio]) }
        else if hasRecoveryAudio, let retry { NSWorkspace.shared.open(retry.directory) }
    }




    // MARK: Update checks

    func checkForUpdates() {
        guard !preview else { return }
        updateStatus = sparkleUpdater.checkForUpdates()
    }

    func shutdown() {
        undo.discard(); stopLiveWords(); liveWordsSlot.downloader?.cancel()
        generation = UUID(); cloudRequest.cancel(); monitor?.invalidate(); expiry?.invalidate(); removeShortcut()
        watchTick(ending: true)
        flushClipboardRestore()
        modelSlot.downloader?.cancel()  // Verified and partial files stay staged; Retry resumes them.
        tdtSlot.downloader?.cancel(); whisperSlot.downloader?.cancel()
        dictionarySlot.downloader?.cancel(); localSlot.downloader?.cancel()
        Task { await LocalCleanupRunner.shared.shutdown() }
        if sessionAvailable { session.shutdown() }
        meetingRecorder.shutdown()
        if let runner, runner.isRunning {
            runner.terminate()
            // Closing the app cancels this one local helper; reap it before process exit.
            if runner.isRunning { kill(runner.processIdentifier, SIGKILL) }
            runner.waitUntilExit()
        }
        warm.stopNow()
        bubble.hide()
        if let previewDirectory { try? FileManager.default.removeItem(at: previewDirectory) }
    }
}

// Probe inputs belong to the caller; only the private copy follows retry cleanup.
func copyProbeAudio(_ audio: URL, into retry: RetryAudio) throws -> URL {
    let ownedAudio = try retry.create()
    do {
        let input = try FileHandle(forReadingFrom: audio)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: ownedAudio)
        defer { try? output.close() }
        while let data = try input.read(upToCount: 1024 * 1024), !data.isEmpty { try output.write(contentsOf: data) }
        try output.synchronize()
        return ownedAudio
    } catch { try? retry.delete(ownedAudio); throw error }
}
