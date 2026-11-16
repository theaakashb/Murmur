// Verifying the pinned model files on disk (sizes, checksums, a per-launch cache) and where the bundled pins and helper live.
import Foundation
import CryptoKit
import Darwin

/// The one place that finds the pins manifest and the ModelBench helper in the app bundle.
enum BundledModels {
    static var pins: URL? { Bundle.main.url(forResource: "artifact-pins", withExtension: "json") }
    static var helper: URL? { Bundle.main.url(forAuxiliaryExecutable: "ModelBench") }
}

/// Hashing the 1.8 GB model took about 0.9 s before every dictation. Verify once, then reuse the
/// result while every pinned file keeps the same size, modification time and inode.
// ponytail: a same-user writer that restores size, mtime and inode evades this; that writer could
// also replace the app itself. Re-hash per dictation if that threat model changes.
final class ModelVerificationCache: @unchecked Sendable {
    static let shared = ModelVerificationCache()
    private let lock = NSLock()
    /// Verified fingerprint per folder (the transcription model and the dictionary model).
    private var verified: [String: [String]] = [:]
    func verify(_ directory: URL, manifest: URL, model: ModelFacts = .batch) throws {
        let fingerprint = try Self.fingerprint(directory, manifest: manifest, model: model)
        lock.lock(); let hit = verified[directory.path] == fingerprint; lock.unlock()
        if hit { return }
        try verifyAppModels(directory, manifest: manifest, model: model)
        lock.lock(); verified[directory.path] = fingerprint; lock.unlock()
    }
    static func fingerprint(_ directory: URL, manifest: URL, model: ModelFacts = .batch) throws -> [String] {
        try pinnedFiles(manifest, model).map { file in
            var info = stat()
            guard stat(directory.appendingPathComponent(file.rfilename).path, &info) == 0 else { throw RetentionError("Missing model file") }
            return "\(file.rfilename):\(info.st_size):\(info.st_mtimespec.tv_sec).\(info.st_mtimespec.tv_nsec):\(info.st_ino)"
        }
    }
}

/// The model's pinned files, read by `ModelManifest` like the downloader reads them.
private func pinnedFiles(_ manifest: URL, _ model: ModelFacts) throws -> [ModelManifestFile] {
    do { return try ModelManifest(contentsOf: manifest, model).files }
    catch ModelDownloadError.unsafePath { throw RetentionError("Invalid model file") }
    catch { throw RetentionError("Invalid model manifest") }
}

func verifyAppModels(_ directory: URL, manifest: URL, model: ModelFacts = .batch) throws {
    for file in try pinnedFiles(manifest, model) {
        let path = directory.appendingPathComponent(file.rfilename)
        let attrs = try FileManager.default.attributesOfItem(atPath: path.path)
        guard (attrs[.size] as? NSNumber)?.int64Value == file.size else { throw RetentionError("Model file size mismatch") }
        let handle = try FileHandle(forReadingFrom: path)
        defer { try? handle.close() }
        switch file.algorithm {
        case .sha256:
            var hash = SHA256()
            while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
            guard hash.finalize().map({ String(format: "%02x", $0) }).joined() == file.checksum else { throw RetentionError("Model checksum mismatch") }
        case .blobSHA1:
            var hash = Insecure.SHA1()
            hash.update(data: Data("blob \(file.size)\0".utf8))
            while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
            guard hash.finalize().map({ String(format: "%02x", $0) }).joined() == file.checksum else { throw RetentionError("Model checksum mismatch") }
        }
    }
}
