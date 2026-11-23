import Foundation

// Each persisted user setting's UserDefaults key and default, declared once. Foundation only. The
// controller's own settings (shortcut, model folder) extend `Prefs` in Controller+Settings.swift
// because their defaults come from non-Foundation files.

struct Setting<Value> {
    let key: String
    let fallback: Value
    init(_ key: String, _ fallback: Value) { self.key = key; self.fallback = fallback }
}

extension UserDefaults {
    /// A missing or wrongly typed value reads as the setting's default.
    subscript<V>(_ setting: Setting<V>) -> V {
        get { object(forKey: setting.key) as? V ?? setting.fallback }
        set { set(newValue, forKey: setting.key) }
    }
    /// A missing or unknown raw value reads as the setting's default.
    subscript<V: RawRepresentable>(_ setting: Setting<V>) -> V where V.RawValue == String {
        get { string(forKey: setting.key).flatMap(V.init(rawValue:)) ?? setting.fallback }
        set { set(newValue.rawValue, forKey: setting.key) }
    }
}

/// Where the dictation indicator and its cards sit on a display with a notch; without one, always the bottom.
enum DictationIndicator: String, CaseIterable, Sendable {
    case notch, bottom
}

/// Where the live meeting indicator sits once the meeting panel is minimized.
enum MeetingIndicator: String, CaseIterable, Sendable {
    case notch, menuBar = "menu-bar"
    /// Beside the notch needs a display that has one; without it the menu bar is used.
    func resolved(hasNotch: Bool) -> MeetingIndicator { self == .notch && hasNotch ? .notch : .menuBar }
    /// The menu bar item's text while it is the indicator.
    static func menuBarTitle(elapsed: String) -> String { " " + elapsed }
}

enum Prefs {
    static let cleanupFillers = Setting("cleanupFillers", true)
    static let cleanupCorrections = Setting("cleanupCorrections", true)
    static let cleanupLists = Setting("cleanupLists", true)
    static let cleanupPunctuation = Setting("cleanupPunctuation", true)
    static let cleanupPolish = Setting("cleanupPolish", false)
    static let cleanupProfessional = Setting("cleanupProfessional", false)
    static let cleanupSpoken = Setting("cleanupSpokenCommands", true)
    static let cleanupRoute = Setting("cleanupRoute", CleanupRoute.local)
    static let learningEnabled = Setting("learningEnabled", true)
    static let readableLearning = Setting("learningRequiresReadableField", false)
    /// Screen context for AI cleanup routes: opt-in, off by default. A writing mode can override it.
    static let screenContext = Setting("cleanupScreenContext", false)
    static let historyMode = Setting("historyMode", HistoryMode.on)
    static let updateChecks = Setting("updateChecksEnabled", true)
    static let retention = Setting("retention", HistoryRetention.thirtyDays)
    static let showMenuBar = Setting("showMenuBar", true)
    static let showInDock = Setting("showInDock", true)
    static let inputDeviceUID = Setting("inputDeviceUID", "")
    static let intelligentLeadingSpace = Setting("intelligentLeadingSpace", false)
    static let automaticInsertion = Setting("automaticInsertion", true)
    static let pressToToggle = Setting("pressToToggle", false)
    static let recordingSounds = Setting("recordingSounds", true)
    static let keepRecordings = Setting("keepRecordings", false)
    static let liveWords = Setting("liveWords", false)
    static let liveWordsOnCloud = Setting("liveWordsOnCloud", false)
    // Window and panel state, read by the views. `settingsSection` holds the raw value of `SettingsPane`.
    static let settingsSection = Setting("settingsSection", 0)
    static let sidebarCollapsed = Setting("sidebarCollapsed", false)
    static let showDetailedSettingsInfo = Setting("showDetailedSettingsInfo", false)
    static let meetingPanelCollapsed = Setting("meetingPanelCollapsed", false)
    static let dictationIndicator = Setting("dictationIndicator", DictationIndicator.notch)
    static let meetingIndicator = Setting("meetingIndicator", MeetingIndicator.notch)
    /// The live meeting panel's top-right corner, x then y.
    static let meetingPanelTopRight = Setting("meetingPanelTopRight", [Double]())
    /// Open at login is switched on once, at the first launch.
    static let openAtLoginDefaulted = Setting("openAtLoginDefaulted", false)
    /// Folded "heard" words of dismissed dictionary suggestions. Derived from dictations, so Delete All Data removes it.
    static let dismissedSuggestions = Setting("dictionarySuggestionsDismissed", [String]())
}
