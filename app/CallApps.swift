import Foundation

// Known call apps and browsers (the catalog the Start menu, detection rules and audio tap read), and
// the stream and source types that say which audio a meeting records.

enum MeetingStream: String, Codable {
    case me, them
    /// The speaker words in transcripts, exports and the notes prompt's grounding.
    var label: String { self == .me ? "Me" : "Them" }
    static let roomLabel = "Room"
}

/// Whose audio "Them" is: one call app (and its helper processes), every app except Murmur, or none.
struct MeetingSource: Codable, Equatable {
    var name: String
    var bundleID: String?
    var allApps = false
    var micOnly = false

    /// The stream that carries the speakers: the microphone alone in a room, else the call's audio.
    var speakerStream: MeetingStream { micOnly ? .me : .them }

    static let everything = MeetingSource(name: "All apps", bundleID: nil, allApps: true)
    static let microphoneOnly = MeetingSource(name: "Only my microphone", bundleID: nil, micOnly: true)
}

// MARK: - Call apps

/// Known call apps and browsers (PRD 11.2 and 11.7). Used for the Start menu and to resolve which
/// processes play the call's audio. ponytail: bundle IDs and helper IDs are the best known values;
/// list each app's audio processes on the test Mac (PRD 11.10) and correct them there.
struct CallApp: Equatable {
    let bundleID: String
    let name: String
    let isBrowser: Bool
    /// Processes outside the bundle-ID prefix that play this app's audio.
    let helpers: [String]

    /// Whether a process belongs to this app: its own ID, an ID under it (`.helper`), or a known
    /// helper. Case is ignored: Arc is `company.thebrowser.Browser`, its audio helper
    /// `company.thebrowser.browser.helper`.
    func matches(_ id: String) -> Bool {
        let id = id.lowercased(), own = bundleID.lowercased()
        return id == own || id.hasPrefix(own + ".") || helpers.contains { $0.lowercased() == id }
    }

    static let catalog: [CallApp] = [
        CallApp(bundleID: "us.zoom.xos", name: "Zoom", isBrowser: false, helpers: []),
        CallApp(bundleID: "com.microsoft.teams2", name: "Microsoft Teams", isBrowser: false, helpers: []),
        CallApp(bundleID: "com.tinyspeck.slackmacgap", name: "Slack", isBrowser: false, helpers: []),
        CallApp(bundleID: "Cisco-Systems.Spark", name: "Webex", isBrowser: false, helpers: []),
        CallApp(bundleID: "com.apple.FaceTime", name: "FaceTime", isBrowser: false, helpers: ["com.apple.avconferenced"]),
        CallApp(bundleID: "com.hnc.Discord", name: "Discord", isBrowser: false, helpers: []),
        CallApp(bundleID: "net.whatsapp.WhatsApp", name: "WhatsApp", isBrowser: false, helpers: []),
        CallApp(bundleID: "org.whispersystems.signal-desktop", name: "Signal", isBrowser: false, helpers: []),
        CallApp(bundleID: "ru.keepcoder.Telegram", name: "Telegram", isBrowser: false, helpers: []),
        CallApp(bundleID: "com.apple.Safari", name: "Safari", isBrowser: true, helpers: ["com.apple.WebKit.GPU", "com.apple.WebKit.WebContent"]),
        CallApp(bundleID: "com.google.Chrome", name: "Google Chrome", isBrowser: true, helpers: []),
        CallApp(bundleID: "company.thebrowser.Browser", name: "Arc", isBrowser: true, helpers: []),
        CallApp(bundleID: "com.microsoft.edgemac", name: "Microsoft Edge", isBrowser: true, helpers: []),
        CallApp(bundleID: "com.brave.Browser", name: "Brave", isBrowser: true, helpers: []),
        CallApp(bundleID: "org.mozilla.firefox", name: "Firefox", isBrowser: true, helpers: []),
    ]
    static func app(_ bundleID: String) -> CallApp? { catalog.first { $0.bundleID == bundleID } }

    /// Bundle IDs to tap for this app: its own, every running audio process whose ID starts with it
    /// (helpers such as `com.google.Chrome.helper`), and its known out-of-prefix helpers.
    func tapBundleIDs(running: [String]) -> [String] {
        var ids = [bundleID] + helpers
        for id in running where matches(id) && !ids.contains(where: { $0.lowercased() == id.lowercased() }) { ids.append(id) }
        return ids
    }
    /// Murmur and its helpers, excluded from an "All apps" tap.
    static func ownBundleIDs(main: String?, running: [String]) -> [String] {
        guard let main else { return [] }
        return [main] + running.filter { $0.hasPrefix(main + ".") }
    }
}
