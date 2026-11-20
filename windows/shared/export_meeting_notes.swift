// deps: AudioFormat Retention CleanupTypes CleanupText CleanupGuard RuleCleaner CleanupPrompt Cleanup CallApps Meetings MeetingNotes MeetingSpeakers MeetingExport
import Foundation

@main struct Gen {
    static func main() throws {
        let line = MeetingLine(id: 1, stream: .them, start: 0, end: 1, text: "QZXLINE")
        func system(_ speaker: String, stream: MeetingStream = .them) -> String {
            let line = MeetingLine(id: line.id, stream: stream, start: line.start, end: line.end, text: line.text)
            return MeetingNotesPrompt.extract([.init(number: 1, line: line, speaker: speaker)], myNotes: "", nonce: "QZXNONCE").system
        }
        func template(_ value: String) -> String {
            value.replacingOccurrences(of: "QZXNONCE", with: "{nonce}").replacingOccurrences(of: "QZXSPEAKERS", with: "{speakers}")
        }
        let notes = [
            "prompt": [
                "extract": template(system("Them")), "extractSpeakers": template(system("QZXSPEAKERS")),
                "extractRoom": template(system("Room", stream: .me)),
                "extractRoomSpeakers": template(MeetingNotesPrompt.extract([.init(number: 1, line: .init(id: 1, stream: .me, start: 0, end: 1, text: "", speaker: 1), speaker: "QZXSPEAKERS")], myNotes: "", nonce: "QZXNONCE").system),
                "merge": template(MeetingNotesPrompt.merge([], nonce: "QZXNONCE").system),
                "mergeSpeakers": template(MeetingNotesPrompt.merge([], nonce: "QZXNONCE", speakers: ["QZXSPEAKERS"]).system),
                "lectureRules": MeetingNotesPrompt.lectureRules
            ] as [String: Any],
            "stopwords": NotesGrounding.stopwords.sorted(),
            "addressed": NotesGrounding.addressed,
            "clauseBreaks": NotesGrounding.clauseBreaks.sorted(),
            "opposites": NotesGrounding.opposites.map { [$0.0.sorted(), $0.1.sorted()] },
            "months": NotesGrounding.months.sorted(),
            "dateWords": NotesGrounding.dateWords.sorted(),
            "notNames": NotesGrounding.notNames.sorted(),
            "subjectCues": NotesGrounding.subjectCues.sorted(),
            "units": NotesGrounding.units,
            "scales": NotesGrounding.scales,
            "agreements": NotesGrounding.agreements,
            "commitments": NotesGrounding.commitments
        ] as [String: Any]
        let data = try JSONSerialization.data(withJSONObject: notes, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        print(String(decoding: data, as: UTF8.self))
    }
}
