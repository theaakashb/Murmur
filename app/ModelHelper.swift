import AVFoundation
import Carbon
import CryptoKit
import Foundation
import os
@preconcurrency import Dispatch

// The ModelBench helper process: what it loads, the warm line protocol, chunked and live transcription.

private let helperOutputLimit = 4 * 1024 * 1024

/// Drain stdout while the helper runs; waiting for exit first can deadlock on a full pipe.
func readHelperOutput(_ handle: FileHandle) throws -> Data {
    var result = Data()
    while let chunk = try handle.read(upToCount: 64 * 1024), !chunk.isEmpty {
        guard chunk.count <= helperOutputLimit - result.count else { throw CaptureError("Transcription output is too large") }
        result.append(chunk)
    }
    return result
}

/// What the transcription helper loads: the model folder plus, when dictionary support is
/// installed and verified and the dictionary has enabled entries, `--ctc DIR --vocabulary FILE`.
/// `key` changes whenever any of those change, which restarts the warm helper. Built only by
/// `make`, so the warm helper, chunks and the one-shot fallback always agree.
struct HelperLaunch: Equatable {
    let models: URL
    let dictionaryArguments: [String]
    let key: String

    /// Nil unless the transcription model verifies. Off the main thread: verifying the models hashes them once per change.
    static func make(models: URL, manifest: URL, vocabulary: URL?, dictionaryModel: URL) -> HelperLaunch? {
        guard (try? ModelVerificationCache.shared.verify(models, manifest: manifest)) != nil,
              let fingerprint = try? ModelVerificationCache.fingerprint(models, manifest: manifest) else { return nil }
        var key = models.path + "|" + fingerprint.joined(separator: ",")
        var arguments: [String] = []
        if let vocabulary, let terms = try? Data(contentsOf: vocabulary), !terms.isEmpty,
           (try? ModelVerificationCache.shared.verify(dictionaryModel, manifest: manifest, model: .dictionary)) != nil,
           let ctc = try? ModelVerificationCache.fingerprint(dictionaryModel, manifest: manifest, model: .dictionary) {
            arguments = ["--ctc", dictionaryModel.path, "--vocabulary", vocabulary.path]
            key += "|" + ctc.joined(separator: ",") + "|" + SHA256.hash(data: terms).map { String(format: "%02x", $0) }.joined()
        }
        return HelperLaunch(models: models, dictionaryArguments: arguments, key: key)
    }
}

/// Keeps `ModelBench MODEL_DIRECTORY --serve` running with the model loaded, so a dictation skips
/// process launch and model load (about 0.4 s). One request at a time on a private queue. Any
/// failure (not ready, died, timed out) answers nil so the caller falls back to a one-shot helper;
/// the warm process restarts on the next request. A changed launch key restarts it.
final class WarmRunner: @unchecked Sendable {
    private let queue = DispatchQueue(label: "murmur.warm-runner", qos: .userInitiated)
    private var task: Process?, input: FileHandle?, output: FileHandle?, key: String?
    private var buffered = Data()
    /// Called on the runner's queue after each start with dictionary arguments: true when the
    /// helper could not load dictionary support (it then transcribes without it).
    var onDictionaryStatus: ((Bool) -> Void)?

    func prepare(helper: URL, launch: HelperLaunch) { queue.async { [self] in _ = start(helper, launch) } }
    /// `boundary`: a clip spanning a chunk cut; the answer lists dictionary replacements, not text.
    func transcribe(_ audio: URL, helper: URL, launch: HelperLaunch, boundary: Bool = false, completion: @escaping (Data?) -> Void) {
        send(boundary ? (try? JSONSerialization.data(withJSONObject: ["boundary": audio.path])).map { String(decoding: $0, as: UTF8.self) } : audio.path,
             helper: helper, launch: launch, completion: completion)
    }
    /// A meeting's Them chunk with speaker separation on: the answer adds `words` (W7d).
    func transcribeTimed(_ audio: URL, helper: URL, launch: HelperLaunch, completion: @escaping (Data?) -> Void) {
        send((try? JSONSerialization.data(withJSONObject: ["timed": audio.path])).map { String(decoding: $0, as: UTF8.self) }, helper: helper, launch: launch, completion: completion)
    }
    /// 16 kHz mono samples held in memory (an undone cancel); the answer is like an audio path's.
    func transcribe(samples: [Float], helper: URL, launch: HelperLaunch, completion: @escaping (Data?) -> Void) {
        let encoded = samples.withUnsafeBytes { Data($0).base64EncodedString() }
        send(#"{"samples":""# + encoded + #""}"#, helper: helper, launch: launch, completion: completion)
    }
    private func send(_ line: String?, helper: URL, launch: HelperLaunch, completion: @escaping (Data?) -> Void) {
        queue.async { [self] in
            guard let line, !line.contains("\n"), start(helper, launch), let input,
                  (try? input.write(contentsOf: Data((line + "\n").utf8))) != nil,
                  let line = readLine(timeout: 300) else { stopLocked(); completion(nil); return }
            completion(line)
        }
    }
    func stop() { queue.async { [self] in stopLocked() } }
    func stopNow() { queue.sync { stopLocked() } }

    private func start(_ helper: URL, _ launch: HelperLaunch) -> Bool {
        if let task, task.isRunning, self.key == launch.key { return true }
        stopLocked()
        let next = Process(), toChild = Pipe(), fromChild = Pipe()
        next.executableURL = helper; next.arguments = [launch.models.path, "--serve"] + launch.dictionaryArguments
        next.standardInput = toChild; next.standardOutput = fromChild; next.standardError = FileHandle.nullDevice
        do { try next.run() } catch { return false }
        task = next; input = toChild.fileHandleForWriting; output = fromChild.fileHandleForReading; self.key = launch.key; buffered = Data()
        // The first load of dictionary support compiles its Core ML model (about 15 s).
        guard let ready = readLine(timeout: launch.dictionaryArguments.isEmpty ? 60 : 120),
              let status = try? JSONSerialization.jsonObject(with: ready) as? [String: Any],
              status["ready"] as? Bool == true else { stopLocked(); return false }
        if !launch.dictionaryArguments.isEmpty { onDictionaryStatus?(status["vocabulary_error"] as? Bool ?? false) }
        timingLog.notice("warm-runner ready pid=\(next.processIdentifier)")
        return true
    }
    /// Reads one line; a watchdog terminates the helper on timeout, which ends the read with EOF.
    private func readLine(timeout: TimeInterval) -> Data? {
        guard let output, let task else { return nil }
        let watchdog = DispatchWorkItem { if task.isRunning { task.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: watchdog)
        defer { watchdog.cancel() }
        while true {
            if let newline = buffered.firstIndex(of: 0x0A) {
                let line = buffered[buffered.startIndex..<newline]; buffered = Data(buffered[(newline + 1)...])
                return Data(line)
            }
            let chunk = output.availableData
            if chunk.isEmpty { return nil }
            guard chunk.count <= helperOutputLimit - buffered.count else { return nil }
            buffered.append(chunk)
        }
    }
    private func stopLocked() {
        try? input?.close()
        if let task, task.isRunning { task.terminate() }
        task = nil; input = nil; output = nil; key = nil; buffered = Data()
    }
}

/// One local recording's chunk transcripts, made while it records (see Chunker), so release waits
/// only for the last chunk. Chunks go to the helper one at a time; the first failure drops the
/// rest, so the full-WAV fallback never queues behind them.
final class ChunkJob: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String?] = [], sentenceEnds: [Bool] = []
    /// A boundary clip is keyed by the chunk before its cut; it repairs a dictionary term the cut split.
    private var queued: [(index: Int, url: URL, boundary: Bool)] = [], inFlight = false
    private var repairs: [Int: [(from: String, to: String)]] = [:], boundariesOpen = 0
    private var failed = false, ended = false, discarded = false
    private var waiter: ((String?) -> Void)?
    private let transcribe: (URL, Bool, @escaping (Data?) -> Void) -> Void

    /// `transcribe(url, boundary, completion)`; see WarmRunner.transcribe.
    init(transcribe: @escaping (URL, Bool, @escaping (Data?) -> Void) -> Void) { self.transcribe = transcribe }

    /// Called on the capture queue, in order.
    func receive(_ event: Chunker.Event) {
        switch event {
        case .failed: lock.withLock { failed = true }; settle()
        case .end: lock.withLock { ended = true }; settle()
        case .chunk(let url, let last, let sentenceEnd):
            let accepted: Bool = lock.withLock {
                guard !discarded, !failed else { return false }
                texts.append(nil); sentenceEnds.append(sentenceEnd); if last { ended = true }
                queued.append((texts.count - 1, url, false)); return true
            }
            guard accepted else { try? FileManager.default.removeItem(at: url); return }
            pump()
        case .boundary(let url):
            let accepted: Bool = lock.withLock {
                guard !discarded, !failed, !texts.isEmpty else { return false }
                boundariesOpen += 1; queued.append((texts.count - 1, url, true)); return true
            }
            guard accepted else { try? FileManager.default.removeItem(at: url); return }
            pump()
        }
    }
    private func pump() {
        let next: (index: Int, url: URL, boundary: Bool)? = lock.withLock {
            guard !inFlight, !queued.isEmpty else { return nil }
            inFlight = true; return queued.removeFirst()
        }
        guard let next else { return }
        transcribe(next.url, next.boundary) { [self] line in
            try? FileManager.default.removeItem(at: next.url)
            let answer = line.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            if next.boundary {
                // A boundary that fails only skips its repair.
                let found = (answer?["replacements"] as? [[String: String]] ?? []).compactMap { r in r["from"].flatMap { f in r["to"].map { (f, $0) } } }
                lock.withLock { inFlight = false; boundariesOpen -= 1; if !found.isEmpty { repairs[next.index] = found } }
                settle(); pump(); return
            }
            let text = answer?["text"] as? String
            let dropped: [URL] = lock.withLock {
                inFlight = false
                if let text { texts[next.index] = text; return [] }
                failed = true; boundariesOpen = 0; defer { queued = [] }; return queued.map(\.url)
            }
            dropped.forEach { try? FileManager.default.removeItem(at: $0) }
            settle(); pump()
        }
    }
    /// Applies each cut's boundary replacements to the chunk texts on either side of it.
    static func repaired(_ texts: [String], _ repairs: [Int: [(from: String, to: String)]]) -> [String] {
        var texts = texts
        for (index, found) in repairs.sorted(by: { $0.key < $1.key }) where index + 1 < texts.count {
            for replacement in found {
                guard let fixed = Chunker.repair(tail: texts[index], head: texts[index + 1], from: replacement.from, to: replacement.to) else { continue }
                texts[index] = fixed.tail; texts[index + 1] = fixed.head
            }
        }
        return texts
    }
    /// Whether the last chunk arrived (or chunking failed), so `result` will answer.
    var isEnded: Bool { lock.withLock { ended || failed } }
    /// Answers once: the joined transcript, or nil on any failure.
    func result(_ completion: @escaping (String?) -> Void) { lock.withLock { waiter = completion }; settle() }
    /// A canceled or abandoned recording: later chunks are deleted unread.
    func discard() {
        let dropped: [URL] = lock.withLock { discarded = true; waiter = nil; defer { queued = [] }; return queued.map(\.url) }
        dropped.forEach { try? FileManager.default.removeItem(at: $0) }
    }
    private func settle() {
        let answer: (((String?) -> Void), String?)? = lock.withLock {
            guard let waiter, failed || (ended && boundariesOpen == 0 && !texts.contains(where: { $0 == nil })) else { return nil }
            self.waiter = nil
            return (waiter, failed ? nil : Chunker.join(Self.repaired(texts.map { $0! }, repairs), sentenceEnds: sentenceEnds))
        }
        if let (waiter, text) = answer { waiter(text) }
    }
}

/// Live words (gap #13): a second helper, `ModelBench LIVE_FOLDER --stream RATE`, that lives only
/// while one recording lasts, so its memory (about 0.5–1 GB wired) is held only then. Audio goes over
/// its stdin pipe from this object's own queue, never to disk; when the helper falls behind, buffers
/// are dropped rather than queued, so capture never waits. The text is display only.
final class LiveStream: @unchecked Sendable {
    private let queue = DispatchQueue(label: "murmur.live-words", qos: .userInitiated)
    private let task = Process()
    private let input: FileHandle
    private let lock = NSLock()
    private var backlog = 0
    /// Buffers waiting for the pipe (about 2 s of 1024-frame buffers at 48 kHz).
    private static let maxBacklog = 96

    /// `onText` runs on the main thread with the running transcript.
    init?(helper: URL, folder: URL, sampleRate: Double, onText: @escaping (String) -> Void) {
        let toChild = Pipe(), fromChild = Pipe()
        task.executableURL = helper; task.arguments = [folder.path, "--stream", String(Int(sampleRate))]
        task.standardInput = toChild; task.standardOutput = fromChild; task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return nil }
        input = toChild.fileHandleForWriting
        let output = fromChild.fileHandleForReading
        DispatchQueue.global(qos: .userInitiated).async {
            var buffered = Data()
            while true {
                let chunk = output.availableData
                if chunk.isEmpty { return }
                buffered.append(chunk)
                while let newline = buffered.firstIndex(of: 0x0A) {
                    let line = buffered[buffered.startIndex..<newline]; buffered = Data(buffered[(newline + 1)...])
                    if let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any], let text = object["partial"] as? String {
                        DispatchQueue.main.async { onText(text) }
                    }
                }
                if buffered.count > 1 << 20 { return }
            }
        }
    }

    /// Any thread: copies the first channel and sends it, or drops it when the helper is behind.
    func feed(_ buffer: AVAudioPCMBuffer) {
        guard let samples = buffer.floatChannelData, buffer.frameLength > 0 else { return }
        let data = Data(bytes: samples[0], count: Int(buffer.frameLength) * MemoryLayout<Float>.size)
        let accepted: Bool = lock.withLock {
            guard !closed, backlog < Self.maxBacklog else { return false }
            backlog += 1; return true
        }
        guard accepted else { return }
        queue.async { [self] in
            if !lock.withLock({ closed }) { _ = try? input.write(contentsOf: data) }
            lock.withLock { backlog -= 1 }
        }
    }
    /// Lock-guarded.
    private var closed = false

    /// Ends the helper: closing its stdin makes it exit. The terminate watchdog is armed here, not
    /// behind the writes: a helper that stopped reading blocks the queue on a full pipe, and
    /// terminating it fails that write (EPIPE), so its memory is never held past the recording.
    func stop() {
        guard lock.withLock({ defer { closed = true }; return !closed }) else { return }
        DispatchQueue.global().asyncAfter(deadline: .now() + 1) { [task] in if task.isRunning { task.terminate() } }
        queue.async { [input] in try? input.close() }
    }
}

/// Starts capture on a dedicated queue as soon as the tap thread sees a shortcut press, so a slow
/// main thread does not delay the microphone. The session adopts it; anything unclaimed is deleted.
final class PreStart: @unchecked Sendable {
    private let queue = DispatchQueue(label: "murmur.prestart", qos: .userInteractive)
    private var pending: (capture: Capture, url: URL, id: UUID)?
    func begin(store: RetryAudio) {
        queue.async { [self] in
            guard pending == nil, !IsSecureEventInputEnabled(), permission() == "authorized", let url = try? store.create() else { return }
            let capture = Capture()
            do { try capture.start(url: url) } catch { try? store.delete(url); return }
            let id = UUID(); pending = (capture, url, id)
            timingLog.notice("prestart engine running at=\(ProcessInfo.processInfo.systemUptime, format: .fixed(precision: 3))")
            // ponytail: unclaimed after 10 s means the main thread never handled the press.
            queue.asyncAfter(deadline: .now() + 10) { [self] in if pending?.id == id { drop(store) } }
        }
    }
    func take() -> (Capture, URL)? { queue.sync { defer { pending = nil }; return pending.map { ($0.capture, $0.url) } } }
    func discard(store: RetryAudio) { queue.sync { drop(store) } }
    private func drop(_ store: RetryAudio) {
        guard let p = pending else { return }
        pending = nil; p.capture.stop(); try? store.delete(p.url)
    }
}

// Chunk transcript text, used by ChunkJob. Capture.swift keeps the audio half of Chunker.
extension Chunker {
    /// Applies a boundary clip's dictionary replacement to the two chunk texts around its cut, when
    /// the replaced words are the last 1–3 words of `tail` plus the first 1–3 of `head` (letters
    /// only, so a word the cut split in two also matches). The term goes at the end of `tail`; the
    /// cut's own punctuation on the tail side is dropped, the head's following punctuation kept.
    /// nil when the replacement does not cross this cut (chunks already handle the rest).
    static func repair(tail: String, head: String, from: String, to: String) -> (tail: String, head: String)? {
        func letters(_ text: Substring) -> String { String(text.lowercased().filter { $0.isLetter || $0.isNumber }) }
        let target = letters(Substring(from))
        let tailWords = tail.split(separator: " "), headWords = head.split(separator: " ")
        guard !target.isEmpty, !tailWords.isEmpty, !headWords.isEmpty else { return nil }
        for t in 1...min(3, tailWords.count) {
            for h in 1...min(3, headWords.count) {
                let spoken = tailWords.suffix(t).map(letters).joined() + headWords.prefix(h).map(letters).joined()
                guard spoken == target else { continue }
                let last = headWords[h - 1]
                let trailing = String(last.reversed().prefix { !$0.isLetter && !$0.isNumber }.reversed())
                let newTail = (tailWords.dropLast(t).map(String.init) + [to + trailing]).joined(separator: " ")
                return (newTail, headWords.dropFirst(h).joined(separator: " "))
            }
        }
        return nil
    }

    /// Joins chunk transcripts. The model punctuates every chunk as if the speaker stopped there:
    /// it ends each with a period and capitalizes each first word. A cut at a sentence-length
    /// pause (`sentenceEnds[i]`) keeps or adds the period. At any other cut, an unpunctuated
    /// chunk's capitalized next word is lowercased. A sentence never ends on a word like "the" or
    /// "and", so that period always goes. Only words in `lowercaseStarts` lose a capital, so names
    /// and "I" keep theirs.
    static func join(_ texts: [String], sentenceEnds: [Bool]) -> String {
        var out = "", sentenceBreak = false
        for (i, raw) in texts.enumerated() {
            var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty {
                if !out.isEmpty {
                    let lastWord = String(out.split(separator: " ").last ?? "").lowercased().trimmingCharacters(in: .punctuationCharacters)
                    let firstWord = String(text.prefix { $0.isLetter || $0 == "'" || $0 == "’" })
                    let dangling = danglingWords.contains(lastWord)
                    if !sentenceBreak || dangling {
                        let lower = lowercaseStarts.contains(firstWord.lowercased()) && firstWord.first?.isUppercase == true
                        // A model period is real unless the sentence would end on a word like "the";
                        // a short pause alone does not prove the speaker kept going.
                        let period = out.last == "." && !out.hasSuffix("..")
                        if period && dangling { out.removeLast() }
                        if lower && (!period || dangling) { text = firstWord.lowercased() + text.dropFirst(firstWord.count) }
                    } else if let last = out.last, !".?!".contains(last) { out += "." }
                    out += " "
                }
                out += text
            }
            sentenceBreak = i < sentenceEnds.count && sentenceEnds[i]
        }
        return out
    }
    /// Words a sentence cannot end on.
    static let danglingWords: Set<String> = ["a", "an", "the", "and", "or", "but", "nor", "of", "my", "your", "our", "their", "its", "than", "because"]
    /// Common words that start a chunk only because the model capitalized it.
    static let lowercaseStarts: Set<String> = danglingWords.union(["to", "in", "on", "at", "for", "with", "from", "by", "into", "as", "that", "which", "who", "is", "are", "was", "were", "be", "been", "will", "would", "could", "should", "can", "may", "might", "must", "do", "does", "did", "have", "has", "had", "this", "these", "those", "his", "her", "very", "so", "if", "when", "while", "where", "like", "about", "some", "any", "every", "each", "no", "not", "it", "it's", "we", "you", "they", "he", "she", "there", "here", "then", "more", "most", "less", "also", "just", "all", "one", "two", "what", "how", "why", "now", "even", "only", "still", "other", "another", "such", "make", "made", "get", "got", "go", "going", "see", "use", "used", "new", "same", "up", "out", "over", "after", "before", "between", "through", "during", "without", "within", "instead", "rather", "well", "really", "much", "many", "few", "things", "thing", "way", "people"])
}
