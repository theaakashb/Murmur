// The settings pages and the settings search index. Foundation only.
import Foundation

/// A settings page. The raw values are stored (`settingsSection`), so they stay stable. Each page
/// belongs to a sidebar group (named by its first page); the group supplies title, icon and lede.
enum SettingsPane: Int, CaseIterable {
    case general = 0, transcription, cleanup, modes, dictionary, privacy, apiKeys, updates, meetings, recording, insertion, snippets

    var name: String {
        switch self {
        case .general: "General"
        case .transcription: "Transcription"
        case .cleanup: "Cleanup"
        case .modes: "Modes"
        case .dictionary: "Dictionary"
        case .snippets: "Snippets"
        case .privacy: "History & Privacy"
        case .apiKeys: "API Keys"
        case .updates: "Updates"
        case .meetings: "Meetings"
        case .recording: "Recording"
        case .insertion: "Insertion"
        }
    }
    static let groups: [SettingsPane] = [.general, .recording, .cleanup, .meetings, .privacy, .apiKeys, .updates]
    var group: SettingsPane {
        switch self {
        case .transcription, .recording, .insertion: .recording
        case .cleanup, .modes, .dictionary, .snippets: .cleanup
        default: self
        }
    }
    var tabs: [SettingsPane] {
        switch group {
        case .recording: [.recording, .transcription, .insertion]
        case .cleanup: [.cleanup, .modes, .dictionary, .snippets]
        default: []
        }
    }
    var title: String {
        switch group {
        case .recording: "Dictation"
        case .cleanup: "Writing"
        case .privacy: "Privacy & Storage"
        case .apiKeys: "Providers"
        default: name
        }
    }
    var icon: String {
        switch group {
        case .general: "gearshape"
        case .recording: "mic"
        case .cleanup: "pencil"
        case .meetings: "person.2"
        case .privacy: "externaldrive"
        case .apiKeys: "key"
        default: "arrow.triangle.2.circlepath"
        }
    }
    var lede: String {
        switch group {
        case .general: "Make Murmur feel at home on your Mac."
        case .recording: "Set up your voice-to-text workflow."
        case .cleanup: "Shape your text and keep your own voice."
        case .privacy: "Choose what Murmur saves on this Mac, and for how long."
        case .apiKeys: "Needed only for cloud routes. Stored in your Keychain."
        case .updates: "Verified updates from GitHub. Choose when they install."
        default: "Where meeting audio is transcribed, apart from dictation, and how notes are written."
        }
    }
}

/// Search only authored setting labels and aliases; never user text or credentials.
enum SettingsSearch {
    struct Result: Identifiable {
        let title: String
        let section: SettingsPane
        let keywords: String
        let target: String
        var id: String { "\(section.rawValue):\(title)" }
        var sectionName: String { section.name }
        init(_ title: String, _ section: SettingsPane, _ keywords: String, target: String? = nil) {
            self.title = title; self.section = section; self.keywords = keywords; self.target = target ?? title
        }
    }
    static let all: [Result] = [
        .init("Shortcut", .recording, "hotkey keyboard fn globe right option hold record"),
        .init("Press once to start, again to stop", .recording, "toggle recording double press"),
        .init("Start and stop sounds", .recording, "sound audio chime feedback"),
        .init("Live words", .recording, "streaming preview real time", target: "live-words"),
        .init("Show live words on cloud routes", .recording, "preview streaming", target: "live-words"),
        .init("Insert at the cursor", .insertion, "automatic paste typing clipboard insertion"),
        .init("Add a leading space when needed", .insertion, "intelligent spacing whitespace"),
        .init("Show menu bar icon", .general, "status tray hide"),
        .init("Show in Dock", .general, "app switcher hide icon"),
        .init("Open at login", .general, "startup launch background"),
        .init("Microphone", .recording, "input device audio mic"),
        .init("Permissions", .privacy, "accessibility input monitoring microphone access"),
        .init("Show detailed info", .general, "help explanations tooltips", target: "detailed-info"),
        .init("Speech model", .transcription, "download parakeet local on device offline", target: "top"),
        .init("Where transcription runs", .transcription, "dictation cloud local on device provider gemini custom api"),
        .init("Model folder", .transcription, "advanced storage location directory", target: "model-folder"),
        .init("AI model", .cleanup, "cleanup runs with location apple intelligence cloud gemini custom provider polish professional"),
        .init("Cleanup style", .cleanup, "faithful strict polish professional tone rewrite"),
        .init("Remove filler words", .cleanup, "um uh cleanup"),
        .init("Apply spoken corrections", .cleanup, "backtrack no wait cleanup"),
        .init("Format spoken lists", .cleanup, "numbered bullets cleanup"),
        .init("Fix punctuation and capitalization", .cleanup, "commas periods capitals cleanup"),
        .init("Spoken commands and numbers", .cleanup, "email paragraph digits symbols cleanup"),
        .init("Use screen context", .cleanup, "clipboard selected text privacy cleanup"),
        .init("Writing modes", .modes, "apps websites trigger words custom instructions tone", target: "top"),
        .init("Dictionary", .dictionary, "vocabulary preferred spelling aliases words", target: "top"),
        .init("Learn from my corrections", .dictionary, "automatic learning spelling dictionary"),
        .init("Limit learning to readable fields", .dictionary, "keystroke learning privacy dictionary"),
        .init("Snippets", .snippets, "saved text expand insert paste email address link signature shortcut cue", target: "top"),
        .init("Transcript history", .privacy, "save off privacy storage"),
        .init("Keep transcripts for", .privacy, "retention delete thirty 30 days six months forever"),
        .init("Keep recordings for diagnostics (7 days)", .privacy, "audio wav replay debug developer quality"),
        .init("Delete all data", .privacy, "erase wipe reset history recordings keys", target: "delete-data"),
        .init("API Keys", .apiKeys, "credentials password keychain gemini custom transcription cleanup", target: "top"),
        .init("Updates", .updates, "version release download install", target: "top"),
        .init("Meeting transcription", .meetings, "call audio cloud local on device provider"),
        .init("Meeting notes", .meetings, "summary transcript only none apple intelligence gemini custom provider"),
        .init("Call detection", .meetings, "automatic meeting prompt offer take notes zoom teams browser", target: "call-detection"),
        .init("Speaker separation", .meetings, "diarization diarizer speakers pyannote model", target: "speaker-separation")
    ]
    static func matches(_ query: String) -> [Result] {
        let words = normalized(query).split(whereSeparator: { $0.isWhitespace })
        guard !words.isEmpty else { return [] }
        return all.filter { result in
            let text = normalized(result.title + " " + result.sectionName + " " + result.keywords)
            return words.allSatisfy { text.contains($0) }
        }
    }
    private static func normalized(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX")).replacingOccurrences(of: "-", with: " ")
    }
}
