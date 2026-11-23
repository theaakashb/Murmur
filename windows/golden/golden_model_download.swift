// deps: Digests ModelManifest ModelDownload ModelText DictationLanguages
import Foundation

// Golden outputs of Digests.swift, ModelManifest.swift and ModelDownload.swift over synthetic data and a fake transport (no network):
// digests of fixed inputs, manifest parsing (cases carry the pins JSON they parse), the real pins files, resolve URLs, path rules,
// error messages, and download state-machine traces (requests, progress, files on disk after each attempt). One case per line.
// Run from the repository root.

func sha256Hex(_ data: Data) -> String { let h = PortableSHA256(); h.update(data); return hexString(h.finalize()) }
func sha1Hex(_ data: Data) -> String { let h = PortableSHA1(); h.update(data); return hexString(h.finalize()) }
func blobSHA1Hex(_ data: Data) -> String { sha1Hex(Data("blob \(data.count)\0".utf8) + data) }
func pattern(_ count: Int, seed: Int = 7) -> Data { Data((0..<count).map { UInt8(truncatingIfNeeded: $0 &* 31 &+ seed) }) }

let encoder: JSONEncoder = { let e = JSONEncoder(); e.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]; return e }()
func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

func describe(_ error: ModelDownloadError) -> String {
    switch error {
    case .network: return "network"
    case .diskFull: return "diskFull"
    case .checksumMismatch: return "checksumMismatch"
    case .httpStatus(let code): return "httpStatus:\(code)"
    case .cancelled: return "cancelled"
    case .invalidManifest: return "invalidManifest"
    case .unsafePath: return "unsafePath"
    case .fileSystem: return "fileSystem"
    case .busy: return "busy"
    }
}

func describe(_ status: ModelStatus) -> String {
    switch status {
    case .notDownloaded: return "notDownloaded"
    case .downloading(let p): return "downloading:\(p.bytesDone)/\(p.totalBytes)"
    case .verifying: return "verifying"
    case .ready: return "ready"
    case .corrupt(let reason): return "corrupt:\(reason)"
    case .failed(let error): return "failed:\(describe(error))"
    }
}

// MARK: digests

struct DigestCase: Encodable { var kind: String, text: String?, byte: Int?, count: Int?, seed: Int?, sha256: String, sha1: String, blobSha1: String }

func digestCases() -> [DigestCase] {
    func add(_ kind: String, text: String? = nil, byte: Int? = nil, count: Int? = nil, seed: Int? = nil, _ data: Data) -> DigestCase {
        DigestCase(kind: kind, text: text, byte: byte, count: count, seed: seed, sha256: sha256Hex(data), sha1: sha1Hex(data), blobSha1: blobSHA1Hex(data))
    }
    var out: [DigestCase] = []
    for text in ["", "abc", "hello\n", "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "Zoë ✓ 日本語"] { out.append(add("text", text: text, Data(text.utf8))) }
    for count in [1, 55, 56, 57, 63, 64, 65, 119, 120, 1000, 4097, 1 << 20] { out.append(add("pattern", count: count, seed: 7, pattern(count))) }
    out.append(add("pattern", count: 100, seed: 99, pattern(100, seed: 99)))
    out.append(add("repeat", byte: 0x61, count: 1_000_000, Data(repeating: 0x61, count: 1_000_000)))
    return out
}

// MARK: manifests

struct FixtureFile { var name: String, bytes: Data, lfs: Bool }
struct Fixture {
    static let repository = "Example/tiny-model"
    static let revision = "0123456789abcdef0123456789abcdef01234567"
    var contents: [FixtureFile]

    static func standard(seed: Int = 7) -> Fixture {
        Fixture(contents: [
            FixtureFile(name: "config.json", bytes: Data("{\"name\":\"tiny\"}\n".utf8), lfs: false),
            FixtureFile(name: "enc.mlmodelc/model.mil", bytes: pattern(9_000, seed: seed + 1), lfs: false),
            FixtureFile(name: "enc.mlmodelc/weights/weight.bin", bytes: pattern(70_000, seed: seed), lfs: true),
        ])
    }

    func json(batch: [String]? = nil, hashOverride: [String: String] = [:], revision: String = Fixture.revision, license: [String: Any]? = nil,
              repository: String = Fixture.repository, lfsSizeOverride: [String: Int] = [:], sizeOverride: [String: Int] = [:]) -> String {
        let files: [[String: Any]] = contents.map { file in
            var entry: [String: Any] = ["rfilename": file.name, "size": sizeOverride[file.name] ?? file.bytes.count, "git_blob_sha1": hashOverride[file.name] ?? blobSHA1Hex(file.bytes)]
            if file.lfs { entry["lfs"] = ["sha256": hashOverride[file.name] ?? sha256Hex(file.bytes), "size": lfsSizeOverride[file.name] ?? file.bytes.count, "pointerSize": 130] }
            return entry
        } + [["rfilename": "README.md", "size": 5, "git_blob_sha1": String(repeating: "a", count: 40)]]
        var model: [String: Any] = ["revision": revision, "files": files, "batch_files": batch ?? contents.map { $0.name }]
        if let license { model["license"] = license }
        let data = try! JSONSerialization.data(withJSONObject: ["models": [repository: model]], options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }
}

struct FileOut: Encodable { var name: String, size: Int64, checksum: String, algorithm: String, url: String? }
struct LicenseOut: Encodable { var pinned: Bool, declarations: [[String]], note: String }
struct ManifestOut: Encodable { var revision: String, total: Int64, files: [FileOut], license: LicenseOut }
struct ManifestCase: Encodable {
    var label: String, json: String?, path: String?, repository: String, list: String
    var error: String?, manifest: ManifestOut?
}

func summarize(_ manifest: ModelManifest, listFiles: Bool) -> ManifestOut {
    ManifestOut(revision: manifest.revision, total: manifest.totalBytes,
                files: manifest.files.map { FileOut(name: $0.rfilename, size: $0.size, checksum: $0.checksum, algorithm: $0.algorithm == .sha256 ? "sha256" : "blobSha1", url: manifest.resolveURL(for: $0)?.absoluteString) },
                license: LicenseOut(pinned: manifest.license.isPinned, declarations: manifest.license.declarations.map { [$0.source, $0.name, $0.url.absoluteString] }, note: manifest.license.note))
}

func manifestCase(_ label: String, json: String? = nil, path: String? = nil, repository: String = Fixture.repository, list: String = ModelManifest.batchList) -> ManifestCase {
    do {
        let manifest = path != nil ? try ModelManifest(contentsOf: URL(fileURLWithPath: path!), repository: repository, list: list)
                                   : try ModelManifest(data: Data(json!.utf8), repository: repository, list: list)
        return ManifestCase(label: label, json: json, path: path, repository: repository, list: list, error: nil, manifest: summarize(manifest, listFiles: true))
    } catch let error as ModelDownloadError {
        return ManifestCase(label: label, json: json, path: path, repository: repository, list: list, error: describe(error), manifest: nil)
    } catch { fatalError("unexpected \(error)") }
}

func manifestCases() -> [ManifestCase] {
    let fixture = Fixture.standard()
    var out = [manifestCase("standard", json: fixture.json())]
    out.append(manifestCase("branch revision", json: fixture.json(revision: "main")))
    out.append(manifestCase("uppercase revision", json: fixture.json(revision: "0123456789ABCDEF0123456789abcdef01234567")))
    out.append(manifestCase("wrong repository", json: fixture.json(), repository: "Other/model"))
    out.append(manifestCase("unsafe repository", json: fixture.json(repository: "../x"), repository: "../x"))
    out.append(manifestCase("batch entry missing from files", json: fixture.json(batch: ["config.json", "missing.bin"])))
    out.append(manifestCase("duplicate batch entry", json: fixture.json(batch: ["config.json", "config.json"])))
    out.append(manifestCase("malformed checksum", json: fixture.json(hashOverride: ["config.json": "xyz"])))
    out.append(manifestCase("uppercase checksum", json: fixture.json(hashOverride: ["config.json": String(repeating: "A", count: 40)])))
    out.append(manifestCase("not json", json: "nope"))
    out.append(manifestCase("empty batch", json: fixture.json(batch: [])))
    out.append(manifestCase("only some files", json: fixture.json(batch: ["config.json"])))
    out.append(manifestCase("lfs size differs", json: fixture.json(lfsSizeOverride: ["enc.mlmodelc/weights/weight.bin": 5])))
    out.append(manifestCase("negative size", json: fixture.json(sizeOverride: ["config.json": -1])))
    out.append(manifestCase("pinned license", json: fixture.json(license: ["name": "Example License", "url": "https://example.com/license"])))
    out.append(manifestCase("http license ignored", json: fixture.json(license: ["name": "X", "url": "http://example.com"])))
    out.append(manifestCase("empty license name ignored", json: fixture.json(license: ["name": "", "url": "https://example.com"])))
    // Path traversal and other unsafe names are rejected before anything is downloaded.
    for bad in ["../evil", "/etc/passwd", "a/../../b", "a//b", "./a", "a/./b", "a\\..\\b", "~/x", "a\u{0}b", "a\nb", "", "x/", "w.part", "a\u{7f}b"] {
        var evil = fixture
        evil.contents.append(FixtureFile(name: bad, bytes: Data("x".utf8), lfs: false))
        out.append(manifestCase("unsafe name \(bad.debugDescription)", json: evil.json()))
    }
    var nested = fixture
    nested.contents.append(FixtureFile(name: "config.json/inner", bytes: Data("x".utf8), lfs: false))
    out.append(manifestCase("file nested inside a file", json: nested.json()))
    var spaced = fixture
    spaced.contents.append(FixtureFile(name: "a b/c#d.bin", bytes: Data("x".utf8), lfs: false))
    spaced.contents.append(FixtureFile(name: "q;x%y/é.bin", bytes: Data("x".utf8), lfs: false))
    out.append(manifestCase("names needing percent-encoding", json: spaced.json()))
    // The real pins files.
    let pins = "module-one/models/artifact-pins.json"
    let object = try! JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath: pins))) as! [String: Any]
    let models = object["models"] as! [String: [String: Any]]
    for repository in models.keys.sorted() {
        for list in ["batch_files", "live_files"] where models[repository]![list] != nil {
            out.append(manifestCase("pins \(repository) \(list)", path: pins, repository: repository, list: list))
        }
    }
    return out
}

// MARK: paths and messages

struct PathCase: Encodable { var path: String, safe: Bool }
struct RepoCase: Encodable { var repository: String, safe: Bool }
struct HexCase: Encodable { var value: String, length: Int, hex: Bool }
struct MessageCase: Encodable { var error: String, message: String }

func pathCases() -> [PathCase] {
    ["parakeet_unified_decoder.mlmodelc/weights/weight.bin", "config.json", "a/b/c.txt", "encoder.int8.onnx", "a b/c d.bin", "../evil", "a/..", "..", ".", "/abs", "~home", "a\\b", "a//b", "a/", "", "x.part", "a/x.part/b", "a/b.part", "tab\there", "bell\u{7}", "del\u{7f}", String(repeating: "a", count: 1024), String(repeating: "a", count: 1025), "é/ü.bin"]
        .map { PathCase(path: $0, safe: ModelManifest.isSafeRelativePath($0)) }
}
func repoCases() -> [RepoCase] {
    ["FluidInference/parakeet-unified-en-0.6b-coreml", "csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming", "a/b", "../x", "a/b/c", "a", "", "/b", "a/", ".//b", "a/..", "a b/c", "a/b c", "é/b", "A_b.c-d/E"]
        .map { RepoCase(repository: $0, safe: ModelManifest.isSafeRepository($0)) }
}
func hexCases() -> [HexCase] {
    [("abc123", 6), ("ABC123", 6), ("abc12g", 6), ("abc12", 6), ("", 0), (String(repeating: "f", count: 40), 40), (String(repeating: "f", count: 39) + "é", 40)]
        .map { HexCase(value: $0.0, length: $0.1, hex: ModelManifest.isHex($0.0, length: $0.1)) }
}
func messageCases() -> [MessageCase] {
    let all: [ModelDownloadError] = [.network, .diskFull, .checksumMismatch, .httpStatus(500), .httpStatus(404), .cancelled, .invalidManifest, .unsafePath, .fileSystem, .busy]
    return all.map { MessageCase(error: describe($0), message: $0.userMessage) }
}

// MARK: download traces

final class FakeTransport: ModelDownloadTransport {
    var files: [String: Data] = [:]
    var statusOverride: [String: Int] = [:]
    var failAfterBytes: [String: Int] = [:]
    var ignoresRange = false
    var chunkSize = 4096
    var onChunk: ((String, Int) -> Void)?
    private(set) var requests: [ModelFetchRequest] = []

    func fetch(_ request: ModelFetchRequest, isCancelled: @escaping () -> Bool,
               onResponse: @escaping (Int) throws -> Void, onData: @escaping (Data) throws -> Void) throws {
        requests.append(request)
        let path = request.url.path
        if isCancelled() { throw ModelDownloadError.cancelled }
        if let status = statusOverride[path] { try onResponse(status); return }
        guard let body = files[path] else { try onResponse(404); return }
        var start = 0
        if request.rangeStart > 0 && !ignoresRange {
            guard request.rangeStart < Int64(body.count) else { try onResponse(416); return }
            start = Int(request.rangeStart)
            try onResponse(206)
        } else { try onResponse(200) }
        var delivered = 0
        var offset = start
        while offset < body.count {
            if isCancelled() { throw ModelDownloadError.cancelled }
            if let limit = failAfterBytes[path], delivered >= limit { throw ModelDownloadError.network }
            let end = min(body.count, offset + chunkSize)
            try onData(body.subdata(in: offset..<end))
            delivered += end - offset
            offset = end
            onChunk?(path, delivered)
        }
    }
}

final class CountingHasher: FileHasher {
    var calls: [String] = []
    private let inner = StreamingFileHasher(chunkSize: 1000)
    func hexDigest(of file: URL, algorithm: ModelHashAlgorithm, isCancelled: () -> Bool) throws -> String {
        calls.append(file.lastPathComponent)
        return try inner.hexDigest(of: file, algorithm: algorithm, isCancelled: isCancelled)
    }
}

/// One install attempt. Fields mirror what the C# test reads back.
struct Attempt: Encodable {
    var seed = 7
    var ignoresRange = false
    var failAfter: [String: Int] = [:]
    var statusOverride: [String: Int] = [:]
    var cancelAt: [String: Int] = [:]          // path -> delivered bytes at which cancel() is called
    var corrupt: [String: Int] = [:]           // path -> index of the byte flipped in the served body
    var extra: [String] = []                   // paths whose served body gets "extra" appended
    var free: Int64? = nil                     // free space the preflight sees; nil skips it
    var strayInStaging = false                 // a stray file and folder exist in staging before the attempt
    var alteredConfigInStaging = false         // config.json in staging holds other bytes
    var remove = false                         // call remove() instead of install()
    var verifyAfter = false
}
struct AttemptResult: Encodable {
    var error: String?, status: String, requests: [[Int64]], paths: [String], progress: [Int64], hashed: [String]
    var files: [String], stagingFiles: [String], partialWeight: Int, verified: String?
}
struct ScenarioCase: Encodable { var name: String, attempts: [Attempt], results: [AttemptResult] }

func listFiles(_ root: URL) -> [String] {
    guard let items = FileManager.default.enumerator(atPath: root.path)?.allObjects as? [String] else { return [] }
    return items.filter { var d: ObjCBool = false; FileManager.default.fileExists(atPath: root.appendingPathComponent($0).path, isDirectory: &d); return !d.boolValue }
        .sorted().map { name in
            let size = (try? FileManager.default.attributesOfItem(atPath: root.appendingPathComponent(name).path)[.size] as? NSNumber)?.intValue ?? -1
            return "\(name):\(size):" + sha256Hex((try? Data(contentsOf: root.appendingPathComponent(name))) ?? Data()).prefix(16)
        }
}

func runScenario(_ name: String, _ attempts: [Attempt], root: URL) -> ScenarioCase {
    let target = root.appendingPathComponent(name.replacingOccurrences(of: " ", with: "-")).appendingPathComponent("model")
    try? FileManager.default.removeItem(at: target.deletingLastPathComponent())
    try! FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
    func path(_ name: String) -> String { "/\(Fixture.repository)/resolve/\(Fixture.revision)/\(name)" }
    var results: [AttemptResult] = []
    for attempt in attempts {
        let fixture = Fixture.standard(seed: attempt.seed)
        let manifest = try! ModelManifest(data: Data(fixture.json().utf8), repository: Fixture.repository)
        let transport = FakeTransport()
        for file in fixture.contents { transport.files[path(file.name)] = file.bytes }
        for (p, index) in attempt.corrupt { var data = transport.files[path(p)]!; data[index] ^= 0xff; transport.files[path(p)] = data }
        for p in attempt.extra { transport.files[path(p)]! += Data("extra".utf8) }
        transport.ignoresRange = attempt.ignoresRange
        transport.failAfterBytes = Dictionary(uniqueKeysWithValues: attempt.failAfter.map { (path($0.key), $0.value) })
        transport.statusOverride = Dictionary(uniqueKeysWithValues: attempt.statusOverride.map { (path($0.key), $0.value) })
        let hasher = CountingHasher()
        let free = attempt.free
        let downloader = ModelDownloader(manifest: manifest, target: target, transport: transport, hasher: hasher, freeSpace: { _ in free })
        if !attempt.cancelAt.isEmpty {
            let cancelAt = Dictionary(uniqueKeysWithValues: attempt.cancelAt.map { (path($0.key), $0.value) })
            transport.onChunk = { p, delivered in if let limit = cancelAt[p], delivered >= limit { downloader.cancel() } }
        }
        let staging = downloader.stagingDirectory
        if attempt.strayInStaging {
            try! FileManager.default.createDirectory(at: staging.appendingPathComponent("junk"), withIntermediateDirectories: true)
            try! Data("x".utf8).write(to: staging.appendingPathComponent("junk/evil.bin"))
            try! Data("x".utf8).write(to: staging.appendingPathComponent("extra.json"))
        }
        if attempt.alteredConfigInStaging {
            try! FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
            try! Data("{\"name\":\"evil\"}\n".utf8).write(to: staging.appendingPathComponent("config.json"))
        }
        var progress: [Int64] = []
        var error: String?
        do { if attempt.remove { try downloader.remove() } else { try downloader.install { progress.append($0.bytesDone) } } }
        catch let e as ModelDownloadError { error = describe(e) } catch { fatalError("unexpected \(error)") }
        let partial = staging.appendingPathComponent("enc.mlmodelc/weights/weight.bin.part")
        let partialSize = (try? FileManager.default.attributesOfItem(atPath: partial.path)[.size] as? NSNumber)?.intValue ?? -1
        results.append(AttemptResult(error: error, status: describe(downloader.status()), requests: transport.requests.map { [$0.rangeStart] }, paths: transport.requests.map { $0.url.path.replacingOccurrences(of: "/\(Fixture.repository)/resolve/\(Fixture.revision)/", with: "") },
                                     progress: progress, hashed: hasher.calls, files: listFiles(target), stagingFiles: listFiles(staging), partialWeight: partialSize,
                                     verified: attempt.verifyAfter ? describe(downloader.verifyInstalled()) : nil))
    }
    return ScenarioCase(name: name, attempts: attempts, results: results)
}

func scenarios(root: URL) -> [ScenarioCase] {
    let weight = "enc.mlmodelc/weights/weight.bin"
    var out: [ScenarioCase] = []
    out.append(runScenario("fresh install", [Attempt(free: 1 << 40, verifyAfter: true)], root: root))
    out.append(runScenario("cancel then resume", [Attempt(cancelAt: [weight: 20_000]), Attempt()], root: root))
    out.append(runScenario("server ignores range", [Attempt(ignoresRange: true, failAfter: [weight: 30_000]), Attempt(ignoresRange: true)], root: root))
    out.append(runScenario("dropped connection resumes with range", [Attempt(failAfter: [weight: 30_000]), Attempt()], root: root))
    out.append(runScenario("http 404 then success", [Attempt(statusOverride: [weight: 404]), Attempt(statusOverride: [weight: 503]), Attempt()], root: root))
    out.append(runScenario("disk full preflight", [Attempt(free: 79_015), Attempt(free: 79_016)], root: root))
    out.append(runScenario("tampered update keeps old model", [Attempt(), Attempt(seed: 99, corrupt: [weight: 100]), Attempt(seed: 99)], root: root))
    out.append(runScenario("tampered fresh install", [Attempt(seed: 99, corrupt: [weight: 100])], root: root))
    out.append(runScenario("oversized body", [Attempt(extra: ["config.json"])], root: root))
    out.append(runScenario("stray staging files pruned", [Attempt(strayInStaging: true)], root: root))
    out.append(runScenario("altered staged file refetched", [Attempt(alteredConfigInStaging: true)], root: root))
    out.append(runScenario("remove after cancel", [Attempt(cancelAt: [weight: 8_000]), Attempt(remove: true)], root: root))
    return out
}

@main struct Gen {
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-golden-model-download-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        print("{\n" + [lines("digests", digestCases()), lines("manifests", manifestCases()), lines("paths", pathCases()), lines("repos", repoCases()), lines("hex", hexCases()),
                       lines("messages", messageCases()), lines("scenarios", scenarios(root: root))].joined(separator: ",\n") + "\n}")
    }
}
