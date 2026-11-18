// Milestones, the voice-edit tip and the settings explore dots: when each may show, in UserDefaults. Foundation only.
import Foundation

/// The tip ledger. Everything here is counts, dates and IDs, never dictation text. Floating tips share one
/// cap: one a day, none after two dismissals of the same tip, none at all with Show tips off.
enum Tips {
    enum ID: String, CaseIterable { case milestone, voiceEdit, modes, dictionary, cues }

    static let enabledKey = "tips.enabled", lastShownKey = "tips.lastShown", dismissalsKey = "tips.dismissals", doneKey = "tips.done"
    static let wordsKey = "tips.lifetimeWords", savedKey = "tips.lifetimeSecondsSaved", rungKey = "tips.milestoneRung"
    static let homeSeenKey = "tips.homeDismissedRung", rewritesKey = "tips.rewrites", firstUseKey = "tips.firstUse", openedKey = "tips.openedPanes"
    /// Delete All Data removes these too: the counts come from dictations.
    static let allKeys = [enabledKey, lastShownKey, dismissalsKey, doneKey, wordsKey, savedKey, rungKey, homeSeenKey, rewritesKey, firstUseKey, openedKey]

    static func enabled(_ d: UserDefaults = .standard) -> Bool { d.object(forKey: enabledKey) as? Bool ?? true }
    static func setEnabled(_ on: Bool, _ d: UserDefaults = .standard) { d.set(on, forKey: enabledKey) }

    private static func dismissals(_ d: UserDefaults) -> [String: Int] { d.dictionary(forKey: dismissalsKey) as? [String: Int] ?? [:] }
    static func isDone(_ id: ID, _ d: UserDefaults = .standard) -> Bool { (d.stringArray(forKey: doneKey) ?? []).contains(id.rawValue) }

    static func canShow(_ id: ID, now: Date, calendar: Calendar = .current, _ d: UserDefaults = .standard) -> Bool {
        guard enabled(d), !isDone(id, d), dismissals(d)[id.rawValue, default: 0] < 2 else { return false }
        if let last = d.object(forKey: lastShownKey) as? Date, calendar.isDate(last, inSameDayAs: now) { return false }
        return true
    }
    static func shown(now: Date, _ d: UserDefaults = .standard) { d.set(now, forKey: lastShownKey) }
    static func dismissed(_ id: ID, _ d: UserDefaults = .standard) {
        var all = dismissals(d); all[id.rawValue, default: 0] += 1; d.set(all, forKey: dismissalsKey)
    }
    static func completed(_ id: ID, _ d: UserDefaults = .standard) {
        let done = d.stringArray(forKey: doneKey) ?? []
        if !done.contains(id.rawValue) { d.set(done + [id.rawValue], forKey: doneKey) }
    }
    /// "Show tips again": dismissed and finished tips and opened panes come back; the counts stay.
    static func reset(_ d: UserDefaults = .standard) {
        for key in [lastShownKey, dismissalsKey, doneKey, homeSeenKey, openedKey] { d.removeObject(forKey: key) }
        setEnabled(true, d)
    }

    // MARK: Milestones

    static let rungs = [1_000, 5_000, 10_000, 25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000]
    struct Milestone: Equatable { let words: Int; let secondsSaved: Double }

    static func lifetimeWords(_ d: UserDefaults = .standard) -> Int { d.integer(forKey: wordsKey) }
    static func lifetimeSecondsSaved(_ d: UserDefaults = .standard) -> Double { d.double(forKey: savedKey) }
    /// The highest rung reached, 0 before the first.
    static func rung(_ d: UserDefaults = .standard) -> Int { d.integer(forKey: rungKey) }

    /// Counts one inserted dictation. The first call seeds the totals from History (`seed`) without a
    /// milestone, so an install that already has thousands of words does not get a burst of them.
    /// History is capped and expires, so these totals are kept here and only ever grow.
    static func record(words: Int, secondsSaved: Double, now: Date, seed: () -> (words: Int, secondsSaved: Double, firstUse: Date?), _ d: UserDefaults = .standard) -> Milestone? {
        if d.object(forKey: wordsKey) == nil {
            let start = seed()
            d.set(start.words, forKey: wordsKey); d.set(max(0, start.secondsSaved), forKey: savedKey)
            let passed = rungs.last { $0 <= start.words } ?? 0
            d.set(passed, forKey: rungKey); d.set(passed, forKey: homeSeenKey)
            d.set(start.firstUse ?? now, forKey: firstUseKey)
        }
        let total = lifetimeWords(d) + max(0, words), saved = lifetimeSecondsSaved(d) + secondsSaved
        d.set(total, forKey: wordsKey); d.set(max(0, saved), forKey: savedKey)
        guard let reached = rungs.last(where: { $0 <= total }), reached > rung(d) else { return nil }
        d.set(reached, forKey: rungKey)
        return Milestone(words: reached, secondsSaved: max(0, saved))
    }
    /// Seconds saved by one dictation against typing it, the formula Insights uses.
    static func secondsSaved(final: String, seconds: Double?) -> Double {
        guard let seconds, seconds.isFinite, seconds > 0 else { return 0 }
        return Insights.standardWords(final) / Insights.typingWPM * 60 - seconds
    }
    static func next(after words: Int) -> Int? { rungs.first { $0 > words } }
    /// Home keeps the latest milestone until it is dismissed there.
    static func homeMilestone(_ d: UserDefaults = .standard) -> Int? {
        let reached = rung(d)
        return enabled(d) && reached > 0 && d.integer(forKey: homeSeenKey) < reached ? reached : nil
    }
    static func dismissHome(_ d: UserDefaults = .standard) { d.set(rung(d), forKey: homeSeenKey) }

    /// "2.5 hours", "14 minutes": rounded the way a person would say it.
    static func duration(_ seconds: Double) -> String {
        let minutes = Int((seconds / 60).rounded())
        if minutes < 60 { return minutes == 1 ? "1 minute" : "\(minutes) minutes" }
        let hours = (seconds / 3600 * 2).rounded() / 2
        let text = hours == hours.rounded() ? String(Int(hours)) : String(format: "%.1f", hours)
        return hours == 1 ? "1 hour" : "\(text) hours"
    }

    // MARK: Voice-edit tip

    /// The user rewrote a dictation by hand: at least 3 words and 30% of it replaced, removed or added.
    static func isRewrite(initial: String, edited: String) -> Bool {
        let count = Insights.words(initial).count
        guard count >= 4 else { return false }
        return Insights.wordsChanged(raw: initial, final: edited) >= max(3, Int((Double(count) * 0.3).rounded(.up)))
    }
    /// Notes a rewrite; true once there were two within a week, the point where the tip is worth a pause.
    static func noteRewrite(now: Date, _ d: UserDefaults = .standard) -> Bool {
        let week = now.addingTimeInterval(-7 * 86_400)
        let recent = ((d.array(forKey: rewritesKey) as? [Date]) ?? []).filter { $0 > week } + [now]
        d.set(recent, forKey: rewritesKey)
        return recent.count >= 2
    }

    // MARK: Explore dots

    struct Usage { var dictations: Int; var apps: Int; var learnedWords: Int; var daysUsed: Int }
    private static let dotPanes: [(ID, SettingsPane)] = [(.modes, .modes), (.dictionary, .dictionary), (.cues, .cues)]

    /// Panes worth a look, earned from use and never all on day one.
    static func eligible(_ usage: Usage, _ d: UserDefaults = .standard) -> [SettingsPane] {
        guard enabled(d) else { return [] }
        return dotPanes.filter { id, _ in
            guard !isDone(id, d) else { return false }
            switch id {
            case .modes: return usage.dictations >= 20 && usage.apps >= 2
            case .dictionary: return usage.learnedWords >= 1
            case .cues: return usage.daysUsed >= 7
            default: return false
            }
        }.map(\.1)
    }
    /// At most two dots, on panes not opened yet; a group shows a dot while one of its tabs has one.
    static func dots(_ usage: Usage, _ d: UserDefaults = .standard) -> Set<SettingsPane> {
        let opened = Set(d.array(forKey: openedKey) as? [Int] ?? [])
        return Set(eligible(usage, d).filter { !opened.contains($0.rawValue) }.prefix(2))
    }
    static func opened(_ pane: SettingsPane, _ d: UserDefaults = .standard) {
        let opened = d.array(forKey: openedKey) as? [Int] ?? []
        if !opened.contains(pane.rawValue) { d.set(opened + [pane.rawValue], forKey: openedKey) }
    }
    static func id(for pane: SettingsPane) -> ID? { dotPanes.first { $0.1 == pane }?.0 }
    static func daysUsed(now: Date, _ d: UserDefaults = .standard) -> Int {
        guard let first = d.object(forKey: firstUseKey) as? Date else { return 0 }
        return Int(now.timeIntervalSince(first) / 86_400)
    }
}
