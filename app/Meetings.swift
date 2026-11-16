import Foundation

// Meeting notes: the record, echo suppression, the transcription
// route and shared pump, stream health, limits and consent. Foundation only, so check_meetings.swift
// builds without audio frameworks. The store is in MeetingStore.swift, export in MeetingExport.swift,
// the call-app catalog and the stream and source types in CallApps.swift, capture in MeetingCapture.swift.

struct MeetingLine: Codable, Equatable, Identifiable {
    let id: Int
    let stream: MeetingStream
    /// Seconds from the meeting's start, by capture time.
    let start: Double
    let end: Double
    /// As transcribed, with the dictionary applied. Never changed by echo suppression.
    let text: String
    /// Me lines only: the text with words that repeat the other side removed, or nil
    /// when nothing was removed. An empty string means the whole line is hidden.
    var shown: String? = nil
    var localFallback: Bool? = nil
    var speaker: Int? = nil
    /// Them lines while speaker separation is pending: [start, end] of each word in `text`, in
    /// seconds from `start`, so the line can be split where the voice changes. Dropped once used.
    var words: [[Double]]? = nil

    var isHidden: Bool { shown == "" }
    var visibleText: String { shown ?? text }
}

struct Meeting: Codable, Identifiable, Equatable {
    let id: String
    /// When recording started; retention counts from here, as for dictations.
    let recordedAt: Date
    var seconds: Double = 0
    var source: MeetingSource
    var lines: [MeetingLine] = []
    var myNotes = ""
    /// Seconds where a stream stopped or restarted, marked in the transcript.
    var gaps: [Double] = []
    /// False while recording, so a crash leaves a record that relaunch finishes.
    var finished = false
    /// The cloud transcriber's name when the meeting used the cloud route; nil means this Mac.
    var transcribedBy: String? = nil
    /// Written after Stop by the notes route. Optional, so records still decode.
    var notes: MeetingNotes? = nil
    /// Reuses the cited-item schema under other section names (`NoteSections.titles(lecture:)`).
    var lectureNotes: MeetingNotes? = nil
    /// Speaker separation: nil when it was off for this meeting.
    var speakers: SpeakerSeparation? = nil

    var title: String { notes?.title ?? defaultTitle }
    var defaultTitle: String {
        let date = DateFormatter.localizedString(from: recordedAt, dateStyle: .medium, timeStyle: .short)
        if source.micOnly { return "Meeting notes, \(date)" }
        return source.allApps ? "Meeting, \(date)" : "Meeting with \(source.name), \(date)"
    }

    /// Search the content people remember, including notes that do not appear in the transcript.
    var searchDetails: [String] {
        var text = [notes?.summary ?? "", myNotes]
        for notes in [notes, lectureNotes].compactMap({ $0 }) {
            text += [notes.title ?? "", notes.summary ?? ""]
            text += (notes.keyPoints + notes.decisions + notes.actions).map {
                [$0.text, $0.owner ?? "", $0.due ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
            }
        }
        text += lines.map(\.visibleText)
        return text.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    }

    func matches(query: String, from: Date?, through: Date?, calendar: Calendar = .current) -> Bool {
        if let from, recordedAt < calendar.startOfDay(for: from) { return false }
        if let through, let end = calendar.date(byAdding: .day, value: 1, to: calendar.startOfDay(for: through)), recordedAt >= end { return false }
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return query.isEmpty || ([title, source.name] + searchDetails).contains { $0.localizedStandardContains(query) }
    }

    /// Word times are only needed to split lines by speaker; drop them once that is done or skipped.
    mutating func dropWordTimes() { for i in lines.indices { lines[i].words = nil } }

    /// The one place that says an edit made written notes out of date: late lines, my notes, speaker
    /// separation and reassigning a line. A rename or merge passes `meetingNotes: false`: it relabels
    /// the meeting notes in place (`relabelNotes`) and only the lecture notes go out of date.
    mutating func markNotesOutdated(meetingNotes: Bool = true) {
        lectureNotes?.outdated = true
        if meetingNotes { notes?.outdated = true }
    }

    mutating func insert(stream: MeetingStream, start: Double, end: Double, text: String, localFallback: Bool = false, words: [[Double]]? = nil) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        var line = MeetingLine(id: (lines.map(\.id).max() ?? 0) + 1, stream: stream, start: start, end: end, text: trimmed)
        if localFallback { line.localFallback = true }
        if let words, words.count == trimmed.split(whereSeparator: \.isWhitespace).count { line.words = words }
        let index = lines.firstIndex { $0.start > start || ($0.start == start && $0.stream == .them && stream == .me) } ?? lines.endIndex
        lines.insert(line, at: index)
        seconds = max(seconds, end)
        MeetingEcho.apply(&lines, near: start - MeetingEcho.window - 30, to: end + MeetingEcho.window + 30)
    }
}

/// Acoustic echo, not vocabulary filtering: with speakers, the microphone also hears the other side,
/// so their words appear in Me too. A run of at least `minimumRun` Me words that repeats a run in a
/// nearby Them line is hidden from Me. Them is never changed, nothing is deleted, and "Show hidden
/// echo" brings it back. This lives only in the meeting layer, never in transcription or providers.
enum MeetingEcho {
    /// ponytail: exact word runs over chunk-level timing; measure on real speaker calls before tuning.
    static let minimumRun = 3
    /// Seconds of slack around a Me line when looking for Them lines; chunks are about 15 s long.
    static let window = 3.0

    static func words(_ text: String) -> [String] {
        text.split(whereSeparator: \.isWhitespace).map { $0.lowercased().filter { $0.isLetter || $0.isNumber } }
    }

    /// The Me text with echoed runs removed; "" when nothing of its own is left; nil when nothing was removed.
    static func shown(me text: String, them: [String]) -> String? {
        let original = text.split(whereSeparator: \.isWhitespace)
        let mine = words(text)
        let theirs = them.map(words).filter { !$0.isEmpty }
        var hidden = [Bool](repeating: false, count: mine.count)
        for start in mine.indices where !mine[start].isEmpty {
            for other in theirs {
                for j in other.indices where other[j] == mine[start] {
                    var length = 0
                    while start + length < mine.count, j + length < other.count, !mine[start + length].isEmpty, mine[start + length] == other[j + length] { length += 1 }
                    if length >= minimumRun { for k in start..<(start + length) { hidden[k] = true } }
                }
            }
        }
        guard hidden.contains(true) else { return nil }
        return zip(original, hidden).filter { !$0.1 }.map { String($0.0) }.joined(separator: " ")
    }

    static func apply(_ lines: inout [MeetingLine], near from: Double, to: Double) {
        for i in lines.indices where lines[i].stream == .me && lines[i].start >= from && lines[i].start <= to {
            let me = lines[i]
            let them = lines.filter { $0.stream == .them && $0.end >= me.start - window && $0.start <= me.end + window }.map(\.text)
            lines[i].shown = shown(me: me.text, them: them)
        }
    }
}

enum MeetingTranscription: String, Codable, CaseIterable {
    case local, cloud
    static let key = "meetingTranscription"
    static func load(_ defaults: UserDefaults) -> MeetingTranscription { MeetingTranscription(rawValue: defaults.string(forKey: key) ?? "") ?? .local }
}

struct ChunkText: Equatable {
    let text: String
    var localFallback = false
    /// Word times for a Them chunk while speaker separation is pending; nil otherwise.
    var words: [[Double]]? = nil
}

enum MeetingCloudResult: Equatable { case text(String), noSpeech, failed }

enum MeetingRoute {
    typealias Local = (URL, @escaping (String?) -> Void) -> Void
    typealias Cloud = (URL, @escaping (MeetingCloudResult) -> Void) -> Void

    /// The pump's transcriber for one meeting. The route is fixed here, when the meeting starts, so
    /// changing the setting never switches a meeting in progress. A cloud chunk that fails (rate
    /// limit, network, an answer-shaped LLM reply) is transcribed on this Mac and marked; the route
    /// itself never changes, and a local chunk never goes to the cloud.
    static func transcriber(cloud: Cloud?, local: @escaping Local) -> (URL, @escaping (ChunkText?) -> Void) -> Void {
        { url, done in
            guard let cloud else { local(url) { done($0.map { ChunkText(text: $0) }) }; return }
            cloud(url) { result in
                switch result {
                case .text(let text): done(ChunkText(text: text))
                case .noSpeech: done(ChunkText(text: ""))
                case .failed: local(url) { done($0.map { ChunkText(text: $0, localFallback: true) }) }
                }
            }
        }
    }

    /// Chunks are 16 kHz mono 16-bit: 115.2 MB an hour per stream, so up to about 230 MB with both.
    static let megabytesPerStreamHour = 115.2

    /// The inline note under Cloud. No price is quoted: per-minute prices are not verified for
    /// either route, so the note says the price depends on the provider.
    static func cloudNote(provider: String, host: String, storesAudio: Bool) -> String {
        var note = "Meetings send all of their audio, including other participants’ voices, to \(provider) at \(host): up to about \(Int((megabytesPerStreamHour * 2).rounded())) MB an hour with both streams. Long meetings can cost significantly; the price depends on the provider and model, so check its pricing before a long call."
        if storesAudio { note += " Google stores audio requests (55 days on the paid tier, 1 day on the free tier)." }
        note += " If a part fails, it is transcribed on this Mac instead and marked; the meeting stays on the cloud route."
        return note
    }
}

/// One queue for both streams' chunks. Only one chunk is with the warm helper at a time, so a
/// dictation waits for at most one chunk; chunks go in the order they were recorded.
/// A chunk that fails twice is left on disk for "Finish transcript"; nothing is dropped.
final class MeetingPump: @unchecked Sendable {
    struct Job: Equatable { let stream: MeetingStream; let url: URL; let start: Double; let duration: Double; var attempts = 0 }
    private let lock = NSLock()
    private var queued: [Job] = [], inFlight: Job?, stopped = false
    private let transcribe: (URL, @escaping (ChunkText?) -> Void) -> Void
    private let deliver: (Job, ChunkText?) -> Void
    static let attempts = 2

    /// `transcribe(url, completion)` answers the text or nil; `deliver(job, text)` gets nil after the last attempt.
    init(transcribe: @escaping (URL, @escaping (ChunkText?) -> Void) -> Void, deliver: @escaping (Job, ChunkText?) -> Void) {
        self.transcribe = transcribe; self.deliver = deliver
    }
    func enqueue(_ job: Job) {
        lock.withLock {
            guard !stopped else { return }
            let index = queued.firstIndex { $0.start > job.start } ?? queued.endIndex
            queued.insert(job, at: index)
        }
        pump()
    }
    var backlog: Double { lock.withLock { queued.reduce(inFlight?.duration ?? 0) { $0 + $1.duration } } }
    var isIdle: Bool { lock.withLock { queued.isEmpty && inFlight == nil } }
    /// Drops the queue (files stay for recovery) and ignores answers still in flight.
    func stop() { lock.withLock { stopped = true; queued = [] } }
    private func pump() {
        let next: Job? = lock.withLock {
            guard !stopped, inFlight == nil, !queued.isEmpty else { return nil }
            inFlight = queued.removeFirst(); return inFlight
        }
        guard let next else { return }
        transcribe(next.url) { [self] text in
            let retry: Bool = lock.withLock {
                inFlight = nil
                guard !stopped else { return false }
                if text == nil && next.attempts + 1 < Self.attempts {
                    var again = next; again.attempts += 1; queued.insert(again, at: 0); return true
                }
                return false
            }
            if !retry && !lock.withLock({ stopped }) { deliver(next, text) }
            pump()
        }
    }
}

/// Watches one stream's delivered audio (a frame or buffer count) and decides when it has stalled
/// and must be rebuilt. A call app turning on voice processing, or a headset switching to
/// its call mode, can stop a microphone engine or a tap's aggregate device without an error; the
/// first FaceTime test lost both streams that way. A stall is reported once (one gap), then the
/// stream is rebuilt every `rebuildEvery` seconds until audio flows again.
struct StreamWatch {
    static let stallAfter: TimeInterval = 3
    static let rebuildEvery: TimeInterval = 3
    enum Action: Equatable { case none, stalled, rebuild, recovered(after: Double) }

    private var lastCount: Int64 = 0
    private var lastProgress: TimeInterval
    private var lastRebuild = -TimeInterval.infinity
    private(set) var stalledSince: TimeInterval?

    init(now: TimeInterval) { lastProgress = now }

    /// `idle`: nothing the stream records is producing audio (a tap delivers no buffers while no
    /// tapped app plays), which is not a stall.
    mutating func update(count: Int64, now: TimeInterval, idle: Bool = false) -> Action {
        if count > lastCount || idle {
            lastCount = max(lastCount, count); lastProgress = now
            guard let since = stalledSince else { return .none }
            stalledSince = nil
            return .recovered(after: now - since)
        }
        guard now - lastProgress >= Self.stallAfter else { return .none }
        if stalledSince == nil { stalledSince = lastProgress; return .stalled }
        return now - lastRebuild >= Self.rebuildEvery ? .rebuild : .none
    }

    /// A new capture or tap counts from zero; only its own audio counts as progress.
    mutating func rebuilt(now: TimeInterval) { lastCount = 0; lastRebuild = now }
}

/// The live meters: call audio from a tap is often much quieter than the microphone, so
/// each meter is scaled to its own recent loudness, with a floor so silence and hiss stay low. The
/// square root lifts quiet speech, as the dictation bubble does.
struct MeterScale {
    static let floor = 0.05
    /// Per update, one every `MeetingLimits.tickInterval` (0.15 s, 6–7 a second): the reference falls
    /// by about two thirds in 5 s. Retune it if the interval changes.
    static let decay = 0.97
    private var reference = floor
    mutating func display(_ level: Double) -> Double {
        reference = max(level, reference * Self.decay, Self.floor)
        return min(1, (max(0, level) / reference).squareRoot())
    }
}

enum MeetingLimits {
    /// How often the controller's timer calls `MeetingRecorder.tick()` (and the call-offer tick). `MeterScale.decay`
    /// is tuned to it; the 1 s and 2 s throttles in `tick()` count real seconds, so they are not.
    static let tickInterval: TimeInterval = 0.15
    static let cap: TimeInterval = 3 * 60 * 60
    static let warning: TimeInterval = cap - 10 * 60
    static let askEnded: TimeInterval = 5 * 60
    static let stopSilent: TimeInterval = 10 * 60
    static let behind: TimeInterval = 2 * 60
    /// The numbers the user sees in "stopped at the 3-hour limit" and "after 10 minutes of silence".
    static let capHours = Int(cap / 3600)
    static let stopSilentMinutes = Int(stopSilent / 60)
    /// A level under this counts as silence (the meters' 0–1 scale).
    static let silenceLevel = 0.02

    enum Action: Equatable { case none, warnCap, stopCap, askEnded, stopSilent }
    static func action(elapsed: TimeInterval, silentFor: TimeInterval) -> Action {
        if elapsed >= cap { return .stopCap }
        if silentFor >= stopSilent { return .stopSilent }
        if elapsed >= warning { return .warnCap }
        if silentFor >= askEnded { return .askEnded }
        return .none
    }
}

enum MeetingConsent {
    /// Raise when the sheet's text changes, so it is shown again.
    static let version = 1
    static let key = "meetingConsentVersion"
    static func needed(_ defaults: UserDefaults) -> Bool { defaults.integer(forKey: key) < version }
    static func acknowledge(_ defaults: UserDefaults) { defaults.set(version, forKey: key) }

    static let title = "Before your first meeting notes"
    static let body = "Murmur records your microphone and the audio of the call or apps you choose, and transcribes both on this Mac. Recording other people may require their consent where you or they are. You are responsible for letting participants know."
    static let acknowledgement = "I'll let participants know"
    static let reminder = "Let others know you're taking notes."
    /// Copied for the user to paste into the call chat. Murmur never sends it.
    static let notice = "Heads up: I'm taking notes on this call with Murmur, which transcribes the audio on my Mac."
}
