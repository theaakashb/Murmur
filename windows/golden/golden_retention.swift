// deps: Retention HistoryRouter
import Foundation

// Golden outputs of the pure parts of Retention.swift and HistoryRouter.swift over synthetic data: retention cutoffs (UTC calendar),
// the history cap plan, DataBackup.merged and isMachineSetting, the destination key set, and fixed wording. One case per line.
// Run from the repository root.
struct Item: RetainedHistoryItem { let id: String, recordedAt: Date }
struct CutoffCase: Encodable { var retention: String, now: Double, cutoff: Double?, label: String }
struct PlanCase: Encodable {
    var retention: String, ages: [Double], sizes: [Int64], other: Int64, cap: Int64
    var dropped: [Int], status: String, used: Int64, warning: String?
}
struct MergeCase: Encodable { var backup: [String: String], current: [String: String], merged: [String: String] }
struct MachineCase: Encodable { var key: String, machine: Bool }
struct SummaryCase: Encodable { var mode: String, count: Int, sessionOnly: Int, text: String }
struct WipeMessage: Encodable { var failures: [String], steps: [String], succeeded: Bool, text: String }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

@main struct Gen {
    static func main() throws {
        var utc = Calendar(identifier: .gregorian); utc.timeZone = TimeZone(secondsFromGMT: 0)!
        var cutoffs: [CutoffCase] = []
        let nows: [(String, Double)] = [("2026-08-31", 1_788_134_400), ("2026-03-31", 1_774_915_200), ("2028-08-31", 1_851_206_400), ("2026-12-31 23:59:59", 1_798_761_599),
                                        ("2026-10-15", 1_792_022_400), ("2027-02-28", 1_803_427_200), ("2026-07-31 06:30", 1_785_479_400)]
        for (label, seconds) in nows {
            let now = Date(timeIntervalSince1970: seconds)
            for retention in HistoryRetention.allCases {
                cutoffs.append(CutoffCase(retention: retention.rawValue, now: seconds, cutoff: retention.cutoff(now: now, calendar: utc)?.timeIntervalSince1970, label: label))
            }
        }

        var plans: [PlanCase] = []
        func plan(_ retention: HistoryRetention, ages: [Double], sizes: [Int64], other: Int64 = 0, cap: Int64) {
            let base = Date(timeIntervalSince1970: 1_790_000_000)
            // Ids are fixed per position so dropped entries are reported by index.
            let entries = ages.enumerated().map { Item(id: String(format: "00000000-0000-0000-0000-%012d", $0.offset), recordedAt: base.addingTimeInterval(-$0.element)) }
            var map: [String: Int64] = [:]; for (i, e) in entries.enumerated() { map[e.id] = sizes[i] }
            let result = HistoryCap.plan(entries: entries, sizes: map, otherBytes: other, retention: retention, capBytes: cap)
            let status: String
            switch result.status { case .withinCap: status = "within"; case .approachingCap: status = "approaching"; case .overCap: status = "over" }
            plans.append(PlanCase(retention: retention.rawValue, ages: ages, sizes: sizes, other: other, cap: cap,
                                  dropped: result.dropped.map { d in entries.firstIndex { e in e.id == d.id }! }, status: status, used: result.status.usedBytes, warning: result.status.warning))
        }
        plan(.thirtyDays, ages: [300, 200, 100], sizes: [40, 40, 40], cap: 100)
        plan(.thirtyDays, ages: [100, 200, 300], sizes: [40, 40, 40], cap: 100)
        plan(.thirtyDays, ages: [10], sizes: [500], cap: 100)
        plan(.sixMonths, ages: [10, 20, 30, 40], sizes: [30, 30, 30, 30], other: 15, cap: 100)
        plan(.sixMonths, ages: [], sizes: [], other: 150, cap: 100)
        plan(.thirtyDays, ages: [5, 4], sizes: [60, 60], cap: 120)
        plan(.thirtyDays, ages: [5, 4], sizes: [60, 61], cap: 120)
        plan(.forever, ages: [5, 4], sizes: [40, 40], cap: 100)
        plan(.forever, ages: [5, 4], sizes: [45, 45], cap: 100)
        plan(.forever, ages: [5, 4], sizes: [46, 45], cap: 100)
        plan(.forever, ages: [5, 4], sizes: [50, 51], cap: 100)
        plan(.forever, ages: [1], sizes: [43_000_000], other: 5_000_000, cap: HistoryCap.defaultCapBytes)
        plan(.forever, ages: [1], sizes: [60_000_000], cap: HistoryCap.defaultCapBytes)
        plan(.thirtyDays, ages: [9, 8, 7, 6], sizes: [20, 20, 20, 20], cap: 50)
        let megabytes = [0, 1, 1_048_575, 1_048_576, 1_572_864, 52_428_800, 47_185_920, 123_456_789].map { HistoryCap.megabytes(Int64($0)) }

        var merges: [MergeCase] = []
        func merge(_ backup: [String: String], _ current: [String: String]) {
            merges.append(MergeCase(backup: backup, current: current, merged: DataBackup.merged(backup: backup, current: current) as! [String: String]))
        }
        merge(["liveWords": "true", "retention": "forever"], ["liveWords": "false", "retention": "30-days"])
        merge(["liveWords": "true", "modelDirectory": "/other", "inputDeviceUID": "other"], ["modelDirectory": "/mine"])
        merge(["customAPIKeyEndpoint.x": "https://a.example", "customAPIKeyEndpoint": "https://a.example"], ["customAPIKeyEndpoint.x": "https://b.example"])
        merge(["cleanupChoice": "custom-api", "customAPIBaseURL": "https://other.example/v1", "transcriptionRoute": "custom", "cloudTranscriber": "x", "localServerURL": "http://x.example"],
              ["cleanupChoice": "local-rules"])
        merge(["cleanupRoute": "cloud", "customTranscriptionAPIBaseURL": "https://t.example"], [:])
        merge(["SUEnableAutomaticChecks": "1", "NSWindow Frame Main": "1 2 3 4", "shortcut": "custom:1:2:K"], ["SUEnableAutomaticChecks": "0"])
        merge([:], ["shortcut": "right-option", "transcriptionRoute": "local"])
        let keys = ["modelDirectory", "inputDeviceUID", "transcriptionRoute", "cloudTranscriber", "cleanupChoice", "cleanupRoute", "customAPIBaseURL",
                    "customTranscriptionAPIBaseURL", "localServerURL", "customAPIKeyEndpoint", "customAPIKeyEndpoint.murmur-cloud", "SUEnableAutomaticChecks", "SUFoo",
                    "NSWindow Frame Settings", "liveWords", "customAPIModel", "customTranscriptionModel", "customAPIReasoningEffort", "su", "shortcut", "retention", "writingModes", ""]
        let machine = keys.map { MachineCase(key: $0, machine: DataBackup.isMachineSetting($0)) }

        var summaries: [SummaryCase] = []
        for (mode, name) in [(HistoryMode.on, "on"), (.off, "off")] {
            for (count, memory) in [(0, 0), (1, 0), (2, 0), (1, 1), (5, 3), (0, 2)] {
                summaries.append(SummaryCase(mode: name, count: count, sessionOnly: memory, text: HistoryText.summary(mode: mode, count: count, sessionOnly: memory)))
            }
        }
        var partial = DeleteAllReport(); partial.failures = ["Could not delete history: denied"]
        let wipes = [
            WipeMessage(failures: [], steps: [], succeeded: true, text: PostWipeStep.message(report: DeleteAllReport(), failedSteps: []).text),
            WipeMessage(failures: partial.failures, steps: ["recreateHistory", "refreshViews"], succeeded: false, text: PostWipeStep.message(report: partial, failedSteps: [.recreateHistory, .refreshViews]).text),
            WipeMessage(failures: [], steps: ["reloadDictionary"], succeeded: false, text: PostWipeStep.message(report: DeleteAllReport(), failedSteps: [.reloadDictionary]).text),
        ]

        let fixed: [String: String] = [
            "offDisclosure": HistoryMode.offDisclosure, "retryWindow": RetryWindow.text, "savedOn": HistoryText.saved(persists: true), "savedOff": HistoryText.saved(persists: false),
            "sessionOnlyNote": HistoryText.sessionOnlyNote, "labels": HistoryRetention.allCases.map(\.label).joined(separator: "|"),
            "raws": HistoryRetention.allCases.map(\.rawValue).joined(separator: "|"), "destination": DataBackup.destinationSettings.sorted().joined(separator: "|"),
            "warningFraction": String(HistoryCap.warningFraction), "cap": String(HistoryCap.defaultCapBytes), "lifetime": String(DiagnosticRecordings.lifetime),
            "retryLifetime": String(RetryWindow.lifetime), "recordings": DiagnosticRecordings.folderName, "steps": PostWipeStep.allCases.map(\.rawValue).joined(separator: "|"),
            "stepLabels": PostWipeStep.allCases.map(\.label).joined(separator: "|"),
        ]
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let fixedLine = String(decoding: try encoder.encode(fixed), as: UTF8.self)
        let megabyteLine = String(decoding: try encoder.encode(megabytes), as: UTF8.self)

        print("{\n" + [lines("cutoffs", cutoffs), lines("plans", plans), lines("merges", merges), lines("machine", machine), lines("summaries", summaries),
                       lines("wipes", wipes), "\"megabytes\": \(megabyteLine)", "\"fixed\": \(fixedLine)"].joined(separator: ",\n") + "\n}")
    }
}
