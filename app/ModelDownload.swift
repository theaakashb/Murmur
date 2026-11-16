import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Downloads the pinned Parakeet Unified batch files from Hugging Face, verifies every byte against
// artifact-pins.json, and moves the verified set into place with one rename. Foundation only, so the
// checks also run on Linux. Digests are in Digests.swift, the manifest and license in ModelManifest.swift.

// MARK: - Errors

/// Every case maps to a short, user-facing message that names no file, path, or URL.
enum ModelDownloadError: Error, Equatable {
    case network
    case diskFull
    case checksumMismatch
    case httpStatus(Int)
    case cancelled
    case invalidManifest
    case unsafePath
    case fileSystem
    case busy

    var userMessage: String {
        switch self {
        case .network: return "The model download could not reach the server. Check your internet connection and try again."
        case .diskFull: return "There is not enough free disk space for the model. Free up space and try again."
        case .checksumMismatch: return "A downloaded model file did not match its pinned checksum and was discarded. Try the download again."
        case .httpStatus(let code): return "The model server returned an error (HTTP \(code)). Try again later."
        case .cancelled: return "The model download was cancelled."
        case .invalidManifest: return "The pinned model manifest is missing or invalid. Reinstall Murmur."
        case .unsafePath: return "The pinned model manifest contains an unsafe file path, so nothing was downloaded."
        case .fileSystem: return "Murmur could not write the model files. Check that the model folder is writable."
        case .busy: return "A model download is already in progress."
        }
    }

    /// Maps file-write failures; ENOSPC / out-of-space becomes `.diskFull`.
    static func fromWrite(_ error: Error) -> ModelDownloadError {
        if let known = error as? ModelDownloadError { return known }
        var current: NSError? = error as NSError
        while let ns = current {
            if (ns.domain == NSPOSIXErrorDomain && (ns.code == Int(ENOSPC) || ns.code == Int(EDQUOT)))
                || (ns.domain == NSCocoaErrorDomain && ns.code == NSFileWriteOutOfSpaceError) { return .diskFull }
            current = ns.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        return .fileSystem
    }
}

// MARK: - Transport

/// One GET. `rangeStart > 0` asks the server for `Range: bytes=<rangeStart>-`.
struct ModelFetchRequest: Equatable {
    let url: URL
    let rangeStart: Int64
}

/// Blocking, streaming HTTP GET. Implementations follow redirects, call `onResponse` once with the
/// final status code before any body bytes, then `onData` in order. If a callback throws, stop and
/// rethrow that error. Poll `isCancelled` and throw `ModelDownloadError.cancelled` when it is true.
/// Transport failures must be thrown as `ModelDownloadError.network`.
protocol ModelDownloadTransport: AnyObject {
    func fetch(_ request: ModelFetchRequest, isCancelled: @escaping () -> Bool,
               onResponse: @escaping (Int) throws -> Void, onData: @escaping (Data) throws -> Void) throws
}

/// URLSession-backed transport. Ephemeral configuration: no cookies, no shared URL cache.
final class URLSessionModelTransport: ModelDownloadTransport {
    private let configuration: URLSessionConfiguration

    init(configuration: URLSessionConfiguration? = nil) {
        let config = configuration ?? URLSessionConfiguration.ephemeral
        if configuration == nil {
            config.timeoutIntervalForRequest = 60
            config.requestCachePolicy = .reloadIgnoringLocalCacheData
            config.urlCache = nil
        }
        self.configuration = config
    }

    func fetch(_ request: ModelFetchRequest, isCancelled: @escaping () -> Bool,
               onResponse: @escaping (Int) throws -> Void, onData: @escaping (Data) throws -> Void) throws {
        var urlRequest = URLRequest(url: request.url)
        urlRequest.httpMethod = "GET"
        if request.rangeStart > 0 { urlRequest.setValue("bytes=\(request.rangeStart)-", forHTTPHeaderField: "Range") }
        let delegate = FetchDelegate(onResponse: onResponse, onData: onData)
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: queue)
        defer { session.invalidateAndCancel() }
        let task = session.dataTask(with: urlRequest)
        task.resume()
        while delegate.done.wait(timeout: .now() + 0.1) == .timedOut {
            if isCancelled() { delegate.fail(ModelDownloadError.cancelled); task.cancel() }
        }
        if let error = delegate.failure { throw error }
        if isCancelled() { throw ModelDownloadError.cancelled }
    }

    private final class FetchDelegate: NSObject, URLSessionDataDelegate {
        let done = DispatchSemaphore(value: 0)
        /// Callbacks run serially on the session's one-operation delegate queue; the error is locked.
        private final class State: @unchecked Sendable {
            let onResponse: (Int) throws -> Void
            let onData: (Data) throws -> Void
            private let lock = NSLock()
            private var stored: Error?
            init(onResponse: @escaping (Int) throws -> Void, onData: @escaping (Data) throws -> Void) {
                self.onResponse = onResponse; self.onData = onData
            }
            var error: Error? { lock.lock(); defer { lock.unlock() }; return stored }
            func set(_ error: Error) { lock.lock(); if stored == nil { stored = error }; lock.unlock() }
        }
        private let state: State
        private var onResponse: (Int) throws -> Void { state.onResponse }
        private var onData: (Data) throws -> Void { state.onData }

        init(onResponse: @escaping (Int) throws -> Void, onData: @escaping (Data) throws -> Void) {
            state = State(onResponse: onResponse, onData: onData)
        }

        var failure: Error? { state.error }
        func fail(_ error: Error) { state.set(error) }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
            guard let http = response as? HTTPURLResponse else { fail(ModelDownloadError.network); completionHandler(.cancel); return }
            do { try onResponse(http.statusCode); completionHandler(.allow) } catch { fail(error); completionHandler(.cancel) }
        }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
            guard failure == nil else { return }
            do { try onData(data) } catch { fail(error); dataTask.cancel() }
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
            if let error {
                let code = (error as? URLError)?.code
                fail(code == .cancelled ? ModelDownloadError.cancelled : ModelDownloadError.network)
            }
            done.signal()
        }
    }
}

// MARK: - Downloader

struct ModelDownloadProgress: Equatable {
    let bytesDone: Int64
    let totalBytes: Int64
    var fraction: Double { totalBytes > 0 ? min(1, Double(bytesDone) / Double(totalBytes)) : 0 }
}

enum ModelStatus: Equatable {
    case notDownloaded
    case downloading(ModelDownloadProgress)
    case verifying
    case ready
    /// `reason` is user-facing and content-free.
    case corrupt(String)
    case failed(ModelDownloadError)
}

/// Downloads exactly the pinned batch files into a staging directory beside `target`, verifies each
/// file's size and checksum, then renames the staging directory onto `target`. The target is never
/// partially written: until the final rename it holds either nothing or the previous model.
///
/// `install` blocks; call it off the main thread (or use `start`). `cancel`, `status`, and `license`
/// are safe from any thread.
final class ModelDownloader {
    static let corruptReason = "Some model files are missing or damaged. Remove the model and download it again."
    static let notDirectoryReason = "The model location is not a folder. Remove the model and download it again."

    let manifest: ModelManifest
    let target: URL
    private let transport: ModelDownloadTransport
    private let hasher: FileHasher
    private let baseURL: URL
    private let freeSpace: (URL) -> Int64?
    private let fileManager = FileManager.default

    private let lock = NSLock()
    private var active: ModelStatus?
    private var lastFailure: ModelDownloadError?
    private var cancelRequested = false
    private var running = false
    private var progressCallback: ((ModelDownloadProgress) -> Void)?
    private var lastPublished: ModelDownloadProgress?

    /// - Parameters:
    ///   - target: final model directory; its parent must be on the volume where staging is created.
    ///   - freeSpace: available bytes on the volume holding a URL; nil skips the preflight.
    init(manifest: ModelManifest, target: URL, transport: ModelDownloadTransport = URLSessionModelTransport(),
         hasher: FileHasher = StreamingFileHasher(), baseURL: URL = URL(string: "https://huggingface.co")!,
         freeSpace: @escaping (URL) -> Int64? = ModelDownloader.systemFreeSpace) {
        self.manifest = manifest
        self.target = target.standardizedFileURL
        self.transport = transport
        self.hasher = hasher
        self.baseURL = baseURL
        self.freeSpace = freeSpace
    }

    var license: ModelLicenseInfo { manifest.license }
    /// Bytes to download, from the pinned sizes. Show this before starting.
    var downloadSize: Int64 { manifest.totalBytes }

    /// Hidden sibling of `target`, keyed by revision so a retry resumes the same pinned set.
    var stagingDirectory: URL {
        target.deletingLastPathComponent()
            .appendingPathComponent(".\(target.lastPathComponent).download-\(manifest.revision.prefix(12))", isDirectory: true)
    }

    static func systemFreeSpace(_ url: URL) -> Int64? {
        var probe = url
        while !FileManager.default.fileExists(atPath: probe.path) && probe.pathComponents.count > 1 { probe.deleteLastPathComponent() }
        return ((try? FileManager.default.attributesOfFileSystem(forPath: probe.path))?[.systemFreeSize] as? NSNumber)?.int64Value
    }

    /// Cheap status: in-flight state, then the last failure, then an on-disk size check (no hashing).
    /// Use `verifyInstalled()` for a full checksum pass.
    func status() -> ModelStatus {
        lock.lock()
        if let active { lock.unlock(); return active }
        if let lastFailure { lock.unlock(); return .failed(lastFailure) }
        lock.unlock()
        return diskStatus(hash: false)
    }

    /// Full size + checksum check of the installed model. Does not change `status()` state.
    func verifyInstalled(isCancelled: @escaping () -> Bool = { false }) -> ModelStatus {
        diskStatus(hash: true, isCancelled: isCancelled)
    }

    /// Requests cancellation; `install` throws `.cancelled` at the next chunk. Already-verified staged
    /// files are kept so a later `install` can skip them; `remove()` discards them.
    func cancel() { lock.lock(); if running { cancelRequested = true }; lock.unlock() }

    /// Runs `install` on a background queue. Callbacks arrive on that queue.
    func start(progress: ((ModelDownloadProgress) -> Void)? = nil,
               completion: @escaping (Result<Void, ModelDownloadError>) -> Void) {
        DispatchQueue.global(qos: .utility).async { [self] in
            do { try install(progress: progress); completion(.success(())) } catch {
                completion(.failure(error as? ModelDownloadError ?? .fileSystem))
            }
        }
    }

    /// Downloads (or resumes) the pinned files, verifies all of them, then installs atomically.
    /// Throws `ModelDownloadError`. An existing `target` is replaced only after every file verifies.
    func install(progress: ((ModelDownloadProgress) -> Void)? = nil) throws {
        lock.lock()
        guard !running else { lock.unlock(); throw ModelDownloadError.busy }
        running = true; cancelRequested = false; lastFailure = nil; progressCallback = progress; lastPublished = nil
        active = .downloading(ModelDownloadProgress(bytesDone: 0, totalBytes: manifest.totalBytes))
        lock.unlock()
        do {
            try performInstall()
            finish(failure: nil)
        } catch {
            let classified = error as? ModelDownloadError ?? ModelDownloadError.fromWrite(error)
            finish(failure: classified)
            throw classified
        }
    }

    /// Deletes the installed model and any staged partial download. Refuses while a download runs.
    func remove() throws {
        lock.lock()
        guard !running else { lock.unlock(); throw ModelDownloadError.busy }
        lastFailure = nil
        lock.unlock()
        for url in [target, stagingDirectory] where fileManager.fileExists(atPath: url.path) || isSymlink(url) {
            do { try fileManager.removeItem(at: url) } catch { throw ModelDownloadError.fileSystem }
        }
    }

    // MARK: Internals

    private var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelRequested }

    private func setActive(_ status: ModelStatus) { lock.lock(); active = status; lock.unlock() }

    private func publish(_ value: ModelDownloadProgress) {
        lock.lock()
        let changed = lastPublished != value
        lastPublished = value
        active = .downloading(value)
        let callback = progressCallback
        lock.unlock()
        if changed { callback?(value) }
    }

    private func finish(failure: ModelDownloadError?) {
        lock.lock(); running = false; active = nil; lastFailure = failure; cancelRequested = false; progressCallback = nil; lock.unlock()
    }

    private func checkCancelled() throws { if isCancelled { throw ModelDownloadError.cancelled } }

    private func isSymlink(_ url: URL) -> Bool {
        (try? fileManager.attributesOfItem(atPath: url.path)[.type] as? FileAttributeType) == .typeSymbolicLink
    }

    /// Resolves `rfilename` under `root`, refusing anything that would land outside it.
    private func location(of file: ModelManifestFile, in root: URL) throws -> URL {
        guard ModelManifest.isSafeRelativePath(file.rfilename) else { throw ModelDownloadError.unsafePath }
        let url = root.appendingPathComponent(file.rfilename).standardizedFileURL
        guard url.path.hasPrefix(root.standardizedFileURL.path + "/") else { throw ModelDownloadError.unsafePath }
        return url
    }

    private func verify(_ file: ModelManifestFile, at url: URL) throws -> Bool {
        guard fileSize(url) == file.size else { return false }
        let digest = try hasher.hexDigest(of: url, algorithm: file.algorithm, isCancelled: { [weak self] in self?.isCancelled ?? true })
        return digest == file.checksum
    }

    private func diskStatus(hash: Bool, isCancelled: @escaping () -> Bool = { false }) -> ModelStatus {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: target.path, isDirectory: &isDirectory) else { return .notDownloaded }
        guard isDirectory.boolValue, !isSymlink(target) else { return .corrupt(Self.notDirectoryReason) }
        for file in manifest.files {
            guard let url = try? location(of: file, in: target), fileSize(url) == file.size else { return .corrupt(Self.corruptReason) }
            if hash {
                guard let digest = try? hasher.hexDigest(of: url, algorithm: file.algorithm, isCancelled: isCancelled),
                      digest == file.checksum else { return .corrupt(Self.corruptReason) }
            }
        }
        return .ready
    }

    private func performInstall() throws {
        let staging = stagingDirectory
        let total = manifest.totalBytes
        var done: Int64 = 0
        func report() {
            report(done)
        }
        func report(_ bytes: Int64) {
            let value = ModelDownloadProgress(bytesDone: bytes, totalBytes: total)
            publish(value)
        }
        // Validate every path and URL before touching the network or disk.
        let plan: [(ModelManifestFile, URL, URL)] = try manifest.files.map { file in
            guard let url = manifest.resolveURL(for: file, base: baseURL) else { throw ModelDownloadError.invalidManifest }
            return (file, try location(of: file, in: staging), url)
        }
        if isSymlink(staging) { try? fileManager.removeItem(at: staging) }
        do { try fileManager.createDirectory(at: staging, withIntermediateDirectories: true) } catch { throw ModelDownloadError.fromWrite(error) }

        // Preflight: remaining bytes against free space (only counts final, not yet verified, files).
        let staged = plan.reduce(Int64(0)) { sum, item in
            sum + (fileSize(item.1) ?? fileSize(item.1.appendingPathExtension("part")) ?? 0)
        }
        if let free = freeSpace(staging), free < total - staged { throw ModelDownloadError.diskFull }

        report()
        for (file, destination, url) in plan {
            try checkCancelled()
            let partial = destination.appendingPathExtension("part")
            do { try fileManager.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true) }
            catch { throw ModelDownloadError.fromWrite(error) }
            // A final-named staged file was verified when it was downloaded; re-hash before trusting it.
            if fileManager.fileExists(atPath: destination.path) {
                setActive(.verifying)
                if try verify(file, at: destination) { done += file.size; report(); continue }
                try? fileManager.removeItem(at: destination)
            }
            done = try download(file, from: url, to: partial, startingAt: done, report: report)
            setActive(.verifying)
            guard try verify(file, at: partial) else {
                try? fileManager.removeItem(at: partial)
                throw ModelDownloadError.checksumMismatch
            }
            do { try fileManager.moveItem(at: partial, to: destination) } catch { throw ModelDownloadError.fromWrite(error) }
            report()
        }
        try checkCancelled()
        setActive(.verifying)
        try pruneUnexpected(in: staging, keeping: Set(plan.map { $0.0.rfilename }))
        try checkCancelled()
        try swapIntoPlace(staging)
    }

    /// Streams one file into `partial`, resuming from its current length when the server honors Range.
    private func download(_ file: ModelManifestFile, from url: URL, to partial: URL,
                          startingAt doneBefore: Int64, report: (Int64) -> Void) throws -> Int64 {
        var offset = fileSize(partial) ?? 0
        if offset > file.size { try? fileManager.removeItem(at: partial); offset = 0 }
        if offset == file.size { report(doneBefore + offset); return doneBefore + offset }   // complete but unverified
        report(doneBefore + offset)
        if offset == 0 {
            try? fileManager.removeItem(at: partial)
            guard fileManager.createFile(atPath: partial.path, contents: nil) else { throw ModelDownloadError.fileSystem }
        }
        let handle: FileHandle
        do { handle = try FileHandle(forWritingTo: partial) } catch { throw ModelDownloadError.fromWrite(error) }
        defer { try? handle.close() }

        // State shared with transport callbacks (which may run on another thread, strictly in order).
        final class Box { var written: Int64 = 0; var started = false; var error: Error? }
        let box = Box()
        let startDone = doneBefore
        let sizeLimit = file.size
        var baseOffset = offset
        let progressLock = NSLock()
        var latest = doneBefore + offset
        let request = ModelFetchRequest(url: url, rangeStart: offset)
        do {
            try transport.fetch(request, isCancelled: { [weak self] in self?.isCancelled ?? true }, onResponse: { status in
                switch status {
                case 206 where baseOffset > 0:
                    try handle.seekToEnd()
                case 200:
                    // Server ignored Range (or none was sent): restart the file from zero.
                    try handle.truncate(atOffset: 0)
                    baseOffset = 0
                default:
                    if status == 416 || (status == 206 && baseOffset == 0) { try? handle.truncate(atOffset: 0); baseOffset = 0 }
                    throw ModelDownloadError.httpStatus(status)
                }
                box.started = true
            }, onData: { [weak self] data in
                guard box.started else { throw ModelDownloadError.network }
                if self?.isCancelled ?? true { throw ModelDownloadError.cancelled }
                guard baseOffset + box.written + Int64(data.count) <= sizeLimit else { throw ModelDownloadError.checksumMismatch }
                do { try handle.write(contentsOf: data) } catch { throw ModelDownloadError.fromWrite(error) }
                box.written += Int64(data.count)
                progressLock.lock(); latest = startDone + baseOffset + box.written; progressLock.unlock()
                let value = ModelDownloadProgress(bytesDone: startDone + baseOffset + box.written, totalBytes: self?.manifest.totalBytes ?? 0)
                self?.publish(value)
            })
        } catch let error as ModelDownloadError {
            try? handle.synchronize()
            if error == .checksumMismatch { try? fileManager.removeItem(at: partial) }
            throw error
        } catch {
            throw ModelDownloadError.fromWrite(error)
        }
        do { try handle.synchronize() } catch { throw ModelDownloadError.fromWrite(error) }
        if baseOffset + box.written != file.size { throw ModelDownloadError.network }  // truncated body
        progressLock.lock(); defer { progressLock.unlock() }
        return latest
    }

    /// Deletes anything in staging that is not a pinned file (or a directory leading to one), so no
    /// stray or substituted file can be installed alongside the model.
    /// `expected` holds paths relative to `staging` (the pinned rfilenames). Relative paths, because
    /// standardizing an existing path can drop a "/private" prefix that a not-yet-created one keeps.
    private func pruneUnexpected(in staging: URL, keeping expected: Set<String>) throws {
        let root = staging.path
        guard let enumerator = fileManager.enumerator(atPath: root) else { throw ModelDownloadError.fileSystem }
        var extras: [String] = []
        while let relative = enumerator.nextObject() as? String {
            let full = root + "/" + relative
            var isDirectory: ObjCBool = false
            _ = fileManager.fileExists(atPath: full, isDirectory: &isDirectory)
            if isDirectory.boolValue && !isSymlink(URL(fileURLWithPath: full)) {
                if !expected.contains(where: { $0.hasPrefix(relative + "/") }) { extras.append(full); enumerator.skipDescendants() }
            } else if !expected.contains(relative) { extras.append(full) }
        }
        for path in extras { do { try fileManager.removeItem(atPath: path) } catch { throw ModelDownloadError.fileSystem } }
    }

    /// Renames staging onto target. With no existing target this is a single atomic rename; otherwise
    /// the old model is renamed aside first and restored if the second rename fails.
    private func swapIntoPlace(_ staging: URL) throws {
        let parent = target.deletingLastPathComponent()
        if fileManager.fileExists(atPath: target.path) || isSymlink(target) {
            let aside = parent.appendingPathComponent(".\(target.lastPathComponent).old-\(UUID().uuidString)")
            do { try fileManager.moveItem(at: target, to: aside) } catch { throw ModelDownloadError.fromWrite(error) }
            do { try fileManager.moveItem(at: staging, to: target) } catch {
                try? fileManager.moveItem(at: aside, to: target)
                throw ModelDownloadError.fromWrite(error)
            }
            try? fileManager.removeItem(at: aside)
        } else {
            do { try fileManager.moveItem(at: staging, to: target) } catch { throw ModelDownloadError.fromWrite(error) }
        }
    }
}
