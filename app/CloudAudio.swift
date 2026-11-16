import Foundation

// Audio preparation for cloud upload: the WAV codec (16 kHz mono 16-bit PCM conversion) and peak measurement.

/// Converts the recorder's WAV (any channel count, 16/24/32-bit integer or float samples) to
/// 16 kHz mono 16-bit PCM before upload, and measures its peak for the silence pre-check. This
/// keeps a five-minute dictation (the recording limit) near 9.6 MB, under the providers' 20–25 MB limits. A file it
/// cannot parse is sent unchanged, with no peak (the silence pre-check is then skipped).
enum CloudAudio {

    struct Prepared {
        var payload: AudioPayload
        var peak: Float?
        var duration: Double?
    }

    static func prepare(_ data: Data) -> Prepared {
        guard let decoded = WAVAudio.decode(data), decoded.sampleRate > 0 else {
            return Prepared(payload: AudioPayload(data: data), peak: nil, duration: nil)
        }
        let rate = decoded.sampleRate > AudioFormat.sampleRate ? AudioFormat.sampleRate : decoded.sampleRate
        let samples = trimTrailingSilence(WAVAudio.downsample(decoded.samples, from: decoded.sampleRate, to: AudioFormat.sampleRate), sampleRate: rate)
        var payload = AudioPayload(data: WAVAudio.encodePCM16(samples, sampleRate: rate))
        payload.levels = windowPeaks(samples, sampleRate: rate)
        return Prepared(payload: payload,
                        peak: min(level(decoded.samples, sampleRate: decoded.sampleRate), 1),
                        duration: Double(decoded.samples.count) / Double(decoded.sampleRate))
    }

    /// The second-highest 100 ms window peak: speech fills several windows, while a lone click or
    /// key bump during a silent hold fills one and would otherwise decide the whole pre-check.
    /// ponytail: typing or repeated clicks during a silent hold still pass; a per-window energy
    /// measure would catch those but needs calibrating against a real voice.
    static func level(_ samples: [Float], sampleRate: Int) -> Float {
        let peaks = windowPeaks(samples, sampleRate: sampleRate).sorted(by: >)
        return peaks.count > 1 ? peaks[1] : peaks.first ?? 0
    }

    /// The peak of each 100 ms window, in order.
    static func windowPeaks(_ samples: [Float], sampleRate: Int) -> [Float] {
        let window = max(sampleRate / 10, 1)
        return stride(from: 0, to: samples.count, by: window).map { start in
            samples[start..<min(start + window, samples.count)].reduce(Float(0)) { max($0, $1.isFinite ? abs($1) : 0) }
        }
    }

    /// Drops the silent tail (below the silence pre-check's level) after the last speech, keeping
    /// `pad` seconds, because Whisper-style models transcribe a silent tail as "Thank you." An
    /// all-silent clip is returned unchanged for the pre-check to judge. A click in the tail keeps
    /// the tail from there on, which costs only the trim, never speech.
    static func trimTrailingSilence(_ samples: [Float], sampleRate: Int, pad: Double = 0.5) -> [Float] {
        guard let last = samples.lastIndex(where: { abs($0) >= AudioFormat.silencePeak }) else { return samples }
        return Array(samples.prefix(last + 1 + Int(pad * Double(sampleRate))))
    }
}

enum WAVAudio {
    struct Decoded { var samples: [Float]; var sampleRate: Int }

    private static func u16(_ b: [UInt8], _ i: Int) -> Int { Int(b[i]) | Int(b[i + 1]) << 8 }
    private static func u32(_ b: [UInt8], _ i: Int) -> Int { u16(b, i) | u16(b, i + 2) << 16 }

    /// Mono samples in -1...1 (channels averaged). Nil for anything but RIFF/WAVE PCM or float.
    static func decode(_ data: Data) -> Decoded? {
        let b = [UInt8](data)
        guard b.count >= 12, b[0..<4] == [0x52, 0x49, 0x46, 0x46], b[8..<12] == [0x57, 0x41, 0x56, 0x45] else { return nil }
        var format = 0, channels = 0, rate = 0, bits = 0
        var body: Range<Int>?
        var i = 12
        while i + 8 <= b.count {
            let id = String(decoding: b[i..<i + 4], as: UTF8.self)
            var size = u32(b, i + 4)
            let start = i + 8
            // A data chunk whose size was never finalized (0 or 0xFFFFFFFF) runs to the end of the file.
            if id == "data" && (size == 0 || size == 0xFFFF_FFFF) { size = b.count - start }
            let end = min(b.count, start + size)
            if id == "fmt " && end - start >= 16 {
                format = u16(b, start); channels = u16(b, start + 2); rate = u32(b, start + 4); bits = u16(b, start + 14)
                if format == 0xFFFE && end - start >= 26 { format = u16(b, start + 24) }
            } else if id == "data" {
                body = start..<end
                break
            }
            guard size >= 0, start + size + (size & 1) > i else { return nil }
            i = start + size + (size & 1)
        }
        guard let body, channels > 0, channels <= 32, rate > 0,
              (format == 1 && [16, 24, 32].contains(bits)) || (format == 3 && [32, 64].contains(bits)) else { return nil }
        let width = bits / 8
        let frame = width * channels
        let frames = body.count / frame
        var samples = [Float](repeating: 0, count: frames)
        for f in 0..<frames {
            var sum: Float = 0
            for c in 0..<channels {
                let o = body.lowerBound + f * frame + c * width
                let value: Float
                switch (format, bits) {
                case (1, 16): value = Float(Int16(truncatingIfNeeded: u16(b, o))) / 32768
                case (1, 24): value = Float(Int32(truncatingIfNeeded: (u16(b, o) | Int(b[o + 2]) << 16) << 8) >> 8) / 8_388_608
                case (1, 32): value = Float(Int32(truncatingIfNeeded: u32(b, o))) / 2_147_483_648
                case (3, 32): value = Float(bitPattern: UInt32(truncatingIfNeeded: u32(b, o)))
                default: value = Float(Double(bitPattern: UInt64(truncatingIfNeeded: u32(b, o)) | UInt64(truncatingIfNeeded: u32(b, o + 4)) << 32))
                }
                sum += value.isFinite ? value : 0
            }
            samples[f] = sum / Float(channels)
        }
        return Decoded(samples: samples, sampleRate: rate)
    }

    /// Box-filter decimation. Rates at or below `to` are returned unchanged (never upsampled).
    static func downsample(_ samples: [Float], from: Int, to: Int) -> [Float] {
        guard from > to, to > 0, !samples.isEmpty else { return samples }
        let ratio = Double(from) / Double(to)
        let count = Int(Double(samples.count) / ratio)
        var out = [Float](repeating: 0, count: count)
        for n in 0..<count {
            let lo = Int(Double(n) * ratio), hi = min(samples.count, max(lo + 1, Int(Double(n + 1) * ratio)))
            var sum: Float = 0
            for k in lo..<hi { sum += samples[k] }
            out[n] = sum / Float(hi - lo)
        }
        return out
    }

    static func encodePCM16(_ samples: [Float], sampleRate: Int) -> Data {
        var out = [UInt8]()
        out.reserveCapacity(44 + samples.count * 2)
        func put32(_ v: Int) { for s in stride(from: 0, to: 32, by: 8) { out.append(UInt8((v >> s) & 0xFF)) } }
        func put16(_ v: Int) { out.append(UInt8(v & 0xFF)); out.append(UInt8((v >> 8) & 0xFF)) }
        let bytes = samples.count * 2
        out += Array("RIFF".utf8); put32(36 + bytes); out += Array("WAVE".utf8)
        out += Array("fmt ".utf8); put32(16); put16(1); put16(1); put32(sampleRate); put32(sampleRate * 2); put16(2); put16(16)
        out += Array("data".utf8); put32(bytes)
        for s in samples {
            let v = Int16(max(-32768, min(32767, (s.isFinite ? s : 0) * 32767)))
            put16(Int(UInt16(bitPattern: v)))
        }
        return Data(out)
    }
}
