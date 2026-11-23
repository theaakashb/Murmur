import Foundation

// Managed local cleanup: the Murmur Cleanup 2B model (a Qwen3.5-2B fine-tune, Apache-2.0, pinned in artifact-pins.json) run by the bundled
// LocalCleanupRunner helper over stdin/stdout. No network is used for inference and no transcript is logged. The model has
// one fixed prompt, so this provider reads the typed `CleanupInput` and ignores the built prompt text: screen context,
// custom instructions and rewrite presets are not supported on this route (ProcessingCoordinator never sends them).

enum LocalCleanupIdentity {
    static let modelName = "Murmur Cleanup 2B"
    static let helperName = "LocalCleanupRunner"
    /// Recorded in History: the pinned repository and the first 7 characters of its revision (artifact-pins.json).
    static let modelID = "clouddevaakash/murmur-cleanup-2b@3416389"
    /// A stable app-owned location, not a user setting.
    static func directory(home: URL) -> URL {
        home.appendingPathComponent("Library/Application Support/Dictation/Models/murmur-cleanup-2b")
    }
}

struct LocalCleanupRequest: Equatable, Sendable {
    var text: String
    /// Always "casual" (the model's default): Professional does not run on this Mac.
    var tone: String
    var terms: [String]
}

/// Longest input accepted by one request; the helper also checks the token count.
let localCleanupMaximumCharacters = 4_000

struct LocalModelCleanupProvider: CleanupProvider {
    let id = ProviderID.localModel
    /// Nil when the model and helper are installed.
    var status: @Sendable () -> OnDeviceModelUnavailable? = { LocalCleanupRunner.installedStatus() }
    var generate: @Sendable (LocalCleanupRequest) async throws -> String = { try await LocalCleanupRunner.shared.clean($0) }
    var modelID: String? { LocalCleanupIdentity.modelID }
    /// One fixed prompt: no screen context or style notes, and rewrite presets fall back to local rules.
    var shape: CleanupShape { CleanupShape(takesContext: false, sectionTarget: 150, keepsCorrectionsAttached: true, takesPresets: false) }
    var disclosure: DataDisclosure {
        DataDisclosure(provider: id, stage: .cleanup, destinationHost: "\(LocalCleanupIdentity.modelName), downloaded to this Mac", leavesDevice: [],
                       staysOnDevice: ["the transcript"], retentionNote: "")
    }

    func cleanup(_ messages: CleanupMessages) async throws -> String {
        // Rewrite presets and notes carry no typed input: this model cannot follow them, so they are refused, not approximated.
        guard let input = messages.input else { throw ProviderError.invalidConfiguration(id) }
        if let reason = status() { throw ProviderError.modelUnavailable(id, reason) }
        guard input.raw.count <= localCleanupMaximumCharacters else { throw ProviderError.payloadTooLarge(id, limitBytes: nil) }
        return try await generate(LocalCleanupRequest(text: input.raw, tone: "casual", terms: Array(input.terms.prefix(12))))
    }

    func validate() async throws { if let reason = status() { throw ProviderError.modelUnavailable(id, reason) } }

    func prewarm() { if status() == nil { Task { await LocalCleanupRunner.shared.warm() } } }
}

/// One helper process, one request at a time, a fresh chat per request inside it. Cancelling or timing out ends only this
/// child; the next request starts a new one. It also exits when idle, on memory pressure and at quit.
actor LocalCleanupRunner {
    static let shared = LocalCleanupRunner()
    static var modelDirectory = LocalCleanupIdentity.directory(home: FileManager.default.homeDirectoryForCurrentUser)
    /// Full pinned-checksum check of the installed files, set by the app; nil (checks) skips it.
    nonisolated(unsafe) static var verifier: (@Sendable () -> Bool)?
    static var helperURL: URL? = Bundle.main.executableURL?.deletingLastPathComponent().appendingPathComponent(LocalCleanupIdentity.helperName)

    static func installedStatus() -> OnDeviceModelUnavailable? {
        guard let helper = helperURL, FileManager.default.isExecutableFile(atPath: helper.path),
              FileManager.default.fileExists(atPath: modelDirectory.appendingPathComponent("model.safetensors").path) else { return .modelNotDownloaded }
        return nil
    }

    private let executable: URL?, directory: URL
    private let startupSeconds: TimeInterval, requestSeconds: TimeInterval, idleSeconds: TimeInterval
    private var process: Process?
    private var input: FileHandle?
    private var generation = 0
    private var queue: [String] = []
    private var exited = true
    private var waiter: CheckedContinuation<String?, Never>?
    private var busy = false
    private var owner: UUID?
    private var waiting: [(ticket: UUID, continuation: CheckedContinuation<Bool, Never>)] = []
    private var verified = false
    private var evictWhenFree = false
    private var nextID = 1
    private var idleTask: Task<Void, Never>?
    private var pressure: DispatchSourceMemoryPressure?

    init(executable: URL? = LocalCleanupRunner.helperURL, directory: URL = LocalCleanupRunner.modelDirectory,
         startupSeconds: TimeInterval = 60, requestSeconds: TimeInterval = 30, idleSeconds: TimeInterval = 300) {
        self.executable = executable; self.directory = directory
        self.startupSeconds = startupSeconds; self.requestSeconds = requestSeconds; self.idleSeconds = idleSeconds
    }

    func clean(_ request: LocalCleanupRequest) async throws -> String {
        let id = ProviderID.localModel, ticket = UUID()
        // Cancelling a queued request removes only that request; cancelling the active one ends only its own child.
        let acquired = await withTaskCancellationHandler { await acquire(ticket) } onCancel: { Task { await self.cancel(ticket) } }
        guard acquired else { throw ProviderError.cancelled }
        defer { release() }
        if Task.isCancelled { throw ProviderError.cancelled }
        idleTask?.cancel()
        if !verified {
            // Verified once per launch of the app, off this actor's queue; remove/download resets it (shutdown).
            let check = Self.verifier
            let ok = await Task.detached { check?() ?? true }.value
            guard ok else { throw ProviderError.modelUnavailable(id, .modelDamaged) }
            verified = true
            if Task.isCancelled { throw ProviderError.cancelled }
        }
        let reply = await withTaskCancellationHandler { await roundTrip(request) } onCancel: { Task { await self.cancel(ticket) } }
        scheduleIdle()
        if Task.isCancelled { terminate(); throw ProviderError.cancelled }
        switch reply {
        case .text(let text): return text
        case .tooLong: throw ProviderError.payloadTooLarge(id, limitBytes: nil)
        case .timeout: throw ProviderError.timeout(id)
        case .failed: throw ProviderError.malformedResponse(id)
        }
    }

    /// Verifies the files and loads the model ahead of the first request (route selected, dictation started), so that
    /// cost is not inside a dictation. Does nothing while a request runs or when the model is not installed.
    func warm() async {
        guard !busy, Self.installedStatus() == nil, await acquire(UUID()) else { return }
        defer { release() }
        idleTask?.cancel()
        if !verified {
            let check = Self.verifier
            guard await Task.detached(operation: { check?() ?? true }).value else { return }
            verified = true
        }
        if process == nil, launch() { if await nextLine(timeout: startupSeconds) == nil { terminate() } }
        scheduleIdle()
    }

    /// Ends the helper (idle, memory pressure, quit). Safe at any time; an in-flight request fails and falls back.
    func shutdown() { idleTask?.cancel(); verified = false; terminate() }


    private enum Reply { case text(String), tooLong, timeout, failed }

    private func acquire(_ ticket: UUID) async -> Bool {
        if Task.isCancelled { return false }
        if !busy { busy = true; owner = ticket; return true }
        return await withCheckedContinuation { waiting.append((ticket, $0)) }
    }
    private func release() {
        if waiting.isEmpty {
            busy = false; owner = nil
            if evictWhenFree { evictWhenFree = false; terminate() }
        } else { let next = waiting.removeFirst(); owner = next.ticket; next.continuation.resume(returning: true) }
    }
    private func cancel(_ ticket: UUID) {
        if owner == ticket { terminate() }
        else if let index = waiting.firstIndex(where: { $0.ticket == ticket }) { waiting.remove(at: index).continuation.resume(returning: false) }
    }

    private func roundTrip(_ request: LocalCleanupRequest) async -> Reply {
        if process == nil {
            guard launch() else { return .failed }
            guard let hello = await nextLine(timeout: startupSeconds), hello.contains("\"ready\":true") else { terminate(); return .timeout }
        }
        let id = nextID; nextID += 1
        let body: [String: Any] = ["id": id, "text": request.text, "tone": request.tone, "terms": request.terms]
        guard let data = try? JSONSerialization.data(withJSONObject: body), let input else { terminate(); return .failed }
        do { try input.write(contentsOf: data + Data("\n".utf8)) } catch { terminate(); return .failed }
        guard let line = await nextLine(timeout: requestSeconds) else { terminate(); return .timeout }
        guard let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], object["id"] as? Int == id else { terminate(); return .failed }
        if let text = object["text"] as? String { return .text(text) }
        return (object["error"] as? String) == "too-long" ? .tooLong : .failed
    }

    private func launch() -> Bool {
        guard let executable else { return false }
        let task = Process(), inPipe = Pipe(), outPipe = Pipe()
        task.executableURL = executable; task.arguments = [directory.path]
        task.environment = ["PATH": "/usr/bin:/bin", "HF_HUB_OFFLINE": "1"]
        task.standardInput = inPipe; task.standardOutput = outPipe; task.standardError = FileHandle.nullDevice
        do { try task.run() } catch { return false }
        generation += 1; let mine = generation
        process = task; input = inPipe.fileHandleForWriting; queue = []; exited = false
        Task.detached { [weak self] in
            do { for try await line in outPipe.fileHandleForReading.bytes.lines { await self?.deliver(line, generation: mine) } } catch {}
            await self?.ended(generation: mine)
        }
        if pressure == nil {
            let source = DispatchSource.makeMemoryPressureSource(eventMask: [.warning, .critical], queue: .global())
            source.setEventHandler { [weak self] in Task { await self?.pressureEvent() } }
            source.resume(); pressure = source
        }
        return true
    }

    /// Memory pressure frees the model now if idle, or as soon as the request in flight finishes.
    private func pressureEvent() { if busy { evictWhenFree = true } else { terminate() } }

    private func deliver(_ line: String, generation mine: Int) {
        guard mine == generation else { return }
        // A reply this large is not ours; stop rather than buffer without bound.
        guard line.utf8.count <= 200_000 else { terminate(); return }
        if let waiter { self.waiter = nil; waiter.resume(returning: line) } else { queue.append(line) }
    }

    private func ended(generation mine: Int) {
        guard mine == generation else { return }
        exited = true; process = nil; input = nil
        if let waiter { self.waiter = nil; waiter.resume(returning: nil) }
    }

    private func nextLine(timeout: TimeInterval) async -> String? {
        if !queue.isEmpty { return queue.removeFirst() }
        if exited { return nil }
        let mine = generation
        let timer = Task { try? await Task.sleep(for: .seconds(timeout)); if !Task.isCancelled { self.timedOut(generation: mine) } }
        defer { timer.cancel() }
        return await withCheckedContinuation { waiter = $0 }
    }

    private func timedOut(generation mine: Int) { if mine == generation { terminate() } }

    private func terminate() {
        process?.terminate()
        process = nil; input = nil; exited = true; generation += 1; queue = []
        if let waiter { self.waiter = nil; waiter.resume(returning: nil) }
    }

    private func scheduleIdle() {
        idleTask?.cancel()
        let seconds = idleSeconds
        idleTask = Task { try? await Task.sleep(for: .seconds(seconds)); if !Task.isCancelled { await self.idleExpired() } }
    }
    private func idleExpired() { if !busy { terminate() } }
}
