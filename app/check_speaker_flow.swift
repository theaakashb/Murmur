// Speaker separation W7d through the real MeetingRecorder (PRD 11.8, 11.10): a stand-in helper
// script answers like `ModelBench --diarize`. No capture, no models, no network.
import AppKit
import Foundation

nonisolated(unsafe) var failures = 0
func check(_ condition: @autoclosure () throws -> Bool, _ name: String) {
    if (try? condition()) == true { print("ok   \(name)") } else { failures += 1; print("FAIL \(name)") }
}

@MainActor func spin(until done: () -> Bool, seconds: Double = 10) {
    let deadline = Date().addingTimeInterval(seconds)
    while !done() && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
}

@main enum SpeakerFlowChecks {
    @MainActor static func main() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("murmur-speaker-flow-\(UUID().uuidString)", isDirectory: true)
        defer { try? fm.removeItem(at: base) }
        let models = base.appendingPathComponent("models", isDirectory: true)
        try fm.createDirectory(at: models, withIntermediateDirectories: true)
        let helper = base.appendingPathComponent("helper")
        try "#!/bin/sh\n[ \"$2\" = --diarize ] && [ -f \"$3\" ] && echo '{\"segments\":[{\"speaker\":\"x\",\"start\":0,\"end\":5},{\"speaker\":\"y\",\"start\":5,\"end\":10}]}'\n"
            .write(to: helper, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
        let store = try MeetingStore(directory: base.appendingPathComponent("history/meetings"))
        let audioRoot = base.appendingPathComponent("retry-audio/meetings", isDirectory: true)
        let speakerRoot = base.appendingPathComponent("retry-audio/speakers", isDirectory: true)

        /// A recorder's one wiring; each check passes only what it exercises.
        func wiring(store: MeetingStore? = nil, audioRoot: URL? = nil, transcribe: @escaping (URL, @escaping (String?) -> Void) -> Void = { _, done in done(nil) },
                    notesRoute: @escaping () -> MeetingNotesRoute? = { nil },
                    notesWriter: @escaping (Meeting, Bool) async throws -> MeetingNotesWriter.Outcome = { _, _ in throw NotesFailure.failed("unused") },
                    verify: @escaping () -> Bool = { false }) -> MeetingRecorder.Wiring {
            .init(store: store, audioRoot: audioRoot, persists: true, applyDictionary: { $0 }, notesRoute: notesRoute, notesWriter: notesWriter, verifySpeakerModel: verify,
                  local: .init(transcribe: transcribe, transcribeTimed: { _, done in done(nil) }, helper: helper))
        }

        /// A finished meeting waiting for separation, with its kept call audio.
        func pending(days: Double = 0, audio: Bool = true) throws -> Meeting {
            var meeting = Meeting(id: UUID().uuidString, recordedAt: Date().addingTimeInterval(-days * 86400), source: MeetingSource(name: "Zoom", bundleID: "us.zoom.xos"))
            meeting.speakers = SpeakerSeparation(state: .pending, threshold: MeetingSpeakers.threshold)
            meeting.insert(stream: .them, start: 0, end: 5, text: "First voice.")
            meeting.insert(stream: .them, start: 5, end: 10, text: "Second voice.")
            meeting.finished = true
            try store.save(meeting)
            if audio { try SpeakerAudio.write([Int16](repeating: 1, count: 160_000), at: 0, to: SpeakerAudio.file(speakerRoot, meeting: meeting.id)) }
            return meeting
        }
        func kept(_ m: Meeting) -> Bool { fm.fileExists(atPath: SpeakerAudio.file(speakerRoot, meeting: m.id).path) }

        let roomDefaults = UserDefaults(suiteName: "murmur-room-setting-\(UUID().uuidString)")!
        let roomSettings = SpeakerModelSettings(defaults: roomDefaults)
        check(!roomSettings.separateRoom, "microphone retention is off by default")
        roomSettings.separateRoom = true
        check(SpeakerModelSettings(defaults: roomDefaults).separateRoom, "room separation opt-in persists")

        let recorder = MeetingRecorder(defaults: UserDefaults(suiteName: "murmur-speaker-flow-\(UUID().uuidString)")!)
        check(!recorder.hasPendingUpdateWork, "idle meetings allow application updates")
        // Notes are written by the real writer; the stand-in model records what it was sent.
        var prompts: [String: String] = [:], states: [String: SpeakerSeparation.State?] = [:]
        let writer: (Meeting, Bool) async throws -> MeetingNotesWriter.Outcome = { meeting, _ in
            states[meeting.id] = meeting.speakers?.state
            return try await MeetingNotesWriter.write(meeting, route: .customAPI, routeName: "Stand-in") { _, user in
                prompts[meeting.id] = user; return "POINT | L1 | First voice"
            }
        }
        func notes(_ m: Meeting) -> MeetingNotes? { recorder.meetings.first { $0.id == m.id }?.notes }

        // Without the model yet (still verifying at launch), a pending meeting waits with its audio.
        let waiting = try pending()
        recorder.wire(wiring(store: store, audioRoot: audioRoot, notesRoute: { .customAPI }, notesWriter: writer, verify: { true }))
        check(recorder.meetings.first { $0.id == waiting.id }?.speakers?.state == .pending && kept(waiting), "without the model, separation waits and the audio stays")
        recorder.writeNotes(waiting.id)
        spin(until: { false }, seconds: 0.3)
        check(states[waiting.id] == nil && !recorder.notesWriting.contains(waiting.id) && notes(waiting) == nil, "no notes while speakers wait to be separated, even on Write Notes")

        var room = Meeting(id: UUID().uuidString, recordedAt: Date(), source: .microphoneOnly)
        room.speakers = SpeakerSeparation(state: .pending, threshold: MeetingSpeakers.threshold)
        room.insert(stream: .me, start: 0, end: 5, text: "First voice.")
        room.insert(stream: .me, start: 5, end: 10, text: "Second voice.")
        room.finished = true
        try store.save(room)
        try SpeakerAudio.write([Int16](repeating: 1, count: 160_000), at: 0, to: SpeakerAudio.file(speakerRoot, meeting: room.id))

        // With the model: separation runs, labels apply and saved, the audio is deleted.
        recorder.speakerModel = models
        recorder.reload()
        check(recorder.hasPendingUpdateWork, "speaker separation postpones application updates after Stop")
        spin { recorder.meetings.first { $0.id == waiting.id }?.speakers?.state != .pending }
        let done = recorder.meetings.first { $0.id == waiting.id }
        check(done?.speakers?.state == .done && done?.lines.map(\.speaker) == [1, 2], "the helper's segments label the lines")
        check(!kept(waiting), "the call audio is deleted once diarization finishes")
        spin { recorder.meetings.first { $0.id == room.id }?.speakers?.state != .pending }
        let roomDone = recorder.meetings.first { $0.id == room.id }
        check(roomDone?.lines.map(\.speaker) == [1, 2] && roomDone?.lines.allSatisfy { $0.stream == .me } == true, "room audio labels microphone lines")
        check(!kept(room), "room audio is deleted after separation")
        spin { notes(room) != nil }
        check(prompts[room.id]?.contains("[L1 Speaker 1 0:00]") == true, "room speaker labels reach notes")
        spin { notes(done!) != nil }
        check(states[waiting.id] == .done && prompts[waiting.id]?.contains("[L1 Speaker 1 0:00] First voice.") == true && prompts[waiting.id]?.contains("[L2 Speaker 2 0:05]") == true && notes(waiting)?.keyPoints.count == 1, "notes are written after separation, with speaker labels")
        check(!recorder.hasPendingUpdateWork, "application updates resume after separation and notes finish")
        recorder.writeNotes(waiting.id)
        check(recorder.hasPendingUpdateWork, "writing notes postpones application updates after Stop")
        spin { !recorder.hasPendingUpdateWork }
        check(!recorder.hasPendingUpdateWork && !recorder.notesWriting.contains(waiting.id), "application updates resume after rewritten notes finish")
        check((try store.meetings().first { $0.id == waiting.id })?.lines.map(\.speaker) == [1, 2], "the labels are saved with the meeting")
        recorder.renameSpeaker(2, to: "Priya", in: waiting.id)
        check((try store.meetings().first { $0.id == waiting.id })?.speakers?.names[2] == "Priya", "a rename is saved")

        // A missing kept file fails cleanly (Them labels kept).
        let missing = try pending(audio: false)
        recorder.reload()
        spin { recorder.meetings.first { $0.id == missing.id }?.speakers?.state != .pending }
        check(recorder.meetings.first { $0.id == missing.id }?.speakers?.state == .failed, "no kept audio: separation fails, the transcript stays")
        spin { notes(missing) != nil }
        check(states[missing.id] == .failed && prompts[missing.id]?.contains("[L1 Them 0:00]") == true, "after a failed separation, notes are written with Me and Them")

        // Skip, delete and retention each remove the audio. Without a model nothing runs, so each acts on a waiting meeting.
        recorder.speakerModel = nil
        let skipped = try pending(), deleted = try pending(), expired = try pending(days: 40)
        recorder.reload()
        recorder.skipSeparation(skipped.id)
        check(recorder.meetings.first { $0.id == skipped.id }?.speakers?.state == .skipped && !kept(skipped), "Skip marks the meeting and deletes its audio")
        spin { notes(skipped) != nil }
        check(states[skipped.id] == .skipped && prompts[skipped.id]?.contains("[L2 Them 0:05]") == true && states[deleted.id] == nil, "Skip writes the notes then, with Them; meetings still waiting have none")
        recorder.delete(deleted.id)
        check(!kept(deleted) && recorder.meetings.allSatisfy { $0.id != deleted.id }, "deleting a meeting deletes its kept audio")
        recorder.purge(cutoff: Date().addingTimeInterval(-30 * 86400))
        check(!kept(expired) && recorder.meetings.allSatisfy { $0.id != expired.id }, "retention deletes the meeting and its kept audio")

        let bulkA = try pending(), bulkB = try pending()
        recorder.reload()
        check(recorder.delete(Set([bulkA.id, bulkB.id])).isEmpty, "bulk deletion reports success")
        check(!kept(bulkA) && !kept(bulkB) && recorder.meetings.allSatisfy { $0.id != bulkA.id && $0.id != bulkB.id }, "bulk deletion removes only selected meetings and retained audio")
        check(try store.meetings().contains { $0.id == skipped.id }, "bulk deletion preserves unselected meetings")
        check(try store.meetings().allSatisfy { $0.id != bulkA.id && $0.id != bulkB.id }, "bulk deletion removes selected records from disk")
        check(recorder.delete(Set<String>()).isEmpty, "empty bulk deletion is harmless")
        let liveCheck = MeetingRecorder()
        liveCheck.wire(wiring())
        liveCheck.showFixture([waiting], live: waiting)
        check(liveCheck.hasPendingUpdateWork, "live recording postpones application updates")
        check(liveCheck.delete(Set([waiting.id])) == [waiting.id] && liveCheck.live?.id == waiting.id && liveCheck.meetings.count == 1, "bulk deletion refuses a live meeting and returns its ID")

        let recovering = try pending(audio: false)
        let recoveryFolder = MeetingAudio.folder(audioRoot, meeting: recovering.id)
        try fm.createDirectory(at: recoveryFolder, withIntermediateDirectories: true)
        try Data().write(to: recoveryFolder.appendingPathComponent(MeetingAudio.name(.me, start: 0)))
        let recoveryCheck = MeetingRecorder()
        var finishTranscription: ((String?) -> Void)?
        recoveryCheck.wire(wiring(store: store, audioRoot: audioRoot, transcribe: { _, done in finishTranscription = done }))
        recoveryCheck.finishLeftovers(recovering.id)
        check(recoveryCheck.live == nil && recoveryCheck.hasPendingUpdateWork, "final transcription postpones updates without a live meeting")
        spin { finishTranscription != nil }
        finishTranscription?("Recovered final words.")
        spin { !recoveryCheck.hasPendingUpdateWork }
        check(!recoveryCheck.hasPendingUpdateWork && recoveryCheck.finishing.isEmpty, "application updates resume after final transcription completes")

        // Force the store's identifier validation to refuse deletion without touching user files.
        let refused = Meeting(id: "../invalid", recordedAt: Date(), source: .microphoneOnly)
        try Data().write(to: store.directory.deletingLastPathComponent().appendingPathComponent("invalid.json"))
        let failureCheck = MeetingRecorder()
        failureCheck.wire(wiring(store: store))
        failureCheck.showFixture([refused], live: nil, separating: [refused.id])
        check(failureCheck.delete(Set([refused.id])) == [refused.id] && failureCheck.message != nil, "failed bulk deletion returns the remaining ID and reports an error")
        check(failureCheck.separating.contains(refused.id), "failed record deletion does not discard background state")

        // Turning the switch off (or removing the model) skips finished meetings still waiting.
        let abandoned = try pending()
        recorder.reload()
        check(kept(abandoned), "still waiting while separation is wanted")
        recorder.speakersWanted = false
        recorder.reload()
        check(recorder.meetings.first { $0.id == abandoned.id }?.speakers?.state == .skipped && !kept(abandoned), "switch off: waiting meetings are skipped and their audio deleted")

        // Delete All Data stops everything.
        recorder.discardAll()
        check(recorder.separating.isEmpty && recorder.meetings.isEmpty, "Delete All Data forgets separations")

        var saved = waiting
        saved.finished = true
        saved.speakers = nil
        saved.notes = MeetingNotes(summary: "Original meeting", route: "fixture", writtenAt: Date())
        saved.lectureNotes = MeetingNotes(summary: "Original lecture", route: "fixture", writtenAt: Date())
        let preservation = MeetingRecorder()
        preservation.wire(wiring(notesWriter: { _, lecture in
            check(lecture, "recorder forwards lecture selection")
            throw NotesFailure.failed("Synthetic failure")
        }))
        preservation.showFixture([saved], live: nil)
        preservation.writeNotes(saved.id, lecture: true)
        spin(until: { !preservation.notesWriting.contains(saved.id) })
        check(preservation.notesProblem[saved.id]?.contains("Synthetic failure") == true, "lecture failure is reported")
        check(preservation.meetings.first?.notes == saved.notes && preservation.meetings.first?.lectureNotes == saved.lectureNotes, "failed lecture generation preserves both note sets")

        print(failures == 0 ? "All speaker flow checks passed." : "\(failures) speaker flow check(s) failed.")
        exit(failures == 0 ? 0 : 1)
    }
}
