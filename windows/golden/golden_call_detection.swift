// deps: CallApps CallDetection
import Foundation

@main struct Gen {
    struct Tick: Encodable {
        let now: Int
        let active: Bool
        var dictating = false
        var meeting = false
        var enabled = true
        var never = false
        var rearm = false
    }

    static func main() throws {
        let scenarios = [
            [Tick(now: 0, active: true), Tick(now: 4, active: true), Tick(now: 5, active: true), Tick(now: 6, active: true), Tick(now: 12, active: false), Tick(now: 16, active: true), Tick(now: 32, active: false), Tick(now: 33, active: true), Tick(now: 38, active: true)],
            [Tick(now: 0, active: true), Tick(now: 5, active: true, dictating: true), Tick(now: 6, active: true), Tick(now: 7, active: true, dictating: true, rearm: true), Tick(now: 8, active: true)],
            [Tick(now: 0, active: true), Tick(now: 5, active: true, meeting: true), Tick(now: 6, active: true)],
            [Tick(now: 0, active: true), Tick(now: 5, active: true, enabled: false), Tick(now: 6, active: true, never: true), Tick(now: 7, active: true)]
        ]
        struct Case: Encodable { var ticks: [Tick]; var offers: [Int] }
        let cases = scenarios.map { ticks in
            var detector = CallDetector()
            let offers = ticks.compactMap { tick -> Int? in
                if tick.rearm { detector.rearm("us.zoom.xos") }
                let result = detector.update(inputs: tick.active ? ["US.ZOOM.XOS"] : [], now: TimeInterval(tick.now),
                    .init(enabled: tick.enabled, never: tick.never ? ["US.ZOOM.XOS"] : [], meetingLive: tick.meeting, dictating: tick.dictating))
                return result == nil ? nil : tick.now
            }
            return Case(ticks: ticks, offers: offers)
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        print(String(decoding: try encoder.encode(cases), as: UTF8.self))
    }
}
