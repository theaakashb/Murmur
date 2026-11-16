import Foundation

// Meeting export: Markdown, text and JSON renderings of a saved meeting.

// MARK: - Export

enum MeetingExport {
    static func clock(_ seconds: Double) -> String {
        let total = max(0, Int(seconds))
        return total >= 3600 ? String(format: "%d:%02d:%02d", total / 3600, total / 60 % 60, total % 60) : String(format: "%d:%02d", total / 60, total % 60)
    }
    /// Visible lines only: hidden echo is left out of exports.
    static func transcript(_ meeting: Meeting) -> String {
        meeting.lines.filter { !$0.isHidden }.map { "[\(clock($0.start))] \(MeetingSpeakers.label($0, in: meeting)): \($0.visibleText)" }.joined(separator: "\n")
    }
    static func markdown(_ meeting: Meeting) -> String {
        var out = "# \(escape(meeting.title))\n\n"
        out += "- Date: \(ISO8601DateFormatter().string(from: meeting.recordedAt))\n- Length: \(clock(meeting.seconds))\n"
        out += "- Audio: \(meeting.source.micOnly ? "my microphone only" : "my microphone and \(meeting.source.name)")\n"
        out += "- Transcribed: \(meeting.transcribedBy.map { "by \($0)" } ?? "on this Mac")\n"
        if meeting.speakers?.state == .done {
            let names = MeetingSpeakers.numbers(meeting).map { meeting.speakers?.names[$0] ?? "Speaker \($0)" }
            if !names.isEmpty { out += "- Speakers: \(escape(([MeetingStream.me.label] + names).joined(separator: ", ")))\n" }
        }
        if let notes = meeting.notes, !notes.isEmpty { out += "\n## Notes\n\n" + self.notes(meeting, markdown: true) + "\n" }
        if meeting.lectureNotes != nil { out += "\n## Lecture notes\n\n" + self.notes(meeting, markdown: true, lecture: true) + "\n" }
        let mine = meeting.myNotes.trimmingCharacters(in: .whitespacesAndNewlines)
        if !mine.isEmpty { out += "\n## My notes\n\n\(mine)\n" }
        out += "\n## Transcript\n\n"
        for line in meeting.lines where !line.isHidden {
            out += "**\(escape(MeetingSpeakers.label(line, in: meeting)))** [\(clock(line.start))]: \(escape(line.visibleText))\n\n"
        }
        return out
    }
    /// The generated notes (PRD 11.6) for Copy Notes, or the Markdown export's Notes section. Each
    /// item ends with the time of the first line it cites. Model text is escaped like transcript text.
    static func notes(_ meeting: Meeting, markdown: Bool = false, lecture: Bool = false) -> String {
        guard let notes = lecture ? meeting.lectureNotes : meeting.notes else { return "" }
        let times = Dictionary(meeting.lines.map { ($0.id, $0.start) }, uniquingKeysWith: { a, _ in a })
        let text: (String) -> String = markdown ? escape : { $0 }
        func item(_ item: NoteItem, box: Bool) -> String {
            var line = (box ? (item.done == true ? "- [x] " : "- [ ] ") : "- ") + text(item.text)
            var extra: [String] = []
            if let owner = item.owner { extra.append(text(owner)) }
            if let due = item.due { extra.append("due " + text(due)) }
            if !extra.isEmpty { line += " (" + extra.joined(separator: ", ") + ")" }
            if let first = item.lines.compactMap({ times[$0] }).min() { line += " [\(clock(first))]" }
            return line
        }
        var sections: [String] = []
        if !markdown { sections.append(meeting.title) }
        if let summary = notes.summary { sections.append(text(summary)) }
        let heading = markdown ? "### " : ""
        let titles = NoteSections.titles(lecture: lecture)
        if !notes.keyPoints.isEmpty { sections.append(heading + titles.keyPoints + "\n" + notes.keyPoints.map { item($0, box: false) }.joined(separator: "\n")) }
        if !notes.decisions.isEmpty { sections.append(heading + titles.decisions + "\n" + notes.decisions.map { item($0, box: false) }.joined(separator: "\n")) }
        if !notes.actions.isEmpty { sections.append(heading + titles.actions + "\n" + notes.actions.map { item($0, box: !lecture) }.joined(separator: "\n")) }
        sections.append("Written by \(notes.route) from the transcript\(notes.outdated == true ? "; out of date" : "").")
        return sections.joined(separator: "\n\n")
    }
    /// Transcript text is data: a line that starts like Markdown structure stays plain text.
    static func escape(_ text: String) -> String {
        var out = text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "*", with: "\\*")
            .replacingOccurrences(of: "_", with: "\\_").replacingOccurrences(of: "<", with: "\\<")
        if let first = out.first, "#>-+|`".contains(first) { out = "\\" + out }
        return out
    }
    static func fileName(_ meeting: Meeting) -> String {
        let formatter = DateFormatter(); formatter.dateFormat = "yyyy-MM-dd HH.mm"
        return "Meeting \(formatter.string(from: meeting.recordedAt)).md"
    }
}
