import FluidAudio
import Foundation
import WhisperKit

extension ModelBench {
    static func multilingual(_ engine: String, arguments: [String]) async throws {
        guard arguments.count == 2 || (arguments.count == 3 && arguments[2] == "--single") else {
            throw NSError(domain: usage, code: 1)
        }
        let folder = URL(fileURLWithPath: arguments[0])
        let transcribe: ([Float]) async throws -> String
        switch engine {
        case "--parakeet-tdt":
            let models = try AsrModels.loadLocal(from: folder, version: .v3)
            let manager = AsrManager(models: models)
            transcribe = { samples in
                var state = try TdtDecoderState()
                return try await manager.transcribe(samples, decoderState: &state).text
            }
        case "--whisper-turbo":
            let directory = folder.appendingPathComponent("dictionlabs_whisper-large-v3-turbo-q6q8")
            // WhisperKit can otherwise try the Hub when local tokenizer parsing fails.
            setenv("HF_ENDPOINT", "file:///dev/null", 1)
            let engine = try await WhisperKit(WhisperKitConfig(
                modelFolder: directory.path, tokenizerFolder: directory,
                computeOptions: ModelComputeOptions(audioEncoderCompute: .cpuAndGPU, textDecoderCompute: .cpuAndGPU),
                verbose: false, load: true, download: false))
            transcribe = { samples in
                let options = DecodingOptions(task: .transcribe, detectLanguage: true, skipSpecialTokens: true, chunkingStrategy: .vad)
                return try await engine.transcribe(audioArray: samples, decodeOptions: options)
                    .map(\.text).joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            }
        default:
            throw NSError(domain: usage, code: 1)
        }

        if arguments[1] != "--serve" {
            try await emit(["text": transcribe(readSamples(arguments[1]))])
            return
        }
        try emit(["ready": true])
        while let line = readLine() {
            do {
                let samples: [Float]
                if line.hasPrefix("{") {
                    guard let request = try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: String],
                          let encoded = request["samples"], let data = Data(base64Encoded: encoded),
                          !data.isEmpty, data.count % 4 == 0 else {
                        try emit(["error": "invalid audio request"]); continue
                    }
                    samples = floats(data)
                } else {
                    samples = try readSamples(line)
                }
                try await emit(["text": transcribe(samples)])
            } catch {
                try emit(["error": "transcription failed"])
            }
        }
    }
}
