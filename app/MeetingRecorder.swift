import AppKit
import AVFoundation
import os


/// Records one meeting at a time: the microphone through a meeting-only Capture,
/// the call's audio through SystemAudioTap, both chunked into one MeetingPump that shares the warm
/// helper with dictation. Lines are saved as they arrive; audio chunks are deleted once transcribed.
@MainActor final class MeetingRecorder: ObservableObject {
    @Published private(set) var live: Meeting?
    /// Finished meetings, newest first (from disk, or memory only with History off).
    @Published private(set) var meetings: [Meeting] = []
    @Published private(set) var meLevel = 0.0
    @Published private(set) var themLevel = 0.0
    @Published private(set) var elapsed: TimeInterval = 0
    @Published private(set) var behind = false
    /// The `MeetingLimits.warning` warning, or a stream that stopped; shown in the live view.
    @Published private(set) var warning: String?
    /// Nothing heard on either stream for `MeetingLimits.askEnded`, or the call app stopped using the microphone.
    @Published private(set) var askEnded = false
    @Published private(set) var finishing: Set<String> = []
    /// Meetings with chunk audio left over (a crash, or a chunk that failed twice).
    @Published private(set) var leftovers: [String: Int] = [:]
    @Published var consentPending: MeetingSource?
    /// Why the call's audio could not be recorded; the view offers "Record only my microphone".
    @Published var systemAudioProblem: String?
    @Published var message: String?
    /// Meetings whose last write failed. Their newest text is kept in memory and written again by the
    /// next tick and at quit. Always empty with History off, which is not a failure.
    @Published private(set) var unsaved: Set<String> = []
    static let unsavedWarning = "A meeting could not be saved to disk. Keep Murmur open and free up space; Murmur will keep trying. Copy the notes or transcript now if you need them."
    @Published private(set) var notesWriting: Set<String> = []
    /// Why notes could not be written, per meeting; the transcript is kept either way.
    @Published var notesProblem: [String: String] = [:]
    /// A meeting waiting for the first-Stop notes route question.
    @Published var notesChoicePending: String?

    /// What the recorder cannot work without, given once to `wire(_:)`: leaving one out is a compile
    /// error. Only the hooks (`onLiveChange`, `onNotesChoiceNeeded`) are optional.
    struct Wiring {
        /// `history/meetings`, nil when private storage could not be opened.
        var store: MeetingStore?
        /// `retry-audio/meetings`.
        var audioRoot: URL?
        var persists: Bool
        var applyDictionary: (String) -> String
        /// The notes route in Settings, nil until the first Stop asks.
        var notesRoute: () -> MeetingNotesRoute?
        /// Writes notes with the route chosen now. Throws `NotesFailure`.
        var notesWriter: (Meeting, Bool) async throws -> MeetingNotesWriter.Outcome
        /// Checks the speaker model folder against its pins (cached by fingerprint); runs off the main thread.
        var verifySpeakerModel: () -> Bool
        /// nil when this Mac cannot transcribe (the fixture, or the helper or manifest is missing): `start` refuses.
        var local: LocalTranscription?
    }

    /// Transcription on this Mac with the warm helper, which also runs diarization.
    struct LocalTranscription {
        var transcribe: (URL, @escaping (String?) -> Void) -> Void
        /// A Them chunk with word times (the warm helper's timed request).
        var transcribeTimed: (URL, @escaping (ChunkText?) -> Void) -> Void
        /// The ModelBench helper.
        var helper: URL
    }

    // Set only by `wire(_:)`; the defaults are the unwired state (the UI fixture).
    private var applyDictionary: (String) -> String = { $0 }
    private var notesRoute: () -> MeetingNotesRoute? = { nil }
    private var notesWriter: ((Meeting, Bool) async throws -> MeetingNotesWriter.Outcome)?
    private(set) var verifySpeakerModel: () -> Bool = { false }
    private var local: LocalTranscription?
    /// Called when the notes question waits after a Stop the user clicked, to open the Meetings page.
    var onNotesChoiceNeeded: (() -> Void)?
    /// Meetings stopped by the user, whose notes question may open the Meetings page; automatic stops
    /// (cap, silence) only ask the next time the page is opened.
    private var askOnDone: Set<String> = []
    /// Called whenever a meeting starts or stops, for the menu bar and the live panel.
    var onLiveChange: (() -> Void)?
    private(set) var store: MeetingStore?
    private(set) var audioRoot: URL?
    var persists = true
    private let defaults: UserDefaults

    private var capture: Capture?
    private var tap: SystemAudioTap?
    private var pumps: [String: MeetingPump] = [:]
    private var started: TimeInterval = 0
    private var lastVoice: TimeInterval = 0
    private var lastSave: TimeInterval = 0
    private var lastRetry: TimeInterval = 0
    private var lastTapRefresh: TimeInterval = 0
    private var callApp: String?
    private var callWasUsingMic = false
    /// The live meeting's chunk folder, pump and source, for rebuilding a stream.
    private var folder: URL?
    private var livePump: MeetingPump?
    private var meWatch = StreamWatch(now: 0), themWatch = StreamWatch(now: 0)
    private var lastPlayingCheck: TimeInterval = 0
    private var themPlaying = true
    private var meScale = MeterScale(), themScale = MeterScale()
    /// The default output device when the tap was built; a change rebuilds the tap.
    private var outputUID: String?

    /// Set by SpeakerModelSettings: the verified diarizer folder while speaker separation is on; nil otherwise.
    /// Read when a meeting starts, so a change never affects a meeting in progress.
    var speakerModel: URL?
    var separateRoomSpeakers = false
    /// Set by SpeakerModelSettings: false once the switch is off or the model is removed. Finished
    /// meetings still waiting are then skipped, so their call audio is not kept for nothing.
    var speakersWanted = true
    @Published private(set) var separating: Set<String> = []
    private let speakerRunner = SpeakerRunner()
    /// `retry-audio/speakers`, beside the chunk folder, so Delete All Data's retry-audio step removes it.
    var speakerRoot: URL? { audioRoot?.deletingLastPathComponent().appendingPathComponent("speakers", isDirectory: true) }

    var hasPendingUpdateWork: Bool { live != nil || !finishing.isEmpty || !notesWriting.isEmpty || !separating.isEmpty }

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    /// The one place the recorder is wired (AppController at launch). Points at `history/meetings` and
    /// `retry-audio/meetings`; finishes records a crash left open.
    func wire(_ wiring: Wiring) {
        applyDictionary = wiring.applyDictionary; notesRoute = wiring.notesRoute; notesWriter = wiring.notesWriter
        verifySpeakerModel = wiring.verifySpeakerModel; local = wiring.local
        let store = wiring.store, audioRoot = wiring.audioRoot
        self.store = store; self.audioRoot = audioRoot; self.persists = wiring.persists
        if let audioRoot { try? FileManager.default.createDirectory(at: audioRoot, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]) }
        if let store, let open = try? store.meetings().filter({ !$0.finished }) {
            for var meeting in open where meeting.id != live?.id { meeting.finished = true
                do { try store.save(meeting) } catch { meetingLog.notice("meeting-recovery-save-failed") }
            }
        }
        reload()
    }

    func reload() {
        // With History off, finished meetings live only in memory, until Murmur quits.
        if persists {
            let waiting = meetings.filter { unsaved.contains($0.id) }
            let onDisk = (((try? store?.meetings()) ?? nil) ?? []).filter { $0.id != live?.id && !unsaved.contains($0.id) }
            meetings = (waiting + onDisk).sorted { $0.recordedAt > $1.recordedAt }
        } else { unsaved = [] }
        purgeAudio()
    }

    /// Starts recording, or asks for consent first. Returns false when nothing started.
    @discardableResult
    /// `cloud`: the meeting's cloud transcriber, fixed for this meeting (nil means this Mac).
    func start(_ source: MeetingSource, cloud: MeetingRoute.Cloud? = nil, cloudName: String? = nil) -> Bool {
        guard live == nil else { message = "A meeting is already being recorded."; return false }
        if MeetingConsent.needed(defaults) { consentPending = source; return false }
        guard let audioRoot, local != nil else { message = "Meeting notes are unavailable: private storage could not be opened."; return false }
        var meeting = Meeting(id: UUID().uuidString, recordedAt: Date(), source: source)
        meeting.transcribedBy = cloud == nil ? nil : cloudName
        // Fixed when the meeting starts, like the route: keep the Them stream for diarization.
        if (!source.micOnly || separateRoomSpeakers), speakerModel != nil { meeting.speakers = SpeakerSeparation(state: .pending, threshold: MeetingSpeakers.threshold) }
        let folder = MeetingAudio.folder(audioRoot, meeting: meeting.id)
        do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]) }
        catch { message = "Meeting notes could not create private storage."; return false }
        let pump = makePump(meeting.id, cloud: cloud, timed: meeting.speakers != nil && cloud == nil, timedStream: source.speakerStream)
        started = ProcessInfo.processInfo.systemUptime; lastVoice = started; lastSave = started
        systemAudioProblem = nil; warning = nil; askEnded = false; behind = false; elapsed = 0; message = nil
        callApp = source.bundleID; callWasUsingMic = false
        meWatch = StreamWatch(now: started); themWatch = StreamWatch(now: started)
        meScale = MeterScale(); themScale = MeterScale(); themPlaying = true; lastPlayingCheck = 0
        self.folder = folder; livePump = pump
        live = meeting
        if let error = startMe() {
            live = nil; self.folder = nil; livePump = nil; pumps[meeting.id] = nil; try? FileManager.default.removeItem(at: folder)
            meetingLog.notice("meeting-mic-start-failed error=\(String(describing: error), privacy: .public)")
            message = "The microphone could not start. Check Microphone access in System Settings."
            return false
        }
        if !source.micOnly, let error = startTap(source) {
            systemAudioProblem = "The call's audio could not be recorded (\(error)). Allow Murmur under System Audio Recording in System Settings → Privacy & Security, then start again."
        }
        save(force: true)
        meetingLog.notice("meeting-started mic-only=\(source.micOnly, privacy: .public) all-apps=\(source.allApps, privacy: .public)")
        onLiveChange?()
        return true
    }

    /// Starts (or, after a stall, rebuilds) the microphone stream; its chunks continue the same
    /// stream from the meeting's current time. The old capture is stopped first, which flushes its last chunk.
    private func startMe() -> Error? {
        guard let folder, let pump = livePump else { return CaptureError("No meeting") }
        capture?.stop(); capture = nil
        let me = Capture()
        let feed = MeetingStreamFeed(stream: .me, folder: folder, offset: ProcessInfo.processInfo.systemUptime - started, enqueue: pump.enqueue) { [weak self] in
            DispatchQueue.main.async { self?.streamFailed(.me) }
        }
        do {
            try me.start(url: nil, fixedFormat: SystemAudioTap.format, chunkFolder: folder, current: false)
            _ = me.attachChunks(feed.receive)
            capture = me
            return nil
        } catch { return error }
    }

    /// Starts (or rebuilds) the call's stream: a new tap and aggregate device on the current processes and output device.
    private func startTap(_ source: MeetingSource) -> Error? {
        guard let folder, let pump = livePump else { return CaptureError("No meeting") }
        tap?.stop(); tap = nil
        let clients = AudioProcessList.all().map(\.bundleID)
        let target: TapTarget
        if let id = source.bundleID {
            target = .only(CallApp.resolve(id, name: source.name))
        } else {
            target = .allExcept(CallApp.ownBundleIDs(main: Bundle.main.bundleIdentifier, running: clients))
        }
        let tap = SystemAudioTap(chunkFolder: folder)
        let feed = MeetingStreamFeed(stream: .them, folder: folder, offset: ProcessInfo.processInfo.systemUptime - started, enqueue: pump.enqueue) { [weak self] in
            DispatchQueue.main.async { self?.streamFailed(.them) }
        }
        tap.attachChunks(feed.receive)
        outputUID = AudioProcessList.defaultOutputUID()
        do { try tap.start(target); self.tap = tap; return nil } catch { return error }
    }

    /// Continues with the microphone only after the call's audio failed.
    func continueWithMicrophoneOnly() {
        guard var meeting = live else { return }
        meeting.source.micOnly = true; meeting.speakers = nil; live = meeting; systemAudioProblem = nil; save(force: true)
        deleteKeptAudio(meeting.id)
    }

    /// Stops capture; the last chunks are still transcribed into the saved meeting.
    func stop() { finish(manual: true) }

    private func finish(manual: Bool) {
        guard var meeting = live else { return }
        capture?.stop(); tap?.stop(); capture = nil; tap = nil; folder = nil; livePump = nil
        meeting.seconds = max(meeting.seconds, ProcessInfo.processInfo.systemUptime - started)
        meeting.finished = true
        live = nil
        meetings.insert(meeting, at: 0)
        write(meeting)
        if manual { askOnDone.insert(meeting.id) }
        if pumps[meeting.id]?.isIdle == false { finishing.insert(meeting.id) } else { pumps[meeting.id] = nil; separate(meeting.id) }
        askEnded = false; warning = nil; meLevel = 0; themLevel = 0
        meetingLog.notice("meeting-stopped seconds=\(Int(meeting.seconds))")
        onLiveChange?(); refreshLeftovers()
        if pumps[meeting.id] == nil { transcriptDone(meeting.id) }
    }

    func cancel() {
        guard let meeting = live else { return }
        capture?.stop(); tap?.stop(); capture = nil; tap = nil; folder = nil; livePump = nil
        pumps.removeValue(forKey: meeting.id)?.stop()
        if let audioRoot { try? FileManager.default.removeItem(at: MeetingAudio.folder(audioRoot, meeting: meeting.id)) }
        deleteKeptAudio(meeting.id)
        unsaved.remove(meeting.id); erase(meeting.id)
        live = nil; askEnded = false; warning = nil; systemAudioProblem = nil
        onLiveChange?()
    }

    /// Delete All Data: stop everything and forget every meeting in memory.
    func discardAll() {
        cancel()
        pumps.values.forEach { $0.stop() }; pumps = [:]
        speakerRunner.cancelAll(); separating = []
        meetings = []; finishing = []; leftovers = [:]; unsaved = []; store = nil; audioRoot = nil
        notesProblem = [:]; notesChoicePending = nil; askOnDone = []
    }

    /// Leftovers are always finished on this Mac (`cloud` nil): nothing falls back to the cloud.
    /// `timed`: Them chunks ask the warm helper for word times too, so a line can be split where the
    /// voice changes. Only on this Mac; cloud lines are assigned whole.
    private func makePump(_ id: String, cloud: MeetingRoute.Cloud? = nil, timed: Bool = false, timedStream: MeetingStream = .them) -> MeetingPump {
        let transcribe = local?.transcribe
        let onThisMac: MeetingRoute.Local = { url, done in
            guard let transcribe else { done(nil); return }
            transcribe(url, done)
        }
        var transcriber = MeetingRoute.transcriber(cloud: cloud, local: onThisMac)
        if timed, let transcribeTimed = local?.transcribeTimed {
            let base = transcriber
            transcriber = { url, done in MeetingAudio.parse(url)?.stream == timedStream ? transcribeTimed(url, done) : base(url, done) }
        }
        let pump = MeetingPump(transcribe: transcriber, deliver: { [weak self] job, text in
            DispatchQueue.main.async { self?.deliver(id, job, text) }
        })
        pumps[id] = pump
        return pump
    }

    private func deliver(_ id: String, _ job: MeetingPump.Job, _ chunk: ChunkText?) {
        guard let chunk else {
            meetingLog.notice("meeting-chunk-failed stream=\(job.stream.rawValue, privacy: .public)")
            refreshLeftovers(); settle(id); return
        }
        // Content-free: how many words a chunk gave, so an all-silent stream shows up as words=0.
        meetingLog.notice("meeting-line stream=\(job.stream.rawValue, privacy: .public) words=\(chunk.text.split(separator: " ").count) fallback=\(chunk.localFallback, privacy: .public)")
        let spelled = applyDictionary(chunk.text)
        update(id) {
            $0.insert(stream: job.stream, start: job.start, end: job.start + job.duration, text: spelled, localFallback: chunk.localFallback, words: chunk.words)
            // A leftover finished after notes were written: they no longer cover the whole transcript.
            if !spelled.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { $0.markNotesOutdated() }
        }
        if job.stream == (anyMeeting(id)?.source.speakerStream ?? .them) { keepAudio(id, job) }
        try? FileManager.default.removeItem(at: job.url)
        settle(id)
    }

    private func anyMeeting(_ id: String) -> Meeting? { live?.id == id ? live : meetings.first { $0.id == id } }

    private func settle(_ id: String) {
        guard live?.id != id, pumps[id]?.isIdle != false else { return }
        pumps[id] = nil; finishing.remove(id); refreshLeftovers()
        separate(id)
        transcriptDone(id)
    }

    /// The transcript is complete and its speakers separated (or that failed or was skipped): write
    /// notes on the chosen route, or ask for one the first time. Called again when separation ends,
    /// so notes use speaker labels. Notes already written are left alone (a finished leftover marks
    /// them out of date instead).
    private func transcriptDone(_ id: String) {
        guard pumps[id] == nil, !finishing.contains(id), let meeting = meetings.first(where: { $0.id == id }),
              meeting.speakers?.state != .pending else { return }
        let manual = askOnDone.remove(id) != nil
        guard meeting.notes == nil,
              meeting.lines.contains(where: { !$0.isHidden }) else { return }
        switch notesRoute() {
        case nil:
            notesChoicePending = id
            if manual { onNotesChoiceNeeded?() }
        case .none?: break
        default: writeNotes(id)
        }
    }

    /// Writes (or rewrites: "Update Notes") a finished meeting's notes. Failure keeps the meeting and says why.
    /// Not while its speakers are being separated: the notes would say Them instead of their names.
    func writeNotes(_ id: String, lecture: Bool = false) {
        guard let writer = notesWriter, !notesWriting.contains(id), !finishing.contains(id),
              let meeting = meetings.first(where: { $0.id == id }), meeting.speakers?.state != .pending else { return }
        guard meeting.lines.contains(where: { !$0.isHidden }) else { notesProblem[id] = "There is no transcript to write notes from."; return }
        notesWriting.insert(id); notesProblem[id] = nil
        Task { [weak self] in
            let result: Result<MeetingNotesWriter.Outcome, Error>
            do { result = .success(try await writer(meeting, lecture)) } catch { result = .failure(error) }
            guard let self else { return }
            self.notesWriting.remove(id)
            switch result {
            case .success(let outcome):
                // Content-free: counts and reasons only.
                let reasons = outcome.reasons.map { "\($0.key.rawValue)=\($0.value)" }.sorted().joined(separator: ",")
                meetingLog.notice("meeting-notes points=\(outcome.notes.keyPoints.count) decisions=\(outcome.notes.decisions.count) actions=\(outcome.notes.actions.count) dropped=\(outcome.notes.dropped) reasons=\(reasons, privacy: .public)")
                self.update(id) { meeting in
                    var notes = outcome.notes
                    // Keep ticked action items that were written again word for word.
                    let ticked = Set((meeting.notes?.actions ?? []).filter { $0.done == true }.map(\.text))
                    for i in notes.actions.indices where !lecture && ticked.contains(notes.actions[i].text) { notes.actions[i].done = true }
                    if lecture { meeting.lectureNotes = notes } else { meeting.notes = notes }
                }
            case .failure(let error):
                let reason: String
                switch error as? NotesFailure {
                case .failed(let text)?: reason = text
                case .tooLarge?: reason = "This meeting is too long for the notes route."
                case nil: reason = "Notes could not be written."
                }
                meetingLog.notice("meeting-notes-failed")
                self.notesProblem[id] = reason + " The transcript is saved."
            }
        }
    }

    /// The first-Stop question was answered (or the route changed in Settings).
    func notesRouteChosen(_ route: MeetingNotesRoute) {
        guard let id = notesChoicePending else { return }
        notesChoicePending = nil
        if route != .none { writeNotes(id) }
    }

    func toggleAction(_ index: Int, of id: String) {
        update(id) { meeting in
            guard var notes = meeting.notes, notes.actions.indices.contains(index) else { return }
            notes.actions[index].done = !(notes.actions[index].done ?? false)
            meeting.notes = notes
        }
    }

    private func update(_ id: String, _ change: (inout Meeting) -> Void) {
        if var meeting = live, meeting.id == id {
            change(&meeting); live = meeting; save(force: false)
        } else if let index = meetings.firstIndex(where: { $0.id == id }) {
            change(&meetings[index]); write(meetings[index])
        }
    }

    /// The live record is saved at most every 2 s (and on every forced save), so a crash keeps it.
    private func save(force: Bool) {
        guard persists, let meeting = live else { return }
        let now = ProcessInfo.processInfo.systemUptime
        guard force || now - lastSave >= 2 else { return }
        lastSave = now
        write(meeting)
    }

    /// Every meeting write goes through here: a failure is remembered (content-free in the log) and shown, never dropped.
    private func write(_ meeting: Meeting) {
        guard persists, let store else { return }
        do { try store.save(meeting); if unsaved.contains(meeting.id) { unsaved.remove(meeting.id) } }
        catch { if unsaved.insert(meeting.id).inserted { meetingLog.notice("meeting-save-failed") } }
    }

    private func retryUnsaved() {
        for id in unsaved { if let meeting = anyMeeting(id) { write(meeting) } else { unsaved.remove(id) } }
    }

    /// A meeting that is being thrown away: a file that stays is logged, not shown (see `delete(_:)` for the user's own deletes).
    private func erase(_ id: String) {
        guard let store, store.exists(id) else { return }
        do { try store.delete(id) } catch { meetingLog.notice("meeting-delete-failed") }
    }

    /// The user's own notes. Changing them marks written notes out of date.
    func setNotes(_ text: String, for id: String) {
        update(id) { meeting in
            guard meeting.myNotes != text else { return }
            meeting.myNotes = text
            meeting.markNotesOutdated()
        }
    }

    /// A chunk file could not be written: mark the gap and rebuild the stream with a new chunker.
    private func streamFailed(_ stream: MeetingStream) {
        guard live != nil else { return }
        gap(stream, reason: "write-failed")
        rebuild(stream, reason: "write-failed")
    }

    private func gap(_ stream: MeetingStream, reason: String) {
        guard var meeting = live else { return }
        meeting.gaps.append(elapsed); live = meeting
        meetingLog.notice("meeting-gap stream=\(stream.rawValue, privacy: .public) reason=\(reason, privacy: .public) at=\(self.elapsed, format: .fixed(precision: 1))")
    }

    private func rebuild(_ stream: MeetingStream, reason: String) {
        guard let meeting = live else { return }
        let error: Error?
        if stream == .me { error = startMe(); meWatch.rebuilt(now: ProcessInfo.processInfo.systemUptime) }
        else { error = startTap(meeting.source); themWatch.rebuilt(now: ProcessInfo.processInfo.systemUptime) }
        meetingLog.notice("meeting-rebuild stream=\(stream.rawValue, privacy: .public) reason=\(reason, privacy: .public) ok=\(error == nil, privacy: .public) error=\(error.map { String(describing: $0) } ?? "-", privacy: .public)")
    }

    /// Applies one stream's watch result. A stall marks one gap and rebuilds; until audio flows
    /// again it rebuilds every few seconds; recovery clears the warning.
    private func watch(_ stream: MeetingStream, _ action: StreamWatch.Action) {
        let name = stream == .me ? "Your microphone" : "The call's audio"
        switch action {
        case .none: break
        case .stalled:
            gap(stream, reason: "no-audio-\(Int(StreamWatch.stallAfter))s")
            warning = "\(name) stopped arriving. Murmur is restarting it."
            rebuild(stream, reason: "no-audio")
        case .rebuild: rebuild(stream, reason: "still-no-audio")
        case .recovered(let after):
            meetingLog.notice("meeting-recovered stream=\(stream.rawValue, privacy: .public) after=\(after, format: .fixed(precision: 1))")
            if warning?.hasPrefix(name) == true { warning = nil }
        }
    }

    func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        if !unsaved.isEmpty, now - lastRetry >= 2 { lastRetry = now; retryUnsaved() }
        guard let meeting = live else { return }
        elapsed = now - started
        let meRaw = capture.map { c in c.queue.sync { c.level } } ?? 0
        let themRaw = tap.map { t in t.queue.sync { t.level } } ?? 0
        meLevel = meScale.display(meRaw); themLevel = themScale.display(themRaw)
        if max(meRaw, themRaw) > MeetingLimits.silenceLevel { lastVoice = now; askEnded = false }
        // The meeting-only Capture is not the controller's: it restarts in place on a device change;
        // if audio still stops, the watch rebuilds it with a new engine.
        if let capture, !capture.engine.isRunning, !capture.restart() {
            meetingLog.notice("meeting-mic-restart-failed at=\(self.elapsed, format: .fixed(precision: 1))")
        }
        watch(.me, meWatch.update(count: capture.map { c in c.queue.sync { Int64(c.frames) } } ?? 0, now: now))
        if live == nil { return }
        if meeting.source.micOnly == false && systemAudioProblem == nil {
            // A tap delivers nothing while no tapped app plays (YouTube paused, a call not yet connected): idle, not a stall.
            if now - lastPlayingCheck >= 1 { lastPlayingCheck = now; themPlaying = tap?.isPlaying(AudioProcessList.all()) ?? true }
            watch(.them, themWatch.update(count: tap.map { t in t.queue.sync { t.buffers } } ?? 0, now: now, idle: !themPlaying))
        }
        behind = (pumps[meeting.id]?.backlog ?? 0) > MeetingLimits.behind
        // A denied tap may deliver silence rather than an error: say so after 60 s without call audio.
        if let tap, systemAudioProblem == nil, elapsed > 60, !tap.queue.sync(execute: { tap.heardSignal }), warning == nil {
            warning = "No call audio yet. If the call is playing, check that Murmur is allowed under System Audio Recording in System Settings → Privacy & Security."
        } else if let tap, warning?.hasPrefix("No call audio") == true, tap.queue.sync(execute: { tap.heardSignal }) { warning = nil }
        if now - lastTapRefresh >= 2 {
            lastTapRefresh = now; tap?.refreshProcesses()
            // A call can switch the output device (a headset entering call mode); the aggregate is built on it.
            if tap != nil, let current = AudioProcessList.defaultOutputUID(), current != outputUID {
                gap(.them, reason: "output-device-changed"); rebuild(.them, reason: "output-device-changed")
            }
            if let callApp {
                let app = CallApp.resolve(callApp)
                let using = AudioProcessList.all().contains { $0.input && app.matches($0.bundleID) }
                if callWasUsingMic && !using { askEnded = true }
                callWasUsingMic = using
            }
        }
        switch MeetingLimits.action(elapsed: elapsed, silentFor: now - lastVoice) {
        case .stopCap: message = "Meeting notes stopped at the \(MeetingLimits.capHours)-hour limit."; finish(manual: false)
        case .stopSilent: message = "Meeting notes stopped after \(MeetingLimits.stopSilentMinutes) minutes of silence."; finish(manual: false)
        case .warnCap: warning = "Meeting notes stop at \(MeetingLimits.capHours) hours, in \(max(1, Int((MeetingLimits.cap - elapsed) / 60))) min."
        case .askEnded: askEnded = true
        case .none: break
        }
        save(force: false)
    }

    func refreshLeftovers() {
        guard let audioRoot else { leftovers = [:]; return }
        var found: [String: Int] = [:]
        for meeting in meetings where pumps[meeting.id] == nil {
            let count = MeetingAudio.leftovers(audioRoot, meeting: meeting.id).count
            if count > 0 { found[meeting.id] = count }
        }
        leftovers = found
    }

    func finishLeftovers(_ id: String) {
        guard let audioRoot, pumps[id] == nil else { return }
        let files = MeetingAudio.leftovers(audioRoot, meeting: id)
        guard !files.isEmpty else { return }
        let pump = makePump(id)
        finishing.insert(id); leftovers[id] = nil
        for url in files {
            guard let parsed = MeetingAudio.parse(url) else { continue }
            pump.enqueue(.init(stream: parsed.stream, url: url, start: parsed.start, duration: MeetingStreamFeed.seconds(url) ?? 0))
        }
    }

    func delete(_ id: String) {
        guard live?.id != id else { message = "Stop the meeting before deleting it."; return }
        if persists, let store, store.exists(id) {
            do { try store.delete(id) } catch { message = "Could not delete this meeting."; return }
        }
        unsaved.remove(id)
        pumps.removeValue(forKey: id)?.stop(); finishing.remove(id); notesProblem[id] = nil
        if notesChoicePending == id { notesChoicePending = nil }
        speakerRunner.cancel(id); separating.remove(id); deleteKeptAudio(id)
        if let audioRoot { try? FileManager.default.removeItem(at: MeetingAudio.folder(audioRoot, meeting: id)) }
        meetings.removeAll { $0.id == id }; refreshLeftovers()
    }

    /// Return IDs that remain so a failed deletion can be retried without selecting again.
    func delete(_ ids: Set<String>) -> Set<String> {
        for id in ids { delete(id) }
        var remaining = Set(meetings.map(\.id))
        if let live { remaining.insert(live.id) }
        let failed = ids.intersection(remaining)
        if !failed.isEmpty { message = "Could not delete \(failed.count) meeting(s). They remain selected; try again." }
        return failed
    }

    /// Expires meetings with History's retention, and chunk audio after `RetryWindow.text`.
    /// Meetings the History cap already deleted from disk.
    func forget(_ ids: Set<String>) {
        for id in ids { pumps.removeValue(forKey: id)?.stop(); finishing.remove(id); speakerRunner.cancel(id); separating.remove(id) }
        unsaved.subtract(ids)
        meetings.removeAll { ids.contains($0.id) }; purgeAudio()
    }

    /// Uses the meetings in memory, so the minute timer never re-reads every file.
    func purge(cutoff: Date?) {
        if let cutoff {
            for meeting in meetings where meeting.recordedAt <= cutoff {
                pumps.removeValue(forKey: meeting.id)?.stop(); finishing.remove(meeting.id)
                speakerRunner.cancel(meeting.id); separating.remove(meeting.id)
                unsaved.remove(meeting.id)
                if persists { erase(meeting.id) }
            }
            meetings.removeAll { $0.recordedAt <= cutoff }
            do { try store?.purgeUnreadable(cutoff: cutoff) } catch { meetingLog.notice("meeting-purge-failed") }
        }
        purgeAudio()
    }

    private func purgeAudio() {
        guard let audioRoot else { return }
        var active = Set(pumps.keys); if let live { active.insert(live.id) }
        var undeleted = MeetingAudio.purge(audioRoot, keep: Set(meetings.map(\.id)), active: active)
        refreshLeftovers()
        // Kept Them audio: only for meetings still waiting for diarization, and never past `RetryWindow.text`.
        if let speakerRoot {
            let pending = Set((meetings + (live.map { [$0] } ?? [])).filter { $0.speakers?.state == .pending }.map(\.id))
            undeleted += SpeakerAudio.purge(speakerRoot, keep: pending, active: active.union(separating))
            // Resumes a separation that quitting interrupted, and fails one whose audio expired.
            for id in pending where !active.contains(id) { separate(id) }
        }
        // Audio past its limit that is still on disk is a privacy fact the user should see, not a log line.
        if undeleted > 0 { message = "Murmur could not delete \(undeleted) expired meeting audio \(undeleted == 1 ? "file" : "files"). It will try again; you can also remove everything with Delete All Data." }
    }

    /// Asked before quitting. Tries the unsaved meetings once more first, so the warning is only for what is still at risk.
    func quitWarning() -> MeetingQuit.Warning? {
        retryUnsaved()
        return MeetingQuit.warning(recording: live != nil, unsaved: unsaved.count)
    }

    /// Murmur is quitting: never keep recording. Untranscribed chunks stay for `RetryWindow.text`.
    func shutdown() {
        if live != nil { finish(manual: false) }
        retryUnsaved() // One last attempt; `quitWarning` has already asked the user about anything still unsaved.
        pumps.values.forEach { $0.stop() }
        // A separation still pending runs again at the next launch while its audio is under `RetryWindow.text` old.
        speakerRunner.cancelAll()
    }

    /// UI fixture only (check_ui.swift): synthetic meetings, and optionally a synthetic live one; no capture.
    func showFixture(_ finished: [Meeting], live: Meeting?, leftovers: [String: Int] = [:], separating: Set<String> = [], unsaved: Set<String> = []) {
        meetings = finished; self.unsaved = unsaved; self.live = live; self.leftovers = leftovers; self.separating = separating
        meLevel = 0.55; themLevel = 0.35; elapsed = live?.seconds ?? 0
        onLiveChange?()
    }

    private func keepAudio(_ id: String, _ job: MeetingPump.Job) {
        guard anyMeeting(id)?.speakers?.state == .pending, let speakerRoot else { return }
        do {
            let file = try AVAudioFile(forReading: job.url, commonFormat: .pcmFormatInt16, interleaved: false)
            guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else { return }
            try file.read(into: buffer)
            guard file.processingFormat.sampleRate == Double(SpeakerAudio.sampleRate), let channel = buffer.int16ChannelData else { return }
            try SpeakerAudio.write(Array(UnsafeBufferPointer(start: channel[0], count: Int(buffer.frameLength))), at: job.start, to: SpeakerAudio.file(speakerRoot, meeting: id))
        } catch { meetingLog.notice("meeting-keep-audio-failed") }
    }

    /// Runs diarization once a finished meeting has no chunks left to transcribe. Without the model
    /// (separation turned off or removed since), the audio waits, at most `RetryWindow.text`.
    private func separate(_ id: String) {
        guard live?.id != id, pumps[id] == nil, !separating.contains(id), let speakerRoot,
              let meeting = meetings.first(where: { $0.id == id }), meeting.speakers?.state == .pending else { return }
        let audio = SpeakerAudio.file(speakerRoot, meeting: id)
        guard FileManager.default.fileExists(atPath: audio.path), (try? SpeakerAudio.finish(audio)) != nil else {
            finishSeparation(id, nil, reason: "no-call-audio"); return
        }
        guard let helper = local?.helper, let models = speakerModel else { if !speakersWanted { skipSeparation(id) }; return }
        separating.insert(id)
        meetingLog.notice("speakers-started")
        speakerRunner.run(id, helper: helper, models: models, audio: audio, check: verifySpeakerModel, threshold: meeting.speakers?.threshold ?? MeetingSpeakers.threshold) { [weak self] segments in
            DispatchQueue.main.async { self?.finishSeparation(id, segments, reason: "helper-failed") }
        }
    }

    private func finishSeparation(_ id: String, _ segments: [SpeakerSegment]?, reason: String) {
        separating.remove(id)
        guard meetings.first(where: { $0.id == id })?.speakers?.state == .pending else { return }
        update(id) { meeting in
            if let segments {
                MeetingSpeakers.apply(segments, to: &meeting)
                // Written before separation (an older version of Murmur): they say Them, not the speakers.
                meeting.markNotesOutdated()
            }
            else {
                meeting.speakers?.state = .failed; meeting.speakers?.failure = reason
                meeting.dropWordTimes()
            }
        }
        deleteKeptAudio(id)
        let found = Set(meetings.first { $0.id == id }?.lines.compactMap(\.speaker) ?? []).count
        meetingLog.notice("speakers-finished ok=\(segments != nil, privacy: .public) speakers=\(found) reason=\(segments == nil ? reason : "-", privacy: .public)")
        transcriptDone(id)
    }

    func skipSeparation(_ id: String) {
        guard let meeting = anyMeeting(id), meeting.speakers?.state == .pending else { return }
        speakerRunner.cancel(id); separating.remove(id)
        update(id) { meeting in
            meeting.speakers?.state = .skipped
            meeting.dropWordTimes()
        }
        deleteKeptAudio(id)
        transcriptDone(id)
    }

    private func deleteKeptAudio(_ id: String) {
        guard let speakerRoot else { return }
        try? FileManager.default.removeItem(at: SpeakerAudio.file(speakerRoot, meeting: id))
    }

    // Renaming, merging and reassigning are per meeting and saved with it. A rename or merge
    // relabels notes already written; a reassignment marks them out of date (MeetingSpeakers.swift).
    func renameSpeaker(_ number: Int, to name: String, in id: String) { update(id) { $0.renameSpeaker(number, to: name) } }
    func mergeSpeaker(_ from: Int, into: Int, in id: String) { update(id) { $0.mergeSpeaker(from, into: into) } }
    func reassignLine(_ line: Int, to speaker: Int, in id: String) { update(id) { $0.reassignLine(line, to: speaker) } }

    private func copyToPasteboard(_ text: String) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string) }
    func copyTranscript(_ meeting: Meeting) { copyToPasteboard(MeetingExport.transcript(meeting)) }
    func saveMarkdown(_ meeting: Meeting) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = MeetingExport.fileName(meeting)
        panel.allowedContentTypes = [.init(filenameExtension: "md") ?? .plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try Data(MeetingExport.markdown(meeting).utf8).write(to: url, options: .atomic) }
        catch { message = "Could not save the Markdown file." }
    }
    func copyNotes(_ meeting: Meeting, lecture: Bool = false) { copyToPasteboard(MeetingExport.notes(meeting, lecture: lecture)) }
    func copyNotice() { copyToPasteboard(MeetingConsent.notice) }

    static func runningCallApps() -> [CallApp] {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier))
        return CallApp.catalog.filter { $0.inStartMenu && running.contains($0.bundleID) }
    }
}
