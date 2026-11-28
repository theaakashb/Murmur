// deps: AudioFormat CleanupTypes CleanupText CleanupGuard CleanupPrompt RuleCleaner ProviderTypes ProviderHTTP OpenAICompatible Credentials
import Foundation

// Exports the provider constants Murmur.Core loads: audio format, limits, Whisper thresholds and timeouts.
// Literals inside Swift functions (the 75 s upload deadline, the verbose_json hosts, the 8 MB response cap) are pinned by golden cases instead.
struct Audio: Encodable { var sampleRate: Int, silencePeak: Float, minimumSpeechSeconds: Double }
struct Limits: Encodable { var maxOutputTokens: Int, validationDeadline: Double }
struct OpenAI: Encodable {
    var noSpeechThreshold: Double, logprobThreshold: Double, minLoudWindows: Int, uploadLimitBytes: Int
    var ollamaDefault: String, defaultTimeout: Double, maximumTimeout: Double
}
struct Export: Encodable { var audioFormat: Audio, providerLimits: Limits, openAI: OpenAI }

@main struct Gen {
    static func main() throws {
        let export = Export(
            audioFormat: Audio(sampleRate: AudioFormat.sampleRate, silencePeak: AudioFormat.silencePeak, minimumSpeechSeconds: AudioFormat.minimumSpeechSeconds),
            providerLimits: Limits(maxOutputTokens: ProviderLimits.maxOutputTokens, validationDeadline: ProviderLimits.validationDeadline),
            openAI: OpenAI(noSpeechThreshold: OpenAITranscriptionFormat.noSpeechThreshold, logprobThreshold: OpenAITranscriptionFormat.logprobThreshold,
                           minLoudWindows: OpenAITranscriptionFormat.minLoudWindows, uploadLimitBytes: OpenAICompatibleTranscriptionProvider.uploadLimitBytes,
                           ollamaDefault: OpenAICompatibleCleanupProvider.ollamaDefault, defaultTimeout: OpenAICompatibleCleanupProvider.defaultTimeout,
                           maximumTimeout: OpenAICompatibleCleanupProvider.maximumTimeout))
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
