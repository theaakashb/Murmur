import Foundation
#if canImport(os)
import os
#endif
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// OpenAI-compatible speech-to-text and chat formats, the custom STT provider, `LocalEndpoint` and the OpenAI-compatible cleanup provider.

enum OpenAITranscriptionFormat {
    static func makeRequest(endpoint: URL, apiKey: APIKey?, model: String, audio: AudioPayload, boundary: String) -> URLRequest {
        // Whisper models return per-segment confidence in verbose_json; gpt-4o-transcribe accepts only json.
        // Only Groq and OpenAI document it; other compatible servers keep json so a rejected format never fails the route.
        let documented = ["api.groq.com", "api.openai.com"].contains(endpoint.host?.lowercased() ?? "")
        let format = documented && model.lowercased().contains("whisper") ? "verbose_json" : "json"
        var fields: [(name: String, value: String)] = [("model", model), ("response_format", format), ("temperature", "0")]
        if let language = audio.language, !language.isEmpty { fields.append(("language", language)) }
        // Whisper treats prompt text as prior transcript context and can echo it. Apply dictionary
        // spellings after transcription; compatible endpoints have no shared structured vocabulary field.
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        if let apiKey { request.setValue("Bearer \(apiKey.secret)", forHTTPHeaderField: "Authorization") }
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = Multipart.body(boundary: boundary, fields: fields, fileField: "file", filename: audio.filename, mimeType: audio.mimeType, fileData: audio.data)
        return request
    }

    // ponytail: Whisper's own defaults (no_speech_threshold, logprob_threshold), not yet calibrated
    // against real dictations; tune once tails from real recordings are measured.
    static let noSpeechThreshold = 0.6
    static let logprobThreshold = -1.0
    // ponytail: a segment with under 300 ms of sound above the silence level is taken as invented;
    // a soft but real "thanks" is louder and longer. Tune from the transcript-tail log lines.
    static let minLoudWindows = 3

    /// 100 ms windows between `start` and `end` seconds whose peak reaches the silence level.
    /// Time past the end of the audio has no sound.
    static func loudWindows(_ levels: [Float], start: Double, end: Double) -> Int {
        let from = max(0, Int(start * 10)), to = min(levels.count, Int((end * 10).rounded(.up)))
        return from < to ? levels[from..<to].filter { $0 >= AudioFormat.silencePeak }.count : 0
    }

    /// `{"text": "..."}`. An empty string (silence) is valid; a missing `text` is not. With verbose_json
    /// `segments`, trailing segments are dropped when the model itself marks them as no speech, or when
    /// `levels` shows the audio under them is quiet (Whisper invents "Thank you." on silent tails); the
    /// words are never inspected. Partial segment data keeps `text`.
    static func logTail(_ line: String) {
        #if canImport(os)
        Logger(subsystem: "cc.clouddev.dictation", category: "timing").notice("transcript-tail \(line, privacy: .public)")
        #endif
    }

    static func parse(provider: ProviderID, result: HTTPResult, levels: [Float]? = nil) throws -> String {
        try parseDetailed(provider: provider, result: result, levels: levels).text
    }

    /// Median and 90th-percentile of the clip's window peaks, for the log and for History.
    static func peakPercentiles(_ levels: [Float]) -> (p50: Float, p90: Float)? {
        guard !levels.isEmpty else { return nil }
        let sorted = levels.sorted()
        return (sorted[sorted.count / 2], sorted[sorted.count * 9 / 10])
    }

    /// `parse` with the segments it saw, so History can keep what the model heard and what was dropped.
    static func parseDetailed(provider: ProviderID, result: HTTPResult, levels: [Float]? = nil) throws -> TranscriptionResult {
        guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: provider, result: result) }
        guard let object = try? JSONSerialization.jsonObject(with: result.body) as? [String: Any] else { throw ProviderError.malformedResponse(provider) }
        if object["error"] != nil { throw ProviderErrorClassifier.classify(provider: provider, result: result) }
        guard let text = object["text"] as? String else { throw ProviderError.malformedResponse(provider) }
        guard let raw = object["segments"] as? [[String: Any]] else { return TranscriptionResult(text: text) }
        var segments: [(text: String, silent: Bool, scores: String, unsure: Bool)] = []
        var details: [TranscriptSegment] = []
        for segment in raw {
            guard let words = segment["text"] as? String, let noSpeech = segment["no_speech_prob"] as? Double,
                  let logprob = segment["avg_logprob"] as? Double else { return TranscriptionResult(text: text) }
            var silent = noSpeech > noSpeechThreshold && logprob < logprobThreshold
            var scores = String(format: "no_speech=%.3f logprob=%.3f", noSpeech, logprob)
            let ratio = segment["compression_ratio"] as? Double
            if let ratio { scores += String(format: " compression=%.2f", ratio) }
            var loudCount: Int?
            if let levels, let start = segment["start"] as? Double, let end = segment["end"] as? Double {
                let loud = loudWindows(levels, start: start, end: end)
                loudCount = loud
                silent = silent || loud < minLoudWindows
                scores += String(format: " start=%.2f end=%.2f clip=%.2f loud_windows=%d", start, end, Double(levels.count) / 10, loud)
            }
            segments.append((words, silent, scores, logprob < logprobThreshold))
            details.append(TranscriptSegment(text: words, start: segment["start"] as? Double, end: segment["end"] as? Double,
                                             avgLogprob: logprob, noSpeechProb: noSpeech, compressionRatio: ratio, loudWindows: loudCount))
        }
        if let levels, let peaks = peakPercentiles(levels) {
            logTail(String(format: "clip peaks p50=%.4f p90=%.4f", peaks.p50, peaks.p90))
        }
        // Groq reports no_speech_prob 0 even for a transcript invented from room noise, but then every
        // segment is low-confidence. Treated as no speech, the recording stays for a retry.
        // ponytail: one invented clip (-1.40) against real speech no worse than -0.37; recalibrate from the log.
        if !segments.isEmpty && segments.allSatisfy(\.unsure) {
            logTail("all \(segments.count) segments below logprob \(logprobThreshold): no speech")
            for index in details.indices { details[index].dropped = true }
            return TranscriptionResult(text: "", segments: details, noSpeechReason: "lowConfidence")
        }
        // Content-free: scores and times of the trailing segments only, never their words.
        var tail = segments.count - 1
        while tail >= 0 {
            logTail("segment=\(tail + 1)/\(segments.count) \(segments[tail].scores) dropped=\(segments[tail].silent)")
            if !segments[tail].silent { break }
            tail -= 1
        }
        guard segments.last?.silent == true else { return TranscriptionResult(text: text, segments: details) }
        for index in (tail + 1)..<details.count { details[index].dropped = true }
        while segments.last?.silent == true { segments.removeLast() }
        // Whisper segment text carries its own leading space.
        return TranscriptionResult(text: segments.map(\.text).joined().trimmingCharacters(in: .whitespacesAndNewlines), segments: details,
                                   noSpeechReason: tail < 0 ? "insufficientLoudness" : nil)
    }
}

/// OpenAI-style `POST …/chat/completions`, shared by the custom cleanup endpoint and local servers.
enum OpenAIChatFormat {
    private struct Message: Encodable { let role: String; let content: String }
    private struct Body: Encodable {
        let model: String
        let messages: [Message]
        let temperature: Double
        let stream: Bool
        let max_tokens: Int?
    }

    /// `bearer` nil sends no Authorization header (local servers without a key).
    static func makeRequest(endpoint: URL, bearer: APIKey?, model: String, messages: CleanupMessages, maxTokens: Int?) throws -> URLRequest {
        let body = Body(model: model,
                        messages: [Message(role: "system", content: messages.system), Message(role: "user", content: messages.user)],
                        temperature: 0, stream: false, max_tokens: maxTokens)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        if let bearer = bearer { request.setValue("Bearer \(bearer.secret)", forHTTPHeaderField: "Authorization") }
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try encoder.encode(body)
        return request
    }

    /// `choices[0].message.content`. Errors can arrive with HTTP 200 (some gateways), so the `error`
    /// object is checked first. A `length` finish (truncated) is an error.
    static func parse(provider: ProviderID, result: HTTPResult) throws -> String {
        guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: provider, result: result) }
        guard let object = try? JSONSerialization.jsonObject(with: result.body) as? [String: Any] else { throw ProviderError.malformedResponse(provider) }
        if object["error"] != nil { throw ProviderErrorClassifier.classify(provider: provider, result: result) }
        guard let choices = object["choices"] as? [[String: Any]], let first = choices.first else { throw ProviderError.malformedResponse(provider) }
        if first["error"] != nil { throw ProviderError.server(provider, status: nil) }
        switch first["finish_reason"] as? String {
        case nil, "stop": break
        case "content_filter": throw ProviderError.contentBlocked(provider)
        default: throw ProviderError.malformedResponse(provider)
        }
        guard let message = first["message"] as? [String: Any], let content = message["content"] as? String else { throw ProviderError.malformedResponse(provider) }
        return content
    }
}

// MARK: - Custom OpenAI-compatible speech to text

/// Uses the same endpoint validation and credential transport policy as custom cleanup.
struct OpenAICompatibleTranscriptionProvider: DetailedTranscriber {
    let server: OpenAICompatibleCleanupProvider
    let id = ProviderID.customAPI
    var transport: HTTPTransport = URLSessionTransport()
    /// Conservative client upload cap; the configured server may impose a smaller one.
    static let uploadLimitBytes = 25_000_000

    init(baseURL: String, model: String, apiKey: APIKey? = nil) throws {
        try OpenAICompatibleCleanupProvider.requireLocalModel(model, provider: .customAPI)
        server = try OpenAICompatibleCleanupProvider(baseURL: baseURL, model: model, apiKey: apiKey, provider: .customAPI)
    }

    var endpoint: URL { URL(string: server.baseURL.absoluteString + "/v1/audio/transcriptions")! }
    var disclosure: DataDisclosure {
        switch server.locality {
        case .onDevice:
            return DataDisclosure(provider: id, stage: .transcription, destinationHost: server.address,
                                  leavesDevice: [], staysOnDevice: ["the recorded audio", "the dictionary", "history"],
                                  retentionNote: "Audio goes only to the server on this Mac; its logging settings apply.")
        case .leavesDevice(let host, let encrypted):
            return DataDisclosure(provider: id, stage: .transcription, destinationHost: host,
                                  leavesDevice: ["audio"],
                                  staysOnDevice: ["dictionary", "history"],
                                  retentionNote: "Server retention applies.",
                                  warning: encrypted ? nil : "Audio is sent over unencrypted http. Your API key is not sent; use https to send it.")
        }
    }

    func makeRequest(_ audio: AudioPayload, boundary: String = Multipart.makeBoundary()) -> URLRequest {
        OpenAITranscriptionFormat.makeRequest(endpoint: endpoint, apiKey: server.sendableKey, model: server.model,
                                             audio: audio, boundary: boundary)
    }
    func transcribe(_ audio: AudioPayload) async throws -> String { try await transcribeDetailed(audio).text }
    func transcribeDetailed(_ audio: AudioPayload) async throws -> TranscriptionResult {
        guard audio.data.count <= Self.uploadLimitBytes else { throw ProviderError.payloadTooLarge(id, limitBytes: Self.uploadLimitBytes) }
        let result = try await ProviderHTTP(provider: id, transport: transport, deadline: 75).execute(makeRequest(audio))
        return try OpenAITranscriptionFormat.parseDetailed(provider: id, result: result, levels: audio.levels)
    }
    func validate() async throws {
        let result = try await ProviderHTTP(provider: id, transport: transport, deadline: 15).execute(server.makeGetRequest(server.modelsURL))
        try validationResult(provider: id, result)
    }
}

// MARK: - Local endpoint classification

/// Where a user-configured server address sends data.
enum EndpointLocality: Equatable, Sendable {
    /// Loopback: the request does not leave this Mac.
    case onDevice
    /// Any other host, including LAN, `.local` and private addresses.
    case leavesDevice(host: String, encrypted: Bool)
}

/// Parses and classifies a user-supplied base URL for an OpenAI-compatible server.
///
/// On-device means exactly: `localhost`, IPv4 127.0.0.0/8 in strict dotted-decimal form, or IPv6
/// `::1` (any spelling, with or without brackets). Everything else is treated as leaving the Mac,
/// including forms that may in fact reach loopback (`127.1`, `2130706433`, `localhost.`,
/// `::ffff:127.0.0.1`): over-disclosure is the safe direction. The unspecified addresses
/// `0.0.0.0` and `::` are rejected because they are listen addresses, not destinations.
enum LocalEndpoint {
    /// Validates and normalizes a base URL. Accepts `http(s)://host[:port]`, an optional path
    /// prefix, and an optional trailing `/v1` (stripped, since endpoints are `{base}/v1/...`).
    /// Rejects other schemes, user info, query, fragment, percent-encoded or unusual hosts, and
    /// unspecified addresses. Throws `invalidConfiguration(.localServer)`.
    static func normalize(_ raw: String) throws -> URL {
        let bad = ProviderError.invalidConfiguration(.localServer)
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 2048,
              trimmed.unicodeScalars.allSatisfy({ $0.isASCII && $0.value > 0x20 && $0.value < 0x7F }),
              !trimmed.contains("@"),
              let components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil,
              let rawHost = components.host, !rawHost.isEmpty, !rawHost.contains("%"),
              !(components.percentEncodedHost ?? "%").contains("%")
        else { throw bad }
        let host = bareHost(rawHost)
        guard !host.isEmpty else { throw bad }
        let isIPv6 = host.contains(":")
        if isIPv6 {
            guard let bytes = ipv6Bytes(host), bytes != [UInt8](repeating: 0, count: 16) else { throw bad }
        } else {
            let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyz0123456789.-")
            guard host.unicodeScalars.allSatisfy({ allowed.contains($0) }), !host.hasPrefix("."), !host.hasPrefix("-"),
                  !host.contains("..") else { throw bad }
            if let octets = ipv4Octets(host), octets == [0, 0, 0, 0] { throw bad }
        }
        if let port = components.port, !(1...65535).contains(port) { throw bad }
        var path = components.percentEncodedPath
        let pathAllowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~/")
        guard path.unicodeScalars.allSatisfy({ pathAllowed.contains($0) }), !path.contains("..") else { throw bad }
        while path.hasSuffix("/") { path.removeLast() }
        if path.lowercased().hasSuffix("/v1") { path.removeLast(3) }
        while path.hasSuffix("/") { path.removeLast() }
        let hostPart = isIPv6 ? "[\(host)]" : host
        let portPart = components.port.map { ":\($0)" } ?? ""
        guard let url = URL(string: "\(scheme)://\(hostPart)\(portPart)\(path)"),
              let check = url.host.map(bareHost), check == host else { throw bad }
        return url
    }

    static func locality(of url: URL) -> EndpointLocality {
        let host = bareHost(url.host ?? "")
        if isLoopback(host) { return .onDevice }
        return .leavesDevice(host: host, encrypted: url.scheme?.lowercased() == "https")
    }

    /// True only for the exact loopback forms listed on the type.
    static func isLoopback(_ rawHost: String) -> Bool {
        let host = bareHost(rawHost)
        if host == "localhost" { return true }
        if let octets = ipv4Octets(host) { return octets[0] == 127 }
        if host.contains(":"), let bytes = ipv6Bytes(host) { return bytes == [UInt8](repeating: 0, count: 15) + [1] }
        return false
    }

    /// "host:port" for messages, with the scheme's default port when none is given.
    static func address(of url: URL) -> String {
        let host = bareHost(url.host ?? "")
        let port = url.port ?? (url.scheme?.lowercased() == "https" ? 443 : 80)
        return "\(host.contains(":") ? "[\(host)]" : host):\(port)"
    }

    /// Lowercased host without IPv6 brackets.
    static func bareHost(_ host: String) -> String {
        var h = host.lowercased()
        if h.hasPrefix("[") && h.hasSuffix("]") { h = String(h.dropFirst().dropLast()) }
        return h
    }

    /// Strict dotted-decimal IPv4: four parts of 1–3 digits, each 0–255. Anything else is nil.
    static func ipv4Octets(_ host: String) -> [Int]? {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var octets: [Int] = []
        for part in parts {
            guard (1...3).contains(part.count), part.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Int(part), value <= 255 else { return nil }
            octets.append(value)
        }
        return octets
    }

    /// RFC 4291 text form (with `::` compression and an optional trailing dotted IPv4), no zone id.
    static func ipv6Bytes(_ host: String) -> [UInt8]? {
        guard !host.isEmpty, host.count <= 45, !host.contains("%") else { return nil }
        var text = host
        var tail: [UInt8] = []
        if let lastColon = text.lastIndex(of: ":"), text[text.index(after: lastColon)...].contains(".") {
            guard let v4 = ipv4Octets(String(text[text.index(after: lastColon)...])) else { return nil }
            tail = v4.map(UInt8.init)
            text = String(text[...lastColon]) + "0:0"   // two placeholder groups, replaced below
        }
        let halves = text.components(separatedBy: "::")
        guard halves.count <= 2 else { return nil }
        func groups(_ s: String) -> [UInt16]? {
            if s.isEmpty { return [] }
            var out: [UInt16] = []
            for g in s.split(separator: ":", omittingEmptySubsequences: false) {
                guard (1...4).contains(g.count), let v = UInt16(g, radix: 16) else { return nil }
                out.append(v)
            }
            return out
        }
        var words: [UInt16]
        if halves.count == 2 {
            guard let head = groups(halves[0]), let rest = groups(halves[1]), head.count + rest.count <= 7 else { return nil }
            words = head + [UInt16](repeating: 0, count: 8 - head.count - rest.count) + rest
        } else {
            guard let all = groups(halves[0]), all.count == 8 else { return nil }
            words = all
        }
        var bytes = words.flatMap { [UInt8($0 >> 8), UInt8($0 & 0xFF)] }
        if !tail.isEmpty { bytes.replaceSubrange(12..<16, with: tail) }
        return bytes
    }
}

// MARK: - Local OpenAI-compatible cleanup

/// Cleanup through a user-run OpenAI-compatible server: Ollama, LM Studio, llama.cpp
/// `llama-server` or vLLM. `POST {base}/v1/chat/completions`, one request, no fallback.
///
/// The disclosure depends on the host: loopback stays on this Mac; anything else (LAN,
/// `.local`, private IPs) is disclosed as leaving it, with an extra warning for plain http.
/// Redirects are not followed, so a server cannot forward the transcript to another host.
struct OpenAICompatibleCleanupProvider: CleanupProvider {
    /// Documented default base URLs (see PROVIDERS.md).
    static let ollamaDefault = "http://localhost:11434"
    /// Local models can take a long time on a cold load; bounded so a hung server still fails.
    static let defaultTimeout: TimeInterval = 120
    static let maximumTimeout: TimeInterval = 300

    let id: ProviderID
    /// Normalized base without a trailing `/v1`.
    let baseURL: URL
    var model: String
    /// Optional; sent as `Authorization: Bearer` only when set.
    var apiKey: APIKey?
    /// Clamped to 1...`maximumTimeout` when used.
    var timeout: TimeInterval = OpenAICompatibleCleanupProvider.defaultTimeout
    var maxTokens: Int? = 8192
    var transport: HTTPTransport = URLSessionTransport()

    /// Throws `invalidConfiguration(.localServer)` for a URL `LocalEndpoint.normalize` rejects.
    init(baseURL: String, model: String, apiKey: APIKey? = nil, provider: ProviderID = .localServer) throws {
        self.baseURL = try LocalEndpoint.normalize(baseURL)
        self.model = model
        self.apiKey = apiKey
        id = provider
    }

    var locality: EndpointLocality { LocalEndpoint.locality(of: baseURL) }
    /// The key is never sent in cleartext: a server off this Mac gets it only over https.
    var sendableKey: APIKey? {
        if case .leavesDevice(_, encrypted: false) = locality { return nil }
        return apiKey
    }
    var address: String { LocalEndpoint.address(of: baseURL) }
    var chatCompletionsURL: URL { URL(string: baseURL.absoluteString + "/v1/chat/completions")! }
    var modelsURL: URL { URL(string: baseURL.absoluteString + "/v1/models")! }
    /// Ollama's native model list, used only when `/v1/models` is missing (404).
    var ollamaTagsURL: URL { URL(string: baseURL.absoluteString + "/api/tags")! }
    var effectiveTimeout: TimeInterval { min(max(timeout, 1), Self.maximumTimeout) }

    var disclosure: DataDisclosure {
        switch locality {
        case .onDevice:
            return DataDisclosure(provider: id, stage: .cleanup, destinationHost: address,
                                  leavesDevice: [],
                                  staysOnDevice: ["audio", "the transcript text", "the dictionary", "history"],
                                  retentionNote: "The transcript and relevant dictionary terms go only to the model server on this Mac; that server's own logging settings apply.")
        case .leavesDevice(let host, let encrypted):
            return DataDisclosure(provider: id, stage: .cleanup, destinationHost: host,
                                  leavesDevice: ["the transcript text", "relevant dictionary terms"],
                                  staysOnDevice: ["audio", "history", "the rest of the dictionary"],
                                  retentionNote: "Retention is up to whoever runs that server.",
                                  warning: encrypted ? nil : "The connection uses plain http and is not encrypted: anyone on the network path can read the transcript.\(apiKey == nil ? "" : " Your API key is not sent over plain http; use https to send it.")")
        }
    }

    static func makeRequest(messages: CleanupMessages, endpoint: URL, model: String, apiKey: APIKey?, maxTokens: Int? = 8192, provider: ProviderID = .localServer) throws -> URLRequest {
        try requireLocalModel(model, provider: provider)
        return try OpenAIChatFormat.makeRequest(endpoint: endpoint, bearer: apiKey, model: model, messages: messages, maxTokens: maxTokens)
    }

    static func parse(_ result: HTTPResult, provider: ProviderID = .localServer) throws -> String { try OpenAIChatFormat.parse(provider: provider, result: result) }

    /// Model ids may contain characters such as `@` or `:` (LM Studio, Ollama tags). They are only
    /// sent inside the JSON body, so any printable ASCII up to 200 characters is accepted.
    static func requireLocalModel(_ model: String, provider: ProviderID = .localServer) throws {
        guard !model.isEmpty, model.count <= 200, model.unicodeScalars.allSatisfy({ $0.value > 0x20 && $0.value < 0x7F })
        else { throw ProviderError.invalidConfiguration(provider) }
    }

    func makeGetRequest(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        if let key = sendableKey { request.setValue("Bearer \(key.secret)", forHTTPHeaderField: "Authorization") }
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    /// OpenAI list shape `{"object":"list","data":[{"id":"..."}]}` (Ollama, LM Studio,
    /// llama-server, vLLM). llama-server may also include a `models` array; `data` is used.
    static func parseModelList(_ result: HTTPResult) throws -> [String] {
        guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: .localServer, result: result) }
        guard let object = try? JSONSerialization.jsonObject(with: result.body) as? [String: Any],
              let data = object["data"] as? [[String: Any]] else { throw ProviderError.malformedResponse(.localServer) }
        return uniqueSorted(data.compactMap { $0["id"] as? String })
    }

    /// Ollama native `{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest",...}]}`.
    static func parseOllamaTags(_ result: HTTPResult) throws -> [String] {
        guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: .localServer, result: result) }
        guard let object = try? JSONSerialization.jsonObject(with: result.body) as? [String: Any],
              let models = object["models"] as? [[String: Any]] else { throw ProviderError.malformedResponse(.localServer) }
        return uniqueSorted(models.compactMap { ($0["model"] as? String) ?? ($0["name"] as? String) })
    }

    private static func uniqueSorted(_ ids: [String]) -> [String] {
        Array(Set(ids.filter { (try? requireLocalModel($0)) != nil })).sorted()
    }

    func cleanup(_ messages: CleanupMessages) async throws -> String {
        let request = try Self.makeRequest(messages: messages, endpoint: chatCompletionsURL, model: model, apiKey: sendableKey, maxTokens: maxTokens, provider: id)
        return try Self.parse(try await ProviderHTTP(provider: id, transport: transport, deadline: effectiveTimeout).execute(request), provider: id)
    }

    /// Lists model ids. Tries `GET {base}/v1/models`; only if that returns 404, tries Ollama's
    /// `GET {base}/api/tags` on the same server. Sends no transcript.
    func discoverModels() async throws -> [String] {
        let http = ProviderHTTP(provider: id, transport: transport, deadline: 15)
        let first = try await http.execute(makeGetRequest(modelsURL))
        if first.status == 404 {
            return try Self.parseOllamaTags(try await http.execute(makeGetRequest(ollamaTagsURL)))
        }
        return try Self.parseModelList(first)
    }

    /// Confirms a server answers `GET /v1/models` (and accepts the key, if one is set).
    func validate() async throws {
        let result = try await ProviderHTTP(provider: id, transport: transport, deadline: 15).execute(makeGetRequest(modelsURL))
        try validationResult(provider: id, result)
    }
}
