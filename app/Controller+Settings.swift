import AppKit

// The controller's persisted user settings. Prefs (Settings.swift) holds each key and default once;
// `persisted` below lists every setting once, and init, applySettings and the settings window's
// apply-on-change all walk it, so a setting is one Prefs entry, one @Published property and one row here.

extension Prefs {
    static let shortcut = Setting("shortcut", ShortcutBinding.defaultID)
    static let modelDirectory = Setting("modelDirectory", ModelFacts.batch.directory(FileManager.default.homeDirectoryForCurrentUser).path)
}

/// One row of `AppController.persisted`: where a setting is held, how it loads and saves, and its current value.
@MainActor struct PersistedSetting {
    let load: (UserDefaults, AppController) -> Void
    let save: (UserDefaults, AppController) -> Void
    let value: (AppController) -> AnyHashable

    /// `savedByProcessing`: ProcessingCoordinator.update persists it (with the ledger sync), so applySettings must not.
    init<V: Hashable>(_ setting: Setting<V>, _ path: ReferenceWritableKeyPath<AppController, V>, savedByProcessing: Bool = false) {
        load = { defaults, controller in controller[keyPath: path] = defaults[setting] }
        save = { defaults, controller in if !savedByProcessing { defaults[setting] = controller[keyPath: path] } }
        value = { $0[keyPath: path] }
    }
    init<V: Hashable & RawRepresentable>(_ setting: Setting<V>, _ path: ReferenceWritableKeyPath<AppController, V>) where V.RawValue == String {
        load = { defaults, controller in controller[keyPath: path] = defaults[setting] }
        save = { defaults, controller in defaults[setting] = controller[keyPath: path] }
        value = { $0[keyPath: path] }
    }
}

extension AppController {
    /// Every setting that is stored in UserDefaults and applies as soon as it changes (README "Settings and recovery").
    /// The model folder is the exception: its own watcher applies it. Routes are `routeSettings`.
    static let persisted: [PersistedSetting] = [
        PersistedSetting(Prefs.shortcut, \.shortcut), PersistedSetting(Prefs.showMenuBar, \.showMenuBar), PersistedSetting(Prefs.showInDock, \.showInDock), PersistedSetting(Prefs.meetingIndicator, \.meetingIndicator),
        PersistedSetting(Prefs.inputDeviceUID, \.inputDeviceUID), PersistedSetting(Prefs.intelligentLeadingSpace, \.intelligentLeadingSpace),
        PersistedSetting(Prefs.automaticInsertion, \.automaticInsertion), PersistedSetting(Prefs.pressToToggle, \.pressToToggle),
        PersistedSetting(Prefs.recordingSounds, \.recordingSounds), PersistedSetting(Prefs.keepRecordings, \.keepRecordings),
        PersistedSetting(Prefs.liveWords, \.liveWords), PersistedSetting(Prefs.liveWordsOnCloud, \.liveWordsOnCloud),
        PersistedSetting(Prefs.retention, \.retention), PersistedSetting(Prefs.historyMode, \.historyMode),
        PersistedSetting(Prefs.updateChecks, \.updateChecksEnabled), PersistedSetting(Prefs.readableLearning, \.learningRequiresReadableField),
        PersistedSetting(Prefs.cleanupFillers, \.cleanupFillers, savedByProcessing: true), PersistedSetting(Prefs.cleanupCorrections, \.cleanupCorrections, savedByProcessing: true),
        PersistedSetting(Prefs.cleanupLists, \.cleanupLists, savedByProcessing: true), PersistedSetting(Prefs.cleanupPunctuation, \.cleanupPunctuation, savedByProcessing: true),
        PersistedSetting(Prefs.cleanupPolish, \.cleanupPolish, savedByProcessing: true), PersistedSetting(Prefs.cleanupProfessional, \.cleanupProfessional, savedByProcessing: true),
        PersistedSetting(Prefs.cleanupSpoken, \.cleanupSpoken, savedByProcessing: true), PersistedSetting(Prefs.screenContext, \.screenContext, savedByProcessing: true),
        PersistedSetting(Prefs.learningEnabled, \.learningEnabled, savedByProcessing: true),
    ]

    func loadSettings(from defaults: UserDefaults) {
        for setting in Self.persisted { setting.load(defaults, self) }
        cleanupPolish = cleanupPolish || cleanupProfessional // As CleanupOptions does: Professional includes Polish.
    }
    func saveSettings(to defaults: UserDefaults) { for setting in Self.persisted { setting.save(defaults, self) } }

    /// Faithful disables the edit toggles; choosing another style restores the standard edits. The one place the style maps to the cleanup flags and route.
    var cleanupStyle: CleanupStyle {
        get {
            guard cleanupFillers || cleanupCorrections || cleanupLists || cleanupPunctuation || cleanupSpoken else { return .faithful }
            return cleanupChoice == .localRules ? .strict : cleanupProfessional ? .professional : cleanupPolish ? .polish : .strict
        }
        set {
            if newValue == .faithful { useFaithfulTranscription(); return }
            if cleanupStyle == .faithful { cleanupFillers = true; cleanupCorrections = true; cleanupLists = true; cleanupPunctuation = true; cleanupSpoken = true }
            cleanupPolish = newValue.needsModel; cleanupProfessional = newValue == .professional
            // Polish and Professional need a model; Strict keeps the stored one for next time.
            if newValue.needsModel, cleanupChoice == .localRules, let first = CleanupChoice.allCases.first(where: { $0 != .localRules }) { cleanupChoice = first }
        }
    }

    /// Changes whenever a setting that applies immediately changes; the settings window calls `settingsChanged` on it.
    var immediateSettings: [AnyHashable] { Self.persisted.map { $0.value(self) } + [installUpdatesAutomatically] }
}
