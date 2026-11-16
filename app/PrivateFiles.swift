import Foundation

// Owner-only private JSON file IO shared by the dictionary, cues, learning and the controller. Throws `StoreError` (Dictionary.swift).

/// Owner-only private JSON files with atomic replacement, using FileManager only.
/// The containing directory is 0700, so the short interval before the file's own 0600
/// attribute is applied is not readable by other users.
enum PrivateFiles {
    static func prepareDirectory(_ url: URL) throws {
        let fm = FileManager.default
        if let attrs = try? fm.attributesOfItem(atPath: url.path) {
            guard attrs[.type] as? FileAttributeType == .typeDirectory else {
                throw StoreError("Private storage requires a real directory")
            }
        } else {
            try fm.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
    }

    static func write(_ data: Data, to url: URL) throws {
        if let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
           attrs[.type] as? FileAttributeType != .typeRegular {
            throw StoreError("Private file is not a regular file")
        }
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    /// nil when the file does not exist yet.
    static func read(_ url: URL) throws -> Data? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path) else { return nil }
        guard attrs[.type] as? FileAttributeType == .typeRegular else {
            throw StoreError("Private file is not a regular file")
        }
        return try Data(contentsOf: url)
    }
}
