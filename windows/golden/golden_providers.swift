// deps: AudioFormat CleanupTypes CleanupText CleanupGuard CleanupPrompt RuleCleaner ProviderTypes ProviderHTTP OpenAICompatible Credentials MurmurCloud
import Foundation
import CryptoKit

// Golden outputs of the provider layer over synthetic data: base-URL validation and locality, request building (method, URL, header
// names and non-secret values, multipart fields with a fixed boundary, JSON bodies), response parsing and error classification of
// synthetic bodies, Retry-After parsing at a fixed time, every error's user message, disclosures, API key validation and helpers.
// The synthetic key appears as "Bearer <key>". One case per line. Run from the repository root.

let secret = "sk-synthetic-FAKEKEY0123456789"
let key = APIKey(secret)!
let now = Date(timeIntervalSince1970: 1_791_000_000)

func line(_ o: Any) -> String {
    String(decoding: try! JSONSerialization.data(withJSONObject: o, options: [.sortedKeys, .withoutEscapingSlashes, .fragmentsAllowed]), as: UTF8.self)
}
func opt(_ v: Any?) -> Any { v ?? NSNull() }
func sha(_ d: Data) -> String { SHA256.hash(data: d).map { String(format: "%02x", $0) }.joined() }
func emit(_ sections: [(String, [String])]) {
    print("{")
    for (n, (name, lines)) in sections.enumerated() {
        print("\"\(name)\": [")
        for (k, l) in lines.enumerated() { print(l + (k + 1 < lines.count ? "," : "")) }
        print("]" + (n + 1 < sections.count ? "," : ""))
    }
    print("}")
}

func errorObject(_ e: ProviderError) -> [String: Any] {
    var o: [String: Any] = ["kind": e.kind, "provider": opt(e.provider?.rawValue), "message": e.userMessage, "disposition": "\(e.disposition)"]
    switch e {
    case .rateLimited(_, let r): o["retryAfter"] = opt(r.map { $0.isFinite ? $0 as Any : "\($0)" })
    case .payloadTooLarge(_, let l): o["limitBytes"] = opt(l)
    case .server(_, let s): o["status"] = opt(s)
    case .rejected(_, let s): o["status"] = s
    case .serverNotRunning(_, let a): o["address"] = a
    case .modelUnavailable(_, let u): o["unavailable"] = u.rawValue
    default: break
    }
    return o
}
func outcome(_ body: () throws -> Any) -> [String: Any] {
    do { return ["value": try body()] } catch let e as ProviderError { return ["error": errorObject(e)] } catch { return ["error": "other"] }
}

/// name → content of a multipart body, in order; non-UTF-8 content as its byte count.
func fields(_ body: Data, _ boundary: String) -> [[String]] {
    // NSString (UTF-16) operations, so "\r\n" is two units, as in the C# split.
    let text = String(decoding: body, as: UTF8.self)
    return text.components(separatedBy: "--\(boundary)").dropFirst().compactMap { part -> [String]? in
        let ns = part as NSString
        let end = ns.range(of: "\r\n\r\n", options: .literal)
        guard ns.hasPrefix("\r\n"), end.location != NSNotFound else { return nil }
        let header = ns.substring(with: NSRange(location: 2, length: end.location - 2))
        var content = ns.substring(from: end.location + 4) as NSString
        if content.hasSuffix("\r\n") { content = content.substring(to: content.length - 2) as NSString }
        return [header, content as String]
    }
}

func request(_ r: URLRequest, boundary: String? = nil) -> [String: Any] {
    let headers = (r.allHTTPHeaderFields ?? [:]).sorted { $0.key.lowercased() < $1.key.lowercased() }
        .map { [$0.key, $0.value == "Bearer \(secret)" ? "Bearer <key>" : $0.value] }
    let body = r.httpBody ?? Data()
    var o: [String: Any] = ["method": r.httpMethod ?? "GET", "url": r.url!.absoluteString, "timeout": r.timeoutInterval, "headers": headers,
                            "bodyLength": body.count, "bodySha256": sha(body)]
    if let boundary { o["fields"] = fields(body, boundary) }
    else if !body.isEmpty { o["json"] = try! JSONSerialization.jsonObject(with: body) }
    return o
}

func locality(_ l: EndpointLocality) -> Any {
    switch l { case .onDevice: return "onDevice"; case .leavesDevice(let host, let encrypted): return ["host": host, "encrypted": encrypted] }
}

func disclosure(_ d: DataDisclosure) -> [String: Any] {
    ["provider": d.provider.rawValue, "stage": d.stage.rawValue, "host": d.destinationHost, "leaves": d.leavesDevice, "stays": d.staysOnDevice,
     "retention": d.retentionNote, "warning": opt(d.warning), "staysOnThisMac": d.staysOnThisMac, "summary": d.summary]
}

func segment(_ s: TranscriptSegment) -> [String: Any] {
    ["text": s.text, "start": opt(s.start), "end": opt(s.end), "avgLogprob": opt(s.avgLogprob), "noSpeechProb": opt(s.noSpeechProb),
     "compressionRatio": opt(s.compressionRatio), "loudWindows": opt(s.loudWindows), "dropped": s.dropped]
}

@main struct Gen {
    static func main() throws {
        // Base URLs: every form in check_providers plus edge forms.
        let urls = [
            "http://localhost:11434", "http://LOCALHOST:11434", "HTTP://LocalHost:1234/v1", "http://127.0.0.1:8080", "http://127.255.255.254", "http://127.0.0.1",
            "https://localhost/v1/", "http://[::1]:1234", "http://[0:0:0:0:0:0:0:1]:8000", "http://[0000::0001]", "http://[::0001]:11434", "  http://localhost:11434/  ",
            "http://127.0.0.1.evil.com", "http://localhost.example:11434", "http://LOCALHOST.EXAMPLE", "http://localhost.", "http://192.168.1.20:11434", "http://10.0.0.5:8000",
            "http://172.16.0.9", "http://mac-studio.local:11434", "https://gpu.example.com/v1", "http://128.0.0.1", "http://127.1", "http://2130706433", "http://0177.0.0.1",
            "http://[::ffff:127.0.0.1]", "http://[::2]", "http://[fe80::1]:11434", "http://[FD00::1]", "http://localhost-ai",
            "", "   ", "localhost:11434", "ftp://localhost", "file:///tmp/sock", "javascript:alert(1)", "ws://localhost:8080", "http://user:pass@localhost:11434",
            "http://user@localhost", "http://localhost@evil.com", "http://evil.com\\@localhost", "http://0.0.0.0:11434", "http://[::]:8000", "http://[0:0:0:0:0:0:0:0]",
            "http://localhost:11434/?q=1", "http://localhost#x", "http://", "http://loc%61lhost", "http://[::1%25lo0]:1234", "http://exa mple.com", "http://localhost:99999",
            "http://localhost/../x", "http://local_host", "http://[::1::2]", "http://[12345::1]", "http://.localhost", "http://ex\u{0430}mple.com",
            "http://localhost:11434/v1/", "https://gpu.example.com/proxy/v1", "http://[::1]:1234/", "http://127.000.0.1", "http://localhost:", "http://localhost:/v1",
            "http://localhost/v1/v1", "http://localhost/V1/", "http://localhost/v1//", "http://localhost//v1", "http://[FE80::AbCd]", "http://[::ffff:1.2.3.4]", "http://[1::2::3]",
            "http://localhost:0", "http://localhost:065535", "http://localhost:65535", "http://localhost:1", "https://example.com:443/v1", "http://example.com:80",
            "http://localhost?", "http://localhost#", "http://[::1", "http://[::1]x", "http://localhost:12:34", "http:/localhost", "http:///x", "https:example.com",
            "http://a.b-c.d/e_f~g.h", "http://a/b%20c", "http://00.0.0.0", "http://0.0.0.1", "http://[::127.0.0.1]", "http://[::0.0.0.1]", "http://[1:2:3:4:5:6:7:8]",
            "http://[1:2:3:4:5:6:7:8:9]", "http://[1:2:3:4:5:6:7::]", "http://[::1:2:3:4:5:6:7]", "http://-a.com", "http://a..b", "http://example.com.", "http://LOCALHOST/Path/V1",
            "http://[0:0:0:0:0:ffff:7f00:1]", "http://localhost:11434/api", "http://x/" + String(repeating: "a", count: 2100),
        ]
        var endpoints: [String] = []
        for raw in urls {
            do {
                let url = try LocalEndpoint.normalize(raw)
                endpoints.append(line(["input": raw, "normalized": url.absoluteString, "locality": locality(LocalEndpoint.locality(of: url)),
                                       "address": LocalEndpoint.address(of: url), "sendsKey": LocalEndpoint.sendable(key, to: url) != nil]))
            } catch { endpoints.append(line(["input": raw, "error": (error as? ProviderError).map { errorObject($0) } ?? "other"])) }
        }
        var loopback: [String] = []
        for host in ["[::1]", "::1", "LOCALHOST", "localhost", "127.8.9.10", "127.0.0.1.nip.io", "0.0.0.0", "::", "127.0.0.256", "[0:0:0:0:0:0:0:1]", "::ffff:127.0.0.1",
                     "localhost.", "127.1", "", "[]", "[localhost]", "0000:0000:0000:0000:0000:0000:0000:0001", "::0:1"] {
            loopback.append(line(["host": host, "loopback": LocalEndpoint.isLoopback(host)]))
        }
        // Addresses of request URLs as written (not normalized).
        var addresses: [String] = []
        for raw in ["http://localhost/v1/chat/completions", "https://gpu.example.com/v1/models", "http://[::1]:1234/v1/models", "https://LOCALHOST:8443", "http://127.0.0.1:80/x"] {
            let url = URL(string: raw)!
            addresses.append(line(["url": raw, "address": LocalEndpoint.address(of: url), "locality": locality(LocalEndpoint.locality(of: url))]))
        }

        // Transcription requests with a fixed boundary.
        let boundary = "TESTBOUNDARY"
        let bytes = Data([0x52, 0x49, 0x46, 0x46, 0x00, 0xFF, 0x0D, 0x0A, 0x2D, 0x2D])
        var transcriptions: [String] = []
        func transcription(_ endpoint: String, _ model: String, _ apiKey: APIKey?, _ audio: AudioPayload) {
            let r = OpenAITranscriptionFormat.makeRequest(endpoint: URL(string: endpoint)!, apiKey: apiKey, model: model, audio: audio, boundary: boundary)
            transcriptions.append(line(["endpoint": endpoint, "model": model, "key": apiKey != nil, "filename": audio.filename, "mimeType": audio.mimeType,
                                        "language": opt(audio.language), "data": [UInt8](audio.data).map(Int.init), "request": request(r, boundary: boundary)]))
        }
        let plain = AudioPayload(data: bytes, filename: "clip.wav", mimeType: "audio/wav", language: "en")
        for endpoint in ["https://stt.example.test/v1/audio/transcriptions", "https://api.groq.com/openai/v1/audio/transcriptions", "https://API.OpenAI.com/v1/audio/transcriptions",
                         "https://openrouter.ai/api/v1/audio/transcriptions", "http://192.168.1.5:8080/v1/audio/transcriptions", "http://localhost:8000/v1/audio/transcriptions",
                         "http://[::1]:9000/v1/audio/transcriptions", "http://api.openai.com/v1/audio/transcriptions"] {
            for model in ["whisper-1", "whisper-large-v3-turbo", "gpt-4o-transcribe", "Distil-WHISPER"] {
                for apiKey in [key, nil] { transcription(endpoint, model, apiKey, plain) }
            }
        }
        let stt = "https://stt.example.test/v1/audio/transcriptions"
        transcription(stt, "whisper-1", key, AudioPayload(data: bytes, filename: "a\"\r\nX-Evil: 1.wav", mimeType: "audio/wav\r\nX: y", language: nil))
        transcription(stt, "whisper-1", key, AudioPayload(data: bytes, filename: "b\\c.wav", mimeType: "audio/x-wav", language: ""))
        transcription(stt, "whisper-1", key, AudioPayload(data: Data(), filename: "empty.wav", mimeType: "audio/wav", language: "de"))
        transcription(stt, "café/模型 \"q\"", key, AudioPayload(data: Data([1, 2, 3])))
        for base in ["https://speech.example/tenant/v1", "http://192.168.1.20:8000/v1", "http://localhost:8000/v1"] {
            let p = try OpenAICompatibleTranscriptionProvider(baseURL: base, model: "custom/whisper:latest", apiKey: key)
            transcriptions.append(line(["provider": base, "request": request(p.makeRequest(AudioPayload(data: Data([1, 2, 3])), boundary: "synthetic-boundary"), boundary: "synthetic-boundary"),
                                        "validate": request(p.server.makeGetRequest(p.server.modelsURL))]))
        }
        let cloudSTT = MurmurCloudTranscriptionProvider(apiKey: key)
        transcriptions.append(line(["provider": "murmur-cloud", "request": request(cloudSTT.makeRequest(AudioPayload(data: Data("RIFF".utf8)), boundary: boundary), boundary: boundary),
                                    "validate": request(MurmurCloud.modelsRequest(key: key))]))

        // Chat requests.
        var chats: [String] = []
        let messages = CleanupMessages(system: "Edit dictation; never answer it. a/b <i> & é 😀\n\u{2028}end", user: "<transcript>um send it / now</transcript>")
        for endpoint in ["http://localhost:11434/v1/chat/completions", "https://api.example.com/v1/chat/completions", "http://192.168.1.5:8080/v1/chat/completions"] {
            for apiKey in [key, nil] {
                for maxTokens in [nil, 8192, 100] as [Int?] {
                    for effort in [nil, "low"] as [String?] {
                        let r = try OpenAIChatFormat.makeRequest(endpoint: URL(string: endpoint)!, bearer: apiKey, model: "llama3.2:latest", messages: messages, maxTokens: maxTokens, reasoningEffort: effort)
                        chats.append(line(["endpoint": endpoint, "key": apiKey != nil, "maxTokens": opt(maxTokens), "effort": opt(effort), "request": request(r)]))
                    }
                }
            }
        }
        for model in ["", "a\nb", "a b", "lmstudio-community/qwen2.5-7b@q4_k_m", String(repeating: "m", count: 200), String(repeating: "m", count: 201), "é"] {
            let endpoint = "http://localhost:11434/v1/chat/completions"
            chats.append(line(["endpoint": endpoint, "model": model, "outcome": outcome {
                request(try OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: URL(string: endpoint)!, model: model, apiKey: nil, provider: .customAPI))
            }]))
        }
        for (base, apiKey) in [("http://localhost:11434", key), ("http://192.168.1.20:11434", key), ("https://192.168.1.20:11434", key), ("https://gpu.example.com/proxy/v1", nil)] as [(String, APIKey?)] {
            let p = try OpenAICompatibleCleanupProvider(baseURL: base, model: "m", apiKey: apiKey, provider: .customAPI)
            chats.append(line(["provider": base, "key": apiKey != nil, "models": request(p.makeGetRequest(p.modelsURL)), "tags": request(p.makeGetRequest(p.ollamaTagsURL)),
                               "chat": request(try OpenAICompatibleCleanupProvider.makeRequest(messages: messages, endpoint: p.chatCompletionsURL, model: p.model, apiKey: p.sendableKey,
                                                                                               maxTokens: p.maxTokens, reasoningEffort: "low", provider: p.id))]))
        }

        // Murmur Cloud cleanup bodies from built prompts (fixed nonce).
        var cloud: [String] = []
        let context = ScreenContext(selected: "Selected / <b>", clipboard: "", window: "Window — Zoë")
        let optionSets: [(String, CleanupOptions)] = [
            ("professional", CleanupOptions(fillers: true, corrections: true, lists: false, punctuation: true, professional: true, spoken: true)),
            ("polish", CleanupOptions(fillers: true, corrections: true, lists: true, punctuation: true, polish: true, spoken: true)),
            ("polishNoSpoken", CleanupOptions(fillers: false, corrections: false, lists: true, punctuation: false, polish: true)),
            ("professionalNoCorrections", CleanupOptions(corrections: false, professional: true)),
            ("strict", CleanupOptions(punctuation: true)),
        ]
        for (name, options) in optionSets {
            for (terms, ctx, notes) in [([], nil, ""), (["clouddev", " clouddev ", "Ana\nMaria"], context, "Use British spelling."), (["Akme"], nil, "")] as [([String], ScreenContext?, String)] {
                for transcript in ["revenue is up ten percent new line send it to clouddev", "um send it", ""] {
                    let built = CleanupPrompt.build(transcript: transcript, options: options, dictionaryTerms: terms, nonce: "N1", context: ctx, instructions: notes)
                    guard let input = built.input else { continue }
                    cloud.append(line(["options": name, "transcript": transcript, "terms": terms, "context": ctx != nil, "notes": notes,
                                       "outcome": outcome { request(try MurmurCloudCleanupFormat.makeRequest(input, key: key)) }]))
                }
            }
        }

        // Response parsing.
        let sttSuccess = #"{"text": "Hello, this is a test of speech-to-text transcription.", "usage": {"seconds": 9.2}}"#
        let chatSuccess = #"{"id": "gen-123", "object": "chat.completion", "choices": [{"finish_reason": "stop", "index": 0, "message": {"role": "assistant", "content": "Shopping list:\n- apples\n- milk"}}]}"#
        func segs(_ s: [(String, Double, Double, Double?, Double?)], ratio: Bool = true) -> String {
            let parts = s.map { #"{"text": "\#($0.0)", "no_speech_prob": \#($0.1), "avg_logprob": \#($0.2), "start": \#($0.3.map { "\($0)" } ?? "null"), "end": \#($0.4.map { "\($0)" } ?? "null")\#(ratio ? #", "compression_ratio": 1.2"# : "")}"# }
            return #"{"text": "\#(s.map(\.0).joined())", "segments": [\#(parts.joined(separator: ","))]}"#
        }
        let spokeThenQuiet = [Float](repeating: 0.3, count: 10) + [Float](repeating: 0.002, count: 10)
        var clicked = spokeThenQuiet; clicked[15] = 0.6
        let transcriptionBodies: [(String, Int, String, [Float]?)] = [
            ("success", 200, sttSuccess, nil), ("empty", 200, #"{"text": ""}"#, nil), ("html", 200, "<html>", nil), ("401", 401, #"{"error":{"message":"Invalid API key.","code":"UNAUTHORIZED"}}"#, nil),
            ("503", 503, "no healthy upstream", nil), ("errorNull", 200, #"{"error": null, "text": "x"}"#, nil), ("errorWith200", 200, #"{"error": {"code": 429}, "text": "x"}"#, nil),
            ("noText", 200, #"{"wrong":"response"}"#, nil), ("textNumber", 200, #"{"text": 5}"#, nil), ("array", 200, #"[{"text":"x"}]"#, nil), ("string", 200, #""text""#, nil),
            ("trailingNoSpeech", 200, segs([(" Send the report.", 0.01, -0.2, 0, 1), (" Maybe shower.", 0.9, -1.5, 0, 1)]), nil),
            ("spokenThanks", 200, segs([(" Send the report.", 0.01, -0.2, 0, 1), (" Thank you.", 0.1, -0.3, 0, 1)]), nil),
            ("middleKept", 200, segs([(" One.", 0.01, -0.2, 0, 1), (" um", 0.9, -1.5, 0, 1), (" Two.", 0.02, -0.2, 0, 1)]), nil),
            ("trailingRun", 200, segs([(" One.", 0.01, -0.2, 0, 1), (" um", 0.9, -1.5, 0, 1), (" Two.", 0.02, -0.2, 0, 1), (" Thank you.", 0.95, -1.2, 0, 1)]), nil),
            ("allNoSpeech", 200, segs([(" Thank you.", 0.9, -1.5, 0, 1), (" Bye.", 0.8, -1.1, 0, 1)]), nil),
            ("highNoSpeechAlone", 200, segs([(" Send it.", 0.01, -0.2, 0, 1), (" Thank you.", 0.9, -0.5, 0, 1)]), nil),
            ("wholeLowConfidence", 200, segs([(" I'm going to go ahead and get started.", 0, -1.4, 0, 1)]), nil),
            ("moderate", 200, segs([(" Ship it on Monday.", 0, -0.8, 0, 1)]), nil),
            ("oneLow", 200, segs([(" Ship it on Monday.", 0, -0.3, 0, 1), (" And the docs.", 0, -1.4, 0, 1)]), nil),
            ("quietTail", 200, segs([(" Send the report.", 0.1, -0.3, 0, 1), (" Thank you.", 0.1, -0.3, 1.2, 1.9)], ratio: false), spokeThenQuiet),
            ("loudTail", 200, segs([(" Send the report.", 0.1, -0.3, 0, 1), (" Thank you.", 0.1, -0.3, 1.2, 1.9)]), [Float](repeating: 0.3, count: 20)),
            ("pastEnd", 200, segs([(" Send the report.", 0.1, -0.3, 0, 1), (" Thank you.", 0.1, -0.3, 2.5, 3)]), spokeThenQuiet),
            ("clickTail", 200, segs([(" Send the report.", 0.1, -0.3, 0, 1), (" Thank you.", 0.1, -0.3, 1.2, 1.9)]), clicked),
            ("quietMiddle", 200, segs([(" One.", 0.1, -0.3, 0, 0.5), (" Two.", 0.1, -0.3, 1.2, 1.4), (" Three.", 0.1, -0.3, 0.5, 1)]), spokeThenQuiet),
            ("allQuiet", 200, segs([(" Hello.", 0.1, -0.3, 1.0, 2.0)]), spokeThenQuiet),
            ("noTimes", 200, segs([(" Hi.", 0.1, -0.3, nil, nil)]), spokeThenQuiet),
            ("partial", 200, #"{"text": " Send it. Thank you.", "segments": [{"text": " Send it.", "start": 0, "end": 1}, {"text": " Thank you.", "no_speech_prob": 0.9, "avg_logprob": -1.5}]}"#, nil),
            ("segmentsString", 200, #"{"text": " Hi.", "segments": "x"}"#, nil),
            ("segmentsMixed", 200, #"{"text": " Hi.", "segments": [{"text": " Hi.", "no_speech_prob": 0, "avg_logprob": -0.1}, 3]}"#, nil),
            ("segmentsEmpty", 200, #"{"text": " Hi.", "segments": []}"#, nil),
            ("boolScores", 200, #"{"text": " Hi.", "segments": [{"text": " Hi.", "no_speech_prob": true, "avg_logprob": false, "start": 0, "end": 1}]}"#, spokeThenQuiet),
            ("stringScore", 200, #"{"text": " Hi.", "segments": [{"text": " Hi.", "no_speech_prob": "0.1", "avg_logprob": -0.1}]}"#, nil),
            ("intScores", 200, #"{"text": " Hi.", "segments": [{"text": " Hi.", "no_speech_prob": 1, "avg_logprob": -2, "start": 0, "end": 1}]}"#, nil),
            ("whitespaceJoin", 200, segs([("\n Send.\u{00A0}", 0.01, -0.2, 0, 1), (" Bye.", 0.9, -1.5, 0, 1)]), nil),
        ]
        var parses: [String] = []
        for (name, status, body, levels) in transcriptionBodies {
            for provider in [ProviderID.customAPI, .murmurCloud] {
                parses.append(line(["fn": "transcription", "name": name, "provider": provider.rawValue, "status": status, "body": body, "levels": opt(levels?.map { Double($0) }),
                                    "outcome": outcome { () -> Any in
                                        let r = try OpenAITranscriptionFormat.parseDetailed(provider: provider, result: HTTPResult(status: status, body: Data(body.utf8)), levels: levels)
                                        return ["text": r.text, "segments": opt(r.segments?.map(segment)), "reason": opt(r.noSpeechReason?.rawValue)]
                                    }]))
            }
        }
        let chatBodies: [(String, Int, String)] = [
            ("success", 200, chatSuccess), ("credits", 402, #"{"error": {"code": 402, "message": "Insufficient credits"}}"#),
            ("midStream", 200, #"{"id": "gen-9", "error": {"code": 502, "message": "Provider returned error"}}"#), ("errorNull", 200, #"{"error": null, "choices": []}"#),
            ("length", 200, #"{"choices": [{"finish_reason": "length", "message": {"role": "assistant", "content": "Shopping"}}]}"#), ("noChoices", 200, #"{"choices": []}"#),
            ("choicesNotObjects", 200, #"{"choices": [1]}"#), ("filter", 200, #"{"choices": [{"finish_reason": "content_filter", "message": {"content": "x"}}]}"#),
            ("finishNull", 200, #"{"choices": [{"finish_reason": null, "message": {"content": "Hi."}}]}"#), ("finishNumber", 200, #"{"choices": [{"finish_reason": 3, "message": {"content": "Hi."}}]}"#),
            ("finishMissing", 200, #"{"choices": [{"message": {"content": "Hi."}}]}"#), ("choiceError", 200, #"{"choices": [{"error": {"code": 500}, "message": {"content": "Hi."}}]}"#),
            ("choiceErrorNull", 200, #"{"choices": [{"error": null, "message": {"content": "Hi."}}]}"#), ("contentNull", 200, #"{"choices": [{"message": {"content": null}}]}"#),
            ("messageString", 200, #"{"choices": [{"message": "Hi."}]}"#), ("reasoning", 200, #"{"choices": [{"message": {"content": "<reasoning>fix</reasoning>Send it today."}}]}"#),
            ("think", 200, #"{"choices": [{"message": {"content": "\n<think>\nhmm\n</think>\n\nSend it today."}}]}"#), ("html", 200, "<html>"),
            ("ollama404", 404, #"{"error":{"message":"model \"llama9\" not found","type":"api_error","param":null,"code":null}}"#),
            ("local401", 401, #"{"error":{"code":401,"message":"Invalid API Key"}}"#), ("loading503", 503, #"{"error":{"code":503,"message":"Loading model"}}"#),
            ("redirect307", 307, ""), ("duplicate", 200, #"{"choices": [{"message": {"content": "first"}}], "choices": [{"message": {"content": "second"}}]}"#),
        ]
        for (name, status, body) in chatBodies {
            for provider in [ProviderID.customAPI, .localServer] {
                parses.append(line(["fn": "chat", "name": name, "provider": provider.rawValue, "status": status, "body": body,
                                    "outcome": outcome { try OpenAIChatFormat.parse(provider: provider, result: HTTPResult(status: status, body: Data(body.utf8))) }]))
            }
        }
        for (name, status, body) in [("text", 200, #"{"text":"Send it."}"#), ("answer", 200, #"{"answer":"x"}"#), ("error", 200, #"{"error":{"code":"c"},"text":"x"}"#),
                                     ("401", 401, #"{"error":{"message":"fixed"}}"#), ("textNull", 200, #"{"text":null}"#), ("html", 200, "<html>")] {
            parses.append(line(["fn": "murmurCloud", "name": name, "status": status, "body": body,
                                "outcome": outcome { try MurmurCloudCleanupFormat.parse(HTTPResult(status: status, body: Data(body.utf8))) }]))
        }
        let ollamaV1 = #"{"object":"list","data":[{"id":"qwen2.5:7b"},{"id":"llama3.2:latest"}]}"#
        let llamaServer = #"{"models":[{"name":"gemma.gguf","model":"gemma.gguf"}],"object":"list","data":[{"id":"gemma.gguf"}]}"#
        let tags = #"{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest"},{"name":"mistral:7b"},{"model":"b:1","name":"a:1"},{"name":5}]}"#
        for (name, status, body) in [("v1", 200, ollamaV1), ("llamaServer", 200, llamaServer), ("tags", 200, tags), ("empty", 200, #"{"object":"list","data":[]}"#),
                                     ("dedup", 200, #"{"data":[{"id":"a"},{"id":"a"},{"id":"bad\nid"},{"name":"x"},{"id":"B"},{"id":"a b"}]}"#),
                                     ("401", 401, #"{"error":"unauthorized"}"#), ("404", 404, "not found"), ("dataNotObjects", 200, #"{"data":["a"]}"#)] {
            parses.append(line(["fn": "modelList", "name": name, "status": status, "body": body,
                                "outcome": outcome { try OpenAICompatibleCleanupProvider.parseModelList(HTTPResult(status: status, body: Data(body.utf8))) }]))
            parses.append(line(["fn": "ollamaTags", "name": name, "status": status, "body": body,
                                "outcome": outcome { try OpenAICompatibleCleanupProvider.parseOllamaTags(HTTPResult(status: status, body: Data(body.utf8))) }]))
        }

        // Classification of HTTP results at a fixed time.
        var classify: [String] = []
        for provider in [ProviderID.customAPI, .localServer, .murmurCloud, .customTranscription] {
            for status in [100, 200, 204, 299, 300, 301, 304, 307, 308, 399, 400, 401, 402, 403, 404, 408, 409, 412, 413, 415, 422, 429, 451, 499, 500, 502, 503, 504, 520, 599, 600] {
                classify.append(line(["provider": provider.rawValue, "status": status, "body": "", "headers": [:] as [String: String],
                                      "error": errorObject(ProviderErrorClassifier.classify(provider: provider, result: HTTPResult(status: status), now: now))]))
            }
        }
        for body in ["", "not json", "{}", #"{"error": {"code": 402}}"#, #"{"error": {"code": 429}}"#, #"{"error": {"code": 399}}"#, #"{"error": {"code": 400}}"#,
                     #"{"error": {"code": 402.0}}"#, #"{"error": {"code": 402.5}}"#, #"{"error": {"code": 4.02e2}}"#, #"{"error": {"code": true}}"#, #"{"error": {"code": "402"}}"#,
                     #"{"error": {"code": "+402"}}"#, #"{"error": {"code": " 402"}}"#, #"{"error": {"code": "402 "}}"#, #"{"error": {"code": "UNAUTHORIZED"}}"#,
                     #"{"error": {"code": null}}"#, #"{"error": null}"#, #"{"error": "x"}"#, #"{"error": {"code": 502}, "error": {"code": 401}}"#, #"[{"error": {"code": 401}}]"#,
                     #"{"error": {"code": 99999999999}}"#, #"{"error": {"code": -1}}"#, #"{"error": {"code": 503}}"#, #"{"error": {"code": 600}}"#] {
            for status in [200, 201] {
                classify.append(line(["provider": "customAPI", "status": status, "body": body, "headers": ["Retry-After": "7"],
                                      "error": errorObject(ProviderErrorClassifier.classify(provider: .customAPI, result: HTTPResult(status: status, headers: ["Retry-After": "7"], body: Data(body.utf8)), now: now))]))
            }
        }
        for headers in [["Retry-After": "60"], ["retry-after": "Sat, 03 Oct 2026 04:00:30 GMT"], ["RETRY-AFTER": "1.5"], [:]] as [[String: String]] {
            classify.append(line(["provider": "customAPI", "status": 429, "body": "", "headers": headers,
                                  "error": errorObject(ProviderErrorClassifier.classify(provider: .customAPI, result: HTTPResult(status: 429, headers: headers), now: now))]))
        }
        var retry: [String] = []
        for value in ["60", "0", "1.2", "+5", "-1", ".5", "5.", "1e3", "1E3", "0x10", "0x1p3", "inf", "nan", "infinity", "1e100", String(Int.max), "9223372036854775807.0",
                      " 5 ", "\t5", "5\n", "", "   ", "abc", "1,5", "1_000", "١٢", "Sat, 03 Oct 2026 04:00:30 GMT", "Sat, 3 Oct 2026 04:00:30 GMT", "sat, 03 oct 2026 04:00:30 gmt",
                      "Mon, 03 Oct 2026 04:00:30 GMT", "Sat, 03 Oct 2026 04:00:30 UTC", "Sat, 03 Oct 2026 04:00:30 PST", "Sat, 03 Oct 2026 04:00:30 +0000", "Sat, 03 Oct 2026 4:00:30 GMT",
                      "Sat, 03 Oct 2026 04:00:30", "Sat 03 Oct 2026 04:00:30 GMT", "Saturday, 03 Oct 2026 04:00:30 GMT", "Sat, 03 October 2026 04:00:30 GMT",
                      "Fri, 02 Oct 2026 04:00:30 GMT", "Sat, 31 Feb 2026 04:00:30 GMT", "Sat, 03 Oct 26 04:00:30 GMT", "Sat, 03 Oct 2026 24:00:30 GMT", "Sat, 03 Oct 2026 04:00:30 GMT+1"] {
            retry.append(line(["value": value, "seconds": opt(ProviderErrorClassifier.parseRetryAfter(value, now: now))]))
        }

        // Transport failures.
        var transport: [String] = []
        let codes: [(String, URLError.Code)] = [("cancelled", .cancelled), ("timedOut", .timedOut), ("dataLengthExceedsMaximum", .dataLengthExceedsMaximum),
            ("cannotConnectToHost", .cannotConnectToHost), ("notConnectedToInternet", .notConnectedToInternet), ("cannotFindHost", .cannotFindHost),
            ("networkConnectionLost", .networkConnectionLost), ("secureConnectionFailed", .secureConnectionFailed), ("badServerResponse", .badServerResponse), ("unknown", .unknown)]
        for (name, code) in codes {
            for provider in [ProviderID.localServer, .customAPI, .customCleanup, .customTranscription, .murmurCloud] {
                for url in [nil, "http://localhost:11434/v1/chat/completions", "http://[::1]:1234/v1/models", "https://api.example.com/v1/models", "http://192.168.1.20:8000/v1/models", "http://127.0.0.1:8080"] as [String?] {
                    transport.append(line(["code": name, "provider": provider.rawValue, "url": opt(url),
                                           "error": errorObject(ProviderErrorClassifier.classify(provider: provider, transportError: URLError(code), url: url.flatMap(URL.init(string:))))]))
                }
            }
        }
        transport.append(line(["code": "cancellationError", "provider": "customAPI", "url": NSNull(), "error": errorObject(ProviderErrorClassifier.classify(provider: .customAPI, transportError: CancellationError()))]))

        // Every error's message, kind and disposition.
        var messagesOut: [String] = []
        for p in ProviderID.allCases {
            var all: [ProviderError] = [.authentication(p), .quota(p), .rateLimited(p, retryAfter: nil), .rateLimited(p, retryAfter: 1.2), .rateLimited(p, retryAfter: 60),
                .rateLimited(p, retryAfter: 0), .rateLimited(p, retryAfter: -3), .rateLimited(p, retryAfter: .infinity), .rateLimited(p, retryAfter: .nan),
                .rateLimited(p, retryAfter: Double(Int.max)), .rateLimited(p, retryAfter: 9.2e18), .network(p), .timeout(p), .payloadTooLarge(p, limitBytes: nil),
                .payloadTooLarge(p, limitBytes: 25_000_000), .payloadTooLarge(p, limitBytes: 999_999), .server(p, status: nil), .server(p, status: 503), .malformedResponse(p),
                .contentBlocked(p), .rejected(p, status: 404), .invalidConfiguration(p), .serverNotRunning(p, address: "localhost:11434")]
            all += [OnDeviceModelUnavailable.deviceNotEligible, .appleIntelligenceOff, .modelNotReady, .modelNotDownloaded, .modelDamaged].map { .modelUnavailable(p, $0) }
            messagesOut += all.map { line(errorObject($0)) }
        }
        messagesOut.append(line(errorObject(.cancelled)))
        var names: [String] = []
        for p in ProviderID.allCases { names.append(line(["raw": p.rawValue, "displayName": p.displayName, "runsProfessional": p.runsProfessional])) }

        // Disclosures.
        var disclosures: [String] = []
        for base in ["http://localhost:11434", "http://[::1]:1234/v1", "http://192.168.1.20:11434", "https://gpu.example.com/v1", "http://studio.local:1234", "http://127.0.0.1.evil.com:11434", "https://localhost"] {
            for apiKey in [key, nil] {
                for provider in [ProviderID.localServer, .customAPI] {
                    let p = try OpenAICompatibleCleanupProvider(baseURL: base, model: "m", apiKey: apiKey, provider: provider)
                    disclosures.append(line(["kind": "cleanup", "base": base, "key": apiKey != nil, "provider": provider.rawValue, "disclosure": disclosure(p.disclosure)]))
                }
                let t = try OpenAICompatibleTranscriptionProvider(baseURL: base, model: "whisper-1", apiKey: apiKey)
                disclosures.append(line(["kind": "transcription", "base": base, "key": apiKey != nil, "disclosure": disclosure(t.disclosure)]))
            }
        }
        disclosures.append(line(["kind": "murmurCloudCleanup", "disclosure": disclosure(MurmurCloudCleanupProvider(apiKey: key).disclosure)]))
        disclosures.append(line(["kind": "murmurCloudTranscription", "disclosure": disclosure(MurmurCloudTranscriptionProvider(apiKey: key).disclosure)]))
        for items in [[], ["audio"], ["a", "b"], ["a", "b", "c"]] {
            disclosures.append(line(["kind": "list", "items": items, "summary": DataDisclosure(provider: .customAPI, stage: .cleanup, destinationHost: "h.example", leavesDevice: items,
                                                                                         staysOnDevice: [], retentionNote: "Note.", warning: items.count == 2 ? "Careful." : nil).summary]))
        }

        // API keys, reasoning blocks, loud windows, percentiles, sanitizing, silence rule.
        var keys: [String] = []
        for raw in ["", "   \n", "ab cd", "ab\ncd", "  abc123 \n", "ключ", "a\u{7F}b", "a\u{0}b", "\u{00A0}abc\u{00A0}", "\u{2028}abc", "tab\tkey", "~!@#$%^&*()_+{}|:<>?",
                    String(repeating: "k", count: 4096), String(repeating: "k", count: 4097), "sk-or-v1-FAKE", "é"] {
            keys.append(line(["raw": raw, "valid": APIKey(raw) != nil, "secret": opt(APIKey(raw)?.secret)]))
        }
        var reasoning: [String] = []
        for content in ["<reasoning>fix punctuation</reasoning>Send it today.", "\n<think>\nhmm\n</think>\n\nSend it today.", "Send <think>it</think> today.", "<think>never closed",
                        "<think></think>", "  <reasoning>a</reasoning>  b  ", "<THINK>x</THINK>y", "<think>a</reasoning>b", "<reasoning>a</think>b", "", "   ", "\u{00A0}<think>x</think>\u{2003}y",
                        "<think>x</think>\r\ny", "<reasoning><think>x</think></reasoning>z", "<think>x</think><think>y</think>z"] {
            reasoning.append(line(["content": content, "result": OpenAIChatFormat.withoutLeadingReasoning(content)]))
        }
        var helpers: [String] = []
        for (levels, start, end) in [([Float(0.3), 0.3, 0.002], 0.0, 0.25), ([0.3], 4, 5), ([0.3, 0.3, 0.3], -1, 0.05), ([0.01, 0.00999, 0.5], 0, 0.3), ([0.3, 0.3], 0.19, 0.11),
                                     ([Float](repeating: 0.5, count: 30), 0.95, 2.01), ([], 0, 1)] as [([Float], Double, Double)] {
            helpers.append(line(["fn": "loudWindows", "levels": levels.map { Double($0) }, "start": start, "end": end, "result": OpenAITranscriptionFormat.loudWindows(levels, start: start, end: end)]))
        }
        for levels in [[], [0.5], [0.1, 0.9], [0.3, 0.1, 0.2, 0.5, 0.4], (0..<23).map { Float(Double($0) / 23) }] as [[Float]] {
            let p = OpenAITranscriptionFormat.peakPercentiles(levels)
            helpers.append(line(["fn": "peakPercentiles", "levels": levels.map { Double($0) }, "result": opt(p.map { [Double($0.p50), Double($0.p90)] })]))
        }
        for value in ["plain", "a\"b", "c\r\nd", "e\\f", "g\u{2028}h", "😀\"", "\"\r\n\\"] {
            helpers.append(line(["fn": "sanitize", "value": value, "result": Multipart.sanitize(value)]))
        }
        for (peak, duration, threshold) in [(0, nil, nil), (0.005, nil, nil), (0.2, nil, nil), (0.02, 2, nil), (0.5, 0.1, nil), (0.5, 0.3, nil), (0.5, 0.29999, nil),
                                            (Float.nan, nil, nil), (Float.infinity, nil, nil), (-0.5, nil, nil), (0.05, nil, 0.04), (0.03, nil, 0.04), (0.01, nil, nil), (0.00999, nil, nil)] as [(Float, Double?, Float?)] {
            let skip = threshold.map { AudioFormat.shouldSkipForSilence(peakLevel: peak, durationSeconds: duration, threshold: $0) } ?? AudioFormat.shouldSkipForSilence(peakLevel: peak, durationSeconds: duration)
            helpers.append(line(["fn": "silence", "peak": peak.isFinite ? Double(peak) : "\(peak)", "duration": opt(duration), "threshold": opt(threshold.map { Double($0) }), "result": skip]))
        }

        emit([("endpoints", endpoints), ("loopback", loopback), ("addresses", addresses), ("transcriptionRequests", transcriptions), ("chatRequests", chats),
              ("murmurCloudRequests", cloud), ("parse", parses), ("classify", classify), ("retryAfter", retry), ("transportErrors", transport),
              ("errors", messagesOut), ("providerIDs", names), ("disclosures", disclosures), ("apiKeys", keys), ("reasoning", reasoning), ("helpers", helpers)])
    }
}
