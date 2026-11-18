import Foundation

nonisolated(unsafe) var failures = 0
func check(_ condition: Bool, _ name: String) { if !condition { failures += 1; print("FAIL: \(name)") } }

@main struct TipsChecks {
    static func main() {
        let suite = "murmur.check.tips"
        let d = UserDefaults(suiteName: suite)!
        d.removePersistentDomain(forName: suite)
        let day = 86_400.0, start = Date(timeIntervalSince1970: 1_800_000_000)
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!

        // Milestones: the first record seeds silently, then each rung fires once.
        var seeded = 0
        check(Tips.record(words: 10, secondsSaved: 5, now: start, seed: { seeded += 1; return (30_000, 3_600, start.addingTimeInterval(-30 * day)) }, d) == nil, "an existing install's total seeds without a milestone")
        check(seeded == 1 && Tips.rung(d) == 25_000 && Tips.lifetimeWords(d) == 30_010, "seed sets the total and the rung already passed")
        check(Tips.homeMilestone(d) == nil, "a seeded rung is not shown on Home either")
        check(Tips.daysUsed(now: start, d) == 30, "days used start at the first History day")
        check(Tips.record(words: 5, secondsSaved: 1, now: start, seed: { seeded += 1; return (0, 0, nil) }, d) == nil && seeded == 1, "the seed runs once")
        let m = Tips.record(words: 20_000, secondsSaved: 10, now: start, seed: { (0, 0, nil) }, d)
        check(m == Tips.Milestone(words: 50_000, secondsSaved: 3_616), "crossing 50k reports 50k with the lifetime seconds saved")
        check(Tips.record(words: 1, secondsSaved: 0, now: start, seed: { (0, 0, nil) }, d) == nil, "a rung fires once")
        check(Tips.record(words: 10, secondsSaved: -500, now: start, seed: { (0, 0, nil) }, d) == nil && Tips.lifetimeSecondsSaved(d) >= 0, "seconds saved never go negative")
        check(Tips.homeMilestone(d) == 50_000, "Home keeps the latest milestone")
        Tips.dismissHome(d); check(Tips.homeMilestone(d) == nil, "dismissing Home hides it until the next rung")
        check(Tips.next(after: 50_000) == 100_000 && Tips.next(after: 1_000_000) == nil, "next rung")
        check(Tips.duration(14 * 60) == "14 minutes" && Tips.duration(9_000) == "2.5 hours" && Tips.duration(3_600) == "1 hour", "durations read naturally")
        check(abs(Tips.secondsSaved(final: String(repeating: "x", count: 5 * 52), seconds: 10) - (52 / Insights.typingWPM * 60 - 10)) < 0.001, "seconds saved uses the Insights formula")

        // The cap: one floating tip a day, none after two dismissals, none with tips off.
        let fresh = UserDefaults(suiteName: suite + ".cap")!; fresh.removePersistentDomain(forName: suite + ".cap")
        check(Tips.canShow(.voiceEdit, now: start, calendar: cal, fresh), "a new tip can show")
        Tips.shown(now: start, fresh)
        check(!Tips.canShow(.milestone, now: start.addingTimeInterval(60), calendar: cal, fresh), "one tip a day across tips")
        check(Tips.canShow(.milestone, now: start.addingTimeInterval(day), calendar: cal, fresh), "the next day it can show")
        Tips.dismissed(.voiceEdit, fresh); check(Tips.canShow(.voiceEdit, now: start.addingTimeInterval(2 * day), calendar: cal, fresh), "one dismissal is not final")
        Tips.dismissed(.voiceEdit, fresh); check(!Tips.canShow(.voiceEdit, now: start.addingTimeInterval(3 * day), calendar: cal, fresh), "two dismissals end the tip")
        Tips.completed(.milestone, fresh); check(!Tips.canShow(.milestone, now: start.addingTimeInterval(3 * day), calendar: cal, fresh), "a finished tip does not come back")
        Tips.setEnabled(false, fresh)
        check(!Tips.canShow(.cues, now: start.addingTimeInterval(4 * day), calendar: cal, fresh), "Show tips off stops every tip")
        Tips.reset(fresh)
        check(Tips.canShow(.voiceEdit, now: start.addingTimeInterval(4 * day), calendar: cal, fresh) && Tips.enabled(fresh), "Show tips again restores dismissed tips")

        // Voice-edit signal: a real rewrite, twice in a week.
        check(Tips.isRewrite(initial: "the launch is on friday this week", edited: "The launch moved to Monday next week."), "a rewrite counts")
        check(!Tips.isRewrite(initial: "the launch is on friday this week", edited: "the launch is on Friday this week"), "a small fix does not")
        check(!Tips.isRewrite(initial: "ok sure", edited: "no"), "a very short dictation does not")
        let rw = UserDefaults(suiteName: suite + ".rw")!; rw.removePersistentDomain(forName: suite + ".rw")
        check(!Tips.noteRewrite(now: start, rw), "one rewrite is not enough")
        check(!Tips.noteRewrite(now: start.addingTimeInterval(8 * day), rw), "a week-old rewrite has expired")
        check(Tips.noteRewrite(now: start.addingTimeInterval(9 * day), rw), "two in a week fire")

        // Dots: earned, at most two, cleared on open; the intro stays until finished.
        let dots = UserDefaults(suiteName: suite + ".dots")!; dots.removePersistentDomain(forName: suite + ".dots")
        check(Tips.dots(.init(dictations: 3, apps: 1, learnedWords: 0, daysUsed: 0), dots).isEmpty, "no dots on day one")
        let busy = Tips.Usage(dictations: 40, apps: 3, learnedWords: 2, daysUsed: 9)
        check(Tips.dots(busy, dots) == [.modes, .dictionary], "at most two dots, in order")
        Tips.opened(.modes, dots)
        check(Tips.dots(busy, dots) == [.dictionary, .cues], "opening a pane clears its dot and the next one appears")
        check(Tips.eligible(busy, dots).contains(.modes), "an opened pane keeps its intro until finished")
        Tips.completed(.modes, dots); check(!Tips.eligible(busy, dots).contains(.modes), "Got it ends the intro")
        Tips.setEnabled(false, dots); check(Tips.dots(busy, dots).isEmpty && Tips.eligible(busy, dots).isEmpty, "Show tips off hides dots")

        for name in [suite, suite + ".cap", suite + ".rw", suite + ".dots"] { UserDefaults.standard.removePersistentDomain(forName: name) }
        if failures == 0 { print("Tips checks passed.") } else { exit(1) }
    }
}
