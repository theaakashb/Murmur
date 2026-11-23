// deps: AudioFormat Retention CleanupTypes CleanupText CleanupGuard RuleCleaner CleanupPrompt Cleanup CallApps Meetings MeetingNotes MeetingSpeakers MeetingExport
import Foundation

@main struct Gen {
    struct Corpus: Decodable {
        struct Action: Decodable { let lines: [Int]; let owner: String? }
        struct Entry: Decodable { let id: String; let myNotes: String; let lines: [[String]]; let decisions: [[Int]]; let actions: [Action]; let traps: [String] }
        let meetings: [Entry]
    }
    static func main() async throws {
        var result: [String: [String: Any]] = [:]
        for file in ["meeting-notes-corpus", "meeting-notes-heldout"] {
            let corpus = try JSONDecoder().decode(Corpus.self, from: Data(contentsOf: URL(fileURLWithPath: "app/\(file).json")))
            for entry in corpus.meetings {
                var meeting = Meeting(id: "FD2302D7-E82D-430A-BC28-A2B670CE0972", recordedAt: Date(timeIntervalSinceReferenceDate: 0), source: .everything)
                meeting.myNotes = entry.myNotes
                for (index, line) in entry.lines.enumerated() {
                    let start = Double(index * 20)
                    meeting.insert(stream: line[0] == "me" ? .me : .them, start: start, end: start + 10, text: line[1])
                }
                let draft = Draft(points: entry.lines.enumerated().map { .init(text: $0.element[1], lines: [$0.offset + 1]) }
                                  + entry.traps.map { .init(text: $0, lines: [entry.lines.count + 1]) },
                                  decisions: entry.decisions.map { .init(text: entry.lines[$0[0] - 1][1], lines: $0) },
                                  actions: entry.actions.map { .init(text: entry.lines[$0.lines[0] - 1][1], lines: $0.lines, owner: $0.owner) })
                // Grounding directly: the item caps would cut the traps before grounding, and this file tests grounding.
                let numbered = MeetingNotesPrompt.number(meeting)
                let whole = NotesGrounding.source(numbered, myNotes: meeting.myNotes)
                var reasons: [NotesGrounding.Reason: Int] = [:]
                let grounded = MeetingNotesWriter.ground(Draft.parse(draft.text)!, in: whole, reasons: &reasons)
                result[entry.id] = [
                    "points": grounded.points.map(\.lines), "decisions": grounded.decisions.map(\.lines),
                    "actions": grounded.actions.map(\.lines), "dropped": reasons.values.reduce(0, +),
                    "reasons": Dictionary(uniqueKeysWithValues: reasons.map { ($0.key.rawValue, $0.value) })
                ]
            }
        }
        let data = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
