// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup CleanupPrompt PrivateFiles Dictionary Learning VoiceCues Retention Settings Credentials ProviderTypes Processing WritingModes Shortcut
import Foundation

// Exports every Prefs key with its Mac default, and the Windows default where it differs (spec/settings.md).
// The three controller keys (Controller+Settings.swift is AppKit) are written out with the Swift literals they use.
// Types: bool, int, string, stringList, doubleList. Sorted by key.

enum JSONValue: Encodable {
    case bool(Bool), int(Int), string(String), list([String]), doubles([Double])
    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .bool(let v): try c.encode(v)
        case .int(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .list(let v): try c.encode(v)
        case .doubles(let v): try c.encode(v)
        }
    }
}
struct Row: Encodable { var key: String, type: String, mac: JSONValue, windows: JSONValue? }

func row(_ s: Setting<Bool>) -> Row { Row(key: s.key, type: "bool", mac: .bool(s.fallback), windows: nil) }
func row(_ s: Setting<Int>) -> Row { Row(key: s.key, type: "int", mac: .int(s.fallback), windows: nil) }
func row(_ s: Setting<String>) -> Row { Row(key: s.key, type: "string", mac: .string(s.fallback), windows: nil) }
func row<V: RawRepresentable>(_ s: Setting<V>, windows: String? = nil) -> Row where V.RawValue == String {
    Row(key: s.key, type: "string", mac: .string(s.fallback.rawValue), windows: windows.map { .string($0) })
}
func row(_ s: Setting<[String]>) -> Row { Row(key: s.key, type: "stringList", mac: .list(s.fallback), windows: nil) }
func row(_ s: Setting<[Double]>) -> Row { Row(key: s.key, type: "doubleList", mac: .doubles(s.fallback), windows: nil) }

@main struct Gen {
    static func main() throws {
        var rows: [Row] = [
            row(Prefs.cleanupFillers), row(Prefs.cleanupCorrections), row(Prefs.cleanupLists), row(Prefs.cleanupPunctuation), row(Prefs.cleanupPolish),
            row(Prefs.cleanupProfessional), row(Prefs.cleanupSpoken), row(Prefs.cleanupRoute), row(Prefs.learningEnabled), row(Prefs.readableLearning),
            row(Prefs.screenContext), row(Prefs.historyMode), row(Prefs.updateChecks), row(Prefs.retention), row(Prefs.showMenuBar), row(Prefs.showInDock),
            row(Prefs.inputDeviceUID), row(Prefs.intelligentLeadingSpace), row(Prefs.automaticInsertion), row(Prefs.pressToToggle), row(Prefs.recordingSounds),
            row(Prefs.keepRecordings), row(Prefs.liveWords), row(Prefs.liveWordsOnCloud), row(Prefs.settingsSection), row(Prefs.sidebarCollapsed),
            row(Prefs.showDetailedSettingsInfo), row(Prefs.meetingPanelCollapsed),
            // Windows has no notch: top/bottom and top/tray (spec/settings.md); the open default is bottom, tray.
            row(Prefs.dictationIndicator, windows: "bottom"), row(Prefs.meetingIndicator, windows: "tray"),
            row(Prefs.meetingPanelTopRight), row(Prefs.openAtLoginDefaulted), row(Prefs.dismissedSuggestions),
            // Controller+Settings.swift: ShortcutBinding.defaultID, EditTrigger.sameKey, and the computed model folder ("" here: Windows computes its own).
            Row(key: "shortcut", type: "string", mac: .string(ShortcutBinding.defaultID), windows: .string("right-ctrl")),
            Row(key: "voiceEditShortcut", type: "string", mac: .string("same"), windows: nil),
            Row(key: "modelDirectory", type: "string", mac: .string(""), windows: nil),
            // New on Windows: the Mac's paste-last chord is fixed (Shortcut.swift:245).
            Row(key: "pasteLastShortcut", type: "string", mac: .string(""), windows: .string("ctrl-win-shift-v")),
        ]
        rows.sort { $0.key < $1.key }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(["settings": rows]), as: UTF8.self))
    }
}
