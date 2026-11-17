import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Deterministic provider checks: no network, no real keys. Fixtures are copied from the
// official docs or from unauthenticated responses observed on 2026-10-03 (see PROVIDERS.md).

private var failures = 0
private var passes = 0
private func check(_ condition: @autoclosure () -> Bool, _ label: String, file: StaticString = #file, line: UInt = #line) {
    if condition() { passes += 1 } else { failures += 1; print("FAIL [\(line)]: \(label)") }
}
private func expectError(_ expected: ProviderError, _ label: String, line: UInt = #line, _ body: () throws -> Any) {
    do { _ = try body(); failures += 1; print("FAIL [\(line)]: \(label): no error thrown") }
    catch let error as ProviderError { check(error == expected, "\(label): got \(error) expected \(expected)", line: line) }
    catch { failures += 1; print("FAIL [\(line)]: \(label): unexpected error type") }
}
private func json(_ data: Data?) -> [String: Any] { (try? JSONSerialization.jsonObject(with: data ?? Data())) as? [String: Any] ?? [:] }
private func result(_ status: Int, _ body: String, _ headers: [String: String] = [:]) -> HTTPResult {
    HTTPResult(status: status, headers: headers, body: Data(body.utf8))
}

/// Records requests; returns canned results or throws; optionally delays.
final class MockTransport: HTTPTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var _requests: [URLRequest] = []
    let response: Result<HTTPResult, Error>
    let delay: TimeInterval
    init(_ response: Result<HTTPResult, Error>, delay: TimeInterval = 0) { self.response = response; self.delay = delay }
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return _requests }
    private func record(_ request: URLRequest) { lock.lock(); _requests.append(request); lock.unlock() }
    func send(_ request: URLRequest) async throws -> HTTPResult {
        record(request)
        if delay > 0 { try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
        return try response.get()
    }
}

// Fake key shaped like a real one; never valid anywhere.
let fakeSecret = "sk-or-v1-FAKEKEY0123456789abcdefFAKE"
let key = APIKey(fakeSecret)!
let transcriptSecret = "Synthetic private dictation about Project Nightingale"

// Fixtures
let sttSuccess = #"{"text": "Hello, this is a test of speech-to-text transcription.", "usage": {"seconds": 9.2, "total_tokens": 113, "input_tokens": 83, "output_tokens": 30, "cost": 0.000508}}"#
let chatSuccess = #"{"id": "gen-123", "provider": "Google", "model": "google/gemini-3.5-flash-lite", "object": "chat.completion", "created": 1791000000, "choices": [{"logprobs": null, "finish_reason": "stop", "native_finish_reason": "STOP", "index": 0, "message": {"role": "assistant", "content": "Shopping list:\n- apples\n- milk\n- bread", "refusal": null}}], "usage": {"prompt_tokens": 40, "completion_tokens": 12, "total_tokens": 52}}"#
let chatCredits = #"{"error": {"code": 402, "message": "Insufficient credits. Add more using https://openrouter.ai/credits"}}"#
let unauthorizedBody = #"{"error":{"message":"Invalid API key.","code":"UNAUTHORIZED","type":"error"}}"#
let chatRate = #"{"error": {"code": 429, "message": "Rate limit exceeded"}}"#
let chatMidStream = #"{"id": "gen-9", "error": {"code": 502, "message": "Provider returned error"}}"#
let chatUnavailable = #"{"error": {"code": 503, "message": "No endpoints found matching your data policy"}}"#
let chatLength = #"{"choices": [{"finish_reason": "length", "message": {"role": "assistant", "content": "Shopping"}}]}"#

func checkKeychainStore() {
    #if canImport(Security)
    let store = KeychainCredentialStore(service: "Murmur-check-\(UUID().uuidString)")
    defer { try? store.deleteAll() }
    check(!store.exists(.customAPI) && (try? store.read(.customAPI)) == .some(nil), "keychain starts empty")
    check((try? store.save(key, for: .customAPI)) != nil && store.exists(.customAPI), "keychain save, then exists without reading")
    check((try? store.read(.customAPI)) == key, "keychain reads the saved key back")
    let replacement = APIKey("AIzaFAKEreplacement0123456789")!
    check((try? store.save(replacement, for: .customAPI)) != nil && (try? store.read(.customAPI)) == replacement, "keychain save replaces")
    var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: store.service,
                                kSecAttrAccount as String: ProviderID.customAPI.rawValue, kSecReturnAttributes as String: true]
    var item: CFTypeRef?
    let found = SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess
    let attributes = item as? [String: Any] ?? [:]
    check(found && (attributes[kSecAttrSynchronizable as String] as? Bool ?? false) == false, "keychain item never syncs to iCloud")
    query[kSecReturnAttributes as String] = nil
    check((try? store.delete(.customAPI)) != nil && !store.exists(.customAPI) && (try? store.delete(.customAPI)) != nil, "keychain delete, and deleting a missing key succeeds")
    check((try? store.save(key, for: .customCleanup)) != nil && (try? store.deleteAll()) != nil && !store.exists(.customCleanup), "keychain delete-all removes every key")
    #endif
}

@main struct ProviderChecks {
    static func main() async {
        await checkCustomTranscription()
        checkAPIKeyAndCredentials()
        checkMultipart()
        checkParsing()
        checkClassification()
        checkNoSecretsInMessages()
        await checkTransportBehaviour()
        checkSilenceRule()
        checkHostClassification()
        checkLocalProviderRequestAndParsing()
        checkLocalErrorsAndRedaction()
        await checkLocalTransportBehaviour()
        checkKeychainStore()
        print(failures == 0
              ? "Provider checks passed (\(passes) assertions: credentials, multipart/JSON shape, parsing, classification, redaction, disclosure, deadline, cancellation, no retry, silence heuristics, host classification, local server, Gemini 3.5 Transcribe)."
              : "Provider checks FAILED: \(failures) of \(passes + failures) assertions.")
        exit(failures == 0 ? 0 : 1)
    }

    static func checkAPIKeyAndCredentials() {
        check(APIKey("") == nil && APIKey("   \n") == nil, "empty key rejected")
        check(APIKey("ab cd") == nil && APIKey("ab\ncd") == nil, "embedded whitespace rejected")
        check(APIKey("  abc123 \n")?.secret == "abc123", "key trimmed")
        check(APIKey("ключ") == nil, "non-ASCII key rejected")
        check(!"\(key)".contains(fakeSecret) && !String(reflecting: key).contains(fakeSecret), "APIKey description redacted")
        var dumped = ""; dump(key, to: &dumped)
        check(!dumped.contains(fakeSecret), "APIKey dump redacted")
        let provider = try! OpenAICompatibleCleanupProvider(baseURL: "https://api.example.com/v1", model: "m", apiKey: key, provider: .customAPI)
        var providerDump = ""; dump(provider, to: &providerDump)
        check(!providerDump.contains(fakeSecret) && !String(describing: provider).contains(fakeSecret), "provider dump redacted")

        let store = InMemoryCredentialStore()
        check((try? store.read(.customTranscription)) == .some(nil), "missing key reads nil")
        try? store.save(APIKey("first")!, for: .customTranscription)
        try? store.save(APIKey("second")!, for: .customTranscription)
        try? store.save(APIKey("other")!, for: .customCleanup)
        check((try? store.read(.customTranscription))??.secret == "second", "save updates existing key")
        try? store.delete(.customTranscription)
        check((try? store.read(.customTranscription)) == .some(nil), "delete removes key")
        check((try? store.delete(.customTranscription)) != nil, "deleting missing key succeeds")
        try? store.deleteAll()
        check((try? store.read(.customCleanup)) == .some(nil), "deleteAll removes every key")
        check(ProviderID.allCases.map(\.rawValue) == ["openrouter", "cerebras", "customAPI", "customTranscription", "customCleanup", "localServer", "appleIntelligence", "local-model"], "Keychain account names are provider ids")
        check(CredentialError.keychain(status: -25300).description == "Keychain operation failed (status -25300).", "credential error has status only")
    }

    static func checkMultipart() {
        let audioBytes = Data([0x52, 0x49, 0x46, 0x46, 0x00, 0xFF, 0x0D, 0x0A, 0x2D, 0x2D])
        let audio = AudioPayload(data: audioBytes, filename: "clip.wav", mimeType: "audio/wav", language: "en")
        let boundary = "TESTBOUNDARY"
        let fw = OpenAITranscriptionFormat.makeRequest(endpoint: URL(string: "https://stt.example.test/v1/audio/transcriptions")!, apiKey: key, model: "whisper-1", audio: audio, boundary: boundary)
        check(fw.httpMethod == "POST", "transcription POST")
        check(fw.url?.absoluteString == "https://stt.example.test/v1/audio/transcriptions", "transcription endpoint")
        check(fw.value(forHTTPHeaderField: "Authorization") == "Bearer \(fakeSecret)", "transcription bearer auth")
        check(fw.value(forHTTPHeaderField: "Content-Type") == "multipart/form-data; boundary=TESTBOUNDARY", "multipart content type")
        check(!(fw.url?.absoluteString.contains(fakeSecret) ?? true), "key not in URL")
        let body = fw.httpBody ?? Data()
        let parts = splitMultipart(body, boundary: boundary)
        check(parts["model"] == Data("whisper-1".utf8), "model field")
        check(parts["response_format"] == Data("json".utf8), "an undocumented whisper server keeps json")
        func format(_ endpoint: String, _ model: String) -> Data? {
            splitMultipart(OpenAITranscriptionFormat.makeRequest(endpoint: URL(string: endpoint)!, apiKey: key, model: model, audio: audio, boundary: boundary).httpBody ?? Data(), boundary: boundary)["response_format"]
        }
        check(format("https://api.groq.com/openai/v1/audio/transcriptions", "whisper-large-v3-turbo") == Data("verbose_json".utf8), "Groq whisper asks for verbose_json segments")
        check(format("https://api.openai.com/v1/audio/transcriptions", "whisper-1") == Data("verbose_json".utf8), "OpenAI whisper asks for verbose_json segments")
        check(format("https://api.openai.com/v1/audio/transcriptions", "gpt-4o-transcribe") == Data("json".utf8), "gpt-4o-transcribe keeps json (its only format)")
        check(format("https://openrouter.ai/api/v1/audio/transcriptions", "openai/whisper-large-v3-turbo") == Data("json".utf8), "OpenRouter whisper keeps json")
        check(parts["language"] == Data("en".utf8), "language field")
        check(parts["prompt"] == nil, "custom transcription omits free-form dictionary prompts")
        check(parts["file"] == audioBytes, "file bytes intact, including CRLF and dashes")
        check(String(decoding: body, as: UTF8.self).contains("Content-Disposition: form-data; name=\"file\"; filename=\"clip.wav\"\r\nContent-Type: audio/wav\r\n\r\n"), "file part headers")
        check(body.suffix(18) == Data("\r\n--TESTBOUNDARY--\r\n".utf8.suffix(18)), "closing boundary")


        let hostile = AudioPayload(data: audioBytes, filename: "a\"\r\nX-Evil: 1.wav", mimeType: "audio/wav\r\nX: y", language: nil)
        let hostileBody = String(decoding: OpenAITranscriptionFormat.makeRequest(endpoint: URL(string: "https://stt.example.test/v1/audio/transcriptions")!, apiKey: key, model: "whisper-1", audio: hostile, boundary: boundary).httpBody!, as: UTF8.self)
        check(!hostileBody.contains("\r\nX-Evil") && !hostileBody.contains("\r\nX: y"), "multipart header injection stripped")
        check(splitMultipart(Data(hostileBody.utf8), boundary: boundary)["language"] == nil, "nil language omitted")
    }

    static func checkParsing() {
        check((try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, sttSuccess))) == "Hello, this is a test of speech-to-text transcription.", "STT success parse")
        check((try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, #"{"text": ""}"#))) == "", "empty transcript (silence) is valid")
        expectError(.malformedResponse(.customAPI), "STT non-JSON") { try OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, "<html>")) }
        expectError(.authentication(.customAPI), "observed 401") { try OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(401, unauthorizedBody)) }
        expectError(.server(.customAPI, status: 503), "503 no healthy upstream") { try OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(503, "no healthy upstream")) }
        func segmented(_ segments: [(String, Double, Double)]) -> String? {
            let parts = segments.map { #"{"text": "\#($0.0)", "no_speech_prob": \#($0.1), "avg_logprob": \#($0.2), "start": 0, "end": 1, "compression_ratio": 1.2}"# }
            let text = segments.map(\.0).joined()
            return try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, #"{"text": "\#(text)", "segments": [\#(parts.joined(separator: ","))]}"#))
        }
        check(segmented([(" Send the report.", 0.01, -0.2), (" Maybe shower.", 0.9, -1.5)]) == "Send the report.", "trailing no-speech segment dropped")
        check(segmented([(" Send the report.", 0.01, -0.2), (" Thank you.", 0.1, -0.3)]) == " Send the report. Thank you.", "spoken Thank you. kept, text unchanged")
        check(segmented([(" One.", 0.01, -0.2), (" um", 0.9, -1.5), (" Two.", 0.02, -0.2)]) == " One. um Two.", "middle low-confidence segment kept")
        check(segmented([(" One.", 0.01, -0.2), (" um", 0.9, -1.5), (" Two.", 0.02, -0.2), (" Thank you.", 0.95, -1.2)]) == "One. um Two.", "only the trailing run is dropped")
        check(segmented([(" Thank you.", 0.9, -1.5), (" Bye.", 0.8, -1.1)]) == "", "all no-speech becomes empty")
        check(segmented([(" Send it.", 0.01, -0.2), (" Thank you.", 0.9, -0.5)]) == " Send it. Thank you.", "high no_speech alone is not enough")
        check(segmented([(" I'm going to go ahead and get started.", 0, -1.4)]) == "", "a wholly low-confidence transcript is no speech")
        check(segmented([(" Ship it on Monday.", 0, -0.8)]) == " Ship it on Monday.", "a moderately confident transcript is kept")
        check(segmented([(" Ship it on Monday.", 0, -0.3), (" And the docs.", 0, -1.4)]) == " Ship it on Monday. And the docs.", "one low-confidence segment alone is kept")
        // With the sent audio's 100 ms peaks: a trailing segment over quiet audio is dropped, whatever the model's confidence.
        func timed(_ segments: [(String, Double, Double)], _ levels: [Float]) -> String? {
            let parts = segments.map { #"{"text": "\#($0.0)", "no_speech_prob": 0.1, "avg_logprob": -0.3, "start": \#($0.1), "end": \#($0.2)}"# }
            return try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, #"{"text": "\#(segments.map(\.0).joined())", "segments": [\#(parts.joined(separator: ","))]}"#), levels: levels)
        }
        let spokeThenQuiet = [Float](repeating: 0.3, count: 10) + [Float](repeating: 0.002, count: 10)
        check(timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], spokeThenQuiet) == "Send the report.", "confident Thank you. over quiet audio dropped")
        check(timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], [Float](repeating: 0.3, count: 20)) == " Send the report. Thank you.", "Thank you. over speech kept")
        check(timed([(" Send the report.", 0, 1), (" Thank you.", 2.5, 3)], spokeThenQuiet) == "Send the report.", "segment past the end of the audio dropped")
        var clicked = spokeThenQuiet; clicked[15] = 0.6
        check(timed([(" Send the report.", 0, 1), (" Thank you.", 1.2, 1.9)], clicked) == "Send the report.", "a click under the tail is not speech")
        check(timed([(" One.", 0, 0.5), (" Two.", 1.2, 1.4), (" Three.", 0.5, 1)], spokeThenQuiet) == " One. Two. Three.", "a quiet middle segment is kept")
        check(OpenAITranscriptionFormat.loudWindows([0.3, 0.3, 0.002], start: 0, end: 0.25) == 2 && OpenAITranscriptionFormat.loudWindows([0.3], start: 4, end: 5) == 0, "windows under a segment are counted, rounding its end up")
        // The segments come back with their scores and which were dropped; the text is the same as parse's.
        let detailed = try? OpenAITranscriptionFormat.parseDetailed(provider: .customAPI, result: result(200, #"{"text": " Send the report. Thank you.", "segments": [{"text": " Send the report.", "no_speech_prob": 0.01, "avg_logprob": -0.2, "compression_ratio": 1.1, "start": 0, "end": 1}, {"text": " Thank you.", "no_speech_prob": 0.1, "avg_logprob": -0.3, "start": 1.2, "end": 1.9}]}"#), levels: spokeThenQuiet)
        check(detailed?.text == "Send the report." && detailed?.segments?.map { $0.dropped } == [false, true] && detailed?.segments?[0].loudWindows == 10 && detailed?.segments?[1].loudWindows == 0
              && detailed?.segments?[0].avgLogprob == -0.2 && detailed?.segments?[0].compressionRatio == 1.1 && detailed?.segments?[1].compressionRatio == nil, "segment detail keeps scores, loud windows and the dropped flag")
        check((try? OpenAITranscriptionFormat.parseDetailed(provider: .customAPI, result: result(200, sttSuccess)))?.segments == nil, "plain json has no segments")
        let partial = #"{"text": " Send it. Thank you.", "segments": [{"text": " Send it.", "start": 0, "end": 1}, {"text": " Thank you.", "no_speech_prob": 0.9, "avg_logprob": -1.5}]}"#
        check((try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, partial))) == " Send it. Thank you.", "segments without confidence fall back to text")
        check((try? OpenAITranscriptionFormat.parse(provider: .customAPI, result: result(200, #"{"text": " Hi.", "segments": "x"}"#))) == " Hi.", "malformed segments fall back to text")

        check((try? OpenAIChatFormat.parse(provider: .customAPI, result: result(200, chatSuccess))) == "Shopping list:\n- apples\n- milk\n- bread", "chat success parse")
        expectError(.server(.customAPI, status: 502), "error inside HTTP 200") { try OpenAIChatFormat.parse(provider: .customAPI, result: result(200, chatMidStream)) }
        expectError(.malformedResponse(.customAPI), "truncated output") { try OpenAIChatFormat.parse(provider: .customAPI, result: result(200, chatLength)) }
        expectError(.malformedResponse(.customAPI), "no choices") { try OpenAIChatFormat.parse(provider: .customAPI, result: result(200, #"{"choices": []}"#)) }
    }

    static func checkClassification() {
        let now = Date(timeIntervalSince1970: 1_791_000_000)
        func c(_ p: ProviderID, _ status: Int, _ body: String, _ headers: [String: String] = [:]) -> ProviderError {
            ProviderErrorClassifier.classify(provider: p, result: result(status, body, headers), now: now)
        }
        check(c(.customAPI, 403, "{}") == .authentication(.customAPI), "403 → authentication")
        check(c(.customAPI, 400, "{}") == .rejected(.customAPI, status: 400) && c(.customAPI, 404, "{}") == .rejected(.customAPI, status: 404), "400 and 404 → rejected")
        check(c(.customAPI, 504, "") == .timeout(.customAPI), "504 → timeout")
        check(c(.customAPI, 401, unauthorizedBody) == .authentication(.customAPI), "401")
        check(c(.customAPI, 402, chatCredits) == .quota(.customAPI), "402 → quota")
        check(c(.customAPI, 429, chatRate, ["Retry-After": "60"]) == .rateLimited(.customAPI, retryAfter: 60), "Retry-After seconds")
        check(c(.customAPI, 429, chatRate, ["retry-after": "Sat, 03 Oct 2026 04:00:30 GMT"]) == .rateLimited(.customAPI, retryAfter: 30), "Retry-After HTTP-date, case-insensitive header")
        check(c(.customAPI, 429, chatRate) == .rateLimited(.customAPI, retryAfter: nil), "429 without Retry-After")
        check(c(.customAPI, 503, chatUnavailable) == .server(.customAPI, status: 503), "503")
        check(c(.customAPI, 408, "") == .timeout(.customAPI), "408 → timeout")
        check(c(.customAPI, 413, "{}") == .payloadTooLarge(.customAPI, limitBytes: nil), "413 → payloadTooLarge")
        check(c(.customAPI, 520, "") == .server(.customAPI, status: 520), "520 → server")
        check(c(.customAPI, 200, "not json") == .server(.customAPI, status: nil), "unexplained 200 error → server")

        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.notConnectedToInternet)) == .network(.customAPI), "offline → network")
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.cannotFindHost)) == .network(.customAPI), "DNS → network")
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.timedOut)) == .timeout(.customAPI), "URL timeout → timeout")
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.cancelled)) == .cancelled, "URL cancel → cancelled")
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: CancellationError()) == .cancelled, "CancellationError → cancelled")

        check(ProviderError.rateLimited(.customAPI, retryAfter: 3).disposition == .preserveRetryLater, "rate limit keeps session for retry")
        check(ProviderError.network(.customAPI).disposition == .preserveRetryLater && ProviderError.timeout(.customAPI).disposition == .preserveRetryLater, "network/timeout keep session")
        check(ProviderError.server(.customAPI, status: 500).disposition == .preserveRetryLater && ProviderError.malformedResponse(.customAPI).disposition == .preserveRetryLater, "server/malformed keep session")
        check(ProviderError.authentication(.customAPI).disposition == .preserveNeedsChange && ProviderError.quota(.customAPI).disposition == .preserveNeedsChange, "auth/quota keep session, need change")
        check(ProviderError.payloadTooLarge(.customAPI, limitBytes: 1).disposition == .preserveNeedsChange, "payload keeps session, needs change")
        check(ProviderError.cancelled.disposition == .discard, "cancel discards")
        check(ProviderError.rateLimited(.customAPI, retryAfter: 1.2).userMessage.contains("2 seconds"), "retry hint rounds up")
        for value in [Double.infinity, -Double.infinity, Double.nan, Double.greatestFiniteMagnitude, Double(Int.max)] {
            check(!ProviderError.rateLimited(.customAPI, retryAfter: value).userMessage.contains("seconds"), "unsafe retry delay uses generic message")
        }
        for value in ["inf", "nan", "1e100", String(Int.max), "-1"] {
            check(ProviderErrorClassifier.parseRetryAfter(value, now: Date()) == nil, "unsafe Retry-After rejected")
        }
        check(ProviderError.payloadTooLarge(.customAPI, limitBytes: 25_000_000).userMessage.contains("25 MB"), "payload message states limit")
    }

    static func checkNoSecretsInMessages() {
        // Bodies that (maliciously or by echo) contain the key and transcript must not leak into messages.
        let echo = #"{"error": {"code": 400, "message": "bad key \#(fakeSecret) for text \#(transcriptSecret)", "status": "INVALID_ARGUMENT"}}"#
        var errors: [ProviderError] = []
        for provider in ProviderID.allCases {
            for status in [200, 400, 401, 402, 403, 404, 408, 412, 413, 429, 500, 503, 504] {
                errors.append(ProviderErrorClassifier.classify(provider: provider, result: result(status, echo, ["Retry-After": "5"])))
            }
        }
        errors += [.cancelled, .invalidConfiguration(.customAPI), .contentBlocked(.customAPI), .payloadTooLarge(.customAPI, limitBytes: nil)]
        for error in errors {
            let texts = [error.userMessage, error.description, error.localizedDescription, String(reflecting: error)]
            check(!texts.contains { $0.contains(fakeSecret) || $0.contains("FAKEKEY") || $0.contains("Nightingale") }, "no key/transcript in \(error)")
        }
        let parsed: Error
        do { _ = try OpenAIChatFormat.parse(provider: .customAPI, result: result(400, echo)); parsed = ProviderError.cancelled } catch { parsed = error }
        check(!"\(parsed)".contains(fakeSecret) && !"\(parsed)".contains("Nightingale"), "thrown parse error redacted")
    }

    static func checkTransportBehaviour() async {
        func custom() -> OpenAICompatibleCleanupProvider { try! OpenAICompatibleCleanupProvider(baseURL: "https://api.example.com/v1", model: "m", apiKey: key, provider: .customAPI) }

        // Deadline.
        var slow = custom()
        slow.transport = MockTransport(.success(result(200, chatSuccess)), delay: 5)
        slow.timeout = 1
        let started = Date()
        do { _ = try await slow.cleanup(CleanupMessages(system: "s", user: "u")); check(false, "deadline should throw") }
        catch { check(error as? ProviderError == .timeout(.customAPI), "total deadline → timeout, got \(error)") }
        check(Date().timeIntervalSince(started) < 3, "deadline enforced promptly")

        // Cancellation through Task.
        var cancellable = custom()
        cancellable.transport = MockTransport(.success(result(200, chatSuccess)), delay: 5)
        let provider = cancellable
        let task = Task { try await provider.cleanup(CleanupMessages(system: "s", user: "u")) }
        try? await Task.sleep(nanoseconds: 100_000_000)
        task.cancel()
        let cancelStart = Date()
        do { _ = try await task.value; check(false, "cancel should throw") }
        catch { check(error as? ProviderError == .cancelled, "task cancellation → cancelled, got \(error)") }
        check(Date().timeIntervalSince(cancelStart) < 2, "cancellation prompt")

        // Transport errors.
        var offline = custom()
        offline.transport = MockTransport(.failure(URLError(.notConnectedToInternet)))
        do { _ = try await offline.cleanup(CleanupMessages(system: "s", user: "u")); check(false, "offline should throw") }
        catch { check(error as? ProviderError == .network(.customAPI), "offline → network") }
    }

    /// Minimal multipart parser for checks: name → content bytes.
    static func splitMultipart(_ body: Data, boundary: String) -> [String: Data] {
        let delimiter = Data("--\(boundary)".utf8)
        var parts: [String: Data] = [:]
        var ranges: [Range<Data.Index>] = []
        var search = body.startIndex
        while let r = body.range(of: delimiter, in: search..<body.endIndex) { ranges.append(r); search = r.upperBound }
        for (i, r) in ranges.enumerated() where i + 1 < ranges.count {
            var chunk = body.subdata(in: r.upperBound..<ranges[i + 1].lowerBound)
            guard chunk.starts(with: Data("\r\n".utf8)) else { continue }
            chunk = chunk.dropFirst(2)
            guard let headerEnd = chunk.range(of: Data("\r\n\r\n".utf8)) else { continue }
            let header = String(decoding: chunk[chunk.startIndex..<headerEnd.lowerBound], as: UTF8.self)
            var content = Data(chunk[headerEnd.upperBound..<chunk.endIndex])
            if content.suffix(2) == Data("\r\n".utf8) { content.removeLast(2) }
            guard let nameRange = header.range(of: "name=\"") else { continue }
            let rest = header[nameRange.upperBound...]
            guard let end = rest.firstIndex(of: "\"") else { continue }
            parts[String(rest[..<end])] = content
        }
        return parts
    }
}

// MARK: - Local server and host classification checks

/// Returns a canned result per URL path; records requests.
final class RoutingTransport: HTTPTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var _requests: [URLRequest] = []
    let routes: [String: HTTPResult]
    init(_ routes: [String: HTTPResult]) { self.routes = routes }
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return _requests }
    private func record(_ request: URLRequest) { lock.lock(); _requests.append(request); lock.unlock() }
    func send(_ request: URLRequest) async throws -> HTTPResult {
        record(request)
        return routes[request.url?.path ?? ""] ?? HTTPResult(status: 404, body: Data("not found".utf8))
    }
}

let localChatOK = #"{"id":"chatcmpl-1","object":"chat.completion","created":1791000000,"model":"llama3.2:latest","system_fingerprint":"fp_ollama","choices":[{"index":0,"message":{"role":"assistant","content":"Please send the report."},"finish_reason":"stop"}],"usage":{"prompt_tokens":60,"completion_tokens":6,"total_tokens":66}}"#
let localChatLength = #"{"choices":[{"index":0,"message":{"role":"assistant","content":"Please"},"finish_reason":"length"}]}"#
let ollamaModelNotFound = #"{"error":{"message":"model \"llama9\" not found, try pulling it first","type":"api_error","param":null,"code":null}}"#
let ollamaV1Models = #"{"object":"list","data":[{"id":"qwen2.5:7b","object":"model","created":1790000000,"owned_by":"library"},{"id":"llama3.2:latest","object":"model","created":1790000001,"owned_by":"library"}]}"#
let llamaServerModels = #"{"models":[{"name":"gemma-3-4b-it-Q4_K_M.gguf","model":"gemma-3-4b-it-Q4_K_M.gguf","modified_at":"","size":"","digest":"","type":"model","capabilities":["completion"]}],"object":"list","data":[{"id":"gemma-3-4b-it-Q4_K_M.gguf","object":"model","created":1790000000,"owned_by":"llamacpp","meta":{"n_params":3880000000}}]}"#
let ollamaTags = #"{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest","modified_at":"2026-09-01T10:00:00.000000-07:00","size":2019393189,"digest":"a80c4f17acd55265feec403c7aef86be0c25983ab279d83f3bcd3abbcb5b8b72","details":{"parent_model":"","format":"gguf","family":"llama","families":["llama"],"parameter_size":"3.2B","quantization_level":"Q4_K_M"}},{"name":"mistral:7b","model":"mistral:7b","modified_at":"2026-08-01T10:00:00Z","size":4100000000,"digest":"f974a74358d6","details":{"format":"gguf","family":"llama","parameter_size":"7B","quantization_level":"Q4_0"}}]}"#

extension ProviderChecks {
    static func checkSilenceRule() {
        typealias G = AudioFormat
        check(G.shouldSkipForSilence(peakLevel: 0), "digital silence skipped")
        check(G.shouldSkipForSilence(peakLevel: 0.005), "below -40 dBFS skipped")
        check(!G.shouldSkipForSilence(peakLevel: 0.2), "normal speech level sent")
        check(!G.shouldSkipForSilence(peakLevel: 0.02, durationSeconds: 2), "quiet but present speech sent")
        check(G.shouldSkipForSilence(peakLevel: 0.5, durationSeconds: 0.1), "accidental tap skipped")
        check(G.shouldSkipForSilence(peakLevel: .nan) && G.shouldSkipForSilence(peakLevel: .infinity), "non-finite level skipped")
        check(!G.shouldSkipForSilence(peakLevel: 0.05, threshold: 0.04) && G.shouldSkipForSilence(peakLevel: 0.03, threshold: 0.04), "custom threshold")
    }

    static func checkHostClassification() {
        func loc(_ s: String) -> EndpointLocality? { (try? LocalEndpoint.normalize(s)).map(LocalEndpoint.locality(of:)) }
        let onDevice = [
            "http://localhost:11434", "http://LOCALHOST:11434", "HTTP://LocalHost:1234/v1", "http://127.0.0.1:8080",
            "http://127.255.255.254", "http://127.0.0.1", "https://localhost/v1/", "http://[::1]:1234",
            "http://[0:0:0:0:0:0:0:1]:8000", "http://[0000::0001]", "http://[::0001]:11434", "  http://localhost:11434/  ",
        ]
        for s in onDevice { check(loc(s) == .onDevice, "on-device: \(s)") }
        let leaves: [(String, String, Bool)] = [
            ("http://127.0.0.1.evil.com", "127.0.0.1.evil.com", false),
            ("http://localhost.example:11434", "localhost.example", false),
            ("http://LOCALHOST.EXAMPLE", "localhost.example", false),
            ("http://localhost.", "localhost.", false),
            ("http://192.168.1.20:11434", "192.168.1.20", false),
            ("http://10.0.0.5:8000", "10.0.0.5", false),
            ("http://172.16.0.9", "172.16.0.9", false),
            ("http://mac-studio.local:11434", "mac-studio.local", false),
            ("https://gpu.example.com/v1", "gpu.example.com", true),
            ("http://128.0.0.1", "128.0.0.1", false),
            ("http://127.1", "127.1", false),
            ("http://2130706433", "2130706433", false),
            ("http://0177.0.0.1", "0177.0.0.1", false),
            ("http://[::ffff:127.0.0.1]", "::ffff:127.0.0.1", false),
            ("http://[::2]", "::2", false),
            ("http://[fe80::1]:11434", "fe80::1", false),
            ("http://[FD00::1]", "fd00::1", false),
            ("http://localhost-ai", "localhost-ai", false),
        ]
        for (s, host, encrypted) in leaves {
            check(loc(s) == .leavesDevice(host: host, encrypted: encrypted), "leaves device: \(s) → \(String(describing: loc(s)))")
        }
        let rejected = [
            "", "   ", "localhost:11434", "ftp://localhost", "file:///tmp/sock", "javascript:alert(1)", "ws://localhost:8080",
            "http://user:pass@localhost:11434", "http://user@localhost", "http://localhost@evil.com", "http://evil.com\\@localhost",
            "http://0.0.0.0:11434", "http://[::]:8000", "http://[0:0:0:0:0:0:0:0]", "http://localhost:11434/?q=1", "http://localhost#x",
            "http://", "http://loc%61lhost", "http://[::1%25lo0]:1234", "http://exa mple.com", "http://localhost:99999",
            "http://localhost/../x", "http://local_host", "http://[::1::2]", "http://[12345::1]", "http://.localhost", "http://ex\u{0430}mple.com",
        ]
        for s in rejected {
            do { _ = try LocalEndpoint.normalize(s); check(false, "should reject: \(s)") }
            catch { check(error as? ProviderError == .invalidConfiguration(.localServer), "rejected as invalidConfiguration: \(s)") }
        }
        check(LocalEndpoint.isLoopback("[::1]") && LocalEndpoint.isLoopback("::1") && LocalEndpoint.isLoopback("LOCALHOST") && LocalEndpoint.isLoopback("127.8.9.10"), "isLoopback basic forms")
        check(!LocalEndpoint.isLoopback("127.0.0.1.nip.io") && !LocalEndpoint.isLoopback("0.0.0.0") && !LocalEndpoint.isLoopback("::") && !LocalEndpoint.isLoopback("127.0.0.256"), "isLoopback rejects look-alikes")

        func base(_ s: String) -> String? { (try? LocalEndpoint.normalize(s))?.absoluteString }
        check(base("http://localhost:11434/v1/") == "http://localhost:11434", "trailing /v1/ stripped")
        check(base("HTTP://LOCALHOST:11434") == "http://localhost:11434", "scheme and host lowercased")
        check(base("https://gpu.example.com/proxy/v1") == "https://gpu.example.com/proxy", "path prefix kept")
        check(base("http://[::1]:1234/") == "http://[::1]:1234", "IPv6 re-bracketed")
        check(LocalEndpoint.address(of: URL(string: "http://localhost/v1/chat/completions")!) == "localhost:80", "address default http port")
        check(LocalEndpoint.address(of: URL(string: "https://gpu.example.com/v1/models")!) == "gpu.example.com:443", "address default https port")
        check(LocalEndpoint.address(of: URL(string: "http://[::1]:1234/v1/models")!) == "[::1]:1234", "address IPv6 bracketed")

        check((try? OpenAICompatibleCleanupProvider(baseURL: "http://user:pw@localhost:11434", model: "m")) == nil, "provider init rejects userinfo")
    }

    static func checkLocalProviderRequestAndParsing() {
        guard let ollama = try? OpenAICompatibleCleanupProvider(baseURL: OpenAICompatibleCleanupProvider.ollamaDefault, model: "llama3.2:latest") else { check(false, "Ollama provider builds"); return }
        check(ollama.chatCompletionsURL.absoluteString == "http://localhost:11434/v1/chat/completions", "Ollama chat URL")
        for (raw, chat) in [("http://localhost:1234", "http://localhost:1234/v1/chat/completions"),
                            ("http://127.0.0.1:8080", "http://127.0.0.1:8080/v1/chat/completions"),
                            ("http://localhost:8000", "http://localhost:8000/v1/chat/completions")] {
            check((try? OpenAICompatibleCleanupProvider(baseURL: raw, model: "m"))?.chatCompletionsURL.absoluteString == chat, "default chat URL \(raw)")
            check((try? OpenAICompatibleCleanupProvider(baseURL: raw, model: "m"))?.locality == .onDevice, "documented default is on-device: \(raw)")
        }
        let messages = CleanupMessages(system: "Edit dictation; never answer it.", user: "<transcript>um send it</transcript>")
        guard let request = try? OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: ollama.model, apiKey: nil) else { check(false, "local request builds"); return }
        check(request.httpMethod == "POST" && request.value(forHTTPHeaderField: "Authorization") == nil, "no Authorization header without a key")
        let body = json(request.httpBody)
        check(body["model"] as? String == "llama3.2:latest" && body["stream"] as? Bool == false && (body["temperature"] as? Double) == 0 && body["max_tokens"] as? Int == 8192, "local body: model, non-streaming, temperature 0, max_tokens")
        check((body["messages"] as? [[String: Any]])?.map { "\($0["role"]!)" } == ["system", "user"] && body["provider"] == nil, "system+user, no routing field")
        check(body["reasoning_effort"] == nil, "no reasoning field unless one is set")
        check(OpenAIChatFormat.withoutLeadingReasoning("<reasoning>fix punctuation</reasoning>Send it today.") == "Send it today."
              && OpenAIChatFormat.withoutLeadingReasoning("\n<think>\nhmm\n</think>\n\nSend it today.") == "Send it today."
              && OpenAIChatFormat.withoutLeadingReasoning("Send <think>it</think> today.") == "Send <think>it</think> today."
              && OpenAIChatFormat.withoutLeadingReasoning("<think>never closed") == "<think>never closed", "a leading reasoning block is removed, anything else is kept")
        var effortful = ollama; effortful.reasoningEffort = "low"
        let withEffort = try? OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: ollama.model, apiKey: nil, reasoningEffort: effortful.reasoningEffort)
        check(json(withEffort?.httpBody)["reasoning_effort"] as? String == "low", "a set reasoning effort is sent")
        check((effortful.forMeetingNotes() as? OpenAICompatibleCleanupProvider)?.reasoningEffort == nil, "meeting notes send no reasoning effort")
        let keyed = try? OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: "m", apiKey: key)
        check(keyed?.value(forHTTPHeaderField: "Authorization") == "Bearer \(fakeSecret)", "optional key sent as bearer")
        expectError(.invalidConfiguration(.localServer), "empty local model") { try OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: "", apiKey: nil) }
        expectError(.invalidConfiguration(.localServer), "local model with newline") { try OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: "a\nb", apiKey: nil) }
        check((try? OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: ollama.chatCompletionsURL, model: "lmstudio-community/qwen2.5-7b@q4_k_m", apiKey: nil)) != nil, "LM Studio-style model id accepted")


        check((try? OpenAICompatibleCleanupProvider.parse(result(200, localChatOK))) == "Please send the report.", "local chat parse")
        expectError(.malformedResponse(.localServer), "local truncated output") { try OpenAICompatibleCleanupProvider.parse(result(200, localChatLength)) }
        expectError(.malformedResponse(.localServer), "local non-JSON") { try OpenAICompatibleCleanupProvider.parse(result(200, "<html>")) }
        expectError(.rejected(.localServer, status: 404), "Ollama unknown model 404") { try OpenAICompatibleCleanupProvider.parse(result(404, ollamaModelNotFound)) }
        expectError(.authentication(.localServer), "local 401 (key required)") { try OpenAICompatibleCleanupProvider.parse(result(401, #"{"error":{"code":401,"message":"Invalid API Key","type":"authentication_error"}}"#)) }
        expectError(.server(.localServer, status: 503), "llama-server loading 503") { try OpenAICompatibleCleanupProvider.parse(result(503, #"{"error":{"code":503,"message":"Loading model","type":"unavailable_error"}}"#)) }
        expectError(.rejected(.localServer, status: 307), "redirect is not followed and not used") { try OpenAICompatibleCleanupProvider.parse(result(307, "", ["Location": "https://evil.example/v1/chat/completions"])) }

        check((try? OpenAICompatibleCleanupProvider.parseModelList(result(200, ollamaV1Models))) == ["llama3.2:latest", "qwen2.5:7b"], "OpenAI /v1/models parse (sorted)")
        check((try? OpenAICompatibleCleanupProvider.parseModelList(result(200, llamaServerModels))) == ["gemma-3-4b-it-Q4_K_M.gguf"], "llama-server /v1/models parse")
        check((try? OpenAICompatibleCleanupProvider.parseOllamaTags(result(200, ollamaTags))) == ["llama3.2:latest", "mistral:7b"], "Ollama /api/tags parse")
        check((try? OpenAICompatibleCleanupProvider.parseModelList(result(200, #"{"object":"list","data":[]}"#))) == [], "empty model list")
        check((try? OpenAICompatibleCleanupProvider.parseModelList(result(200, #"{"data":[{"id":"a"},{"id":"a"},{"id":"bad\nid"},{"name":"x"}]}"#))) == ["a"], "model list dedups and drops invalid ids")
        expectError(.malformedResponse(.localServer), "model list wrong shape") { try OpenAICompatibleCleanupProvider.parseModelList(result(200, ollamaTags)) }
        expectError(.malformedResponse(.localServer), "tags wrong shape") { try OpenAICompatibleCleanupProvider.parseOllamaTags(result(200, ollamaV1Models)) }

        // Disclosure by host.
        let local = ollama.disclosure
        check(local.staysOnThisMac && local.leavesDevice.isEmpty && local.warning == nil && local.destinationHost == "localhost:11434", "loopback disclosure: nothing leaves")
        check(local.summary.contains("nothing leaves this Mac") && local.staysOnDevice.contains("the transcript text"), "loopback summary")
        let lan = try! OpenAICompatibleCleanupProvider(baseURL: "http://192.168.1.20:11434", model: "m")
        check(!lan.disclosure.staysOnThisMac && lan.disclosure.leavesDevice.joined().contains("transcript") && lan.disclosure.summary.contains("to 192.168.1.20"), "LAN disclosure: transcript leaves to host")
        check(lan.disclosure.warning?.contains("not encrypted") == true && lan.disclosure.summary.contains("not encrypted") && !(lan.disclosure.warning ?? "").contains("API key"), "plain http to LAN warns (no key mentioned without key)")
        var lanKeyed = lan; lanKeyed.apiKey = key
        check(lanKeyed.disclosure.warning?.contains("API key") == true && !lanKeyed.disclosure.summary.contains(fakeSecret), "http with key warns about key, never shows it")
        // The key never crosses the network in cleartext: plain http off this Mac sends no key.
        check(lanKeyed.sendableKey == nil && lanKeyed.makeGetRequest(lanKeyed.modelsURL).value(forHTTPHeaderField: "Authorization") == nil, "plain http to LAN sends no key")
        check(lanKeyed.disclosure.warning?.contains("not sent over plain http") == true, "plain http with key says the key is not sent")
        // The static builders apply the rule themselves, so a caller passing a raw key cannot leak it.
        let plainLAN = URL(string: "http://192.168.1.5:8080/v1/chat/completions")!
        check(((try? OpenAIChatFormat.makeRequest(endpoint: plainLAN, bearer: key, model: "m", messages: CleanupMessages(system: "s", user: "u"), maxTokens: nil))?.value(forHTTPHeaderField: "Authorization")) == nil
              && OpenAITranscriptionFormat.makeRequest(endpoint: plainLAN, apiKey: key, model: "m", audio: AudioPayload(data: Data([1])), boundary: "b").value(forHTTPHeaderField: "Authorization") == nil, "request builders drop a key bound for plain http off this Mac")
        var lanTLS = try! OpenAICompatibleCleanupProvider(baseURL: "https://192.168.1.20:11434", model: "m"); lanTLS.apiKey = key
        check(lanTLS.makeGetRequest(lanTLS.modelsURL).value(forHTTPHeaderField: "Authorization") == "Bearer \(fakeSecret)", "https to LAN sends the key")
        var loopKeyed = ollama; loopKeyed.apiKey = key
        check(loopKeyed.makeGetRequest(loopKeyed.modelsURL).value(forHTTPHeaderField: "Authorization") == "Bearer \(fakeSecret)", "http to loopback sends the key (it never leaves this Mac)")
        let dotLocal = try! OpenAICompatibleCleanupProvider(baseURL: "http://studio.local:1234", model: "m")
        check(dotLocal.disclosure.summary.contains("to studio.local"), ".local disclosed as leaving")
        let remote = try! OpenAICompatibleCleanupProvider(baseURL: "https://gpu.example.com", model: "m")
        check(remote.disclosure.warning == nil && remote.disclosure.summary.contains("gpu.example.com"), "https remote: disclosed, no unencrypted warning")
        let trick = try! OpenAICompatibleCleanupProvider(baseURL: "http://127.0.0.1.evil.com:11434", model: "m")
        check(!trick.disclosure.staysOnThisMac && trick.disclosure.warning != nil, "127.0.0.1.evil.com is not on-device")
    }

    static func checkLocalErrorsAndRedaction() {
        let chat = URL(string: "http://localhost:11434/v1/chat/completions")!
        check(ProviderErrorClassifier.classify(provider: .localServer, transportError: URLError(.cannotConnectToHost), url: chat) == .serverNotRunning(.localServer, address: "localhost:11434"), "connection refused → serverNotRunning")
        check(ProviderErrorClassifier.classify(provider: .localServer, transportError: URLError(.cannotConnectToHost), url: URL(string: "http://[::1]:1234/v1/models")!) == .serverNotRunning(.localServer, address: "[::1]:1234"), "refused IPv6 address")
        check(ProviderErrorClassifier.classify(provider: .localServer, transportError: URLError(.cannotConnectToHost)) == .serverNotRunning(.localServer, address: "the configured address"), "refused without URL")
        let cloud = URL(string: "https://api.example.com/v1/models")!
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.cannotConnectToHost), url: cloud) == .network(.customAPI), "cloud refused stays network")
        check(ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.networkConnectionLost), url: chat) == .network(.localServer)
              && ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.timedOut), url: chat) == .timeout(.localServer), "a custom provider on this Mac gets the local server's wording, not the internet one")
        check(!ProviderError.network(.localServer).userMessage.contains("internet") && ProviderErrorClassifier.classify(provider: .customAPI, transportError: URLError(.timedOut), url: cloud) == .timeout(.customAPI), "a remote custom provider keeps the generic wording")
        for custom in [ProviderID.customAPI, .customCleanup, .customTranscription] {
            check(ProviderErrorClassifier.classify(provider: custom, transportError: URLError(.cannotConnectToHost), url: chat) == .serverNotRunning(custom, address: "localhost:11434"), "\(custom.rawValue) on loopback refused → serverNotRunning")
            check(ProviderErrorClassifier.classify(provider: custom, transportError: URLError(.cannotConnectToHost), url: URL(string: "http://[::1]:1234/v1/models")!) == .serverNotRunning(custom, address: "[::1]:1234"), "\(custom.rawValue) on IPv6 loopback refused")
            check(ProviderErrorClassifier.classify(provider: custom, transportError: URLError(.cannotConnectToHost), url: URL(string: "https://llm.example.com/v1/models")!) == .network(custom), "\(custom.rawValue) remote refused stays network")
            check(ProviderErrorClassifier.classify(provider: custom, transportError: URLError(.cannotConnectToHost), url: URL(string: "http://192.168.1.20:8000/v1/models")!) == .network(custom), "\(custom.rawValue) LAN refused stays network")
        }
        check(ProviderErrorClassifier.classify(provider: .localServer, transportError: URLError(.cannotFindHost), url: chat) == .network(.localServer), "local DNS failure → network")
        check(ProviderErrorClassifier.classify(provider: .localServer, transportError: URLError(.timedOut), url: chat) == .timeout(.localServer), "local timeout")
        let refused = ProviderError.serverNotRunning(.localServer, address: "localhost:11434")
        check(refused.userMessage == "No local model server is running at localhost:11434. Start it, or check the address in Settings.", "refused message names host:port")
        check(refused.disposition == .preserveRetryLater && refused.provider == .localServer, "refused keeps session for retry")
        check(ProviderError.network(.localServer).userMessage.contains("local model server") && !ProviderError.network(.localServer).userMessage.contains("internet"), "local network message is not about the internet")
        check(ProviderError.timeout(.localServer).userMessage.contains("loading"), "local timeout mentions cold load")
        check(ProviderError.invalidConfiguration(.localServer).userMessage.contains("server address"), "local config message")

        // Hostile responses echoing key and transcript must not leak.
        let echo = #"{"error": {"code": 400, "message": "bad key \#(fakeSecret) for text \#(transcriptSecret)"}}"#
        var errors: [ProviderError] = [refused]
        for status in [200, 307, 400, 401, 403, 404, 413, 429, 500, 503] {
            errors.append(ProviderErrorClassifier.classify(provider: .localServer, result: result(status, echo)))
        }
        for error in errors {
            let texts = [error.userMessage, error.description, String(reflecting: error)]
            check(!texts.contains { $0.contains(fakeSecret) || $0.contains("Nightingale") }, "local error redacted: \(error)")
        }
        var keyed = try! OpenAICompatibleCleanupProvider(baseURL: "http://localhost:11434", model: "m"); keyed.apiKey = key
        var dumped = ""; dump(keyed, to: &dumped)
        check(!dumped.contains(fakeSecret) && !String(describing: keyed).contains(fakeSecret), "local provider dump redacted")
    }

    static func checkLocalTransportBehaviour() async {
        let messages = CleanupMessages(system: "s", user: "u")
        var provider = try! OpenAICompatibleCleanupProvider(baseURL: "http://localhost:11434", model: "llama3.2:latest")
        let ok = MockTransport(.success(result(200, localChatOK)))
        provider.transport = ok
        let cleaned = try? await provider.cleanup(messages)
        check(cleaned == "Please send the report.", "local cleanup through transport")
        check(ok.requests.count == 1 && ok.requests.first?.timeoutInterval == 120, "one request, 120 s default timeout")
        check(ok.requests.first?.url?.host == "localhost", "request goes only to the configured host")

        provider.timeout = 10_000
        _ = try? await provider.cleanup(messages)
        check(ok.requests.last?.timeoutInterval == 300, "timeout bounded at 300 s")
        provider.timeout = 0
        _ = try? await provider.cleanup(messages)
        check(ok.requests.last?.timeoutInterval == 1, "timeout floor 1 s")

        let failing = MockTransport(.success(result(503, #"{"error":{"code":503,"message":"Loading model"}}"#)))
        provider.timeout = 120; provider.transport = failing
        do { _ = try await provider.cleanup(messages); check(false, "503 should throw") }
        catch { check(error as? ProviderError == .server(.localServer, status: 503), "local 503 surfaces") }
        check(failing.requests.count == 1, "no retry, no fallback")

        let refused = MockTransport(.failure(URLError(.cannotConnectToHost)))
        provider.transport = refused
        do { _ = try await provider.cleanup(CleanupMessages(system: "s", user: transcriptSecret)); check(false, "refused should throw") }
        catch {
            check(error as? ProviderError == .serverNotRunning(.localServer, address: "localhost:11434"), "refused through provider → serverNotRunning, got \(error)")
            check(!"\(error)".contains("Nightingale"), "refused message has no transcript")
        }
        check(refused.requests.count == 1, "refused: one attempt")

        // Discovery: /v1/models first; Ollama /api/tags only on 404.
        let modern = RoutingTransport(["/v1/models": result(200, ollamaV1Models)])
        provider.transport = modern
        let modernModels = try? await provider.discoverModels()
        check(modernModels == ["llama3.2:latest", "qwen2.5:7b"], "discover via /v1/models")
        check(modern.requests.map { $0.url?.path ?? "" } == ["/v1/models"] && modern.requests.first?.httpMethod == "GET", "no tags call when /v1/models works")
        let legacy = RoutingTransport(["/api/tags": result(200, ollamaTags)])
        provider.transport = legacy
        let legacyModels = try? await provider.discoverModels()
        check(legacyModels == ["llama3.2:latest", "mistral:7b"], "discover falls back to /api/tags on 404")
        check(legacy.requests.map { $0.url?.path ?? "" } == ["/v1/models", "/api/tags"] && legacy.requests.allSatisfy { $0.url?.host == "localhost" && $0.httpBody == nil }, "tags call on same host, no body")
        let unauthorized = RoutingTransport(["/v1/models": result(401, #"{"error":"unauthorized"}"#)])
        provider.transport = unauthorized
        do { _ = try await provider.discoverModels(); check(false, "401 discovery should throw") }
        catch { check(error as? ProviderError == .authentication(.localServer) && unauthorized.requests.count == 1, "401 does not fall back to tags") }
        let keyedRoutes = RoutingTransport(["/v1/models": result(200, ollamaV1Models)])
        provider.transport = keyedRoutes; provider.apiKey = key
        try? await provider.validate()
        check(keyedRoutes.requests.first?.value(forHTTPHeaderField: "Authorization") == "Bearer \(fakeSecret)" && keyedRoutes.requests.first?.url?.path == "/v1/models", "validate GET /v1/models with optional key")
    }
}

func checkCustomTranscription() async {
    let audio = AudioPayload(data: Data([1, 2, 3]))
    for address in ["https://speech.example/tenant/v1", "http://192.168.1.20:8000/v1", "http://localhost:8000/v1"] {
        guard var provider = try? OpenAICompatibleTranscriptionProvider(baseURL: address, model: "custom/whisper:latest", apiKey: key) else { check(false, "custom speech server builds"); continue }
        let request = provider.makeRequest(audio, boundary: "synthetic-boundary")
        let normalized = try! LocalEndpoint.normalize(address)
        check(request.url?.absoluteString == normalized.absoluteString + "/v1/audio/transcriptions", "custom speech request uses the configured host and prefix")
        let body = String(data: request.httpBody!, encoding: .utf8)!
        check(body.contains("custom/whisper:latest") && !body.contains("Murmur") && !body.contains("name=\"prompt\""), "custom speech sends the model without dictionary decoder context")
        check((request.value(forHTTPHeaderField: "Authorization") != nil) == !address.contains("192.168"), "custom speech refuses to send keys over off-device http")
        check(provider.disclosure.staysOnThisMac == address.contains("localhost"), "custom speech locality disclosure")
        check(provider.disclosure.staysOnDevice.contains { $0.contains("dictionary") } && !provider.disclosure.leavesDevice.joined().contains("spelling"), "custom disclosure keeps dictionary local to transcription")
        let transport = MockTransport(.success(result(200, #"{"text":"Synthetic custom provider transcript."}"#)))
        provider.transport = transport
        let text = try? await provider.transcribe(audio)
        check(text == "Synthetic custom provider transcript." && transport.requests.count == 1, "custom speech transcribes with one configured request")
        try? await provider.validate()
        check(transport.requests.last?.url == provider.server.modelsURL, "custom speech validation targets the configured server")
    }
    let refusing = MockTransport(.failure(URLError(.cannotConnectToHost)))
    for (address, expected) in [("http://localhost:8000", "No local model server is running at localhost:8000. Start it, or check the address in Settings."), ("https://speech.example/v1", ProviderError.network(.customAPI).userMessage)] {
        var provider = try! OpenAICompatibleTranscriptionProvider(baseURL: address, model: "whisper-1")
        provider.transport = refusing
        do { _ = try await provider.transcribe(audio); check(false, "refused custom speech should throw") }
        catch { check((error as? ProviderError)?.userMessage == expected, "custom speech refused at \(address) reports the right error") }
    }
    for model in ["whisper-1", "whisper-large-v3", "whisper-large-v3-turbo", "private-model-alias", "gpt-4o-transcribe"] {
        let provider = try! OpenAICompatibleTranscriptionProvider(baseURL: "https://speech.example/v1", model: model, apiKey: key)
        let fields = ProviderChecks.splitMultipart(provider.makeRequest(audio, boundary: "MODEL").httpBody!, boundary: "MODEL")
        check(fields["prompt"] == nil && fields["model"] == Data(model.utf8), "custom model names never select a free-form vocabulary prompt")
    }
    let unkeyed = try! OpenAICompatibleTranscriptionProvider(baseURL: "http://localhost:8000", model: "whisper-1")
    check(unkeyed.makeRequest(audio).value(forHTTPHeaderField: "Authorization") == nil, "unkeyed custom speech sends no bearer")
    check((try? OpenAICompatibleTranscriptionProvider(baseURL: "https://user:password@example.com/v1", model: "m")) == nil, "custom speech rejects embedded credentials")
    check((try? OpenAICompatibleTranscriptionProvider(baseURL: "https://example.com/v1", model: "bad\nmodel")) == nil, "custom speech rejects model control characters")
    var broken = unkeyed
    broken.transport = MockTransport(.success(result(200, #"{"wrong":"response"}"#)))
    do { _ = try await broken.transcribe(audio); check(false, "malformed custom speech refuses") }
    catch { check(error as? ProviderError == .malformedResponse(.customAPI), "custom speech errors retain the custom provider identity") }
    do { _ = try await broken.transcribe(AudioPayload(data: Data(repeating: 0, count: OpenAICompatibleTranscriptionProvider.uploadLimitBytes + 1))); check(false, "oversized custom speech refuses") }
    catch { check(error as? ProviderError == .payloadTooLarge(.customAPI, limitBytes: OpenAICompatibleTranscriptionProvider.uploadLimitBytes), "custom speech upload is bounded") }
}
