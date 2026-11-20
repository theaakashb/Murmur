// deps: Insights
import Foundation

// Golden outputs of Insights.compute over synthetic items in several time zones, plus wordsChanged and duration. Times are epoch seconds.
// One scenario per entry; "stats" is absent when compute returns historyOff or empty. Run from the repository root.
@main struct Gen {
    static func main() throws {
        let now = 1_792_065_600.0 // noon UTC on 2026-10-15
        typealias Row = (Double, String, String?, Double?, String?, Bool) // daysAgo, raw, final, seconds, app, expanded
        func r(_ d: Double, _ raw: String = "hello there", _ final: String? = nil, _ s: Double? = nil, _ app: String? = nil, _ x: Bool = false) -> Row { (d, raw, final, s, app, x) }
        let fifty = "abcd abcd abcd abcd abcd abcd abcd abcd abcd abcde"
        let scenarios: [(String, String, Bool, Double, [Row])] = [
            ("history off", "UTC", false, now, [r(0)]), ("empty", "UTC", true, now, []), ("filler only", "UTC", true, now, [r(0, "um", "")]),
            ("paced", "UTC", true, now, [r(0, fifty, nil, 6)]), ("pooled", "UTC", true, now, [r(0, String(repeating: "x", count: 50), nil, 6), r(0, String(repeating: "x", count: 50), nil, 18)]),
            ("slow", "UTC", true, now, [r(0, "short", nil, 60)]), ("untimed", "UTC", true, now, [r(0, "one two three")]),
            ("changed", "UTC", true, now, [r(0, "um hello", "Hello"), r(0, "insert my link here", "https://example.com/a/b", nil, nil, true)]),
            ("apps", "UTC", true, now, [r(0, "hello there", nil, nil, "com.apple.TextEdit"), r(0, "hello there", nil, nil, "com.apple.mail"), r(1, "hello there", nil, nil, "Com.Apple.Mail"), r(1)]),
            ("activity", "UTC", true, now, [r(0, "one two three", nil, 8, "a"), r(0, "one", nil, .infinity, "b"), r(1, "one two", nil, -1, "b"), r(0, "  ", "  ")]),
            ("by words", "UTC", true, now, [r(0, "one two three four", nil, nil, "a"), r(0, "one", nil, nil, "b"), r(1, "two", nil, nil, "b")]),
            ("tokyo", "Asia/Tokyo", true, now, [r(0), r(0.1), r(0.2)]), ("new york", "America/New_York", true, now, [r(0), r(0.6), r(1)]),
            ("streak", "UTC", true, now, [r(0), r(1), r(2), r(5), r(6)]), ("yesterday", "UTC", true, now, [r(1), r(2)]), ("missed", "UTC", true, now, [r(2), r(3), r(4), r(5)]),
            ("dst", "America/New_York", true, now + 17.5 * 86400, [r(17.2), r(16.2), r(15.2), r(14.2)]),
            ("unicode", "UTC", true, now, [r(0, "Zoë 😀 café\u{301} 東京 team", nil, 3, "app")]),
        ]
        func cal(_ zone: String) -> Calendar { var c = Calendar(identifier: .gregorian); c.timeZone = TimeZone(identifier: zone)!; return c }
        func day(_ d: Date, _ c: Calendar) -> String { let f = DateFormatter(); f.calendar = c; f.timeZone = c.timeZone; f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd"; return f.string(from: d) }
        var out: [[String: Any]] = []
        for (label, zone, on, at, rows) in scenarios {
            let c = cal(zone)
            let items = rows.map { InsightsItem(recordedAt: Date(timeIntervalSince1970: now - $0.0 * 86400), rawText: $0.1, finalText: $0.2 ?? $0.1, seconds: $0.3, app: $0.4, expanded: $0.5) }
            var entry: [String: Any] = ["label": label, "zone": zone, "historyOn": on, "now": at,
                "items": rows.map { ["recordedAt": now - $0.0 * 86400, "raw": $0.1, "final": $0.2 ?? $0.1, "seconds": $0.3 as Any? ?? NSNull(), "app": $0.4 as Any? ?? NSNull(), "expanded": $0.5] as [String: Any] }]
            // JSON has no infinity: send it as a string the C# side maps back.
            entry["items"] = (entry["items"] as! [[String: Any]]).map { var m = $0; if let s = m["seconds"] as? Double, !s.isFinite { m["seconds"] = "inf" }; return m }
            switch Insights.compute(items, historyOn: on, now: Date(timeIntervalSince1970: at), calendar: c) {
            case .historyOff: entry["result"] = "historyOff"
            case .empty: entry["result"] = "empty"
            case .stats(let s):
                entry["result"] = "stats"
                var daily: [String: Int] = [:]; for (d, n) in s.dailyWords { daily[day(d, c)] = n }
                entry["stats"] = ["dictations": s.dictations, "words": s.words, "wordsChanged": s.wordsChanged, "timedDictations": s.timedDictations, "withoutApp": s.withoutApp,
                    "currentStreak": s.currentStreak, "longestStreak": s.longestStreak, "dayCount": s.days.count, "averageWords": s.averageWords,
                    "wordsPerMinute": s.wordsPerMinute as Any? ?? NSNull(), "secondsSaved": s.secondsSaved as Any? ?? NSNull(), "recordingSeconds": s.recordingSeconds as Any? ?? NSNull(),
                    "hourly": s.hourlyDictations, "daily": daily,
                    "apps": s.apps.map { ["app": $0.app, "count": $0.count, "words": $0.words] as [String: Any] }] as [String: Any]
            }
            out.append(entry)
        }
        let pairs = [("um so the meeting is at three", "So the meeting is at 3."), ("same words", " same words"), ("ship it to cube control", "ship it to kubectl"), ("", ""), ("a b c", ""), ("Zoë café", "zoë Café!")]
        let changed = pairs.map { ["raw": $0.0, "final": $0.1, "count": Insights.wordsChanged(raw: $0.0, final: $0.1)] as [String: Any] }
        let durations = [0, 0.4, 0.5, 40, 59.5, 60, 720, 3600, 3900, 7260].map { ["seconds": $0, "text": Insights.duration($0)] as [String: Any] }
        let data = try JSONSerialization.data(withJSONObject: ["scenarios": out, "changed": changed, "durations": durations], options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
