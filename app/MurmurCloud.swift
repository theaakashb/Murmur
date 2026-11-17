import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Murmur Cloud: Murmur's hosted cleanup and transcription, reached with one Murmur Cloud key.
// Cleanup is Murmur's own JSON (the server builds the prompt), not OpenAI chat; transcription is the
// OpenAI-style multipart upload. The guard stays in the app (Processing.cloudClean).

enum MurmurCloud {
    static let baseURL = URL(string: "https://api-murmur.clouddev.cc")!
    static var host: String { baseURL.host! }
    static let cleanupModel = "murmur-cleanup"
    static let transcriptionModel = "murmur-transcribe"
    /// The server's cleanup call to its model times out after 30 s; this leaves room for the upload and reply.
    static let cleanupDeadline: TimeInterval = 45

    /// `GET /v1/models` with the key: 200 accepted, 401 bad key, 402 no active plan.
    static func modelsRequest(key: APIKey) -> URLRequest {
        var request = URLRequest(url: baseURL.appendingPathComponent("v1/models"))
        request.httpMethod = "GET"
        request.setValue("Bearer \(key.secret)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    static func validate(key: APIKey, transport: HTTPTransport) async throws {
        let result = try await ProviderHTTP(provider: .murmurCloud, transport: transport, deadline: ProviderLimits.validationDeadline).execute(modelsRequest(key: key))
        try validationResult(provider: .murmurCloud, result)
    }
}

/// `POST /v1/cleanup`. The server rejects any key it does not document (400), so the body is exactly these fields.
enum MurmurCloudCleanupFormat {
    struct Context: Encodable, Equatable { let selected: String, clipboard: String, window: String }
    struct Body: Encodable, Equatable {
        let transcript: String
        let level: String
        let fillers: Bool, corrections: Bool, punctuation: Bool, lists: Bool
        let terms: [String]?
        let style_notes: String?
        let context: Context?
    }

    /// Nil for Strict (no polish): the server takes only polish and professional, and Strict never uses a model.
    static func body(_ input: CleanupInput) -> Body? {
        let options = input.options
        guard options.polish, !input.sent.isEmpty else { return nil }
        return Body(transcript: input.sent, level: options.professional ? "professional" : "polish",
                    fillers: options.fillers, corrections: options.corrections, punctuation: options.punctuation, lists: options.lists,
                    terms: input.promptTerms.isEmpty ? nil : input.promptTerms,
                    style_notes: input.notes.isEmpty ? nil : input.notes,
                    context: input.context.map { Context(selected: $0.selected, clipboard: $0.clipboard, window: $0.window) })
    }

    static func makeRequest(_ input: CleanupInput, key: APIKey) throws -> URLRequest {
        guard let body = body(input) else { throw ProviderError.invalidConfiguration(.murmurCloud) }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var request = URLRequest(url: MurmurCloud.baseURL.appendingPathComponent("v1/cleanup"))
        request.httpMethod = "POST"
        request.setValue("Bearer \(key.secret)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try encoder.encode(body)
        return request
    }

    /// `{"text": "..."}`; any other status or an error object goes through the shared classifier.
    static func parse(_ result: HTTPResult) throws -> String {
        guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: .murmurCloud, result: result) }
        guard let object = try? JSONSerialization.jsonObject(with: result.body) as? [String: Any] else { throw ProviderError.malformedResponse(.murmurCloud) }
        if object["error"] != nil { throw ProviderErrorClassifier.classify(provider: .murmurCloud, result: result) }
        guard let text = object["text"] as? String else { throw ProviderError.malformedResponse(.murmurCloud) }
        return text
    }
}

struct MurmurCloudCleanupProvider: CleanupProvider {
    let id = ProviderID.murmurCloud
    let apiKey: APIKey
    var transport: HTTPTransport = URLSessionTransport()
    var modelID: String? { MurmurCloud.cleanupModel }
    var shape: CleanupShape { CleanupShape(takesPresets: false) }

    var disclosure: DataDisclosure {
        DataDisclosure(provider: id, stage: .cleanup, destinationHost: MurmurCloud.host,
                       leavesDevice: ["the transcript text", "relevant dictionary terms", "any enabled screen context and style notes"],
                       staysOnDevice: ["audio", "history", "the rest of the dictionary"],
                       retentionNote: "Murmur Cloud has it cleaned by a model on Amazon Bedrock.")
    }

    /// Rewrite presets and meeting notes carry no typed input: the server does not take them, so they are refused, not approximated.
    func cleanup(_ messages: CleanupMessages) async throws -> String {
        guard let input = messages.input else { throw ProviderError.invalidConfiguration(id) }
        let request = try MurmurCloudCleanupFormat.makeRequest(input, key: apiKey)
        return try MurmurCloudCleanupFormat.parse(try await ProviderHTTP(provider: id, transport: transport, deadline: MurmurCloud.cleanupDeadline).execute(request))
    }

    func validate() async throws { try await MurmurCloud.validate(key: apiKey, transport: transport) }
}

/// `POST /v1/audio/transcriptions`, shaped like the custom provider's upload. No dictionary prompt (VOCABULARY.md).
struct MurmurCloudTranscriptionProvider: DetailedTranscriber {
    let id = ProviderID.murmurCloud
    let apiKey: APIKey
    var transport: HTTPTransport = URLSessionTransport()
    var modelID: String? { MurmurCloud.transcriptionModel }
    var endpoint: URL { MurmurCloud.baseURL.appendingPathComponent("v1/audio/transcriptions") }

    var disclosure: DataDisclosure {
        DataDisclosure(provider: id, stage: .transcription, destinationHost: MurmurCloud.host,
                       leavesDevice: ["audio"], staysOnDevice: ["dictionary", "history"],
                       retentionNote: "Murmur Cloud has it transcribed by Groq.")
    }

    func makeRequest(_ audio: AudioPayload, boundary: String = Multipart.makeBoundary()) -> URLRequest {
        OpenAITranscriptionFormat.makeRequest(endpoint: endpoint, apiKey: apiKey, model: MurmurCloud.transcriptionModel, audio: audio, boundary: boundary)
    }
    func transcribe(_ audio: AudioPayload) async throws -> String { try await transcribeDetailed(audio).text }
    func transcribeDetailed(_ audio: AudioPayload) async throws -> TranscriptionResult {
        let limit = OpenAICompatibleTranscriptionProvider.uploadLimitBytes
        guard audio.data.count <= limit else { throw ProviderError.payloadTooLarge(id, limitBytes: limit) }
        let result = try await ProviderHTTP(provider: id, transport: transport, deadline: 75).execute(makeRequest(audio))
        return try OpenAITranscriptionFormat.parseDetailed(provider: id, result: result, levels: audio.levels)
    }
    func validate() async throws { try await MurmurCloud.validate(key: apiKey, transport: transport) }
}
