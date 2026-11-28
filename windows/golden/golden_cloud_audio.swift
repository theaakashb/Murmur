// deps: AudioFormat CleanupTypes CleanupText CleanupGuard CleanupPrompt RuleCleaner ProviderTypes ProviderHTTP OpenAICompatible Credentials CloudAudio DictationLanguages
import Foundation
import CryptoKit

// Golden outputs of WAVAudio and CloudAudio over synthetic audio. Samples come from integer recipes (no sin), so both sides make
// the same floats bit for bit; WAV files are described by their layout and built the same way on both sides. Floats are recorded
// as UInt32 bit patterns, sample arrays and WAV bytes as SHA-256 and length. One case per line. Run from the repository root.

func line(_ o: Any) -> String {
    String(decoding: try! JSONSerialization.data(withJSONObject: o, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
}
func opt(_ v: Any?) -> Any { v ?? NSNull() }
func sha(_ d: Data) -> String { SHA256.hash(data: d).map { String(format: "%02x", $0) }.joined() }
func floatSha(_ s: [Float]) -> String { var d = Data(); for f in s { withUnsafeBytes(of: f.bitPattern.littleEndian) { d.append(contentsOf: $0) } }; return sha(d) }
func bits(_ f: Float) -> Int { Int(f.bitPattern) }

/// One segment of a recipe: kind, sample count, amplitude.
/// lcg: pseudo-random in -amp...amp; square: ±amp, 40-sample period; ramp: a 200-sample sawtooth; flat: amp; spike: zeros with one amp sample in the middle.
func samples(_ recipe: [[Any]]) -> [Float] {
    var out: [Float] = []
    var x: UInt32 = 12345
    for seg in recipe {
        let kind = seg[0] as! String, count = seg[1] as! Int, amp = Float(seg[2] as! Double)
        for i in 0..<count {
            switch kind {
            case "lcg":
                x = x &* 1_103_515_245 &+ 12345
                out.append(Float(Int((x >> 8) & 0xFFFF) - 32768) / 32768 * amp)
            case "square": out.append((i / 20) % 2 == 0 ? amp : -amp)
            case "ramp": out.append(Float(i % 200 - 100) / 100 * amp)
            case "spike": out.append(i == count / 2 ? amp : 0)
            default: out.append(amp)
            }
        }
    }
    return out
}

/// A WAV file: format 1 (PCM) or 3 (float), optionally WAVE_FORMAT_EXTENSIBLE, with extra chunks and a data size override.
/// Channel c carries the sample times (c + 1) / channels.
func wav(_ s: [Float], rate: Int, channels: Int, format: Int, bits: Int, extensible: Bool, extra: Bool, dataSize: Int?, truncate: Int) -> Data {
    var b = [UInt8]()
    func p32(_ v: Int) { for k in stride(from: 0, to: 32, by: 8) { b.append(UInt8((v >> k) & 0xFF)) } }
    func p16(_ v: Int) { b.append(UInt8(v & 0xFF)); b.append(UInt8((v >> 8) & 0xFF)) }
    let width = bits / 8
    b += Array("RIFF".utf8); p32(0); b += Array("WAVE".utf8)
    if extra { b += Array("LIST".utf8); p32(5); b += [1, 2, 3, 4, 5, 0] }
    b += Array("fmt ".utf8); p32(extensible ? 40 : 16); p16(extensible ? 0xFFFE : format); p16(channels); p32(rate); p32(rate * channels * width); p16(channels * width); p16(bits)
    if extensible { p16(22); p16(bits); p32(3); p16(format); b += [0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71] }
    if extra { b += Array("FLLR".utf8); p32(4); b += [0, 0, 0, 0] }
    b += Array("data".utf8); p32(dataSize ?? s.count * channels * width)
    for v in s {
        for c in 0..<channels {
            let x = v * Float(c + 1) / Float(channels)
            switch (format, bits) {
            case (1, 16): p16(Int(UInt16(bitPattern: Int16(max(-32768, min(32767, x * 32767))))))
            case (1, 24): let i = Int(Int32(max(-8_388_608, min(8_388_607, x * 8_388_607)))); for k in [0, 8, 16] { b.append(UInt8((i >> k) & 0xFF)) }
            case (1, 32): p32(Int(Int32(max(-2_147_483_648, min(2_147_483_647, Double(x) * 2_147_483_647)))))
            case (3, 32): p32(Int(x.bitPattern))
            case (3, 64): let u = Double(x).bitPattern; p32(Int(u & 0xFFFF_FFFF)); p32(Int(u >> 32))
            default: for _ in 0..<width { b.append(0x7F) }
            }
        }
    }
    let riff = b.count - 8; for k in 0..<4 { b[4 + k] = UInt8((riff >> (8 * k)) & 0xFF) }
    return Data(b.prefix(max(0, b.count - truncate)))
}

@main struct Gen {
    static func main() throws {
        let recipes: [(String, [[Any]])] = [
            ("empty", []), ("one", [["flat", 1, 0.5]]), ("speech", [["lcg", 4800, 0.5]]), ("speechThenSilence", [["lcg", 9600, 0.3], ["flat", 19200, 0.001]]),
            ("silence", [["flat", 48000, 0.001]]), ("square", [["square", 16000, 0.8]]), ("ramp", [["ramp", 7777, 1.0]]), ("click", [["flat", 24000, 0.002], ["spike", 101, 0.5], ["flat", 23899, 0.002]]),
            ("word", [["flat", 24000, 0.002], ["lcg", 9600, 0.3], ["flat", 24000, 0.002]]), ("loud", [["lcg", 3000, 1.0]]), ("clipped", [["square", 400, 1.5]]),
            ("tailClick", [["lcg", 4800, 0.3], ["flat", 20000, 0.0], ["spike", 11, 0.2], ["flat", 30000, 0.0]]), ("threshold", [["flat", 100, 0.01], ["flat", 100, 0.0099]]),
            ("negative", [["flat", 50, -0.7]]),
        ]
        var functions: [String] = []
        for (name, recipe) in recipes {
            let s = samples(recipe)
            for rate in [48_000, 44_100, 16_000, 8_000, 22_050, 1, 5] {
                let down = WAVAudio.downsample(s, from: rate, to: AudioFormat.sampleRate)
                let peaks = CloudAudio.windowPeaks(s, sampleRate: rate)
                functions.append(line(["recipe": recipe, "name": name, "rate": rate, "count": s.count, "samplesSha": floatSha(s),
                                       "downsampleCount": down.count, "downsampleSha": floatSha(down),
                                       "peaksCount": peaks.count, "peaksSha": floatSha(peaks), "peaksHead": peaks.prefix(12).map(bits),
                                       "level": bits(CloudAudio.level(s, sampleRate: rate)), "trimmed": CloudAudio.trimTrailingSilence(s, sampleRate: rate).count,
                                       "trimmedPad0": CloudAudio.trimTrailingSilence(s, sampleRate: rate, pad: 0).count,
                                       "pcm16Length": WAVAudio.encodePCM16(s, sampleRate: rate).count, "pcm16Sha": sha(WAVAudio.encodePCM16(s, sampleRate: rate))]))
            }
        }
        // Non-finite samples: encoded as 0, ignored by peaks.
        let odd: [Float] = [.nan, .infinity, -.infinity, 0.5, -2, 2, 1, -1, 0.99999, -0.99999, Float.leastNonzeroMagnitude]
        functions.append(line(["special": odd.map(bits), "pcm16Sha": sha(WAVAudio.encodePCM16(odd, sampleRate: 16_000)), "peaks": CloudAudio.windowPeaks(odd, sampleRate: 10).map(bits),
                               "trimmed": CloudAudio.trimTrailingSilence(odd, sampleRate: 4).count]))

        var files: [String] = []
        let layouts: [(Int, Int, Int, Bool)] = [(1, 16, 1, false), (1, 16, 2, false), (1, 24, 1, false), (1, 24, 3, true), (1, 32, 2, false), (3, 32, 1, false), (3, 32, 2, true),
                                                (3, 64, 1, false), (3, 64, 2, true), (1, 8, 1, false), (2, 16, 1, false), (3, 16, 1, false), (1, 16, 0, false), (1, 16, 33, false), (1, 16, 32, false)]
        for (name, recipe) in [("speechThenSilence", recipes[3].1), ("word", recipes[8].1), ("click", recipes[7].1), ("short", [["lcg", 777, 0.4]] as [[Any]]), ("empty", [])] {
            let s = samples(recipe)
            for (format, bitsPerSample, channels, extensible) in layouts {
                for (rate, extra, dataSize, truncate) in [(48_000, false, nil, 0), (44_100, true, nil, 0), (16_000, false, 0, 0), (8_000, false, 0xFFFF_FFFF, 3), (48_000, true, nil, 1), (0, false, nil, 0)] as [(Int, Bool, Int?, Int)] {
                    if name != "speechThenSilence" && (extra || dataSize != nil || truncate != 0 || rate == 0) && channels != 1 { continue }
                    let data = wav(s, rate: rate, channels: channels, format: format, bits: bitsPerSample, extensible: extensible, extra: extra, dataSize: dataSize, truncate: truncate)
                    let decoded = WAVAudio.decode(data)
                    let prepared = CloudAudio.prepare(data)
                    files.append(line(["recipe": recipe, "name": name, "format": format, "bits": bitsPerSample, "channels": channels, "extensible": extensible, "rate": rate,
                                       "extra": extra, "dataSize": opt(dataSize), "truncate": truncate, "fileLength": data.count, "fileSha": sha(data),
                                       "decoded": opt(decoded.map { ["rate": $0.sampleRate, "count": $0.samples.count, "samplesSha": floatSha($0.samples)] as [String: Any] }),
                                       "prepared": ["length": prepared.payload.data.count, "sha": sha(prepared.payload.data), "peak": opt(prepared.peak.map(bits)),
                                                    "duration": opt(prepared.duration), "levels": opt(prepared.payload.levels.map(floatSha)), "levelsCount": opt(prepared.payload.levels?.count),
                                                    "mimeType": prepared.payload.mimeType, "filename": prepared.payload.filename, "language": opt(prepared.payload.language)] as [String: Any]]))
                }
            }
        }
        for raw in ["", "RIFF", "not a wav", "RIFF\u{0}\u{0}\u{0}\u{0}WAVE", "RIFF\u{0}\u{0}\u{0}\u{0}WAVEfmt "] {
            let data = Data(raw.utf8)
            files.append(line(["raw": raw, "decoded": opt(WAVAudio.decode(data).map { $0.samples.count }), "prepared": ["length": CloudAudio.prepare(data).payload.data.count,
                               "peak": opt(CloudAudio.prepare(data).peak.map(bits))]]))
        }
        print("{\n\"functions\": [\n" + functions.joined(separator: ",\n") + "\n],\n\"files\": [\n" + files.joined(separator: ",\n") + "\n]\n}")
    }
}
