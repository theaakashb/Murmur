// deps: AudioFormat CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention Cleanup Settings CleanupPrompt ProviderTypes ProviderHTTP OpenAICompatible Credentials VoiceCues Processing WritingModes Routes CloudAudio KeyCheck AppleCleanup LocalCleanup AIEngine DictationLanguages
import Foundation

// Exports the route settings' keys and placeholders, the key-binding preference name, the key UI strings and the
// cloud transcription messages that Murmur.Core loads. Windows ports no migrations, but keeps the legacy key names
// that DataBackup.destinationSettings lists, so a restore never redirects dictations on either app.
struct Keys: Encodable {
    var transcription, legacyCloudTranscriber, cleanup, customAPIBaseURL, customTranscriptionAPIBaseURL: String
    var customAPIModel, customTranscriptionModel, customAPIReasoningEffort, legacyServerURL, legacyServerModel: String
}
struct Text: Encodable {
    var saved, none, invalid, storeFailed, removeFailed, removed, checking, missing, unreadable: String
    var providers: [String]
}
struct Export: Encodable {
    var keys: Keys
    var placeholderAPIURL, placeholderModel, defaultReasoningEffort, keyBindingPreference: String
    var unreadableAudio, unknownFailure: String
    var keyText: Text
}

@main struct Gen {
    static func main() throws {
        typealias K = RouteSettings.Key
        let export = Export(
            keys: Keys(transcription: K.transcription, legacyCloudTranscriber: K.legacyCloudTranscriber, cleanup: K.cleanup, customAPIBaseURL: K.customAPIBaseURL,
                       customTranscriptionAPIBaseURL: K.customTranscriptionAPIBaseURL, customAPIModel: K.customAPIModel, customTranscriptionModel: K.customTranscriptionModel,
                       customAPIReasoningEffort: K.customAPIReasoningEffort, legacyServerURL: K.legacyServerURL, legacyServerModel: K.legacyServerModel),
            placeholderAPIURL: RouteSettings.placeholderAPIURL, placeholderModel: RouteSettings.placeholderModel,
            defaultReasoningEffort: RouteSettings().customAPIReasoningEffort, keyBindingPreference: CustomAPIKeyBinding.preference,
            unreadableAudio: CloudTranscription.unreadableAudio, unknownFailure: CloudTranscription.unknownFailure,
            keyText: Text(saved: KeyText.saved, none: KeyText.none, invalid: KeyText.invalid, storeFailed: KeyText.storeFailed, removeFailed: KeyText.removeFailed,
                          removed: KeyText.removed, checking: KeyText.checking, missing: KeyText.missing, unreadable: KeyText.unreadable,
                          providers: KeyText.providers.map(\.rawValue)))
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
        print(String(decoding: try encoder.encode(export), as: UTF8.self))
    }
}
