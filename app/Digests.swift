import Foundation

// Portable SHA-1/SHA-256 digests and the file hasher built on them (no CryptoKit, so Linux checks build).

// MARK: - Hashing

/// Streaming SHA-256 (FIPS 180-4). Feed any number of chunks, then call `finalize()` once.
final class PortableSHA256 {
    private let core = DigestCore(sha256: true)
    init() {}
    func update(_ data: Data) { data.withUnsafeBytes { core.absorb($0) } }
    func finalize() -> [UInt8] { core.finish() }
}

/// Streaming SHA-1. Used only to match the blob IDs Hugging Face publishes for non-LFS files;
/// it is an integrity check against pinned values, not a security boundary on its own.
final class PortableSHA1 {
    private let core = DigestCore(sha256: false)
    init() {}
    func update(_ data: Data) { data.withUnsafeBytes { core.absorb($0) } }
    func finalize() -> [UInt8] { core.finish() }
}

/// Shared 64-byte-block engine. Raw buffers and closure-free loops keep unoptimized (debug) app
/// builds usable: `@_optimize(speed)` makes the hot functions fast even when the target is -Onone.
private final class DigestCore {
    private static let sha256K: UnsafeMutablePointer<UInt32> = {
        let k: [UInt32] = [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        ]
        let table = UnsafeMutablePointer<UInt32>.allocate(capacity: 64)  // process-lifetime constant
        table.initialize(from: k, count: 64)
        return table
    }()

    private let isSHA256: Bool
    private let k: UnsafeMutablePointer<UInt32>
    private let h = UnsafeMutablePointer<UInt32>.allocate(capacity: 8)
    private let w = UnsafeMutablePointer<UInt32>.allocate(capacity: 80)
    private let pending = UnsafeMutableRawPointer.allocate(byteCount: 64, alignment: 8)
    private var pendingCount = 0
    private var totalBytes: UInt64 = 0
    private var finished = false

    init(sha256: Bool) {
        isSHA256 = sha256
        k = DigestCore.sha256K
        let initial: [UInt32] = sha256
            ? [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]
            : [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0, 0, 0, 0]
        h.initialize(from: initial, count: 8)
        w.initialize(repeating: 0, count: 80)
    }

    deinit { h.deallocate(); w.deallocate(); pending.deallocate() }

    @_optimize(speed) func absorb(_ bytes: UnsafeRawBufferPointer) {
        precondition(!finished, "digest already finalized")
        guard let base = bytes.baseAddress, bytes.count > 0 else { return }
        let count = bytes.count
        totalBytes &+= UInt64(count)
        var offset = 0
        if pendingCount > 0 {
            let take = min(64 - pendingCount, count)
            (pending + pendingCount).copyMemory(from: base, byteCount: take)
            pendingCount += take; offset = take
            if pendingCount < 64 { return }
            compress(UnsafeRawPointer(pending)); pendingCount = 0
        }
        while count - offset >= 64 { compress(base + offset); offset += 64 }
        if count > offset { pending.copyMemory(from: base + offset, byteCount: count - offset); pendingCount = count - offset }
    }

    func finish() -> [UInt8] {
        let bitLength = totalBytes &* 8
        var tail: [UInt8] = [0x80]
        let afterMarker = (pendingCount + 1) % 64
        tail.append(contentsOf: repeatElement(0, count: afterMarker <= 56 ? 56 - afterMarker : 120 - afterMarker))
        for shift in stride(from: 56, through: 0, by: -8) { tail.append(UInt8(truncatingIfNeeded: bitLength >> UInt64(shift))) }
        tail.withUnsafeBytes { absorb($0) }
        finished = true
        var out = [UInt8]()
        for i in 0..<(isSHA256 ? 8 : 5) {
            let word = h[i]
            out += [UInt8(word >> 24), UInt8(truncatingIfNeeded: word >> 16), UInt8(truncatingIfNeeded: word >> 8), UInt8(truncatingIfNeeded: word)]
        }
        return out
    }

    @_optimize(speed) private func compress(_ p: UnsafeRawPointer) {
        if isSHA256 { sha256Block(p) } else { sha1Block(p) }
    }

    @_optimize(speed) private func sha256Block(_ p: UnsafeRawPointer) {
        let w = self.w, k = self.k, h = self.h
        var i = 0
        while i < 16 { w[i] = UInt32(bigEndian: p.loadUnaligned(fromByteOffset: i &* 4, as: UInt32.self)); i &+= 1 }
        while i < 64 {
            let x = w[i &- 15], y = w[i &- 2]
            let s0 = ((x >> 7) | (x << 25)) ^ ((x >> 18) | (x << 14)) ^ (x >> 3)
            let s1 = ((y >> 17) | (y << 15)) ^ ((y >> 19) | (y << 13)) ^ (y >> 10)
            w[i] = w[i &- 16] &+ s0 &+ w[i &- 7] &+ s1
            i &+= 1
        }
        var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7]
        i = 0
        while i < 64 {
            let bigS1 = ((e >> 6) | (e << 26)) ^ ((e >> 11) | (e << 21)) ^ ((e >> 25) | (e << 7))
            let t1 = hh &+ bigS1 &+ ((e & f) ^ (~e & g)) &+ k[i] &+ w[i]
            let bigS0 = ((a >> 2) | (a << 30)) ^ ((a >> 13) | (a << 19)) ^ ((a >> 22) | (a << 10))
            let t2 = bigS0 &+ ((a & b) ^ (a & c) ^ (b & c))
            hh = g; g = f; f = e; e = d &+ t1; d = c; c = b; b = a; a = t1 &+ t2
            i &+= 1
        }
        h[0] = h[0] &+ a; h[1] = h[1] &+ b; h[2] = h[2] &+ c; h[3] = h[3] &+ d
        h[4] = h[4] &+ e; h[5] = h[5] &+ f; h[6] = h[6] &+ g; h[7] = h[7] &+ hh
    }

    @_optimize(speed) private func sha1Block(_ p: UnsafeRawPointer) {
        let w = self.w, h = self.h
        var i = 0
        while i < 16 { w[i] = UInt32(bigEndian: p.loadUnaligned(fromByteOffset: i &* 4, as: UInt32.self)); i &+= 1 }
        while i < 80 {
            let x = w[i &- 3] ^ w[i &- 8] ^ w[i &- 14] ^ w[i &- 16]
            w[i] = (x << 1) | (x >> 31)
            i &+= 1
        }
        var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4]
        i = 0
        while i < 80 {
            let f: UInt32, k: UInt32
            if i < 20 { f = (b & c) | (~b & d); k = 0x5A827999 }
            else if i < 40 { f = b ^ c ^ d; k = 0x6ED9EBA1 }
            else if i < 60 { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC }
            else { f = b ^ c ^ d; k = 0xCA62C1D6 }
            let t = ((a << 5) | (a >> 27)) &+ f &+ e &+ k &+ w[i]
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = t
            i &+= 1
        }
        h[0] = h[0] &+ a; h[1] = h[1] &+ b; h[2] = h[2] &+ c; h[3] = h[3] &+ d; h[4] = h[4] &+ e
    }
}

func hexString(_ bytes: [UInt8]) -> String {
    let digits = Array("0123456789abcdef".utf8)
    var out = [UInt8](); out.reserveCapacity(bytes.count * 2)
    for byte in bytes { out.append(digits[Int(byte >> 4)]); out.append(digits[Int(byte & 0x0f)]) }
    return String(decoding: out, as: UTF8.self)
}

enum ModelHashAlgorithm: Equatable {
    /// Plain SHA-256 of the file bytes (Hugging Face LFS `sha256`).
    case sha256
    /// SHA-1 of `"blob <size>\0" + bytes` (the `git_blob_sha1` field in the pins).
    case blobSHA1
}

/// Computes file digests. Injected so tests can count calls or simulate failures.
protocol FileHasher {
    /// Returns the lowercase hex digest. Must read in bounded chunks and throw
    /// `ModelDownloadError.cancelled` promptly once `isCancelled()` returns true.
    func hexDigest(of file: URL, algorithm: ModelHashAlgorithm, isCancelled: () -> Bool) throws -> String
}

/// Default hasher: streams the file in 1 MiB chunks through the portable digests.
struct StreamingFileHasher: FileHasher {
    var chunkSize = 1 << 20

    func hexDigest(of file: URL, algorithm: ModelHashAlgorithm, isCancelled: () -> Bool) throws -> String {
        let handle: FileHandle
        do { handle = try FileHandle(forReadingFrom: file) } catch { throw ModelDownloadError.fileSystem }
        defer { try? handle.close() }
        let sha256 = PortableSHA256(), sha1 = PortableSHA1()
        if algorithm == .blobSHA1 {
            guard let size = fileSize(file) else { throw ModelDownloadError.fileSystem }
            sha1.update(Data("blob \(size)\0".utf8))
        }
        while true {
            if isCancelled() { throw ModelDownloadError.cancelled }
            let chunk: Data
            do { chunk = try handle.read(upToCount: chunkSize) ?? Data() } catch { throw ModelDownloadError.fileSystem }
            if chunk.isEmpty { break }
            switch algorithm {
            case .sha256: sha256.update(chunk)
            case .blobSHA1: sha1.update(chunk)
            }
        }
        return hexString(algorithm == .sha256 ? sha256.finalize() : sha1.finalize())
    }
}

func fileSize(_ url: URL) -> Int64? {
    guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
          (attrs[.type] as? FileAttributeType) == .typeRegular else { return nil }
    return (attrs[.size] as? NSNumber)?.int64Value
}
