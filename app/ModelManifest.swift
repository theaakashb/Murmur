import Foundation

// Model manifest, license declarations and the URL building for pinned files.

// MARK: - Manifest and license

struct ModelManifestFile: Equatable {
    let rfilename: String
    let size: Int64
    /// Lowercase hex: SHA-256 for LFS files, blob SHA-1 otherwise.
    let checksum: String
    let algorithm: ModelHashAlgorithm
}

struct ModelLicenseDeclaration: Equatable {
    /// Where the declaration comes from, e.g. "Upstream NVIDIA model card".
    let source: String
    let name: String
    let url: URL
}

/// License facts to show before download. `isPinned` is true only when the manifest itself records a
/// license; otherwise the declarations are what the model cards say, and `note` says so plainly.
struct ModelLicenseInfo: Equatable {
    let isPinned: Bool
    let declarations: [ModelLicenseDeclaration]
    let note: String

    /// Facts from the Hugging Face model APIs (checked 2026-10-03):
    /// nvidia/parakeet-unified-en-0.6b declares `license: other`, `license_name: nvidia-open-model-license`
    /// with the link below; the pinned conversion repository declares `license: cc-by-4.0`.
    static let parakeetUnifiedUnpinned = ModelLicenseInfo(
        isPinned: false,
        declarations: [
            ModelLicenseDeclaration(source: "Upstream NVIDIA model card (nvidia/parakeet-unified-en-0.6b)",
                                    name: "NVIDIA Open Model License",
                                    url: URL(string: "https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/")!),
            ModelLicenseDeclaration(source: "Core ML conversion model card (FluidInference/parakeet-unified-en-0.6b-coreml)",
                                    name: "CC-BY-4.0",
                                    url: URL(string: "https://huggingface.co/FluidInference/parakeet-unified-en-0.6b-coreml")!),
        ],
        note: "The license is not recorded in Murmur's pinned model manifest. The upstream and conversion model cards declare different licenses, and Murmur has not resolved which terms apply. Review both before downloading.")
}

struct ModelManifest: Equatable {
    static let defaultRepository = "FluidInference/parakeet-unified-en-0.6b-coreml"

    let repository: String
    /// Immutable 40-hex commit; branch or tag names are rejected so the source cannot drift.
    let revision: String
    /// Exactly the pinned file list (`batch_files`, or `live_files` for live words), in manifest order.
    let files: [ModelManifestFile]
    let license: ModelLicenseInfo

    /// Sum of pinned file sizes: the download size to show before downloading.
    var totalBytes: Int64 { files.reduce(0) { $0 + $1.size } }

    init(contentsOf url: URL, repository: String = ModelManifest.defaultRepository, list: String = "batch_files") throws {
        guard let data = try? Data(contentsOf: url) else { throw ModelDownloadError.invalidManifest }
        try self.init(data: data, repository: repository, list: list)
    }

    /// Parses artifact-pins.json. Throws `.unsafePath` for traversal-like names, otherwise `.invalidManifest`.
    init(data: Data, repository: String = ModelManifest.defaultRepository, list: String = "batch_files") throws {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let models = object["models"] as? [String: Any],
              let model = models[repository] as? [String: Any],
              let revision = model["revision"] as? String,
              let entries = model["files"] as? [[String: Any]],
              let batch = model[list] as? [String], !batch.isEmpty,
              ModelManifest.isSafeRepository(repository), ModelManifest.isHex(revision, length: 40)
        else { throw ModelDownloadError.invalidManifest }
        var byName: [String: [String: Any]] = [:]
        for entry in entries { if let name = entry["rfilename"] as? String { byName[name] = entry } }
        var files: [ModelManifestFile] = []
        var seen = Set<String>()
        for name in batch {
            guard ModelManifest.isSafeRelativePath(name) else { throw ModelDownloadError.unsafePath }
            guard seen.insert(name).inserted, let entry = byName[name],
                  let size = (entry["size"] as? NSNumber)?.int64Value, size >= 0 else { throw ModelDownloadError.invalidManifest }
            if let lfs = entry["lfs"] as? [String: Any] {
                guard let sha = lfs["sha256"] as? String, ModelManifest.isHex(sha, length: 64) else { throw ModelDownloadError.invalidManifest }
                if let lfsSize = (lfs["size"] as? NSNumber)?.int64Value, lfsSize != size { throw ModelDownloadError.invalidManifest }
                files.append(ModelManifestFile(rfilename: name, size: size, checksum: sha, algorithm: .sha256))
            } else if let sha1 = entry["git_blob_sha1"] as? String, ModelManifest.isHex(sha1, length: 40) {
                files.append(ModelManifestFile(rfilename: name, size: size, checksum: sha1, algorithm: .blobSHA1))
            } else { throw ModelDownloadError.invalidManifest }
        }
        // Reject names where one file would sit "inside" another file, e.g. "a" and "a/b".
        for file in files where files.contains(where: { $0.rfilename.hasPrefix(file.rfilename + "/") }) {
            throw ModelDownloadError.unsafePath
        }
        self.repository = repository
        self.revision = revision
        self.files = files
        if let license = model["license"] as? [String: Any], let name = license["name"] as? String, !name.isEmpty,
           let link = license["url"] as? String, let url = URL(string: link), url.scheme == "https" {
            self.license = ModelLicenseInfo(isPinned: true,
                                            declarations: [ModelLicenseDeclaration(source: "Pinned model manifest", name: name, url: url)],
                                            note: "This license is recorded in Murmur's pinned model manifest.")
        } else if repository == ModelManifest.defaultRepository {
            self.license = .parakeetUnifiedUnpinned
        } else {
            self.license = ModelLicenseInfo(isPinned: false, declarations: [],
                                            note: "The license for this model is not recorded in Murmur's pinned model manifest.")
        }
    }

    /// Relative, forward-slash path with no empty, ".", or ".." components, no backslashes,
    /// control characters, or leading "~". Anything else could escape the model directory.
    static func isSafeRelativePath(_ path: String) -> Bool {
        guard !path.isEmpty, path.utf8.count <= 1024, !path.hasPrefix("/"), !path.hasPrefix("~"),
              !path.contains("\\"), !path.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7f }) else { return false }
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        return parts.allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." && !$0.hasSuffix(".part") }
    }

    static func isSafeRepository(_ repository: String) -> Bool {
        let parts = repository.split(separator: "/", omittingEmptySubsequences: false)
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.")
        return parts.count == 2 && parts.allSatisfy {
            !$0.isEmpty && $0 != "." && $0 != ".." && $0.unicodeScalars.allSatisfy { allowed.contains($0) }
        }
    }

    static func isHex(_ value: String, length: Int) -> Bool {
        value.utf8.count == length && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    /// `https://huggingface.co/<repo>/resolve/<revision>/<rfilename>`, each path component percent-encoded.
    func resolveURL(for file: ModelManifestFile, base: URL = URL(string: "https://huggingface.co")!) -> URL? {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "/?#;")
        let encoded = file.rfilename.split(separator: "/").map { String($0).addingPercentEncoding(withAllowedCharacters: allowed) }
        guard !encoded.contains(where: { $0 == nil }) else { return nil }
        let path = "\(repository)/resolve/\(revision)/" + encoded.map { $0! }.joined(separator: "/")
        return URL(string: base.absoluteString.hasSuffix("/") ? base.absoluteString + path : base.absoluteString + "/" + path)
    }
}
