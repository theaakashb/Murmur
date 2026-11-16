// Verifying the pinned model files on disk (sizes, checksums, a per-launch cache) and the repository names they come from.
import Foundation
import CryptoKit
import Darwin

/// Hashing the 1.8 GB model took about 0.9 s before every dictation. Verify once, then reuse the
/// result while every pinned file keeps the same size, modification time and inode.
// ponytail: a same-user writer that restores size, mtime and inode evades this; that writer could
// also replace the app itself. Re-hash per dictation if that threat model changes.
final class ModelVerificationCache: @unchecked Sendable {
    static let shared = ModelVerificationCache()
    private let lock = NSLock()
    /// Verified fingerprint per folder (the transcription model and the dictionary model).
    private var verified: [String: [String]] = [:]
    func verify(_ directory: URL, manifest: URL, repository: String = unifiedRepository, list: String = "batch_files") throws {
        let fingerprint = try Self.fingerprint(directory, manifest: manifest, repository: repository, list: list)
        lock.lock(); let hit = verified[directory.path] == fingerprint; lock.unlock()
        if hit { return }
        try verifyAppModels(directory, manifest: manifest, repository: repository, list: list)
        lock.lock(); verified[directory.path] = fingerprint; lock.unlock()
    }
    static func fingerprint(_ directory: URL, manifest: URL, repository: String = unifiedRepository, list: String = "batch_files") throws -> [String] {
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: manifest)) as? [String: Any]
        guard let models = object?["models"] as? [String: Any],
              let model = models[repository] as? [String: Any],
              let batch = model[list] as? [String] else { throw RetentionError("Invalid model manifest") }
        return try batch.map { name in
            var info = stat()
            guard stat(directory.appendingPathComponent(name).path, &info) == 0 else { throw RetentionError("Missing model file") }
            return "\(name):\(info.st_size):\(info.st_mtimespec.tv_sec).\(info.st_mtimespec.tv_nsec):\(info.st_ino)"
        }
    }
}

let unifiedRepository = "FluidInference/parakeet-unified-en-0.6b-coreml"
/// The live-words files (gap #13): the streaming encoder plus the decoder, joint and vocabulary it
/// loads with, in their own folder so the batch model is never touched.
let liveWordsFileList = "live_files"
/// The CTC model that rescores local transcripts toward the dictionary.
let dictionaryModelRepository = "FluidInference/parakeet-ctc-110m-coreml"

func verifyAppModels(_ directory: URL, manifest: URL, repository: String = unifiedRepository, list: String = "batch_files") throws {
    let object = try JSONSerialization.jsonObject(with: Data(contentsOf: manifest)) as? [String: Any]
    guard let models = object?["models"] as? [String: Any],
          let model = models[repository] as? [String: Any],
          let files = model["files"] as? [[String: Any]], let batch = model[list] as? [String] else {
        throw RetentionError("Invalid model manifest")
    }
    for name in batch {
        guard !name.hasPrefix("/"), !name.split(separator: "/").contains(".."),
              let metadata = files.first(where: { $0["rfilename"] as? String == name }),
              let size = metadata["size"] as? Int else { throw RetentionError("Invalid model file") }
        let path = directory.appendingPathComponent(name)
        let attrs = try FileManager.default.attributesOfItem(atPath: path.path)
        guard (attrs[.size] as? NSNumber)?.intValue == size else { throw RetentionError("Model file size mismatch") }
        let handle = try FileHandle(forReadingFrom: path)
        defer { try? handle.close() }
        if let lfs = metadata["lfs"] as? [String: Any], let expected = lfs["sha256"] as? String {
            var hash = SHA256()
            while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
            guard hash.finalize().map({ String(format: "%02x", $0) }).joined() == expected else { throw RetentionError("Model checksum mismatch") }
        } else if let expected = metadata["git_blob_sha1"] as? String {
            var hash = Insecure.SHA1()
            hash.update(data: Data("blob \(size)\0".utf8))
            while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
            guard hash.finalize().map({ String(format: "%02x", $0) }).joined() == expected else { throw RetentionError("Model checksum mismatch") }
        } else { throw RetentionError("Missing model checksum") }
    }
}
