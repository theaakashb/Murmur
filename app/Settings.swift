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
    /// Screen context for AI cleanup routes (gap #8): opt-in, off by default. A writing mode can override it.
    static let screenContext = Setting("cleanupScreenContext", false)
    static let historyMode = Setting("historyMode", HistoryMode.on)
    static let updateChecks = Setting("updateChecksEnabled", true)
    static let retention = Setting("retention", HistoryRetention.thirtyDays.rawValue)
    static let showMenuBar = Setting("showMenuBar", true)
    /// A Dock icon (gap #20); off by default, as in PRD R6.
    static let showInDock = Setting("showInDock", false)
    static let inputDeviceUID = Setting("inputDeviceUID", "")
    static let intelligentLeadingSpace = Setting("intelligentLeadingSpace", false)
    static let automaticInsertion = Setting("automaticInsertion", true)
    static let pressToToggle = Setting("pressToToggle", false)
    static let recordingSounds = Setting("recordingSounds", true)
    static let keepRecordings = Setting("keepRecordings", false)
    static let liveWords = Setting("liveWords", true)
    static let liveWordsOnCloud = Setting("liveWordsOnCloud", false)
}
