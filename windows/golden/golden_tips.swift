// deps: Tips Insights SettingsPanes
import Foundation

// Golden outputs of the pure functions in Tips.swift: duration text, seconds saved, the rewrite rule and the next rung. Run from the repository root.
@main struct Gen {
    static func main() throws {
        let durations = [0, 29, 30, 89, 90, 14 * 60, 59 * 60, 3_569, 3_570, 3_600, 5_400, 9_000, 9_100, 10_800, 36_000, 360_000].map {
            ["seconds": Double($0), "text": Tips.duration(Double($0))] as [String: Any]
        }
        let finals = ["", String(repeating: "x", count: 5 * 52), "Zoë 😀 café 東京", "e\u{301}"]
        let saved: [[String: Any]] = finals.flatMap { f in [nil, 0, -1, 10, 100, .infinity, .nan].map { (s: Double?) in
            ["final": f, "seconds": s.map { $0.isFinite ? ($0 as Any) : (($0.isNaN ? "nan" : "inf") as Any) } ?? NSNull(), "saved": Tips.secondsSaved(final: f, seconds: s)] as [String: Any] } }
        let rewrites = [("the launch is on friday this week", "The launch moved to Monday next week."), ("the launch is on friday this week", "the launch is on Friday this week"),
                        ("ok sure", "no"), ("a b c d", "a b c d e f g"), ("a b c d", "x y z d"), ("a b c d e f g h i j", "a b c d e f g x y z"), ("a b c d e f g h i j", "a b c d e f g h x y"), ("", "x"),
                        ("one two three four", ""), ("Zoë café 東京 team", "zoë cafe tokyo team!")]
            .map { ["initial": $0.0, "edited": $0.1, "rewrite": Tips.isRewrite(initial: $0.0, edited: $0.1)] as [String: Any] }
        let next = ([0] + Tips.rungs + [Tips.rungs.last! + 1]).map { ["after": $0, "next": Tips.next(after: $0) as Any? ?? NSNull()] as [String: Any] }
        let data = try JSONSerialization.data(withJSONObject: ["durations": durations, "saved": saved, "rewrites": rewrites, "next": next], options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
