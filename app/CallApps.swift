import Foundation

// Known call apps and browsers (the catalog the Start menu, detection rules and audio tap read), and
// the stream and source types that say which audio a meeting records.

enum MeetingStream: String, Codable {
    case me, them
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
    /// The Start menu's labels for the two sources that are not an app (the window and the menu bar).
    static let everythingMenuTitle = everything.name + " (includes music and notifications)"
    static let microphoneOnlyMenuTitle = microphoneOnly.name
}

/// Known call apps and browsers. Used for the Start menu and to resolve which
/// processes play the call's audio. ponytail: bundle IDs and helper IDs are the best known values;
/// list each app's audio processes on the test Mac and correct them there.
struct CallApp: Equatable {
    let bundleID: String
    let name: String
    let isBrowser: Bool
    /// Processes outside the bundle-ID prefix that play this app's audio.
    let helpers: [String]
    /// False for an ID that detection recognises but the Start menu does not list (a second client of
    /// the same app).
    var inStartMenu = true

    enum Scope { case exact, orChildren, childrenOnly }

    /// The one bundle-ID rule: is `id` the same ID as `base`, or (by scope) an ID under it
    /// (`base.helper`)? `caseSensitive` is true only where the code has always compared exactly
    /// (`app(_:)`, `ownBundleIDs`); every other site ignores case, as Arc is
    /// `company.thebrowser.Browser` and its audio helper `company.thebrowser.browser.helper`.
    static func isBundle(_ id: String, of base: String, scope: Scope = .orChildren, caseSensitive: Bool = false) -> Bool {
        let id = caseSensitive ? id : id.lowercased(), base = caseSensitive ? base : base.lowercased()
        switch scope {
        case .exact: return id == base
        case .orChildren: return id == base || id.hasPrefix(base + ".")
        case .childrenOnly: return id.hasPrefix(base + ".")
        }
    }

    func matches(_ id: String) -> Bool {
        Self.isBundle(id, of: bundleID) || helpers.contains { Self.isBundle(id, of: $0, scope: .exact) }
    }

    /// Safari plays audio through a WebKit process that every WebKit app shares.
    var sharesWebKit: Bool { helpers.contains("com.apple.WebKit.GPU") }

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
        CallApp(bundleID: "com.tdesktop.Telegram", name: "Telegram", isBrowser: false, helpers: [], inStartMenu: false),
        CallApp(bundleID: "com.apple.Safari", name: "Safari", isBrowser: true, helpers: ["com.apple.WebKit.GPU", "com.apple.WebKit.WebContent"]),
        CallApp(bundleID: "com.google.Chrome", name: "Google Chrome", isBrowser: true, helpers: []),
        CallApp(bundleID: "company.thebrowser.Browser", name: "Arc", isBrowser: true, helpers: []),
        CallApp(bundleID: "com.microsoft.edgemac", name: "Microsoft Edge", isBrowser: true, helpers: []),
        CallApp(bundleID: "com.brave.Browser", name: "Brave", isBrowser: true, helpers: []),
        CallApp(bundleID: "org.mozilla.firefox", name: "Firefox", isBrowser: true, helpers: []),
    ]
    static func resolve(_ bundleID: String, name: String = "") -> CallApp { app(bundleID) ?? CallApp(bundleID: bundleID, name: name, isBrowser: false, helpers: []) }
    static func app(_ bundleID: String) -> CallApp? { catalog.first { isBundle(bundleID, of: $0.bundleID, scope: .exact, caseSensitive: true) } }

    /// Bundle IDs to tap for this app: its own, every running audio process whose ID starts with it
    /// (helpers such as `com.google.Chrome.helper`), and its known out-of-prefix helpers.
    func tapBundleIDs(running: [String]) -> [String] {
        var ids = [bundleID] + helpers
        for id in running where matches(id) && !ids.contains(where: { Self.isBundle(id, of: $0, scope: .exact) }) { ids.append(id) }
        return ids
    }
    static func ownBundleIDs(main: String?, running: [String]) -> [String] {
        guard let main else { return [] }
        return [main] + running.filter { isBundle($0, of: main, scope: .childrenOnly, caseSensitive: true) }
    }
}
