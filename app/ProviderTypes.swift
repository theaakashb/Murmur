import Foundation

// Rules enforced here:
// - Exactly one HTTP request per call. No automatic retry (a retry could duplicate output)
//   and no fallback to another provider or model.
// - Every request has an explicit total deadline. Task cancellation cancels the request.
// - Error messages are built from fixed strings, the provider name and status codes. They never
//   include response bodies, API keys or transcript text.



/// Recorded audio sent to a transcription provider.
struct AudioPayload: Sendable {
    var data: Data
    var filename: String
    var mimeType: String
    /// ISO-639-1, e.g. "en". Nil lets the provider detect it.
    var language: String?
    /// Peak level of each 100 ms window of the audio sent, so a transcript segment can be checked
    /// against the sound under it. Nil when the audio was not decoded. Never sent.
    var levels: [Float]? = nil

    init(data: Data, filename: String = "audio.wav", mimeType: String = "audio/wav", language: String? = "en") {
        self.data = data; self.filename = filename; self.mimeType = mimeType
        self.language = language
    }
}

/// Why a transcript was judged no speech. Raw values are only logged, never stored.
enum NoSpeechReason: String, Sendable { case precheck, lowConfidence, insufficientLoudness, empty, punctuationOnly }

/// The silence pre-check's result. Stored in History as its raw value (the field stays a String so older values survive).
enum Precheck: String, Sendable { case passed, skipped, unchecked }

/// A transcriber's answer with the detail History keeps. Segments only where the model reports them.
struct TranscriptionResult: Sendable, Equatable {
    var text: String
    var segments: [TranscriptSegment]? = nil
    /// The parser's rejection path, rather than an inference from the dropped flags.
    var noSpeechReason: NoSpeechReason? = nil
}

/// One Whisper segment: its scores and times, the 100 ms windows under it that reach the silence level,
/// and whether the parser dropped it from the transcript.
struct TranscriptSegment: Sendable, Equatable {
    var text: String
    var start: Double?, end: Double?
    var avgLogprob: Double?, noSpeechProb: Double?, compressionRatio: Double?
    var loudWindows: Int?
    var dropped = false
}

/// A transcriber that returns segment detail too; `transcribe` stays its text.
protocol DetailedTranscriber: TranscriptionProvider {
    func transcribeDetailed(_ audio: AudioPayload) async throws -> TranscriptionResult
}

enum ProcessingStage: String, Sendable { case transcription, cleanup }

/// What leaves the device for one route. Show it before the first cloud use.
struct DataDisclosure: Equatable, Sendable {
    let provider: ProviderID
    let stage: ProcessingStage
    let destinationHost: String
    let leavesDevice: [String]
    let staysOnDevice: [String]
    let retentionNote: String
    /// Extra caution shown with the disclosure, e.g. an unencrypted connection.
    var warning: String? = nil

    /// True when nothing in this route leaves the Mac (a loopback local server).
    var staysOnThisMac: Bool { leavesDevice.isEmpty }

    var summary: String {
        let main: String
        if staysOnThisMac {
            main = "\(provider.displayName) \(stage.rawValue) runs on this Mac (\(destinationHost)); nothing leaves this Mac. \(retentionNote)"
        } else {
            func list(_ items: [String]) -> String { items.count < 2 ? items.joined() : items.dropLast().joined(separator: ", ") + " and " + items.last! }
            main = provider == .murmurCloud
                ? "Sends \(list(leavesDevice)) to Murmur Cloud. See murmurdictation.com/privacy for details."
                : "Sends \(list(leavesDevice)) to \(destinationHost). \(retentionNote)"
        }
        return warning.map { main + " " + $0 } ?? main
    }
}

/// The caller's choice for the session after a provider error.
enum SessionDisposition: Equatable, Sendable {
    /// Keep audio or raw text. Offer an explicit retry later with the same route.
    case preserveRetryLater
    /// Keep audio or raw text. Retrying unchanged will fail; the user must change a key,
    /// plan, model, or route first.
    case preserveNeedsChange
    /// The user cancelled. Discard according to the cancellation policy.
    case discard
}

enum ProviderError: Error, Equatable, Sendable, CustomStringConvertible, LocalizedError {
    case authentication(ProviderID)
    case quota(ProviderID)
    case rateLimited(ProviderID, retryAfter: TimeInterval?)
    case network(ProviderID)
    case timeout(ProviderID)
    case payloadTooLarge(ProviderID, limitBytes: Int?)
    case server(ProviderID, status: Int?)
    case malformedResponse(ProviderID)
    /// The reply stopped at the output-token cap (`finish_reason` "length"): the answer is cut off. Meeting notes split the request once and retry; everything else treats it like `malformedResponse`.
    case outputTruncated(ProviderID)
    /// The provider's safety or moderation filter declined the input.
    case contentBlocked(ProviderID)
    /// Other 4xx, e.g. an unknown model or a bad parameter.
    case rejected(ProviderID, status: Int)
    /// Missing key or an invalid local setting; no request was sent.
    case invalidConfiguration(ProviderID)
    /// Connection refused by a user-run model server: nothing is listening at `address`
    /// ("host:port", taken from the configured base URL, never from request content).
    case serverNotRunning(ProviderID, address: String)
    /// An on-device model that cannot run now (Apple Intelligence); nothing was generated.
    case modelUnavailable(ProviderID, OnDeviceModelUnavailable)
    case cancelled

    /// The case name, for History and the timing log (stored, so not derived from reflection).
    var kind: String {
        switch self {
        case .authentication: return "authentication"
        case .quota: return "quota"
        case .rateLimited: return "rateLimited"
        case .network: return "network"
        case .timeout: return "timeout"
        case .payloadTooLarge: return "payloadTooLarge"
        case .server: return "server"
        case .malformedResponse: return "malformedResponse"
        case .outputTruncated: return "outputTruncated"
        case .contentBlocked: return "contentBlocked"
        case .rejected: return "rejected"
        case .invalidConfiguration: return "invalidConfiguration"
        case .serverNotRunning: return "serverNotRunning"
        case .modelUnavailable: return "modelUnavailable"
        case .cancelled: return "cancelled"
        }
    }

    var provider: ProviderID? {
        switch self {
        case .authentication(let p), .quota(let p), .rateLimited(let p, _), .network(let p), .timeout(let p),
             .payloadTooLarge(let p, _), .server(let p, _), .malformedResponse(let p), .outputTruncated(let p), .contentBlocked(let p),
             .rejected(let p, _), .invalidConfiguration(let p), .serverNotRunning(let p, _), .modelUnavailable(let p, _):
            return p
        case .cancelled:
            return nil
        }
    }

    var disposition: SessionDisposition {
        switch self {
        case .rateLimited, .network, .timeout, .server, .malformedResponse, .outputTruncated, .serverNotRunning: return .preserveRetryLater
        case .authentication, .quota, .payloadTooLarge, .contentBlocked, .rejected, .invalidConfiguration, .modelUnavailable: return .preserveNeedsChange
        case .cancelled: return .discard
        }
    }

    /// Short message for the bubble or history. Contains no key, response body or transcript text.
    var userMessage: String {
        let name = provider?.displayName ?? "The provider"
        switch self {
        case .authentication:
            return "\(name) rejected the API key. Check the key in Settings."
        case .quota:
            return "\(name) reports no remaining quota or credits for this account. Check your plan or billing, or choose another route."
        case .rateLimited(_, let retryAfter):
            if let seconds = retryAfter, seconds.isFinite, seconds > 0, seconds.rounded(.up) < Double(Int.max) { return "\(name) is rate limiting requests. Try again in \(Int(seconds.rounded(.up))) seconds." }
            return "\(name) is rate limiting requests. Try again shortly."
        case .network(let p) where p == .localServer:
            return "Couldn't reach the local model server. Check that it is running and that the address in Settings is correct."
        case .network:
            return "Couldn't reach \(name). Check your internet connection."
        case .timeout(let p) where p == .localServer:
            return "The local model server didn't respond in time. The model may still be loading; try again, or choose a smaller model."
        case .timeout:
            return "\(name) didn't respond in time."
        case .payloadTooLarge(_, let limit):
            if let limit = limit { return "The recording is too large for \(name) (limit \(limit / 1_000_000) MB)." }
            return "The request is too large for \(name)."
        case .server(_, let status):
            if let status = status { return "\(name) had a server problem (HTTP \(status))." }
            return "\(name) had a server problem."
        case .malformedResponse, .outputTruncated:
            return "\(name) returned a response Murmur couldn't use."
        case .contentBlocked:
            return "\(name) declined to process this dictation (content filter)."
        case .rejected(let p, let status) where p == .murmurCloud:
            return "\(name) rejected the request (HTTP \(status))."
        case .rejected(_, let status):
            return "\(name) rejected the request (HTTP \(status)). Check the model setting."
        case .invalidConfiguration(let p) where p == .localServer:
            return "The local model server isn't set up. Check the server address (http or https, no user name or password) and the model name in Settings."
        case .invalidConfiguration(let p) where p == .customAPI:
            return "The custom API provider isn't set up. Check the API base URL, model name and key in Settings."
        case .invalidConfiguration(let p) where p == .murmurCloud:
            return "Murmur Cloud isn't set up. Add your Murmur Cloud key in Settings."
        case .invalidConfiguration:
            return "\(name) isn't set up. Add an API key and model in Settings."
        case .serverNotRunning(_, let address):
            return "No local model server is running at \(address). Start it, or check the address in Settings."
        case .modelUnavailable(_, let reason):
            return reason.userMessage
        case .cancelled:
            return "Cancelled."
        }
    }

    var description: String { userMessage }
    var errorDescription: String? { userMessage }
}

/// Why Apple's on-device model can't run (FoundationModels `SystemLanguageModel.availability`).
enum OnDeviceModelUnavailable: String, Equatable, Sendable {
    case deviceNotEligible
    case appleIntelligenceOff
    case modelNotReady
    case modelNotDownloaded
    case modelDamaged

    var userMessage: String {
        switch self {
        case .deviceNotEligible: return "This Mac can't run Apple Intelligence. Choose another cleanup route in Settings."
        case .appleIntelligenceOff: return "Apple Intelligence is off. Turn it on in System Settings → Apple Intelligence & Siri, or choose another cleanup route."
        case .modelNotReady: return "The Apple Intelligence model isn't ready yet; it may still be downloading. Try again later."
        case .modelDamaged: return "The local cleanup model files are damaged. Remove the model in Settings and download it again, or choose another cleanup route."
        case .modelNotDownloaded: return "The local cleanup model is not downloaded. Download it in Settings or choose another cleanup route."
        }
    }
}

/// Limits and deadlines shared by the provider adapters.
enum ProviderLimits {
    /// Bounds runaway output (a hallucinating model); five minutes of speech is about 1,500 tokens.
    static let maxOutputTokens = 8192
    /// The deadline for key validation and model-list requests.
    static let validationDeadline: TimeInterval = 15
}

/// How shared cleanup code must shape a request for one provider. A provider declares it; callers never test for a concrete type.
struct CleanupShape: Sendable, Equatable {
    /// False: a fixed prompt that takes no screen context or style notes (the managed local model).
    var takesContext = true
    /// Character caps on screen context, for a small context window; nil sends it whole.
    var contextCaps: ContextCaps? = nil
    /// Words per section when long text is cleaned in parallel, each guarded on its own; nil sends the text whole.
    var sectionTarget: Int? = nil
    /// With sections: a sentence opening with a correction cue stays with the one it repairs.
    var keepsCorrectionsAttached = false
    /// False: rewrite presets are refused, and their instructions are not sent as style notes on the ordinary cleanup that follows.
    var takesPresets = true

    struct ContextCaps: Sendable, Equatable { var selected: Int, clipboard: Int, window: Int }
}

protocol TranscriptionProvider: Sendable {
    var id: ProviderID { get }
    var disclosure: DataDisclosure { get }
    /// The model's identifier for History; nil where there is none.
    var modelID: String? { get }
    /// One request, no retry. Throws `ProviderError`; `.cancelled` if the calling Task is cancelled.
    func transcribe(_ audio: AudioPayload) async throws -> String
    /// Minimal authenticated call that consumes no tokens. Throws `ProviderError`.
    func validate() async throws
}

extension TranscriptionProvider {
    var modelID: String? { nil }
}

protocol CleanupProvider: Sendable {
    var id: ProviderID { get }
    var disclosure: DataDisclosure { get }
    /// The model's identifier for History, read after the call; nil where there is none.
    var modelID: String? { get }
    var shape: CleanupShape { get }
    /// This provider with the longest wait it permits, for a whole meeting's notes.
    func forMeetingNotes() -> CleanupProvider
    /// One request, no retry. Returns the model text unchanged; the caller runs the bound/guard check.
    func cleanup(_ messages: CleanupMessages) async throws -> String
    func validate() async throws
}

extension CleanupProvider {
    var modelID: String? { nil }
    var shape: CleanupShape { CleanupShape() }
    func forMeetingNotes() -> CleanupProvider { self }
}
