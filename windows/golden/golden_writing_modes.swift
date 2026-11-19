// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner Cleanup CleanupPrompt PrivateFiles Dictionary Learning VoiceCues Retention Settings Credentials ProviderTypes Processing WritingModes
import Foundation

// Golden outputs of WritingModes (website and app matching, trigger stripping, the presets, the mode JSON) and DictationStyle over
// synthetic modes, URLs and transcripts. One case per line. Run from the repository root.
struct SiteCase: Encodable { var pattern: String, url: String, matches: Bool }
struct StripCase: Encodable { var phrase: String, text: String, startOnly: Bool, result: String? }
struct MatchCase: Encodable { var modes: [String], bundleID: String?, url: String?, matched: String? }
struct TriggerCase: Encodable { var modes: [String], transcript: String, matched: String?, text: String? }
struct ModeCase: Encodable { var label: String, json: String }
struct StyleCase: Encodable {
    var label: String, mode: String?, globalOptions: String, globalScreenContext: Bool, context: Bool
    var options: String, tone: String, instructions: String, transform: String?, usesEngine: Bool, usesModel: Bool, hasContext: Bool
}
struct NeedsCase: Encodable { var label: String, mode: String?, modes: [String], globalOptions: String, globalScreenContext: Bool, needs: Bool }

let encoder: JSONEncoder = { let e = JSONEncoder(); e.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]; return e }()
func json<T: Encodable>(_ value: T) -> String { String(decoding: try! encoder.encode(value), as: UTF8.self) }
func lines<T: Encodable>(_ name: String, _ items: [T]) -> String { "\"\(name)\": [\n" + items.map(json).joined(separator: ",\n") + "\n]" }

@main struct Gen {
    static func main() throws {
        var sites: [SiteCase] = []
        func site(_ p: String, _ u: String) { sites.append(SiteCase(pattern: p, url: u, matches: WritingModes.websiteMatches(p, url: URL(string: u)!))) }
        for (p, u) in [("google.com", "https://mail.google.com/x"), ("google.com", "https://google.com"), ("google.com", "https://notgoogle.com"), ("*.slack.com", "https://acme.slack.com/client"),
                       ("*.slack.com", "https://slack.com"), ("github.com/org/*", "https://github.com/org/repo"), ("github.com/org", "https://github.com/org"),
                       ("github.com/org/*", "https://github.com/organization"), ("github.com/org", "https://gist.github.com.evil.io/org"),
                       ("HTTPS://Docs.Google.com/Document/", "https://docs.google.com:443/document/d/1?x=1#y"), ("www.example.com", "https://example.com/a"), ("", "https://example.com"),
                       ("localhost:3000", "http://localhost:3000/app"), ("  example.com  ", "https://example.com"), ("example.com/a/b", "https://example.com/a/b/c"),
                       ("example.com/a/b", "https://example.com/a"), ("example.com/a/", "https://example.com/a/b"), ("example.com/A", "https://example.com/a"),
                       ("example.com/a b", "https://example.com/a%20b"), ("*", "https://example.com"), ("/x", "https://example.com/x"), ("http://example.com", "https://sub.example.com"),
                       ("example.com", "https://EXAMPLE.com/Path"), ("example.com/*", "https://example.com"), ("example.com/", "https://example.com/deep/path"),
                       ("1.2.3.4", "http://1.2.3.4/x"), ("example.com", "https://example.com./x"), ("m\u{fc}nchen.de", "https://xn--mnchen-3ya.de/")] { site(p, u) }

        var strips: [StripCase] = []
        func strip(_ phrase: String, _ text: String, startOnly: Bool = false) { strips.append(StripCase(phrase: phrase, text: text, startOnly: startOnly, result: WritingModes.stripTrigger(phrase, from: text, startOnly: startOnly))) }
        for (p, t) in [("slack mode", "Slack mode, fix the build."), ("slack mode", "slack mode fix the build"), ("code", "Code: fix it"), ("email mode", "Email mode. Fix it."),
                       ("slack mode", "Fix it, slack mode."), ("slack mode", "Is it fixed slack mode?"), ("slack mode", "Fix it. Slack mode."), ("code", "Codebase looks good"),
                       ("code", "This is my barcode"), ("slack mode", "Tell them slack mode is great"), ("slack mode", "Slack mode."), ("slack mode", "Slack is down"),
                       ("  ", "anything"), ("", "anything"), ("slack mode", "SLACK MODE; fix it"), ("slack mode", "Slack mode - fix it"), ("slack mode", "Slack mode\nfix it"),
                       ("slack mode", "Fix it\nSlack mode"), ("slack mode", "fix it - slack mode!"), ("slack mode", "Slack mode, \u{e9}lan is here"), ("slack mode", "slack mode, \u{e9}lan is here"),
                       ("slack mode", "Fix it, slack mode..."), ("prompt mode", "Prompt mode: write a haiku about rain"), ("slack mode", "fix it, slack mode"), ("don't mode", "Don't mode, go"),
                       ("slack mode", "slack mode slack mode fix"), ("a", "a b"), ("a", "b a"), ("a b", "a"), ("slack-mode", "Slack mode, hi there"), ("slack mode", "Hi,   slack mode  ")] {
            strip(p, t)
            strip(p, t, startOnly: true)
        }

        func mode(_ name: String, enabled: Bool = true, apps: [String] = [], websites: [String] = [], triggers: [String] = [], tone: Tone = .formal, transform: ModeTransform? = nil) -> WritingMode {
            // A stable id per name keeps the golden file deterministic.
            let id = UUID(uuidString: "00000000-0000-0000-0000-" + String(format: "%012x", abs(name.hashValue &* 0 &+ name.utf8.reduce(0) { ($0 &* 31 &+ Int($1)) & 0xFFFFFFFF })))!
            return WritingMode(id: id, name: name, enabled: enabled, apps: apps, websites: websites, triggers: triggers, tone: tone, transform: transform)
        }
        let appMode = mode("App", apps: ["slack.exe"], tone: .casual), siteMode = mode("Site", websites: ["mail.google.com"], tone: .excited)
        let wordMode = mode("Word", triggers: ["formal mode"]), off = mode("Off", enabled: false, apps: ["notepad.exe"], websites: ["mail.google.com"], triggers: ["formal mode"])
        let both = mode("Both", apps: ["x.app"], websites: ["google.com"]), second = mode("Second", apps: ["x.app"])
        let site2 = mode("Site2", websites: ["google.com"])
        var matches: [MatchCase] = []
        func match(_ modes: [WritingMode], _ bundleID: String?, _ url: String?) {
            let m = WritingModes.match(modes, bundleID: bundleID, url: url.flatMap { URL(string: $0) })
            matches.append(MatchCase(modes: modes.map(json), bundleID: bundleID, url: url, matched: m?.name))
        }
        let all = [off, appMode, siteMode, wordMode]
        for (b, u) in [("notepad.exe", "https://mail.google.com/x"), ("slack.exe", "https://mail.google.com/x"), ("slack.exe", nil), ("SLACK.EXE", nil), ("notepad.exe", nil),
                       (nil, nil), ("", nil), (nil, "https://mail.google.com/"), ("slack.exe", "https://example.com"), ("x.app", nil)] as [(String?, String?)] { match(all, b, u) }
        match([appMode, site2], "slack.exe", "https://mail.google.com/x")
        match([mode("First", apps: ["x.app"]), second], "x.app", nil)
        match([both, second], "x.app", nil)
        match([second, both], "x.app", "https://google.com")
        match([], "x.app", nil)

        let ps = WritingMode.presets, custom = mode("Mine", triggers: ["slack mode"])
        var triggers: [TriggerCase] = []
        func trigger(_ modes: [WritingMode], _ transcript: String) {
            let t = WritingModes.trigger(modes, in: transcript)
            triggers.append(TriggerCase(modes: modes.map(json), transcript: transcript, matched: t?.mode.name, text: t?.text))
        }
        for t in ["Prompt mode, fix the login bug", "PROMPT MODE fix it", "Prompt engineer, fix the login bug", "fix the login bug, prompt mode", "I said email mode is odd later",
                  "notes mode", "Email mode. Dear Sam", "Notes mode: buy milk", "fix it, slack mode."] { trigger(ps, t); trigger([custom], t); trigger([custom] + ps, t) }
        trigger([off, wordMode], "Formal mode, hello there"); trigger([wordMode, off], "hello there, formal mode"); trigger([], "anything")

        var modeCases: [ModeCase] = []
        for preset in ps { modeCases.append(ModeCase(label: "preset \(preset.name)", json: json(preset))) }
        modeCases.append(ModeCase(label: "default", json: json(WritingMode(id: UUID(uuidString: "00000000-0000-0000-0000-000000000001")!))))
        modeCases.append(ModeCase(label: "full", json: json(WritingMode(id: UUID(uuidString: "00000000-0000-0000-0000-000000000002")!, name: "Full \u{e9} \"q\"", enabled: false, apps: ["a.exe", "B.EXE"],
                                                                      websites: ["b.com/c", "*.d.com"], triggers: ["d e"], options: .faithful, tone: .veryCasual, screenContext: false, instructions: "Short.\nSecond line.", transform: .notes))))
        modeCases.append(ModeCase(label: "options and context", json: json(WritingMode(id: UUID(uuidString: "00000000-0000-0000-0000-000000000003")!, options: CleanupOptions(polish: true, trustModel: true), tone: .excited, screenContext: true, transform: .aiPrompt))))
        modeCases.append(ModeCase(label: "standard options", json: json(WritingMode(id: UUID(uuidString: "00000000-0000-0000-0000-000000000004")!, options: .standard, tone: .casual, transform: .email))))

        // Every mode in a case carries a fixed id so the file is the same on every run.
        let fixedID = UUID(uuidString: "00000000-0000-0000-0000-0000000000FF")!
        func settings(_ options: CleanupOptions, _ screen: Bool) -> ProcessingSettings { var s = ProcessingSettings(); s.options = options; s.screenContext = screen; return s }
        let ctx = ScreenContext(selected: "Synthetic selection", clipboard: "", window: "")
        var styles: [StyleCase] = []
        func style(_ label: String, _ m: WritingMode?, _ options: CleanupOptions, screen: Bool, context: Bool) {
            let d = DictationStyle(mode: m, settings: settings(options, screen), context: context ? ctx : nil)
            styles.append(StyleCase(label: label, mode: m.map(json), globalOptions: json(options), globalScreenContext: screen, context: context, options: json(d.options), tone: d.tone.rawValue,
                                    instructions: d.instructions, transform: d.transform?.rawValue, usesEngine: d.usesEngine, usesModel: d.usesModel, hasContext: d.context != nil))
        }
        var trusting = CleanupOptions.standard; trusting.trustModel = true
        let polished = CleanupOptions(polish: true)
        style("no mode", nil, .standard, screen: false, context: true)
        style("no mode, global context on, rules only", nil, .standard, screen: true, context: true)
        style("no mode, polish, global context", nil, polished, screen: true, context: true)
        style("inherit", WritingMode(id: fixedID, name: "Inherit"), polished, screen: true, context: true)
        style("faithful override", WritingMode(id: fixedID, options: .faithful, tone: .casual, screenContext: false, instructions: String(repeating: "x", count: 2_500)), polished, screen: true, context: true)
        style("trust carries", WritingMode(id: fixedID, options: .faithful), trusting, screen: false, context: false)
        style("mode context on, rules only", WritingMode(id: fixedID, screenContext: true), .standard, screen: false, context: true)
        style("mode context on, polish", WritingMode(id: fixedID, options: polished, screenContext: true), .standard, screen: false, context: true)
        for preset in ps { style("preset \(preset.name)", preset, .standard, screen: true, context: true) }
        style("emoji instructions", WritingMode(id: fixedID, instructions: String(repeating: "\u{1F600}", count: 2_001)), .standard, screen: false, context: false)

        var needs: [NeedsCase] = []
        func need(_ label: String, _ m: WritingMode?, _ modes: [WritingMode], _ options: CleanupOptions, screen: Bool) {
            needs.append(NeedsCase(label: label, mode: m.map(json), modes: modes.map(json), globalOptions: json(options), globalScreenContext: screen,
                                   needs: DictationStyle.needsScreenContext(mode: m, modes: modes, settings: settings(options, screen))))
        }
        var strict = WritingMode(id: fixedID); strict.options = .standard
        var trig = WritingMode(id: fixedID); trig.transform = .aiPrompt; trig.triggers = ["prompt mode"]
        var trigOff = trig; trigOff.enabled = false
        var trigNoContext = trig; trigNoContext.screenContext = false
        need("global off", nil, [], polished, screen: false)
        need("global on, polish", nil, [], polished, screen: true)
        need("global on, rules", nil, [], .standard, screen: true)
        need("mode on, rules", WritingMode(id: fixedID, screenContext: true), [], .standard, screen: false)
        need("mode strict", strict, [], .standard, screen: true)
        need("trigger candidate", nil, [trig], .standard, screen: true)
        need("disabled trigger candidate", nil, [trigOff], .standard, screen: true)
        need("trigger candidate without context", nil, [trigNoContext], .standard, screen: true)
        need("trigger candidate, global off", nil, [trig], .standard, screen: false)
        need("preset triggers", nil, ps, .standard, screen: true)

        let presetLine = json(ps.map { [$0.id.uuidString, $0.name, $0.triggers.joined(separator: "|"), $0.transform?.rawValue ?? "", $0.instructions] })
        print("{\n" + [lines("sites", sites), lines("strips", strips), lines("matches", matches), lines("triggers", triggers), lines("modes", modeCases), lines("styles", styles),
                       lines("needs", needs), "\"presets\": \(presetLine)"].joined(separator: ",\n") + "\n}")
    }
}
