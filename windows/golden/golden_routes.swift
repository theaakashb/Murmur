// deps: AudioFormat CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention Cleanup Settings CleanupPrompt ProviderTypes ProviderHTTP OpenAICompatible Credentials VoiceCues Processing WritingModes MurmurCloud Routes CloudAudio KeyCheck AppleCleanup LocalCleanup AIEngine DictationLanguages
import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Golden outputs of Routes.swift and KeyCheck.swift over synthetic settings, keys and canned responses: route choices, route plans
// (providers, refusals, disclosures, which keys were asked for), settings load and save without legacy keys, key-to-endpoint binding,
// key checks against mock transports, and cloud transcription outcomes and History detail over synthetic WAVs. The destination-settings
// list a backup restore keeps is exported for the C# key names. One case per line. Run from the repository root.

let secret = "sk-synthetic-FAKEKEY0123456789"
let key = APIKey(secret)!

func line(_ o: Any) -> String {
    String(decoding: try! JSONSerialization.data(withJSONObject: o, options: [.sortedKeys, .withoutEscapingSlashes, .fragmentsAllowed]), as: UTF8.self)
}
func opt(_ v: Any?) -> Any { v ?? NSNull() }
func bits(_ f: Float) -> Int { Int(f.bitPattern) }

func errorObject(_ e: ProviderError) -> [String: Any] { ["kind": e.kind, "provider": opt(e.provider?.rawValue), "message": e.userMessage] }

func disclosure(_ d: DataDisclosure) -> [String: Any] {
    ["provider": d.provider.rawValue, "stage": d.stage.rawValue, "host": d.destinationHost, "leaves": d.leavesDevice, "stays": d.staysOnDevice,
     "retention": d.retentionNote, "warning": opt(d.warning), "staysOnThisMac": d.staysOnThisMac, "summary": d.summary]
}

func transcriber(_ t: TranscriptionProvider?) -> Any {
    switch t {
    case let p as OpenAICompatibleTranscriptionProvider:
        return ["type": "custom", "id": p.id.rawValue, "model": opt(p.modelID), "endpoint": p.endpoint.absoluteString, "key": opt(p.server.apiKey?.secret == secret ? "key" : p.server.apiKey?.secret),
                "precheck": (p as Any) is SilencePrecheckedTranscriber]
    case let p as MurmurCloudTranscriptionProvider:
        return ["type": "murmurCloud", "id": p.id.rawValue, "model": opt(p.modelID), "endpoint": p.endpoint.absoluteString, "key": p.apiKey.secret == secret ? "key" : p.apiKey.secret,
                "precheck": (p as Any) is SilencePrecheckedTranscriber]
    case nil: return NSNull()
    default: return "other"
    }
}

func cleaner(_ c: CleanupProvider?) -> Any {
    switch c {
    case let p as OpenAICompatibleCleanupProvider:
        return ["type": "custom", "id": p.id.rawValue, "model": opt(p.modelID), "base": p.baseURL.absoluteString, "key": opt(p.apiKey.map { $0.secret == secret ? "key" : $0.secret }),
                "effort": opt(p.reasoningEffort), "timeout": p.timeout, "notesTimeout": (p.forMeetingNotes() as! OpenAICompatibleCleanupProvider).timeout]
    case let p as MurmurCloudCleanupProvider:
        return ["type": "murmurCloud", "id": p.id.rawValue, "model": opt(p.modelID), "key": p.apiKey.secret == secret ? "key" : p.apiKey.secret]
    case let p as LocalModelCleanupProvider:
        return ["type": "localModel", "id": p.id.rawValue, "model": opt(p.modelID), "disclosure": disclosure(p.disclosure),
                "shape": [p.shape.takesContext, p.shape.keepsCorrectionsAttached, p.shape.takesPresets], "sectionTarget": opt(p.shape.sectionTarget)]
    case nil: return NSNull()
    default: return "other"
    }
}

func settingsObject(_ s: RouteSettings) -> [String: Any] {
    ["transcription": s.transcription.rawValue, "cleanup": s.cleanup.rawValue, "customAPIBaseURL": s.customAPIBaseURL, "customTranscriptionAPIBaseURL": s.customTranscriptionAPIBaseURL,
     "customAPIModel": s.customAPIModel, "customTranscriptionModel": s.customTranscriptionModel, "customAPIReasoningEffort": s.customAPIReasoningEffort]
}

final class Mock: HTTPTransport, @unchecked Sendable {
    let status: Int, body: String, fail: URLError.Code?
    private let lock = NSLock(); private var _paths: [String] = []
    init(_ status: Int, _ body: String = "", fail: URLError.Code? = nil) { self.status = status; self.body = body; self.fail = fail }
    var paths: [String] { lock.withLock { _paths } }
    func send(_ request: URLRequest) async throws -> HTTPResult {
        lock.withLock { _paths.append("\(request.httpMethod ?? "GET") \(request.url!.absoluteString) auth=\(request.value(forHTTPHeaderField: "Authorization") == nil ? "no" : "yes")") }
        if let fail { throw URLError(fail) }
        return HTTPResult(status: status, body: Data(body.utf8))
    }
}

/// A transcriber stub without a pre-check or segment detail.
struct Spy: TranscriptionProvider {
    let id = ProviderID.customAPI
    let response: Result<String, ProviderError>
    var disclosure: DataDisclosure { DataDisclosure(provider: id, stage: .transcription, destinationHost: "example.invalid", leavesDevice: ["the recorded audio"], staysOnDevice: [], retentionNote: "test") }
    func transcribe(_ audio: AudioPayload) async throws -> String { try response.get() }
    func validate() async throws {}
}

/// The real verbose_json parser on the route's audio levels, no pre-check.
struct Verbose: DetailedTranscriber {
    let id = ProviderID.customAPI
    let json: String
    var disclosure: DataDisclosure { DataDisclosure(provider: id, stage: .transcription, destinationHost: "example.invalid", leavesDevice: ["the recorded audio"], staysOnDevice: [], retentionNote: "test") }
    func transcribe(_ audio: AudioPayload) async throws -> String { try await transcribeDetailed(audio).text }
    func transcribeDetailed(_ audio: AudioPayload) async throws -> TranscriptionResult {
        try OpenAITranscriptionFormat.parseDetailed(provider: .customAPI, result: HTTPResult(status: 200, body: Data(json.utf8)), levels: audio.levels)
    }
    func validate() async throws {}
}

/// Same recipes as golden_cloud_audio.swift.
func samples(_ recipe: [[Any]]) -> [Float] {
    var out: [Float] = []
    var x: UInt32 = 12345
    for seg in recipe {
        let kind = seg[0] as! String, count = seg[1] as! Int, amp = Float(seg[2] as! Double)
        for i in 0..<count {
            switch kind {
            case "lcg": x = x &* 1_103_515_245 &+ 12345; out.append(Float(Int((x >> 8) & 0xFFFF) - 32768) / 32768 * amp)
            case "square": out.append((i / 20) % 2 == 0 ? amp : -amp)
            case "ramp": out.append(Float(i % 200 - 100) / 100 * amp)
            case "spike": out.append(i == count / 2 ? amp : 0)
            default: out.append(amp)
            }
        }
    }
    return out
}

func verbose(_ segments: [(String, Double, Double, Double)]) -> String {
    let items: [[String: Any]] = segments.map { ["text": $0.0, "start": $0.1, "end": $0.2, "avg_logprob": $0.3, "no_speech_prob": 0] }
    return String(decoding: try! JSONSerialization.data(withJSONObject: ["text": segments.map(\.0).joined(), "segments": items], options: [.sortedKeys]), as: UTF8.self)
}

@main struct Gen {
    static func main() async throws {
        var choices: [String] = []
        for c in CleanupChoice.allCases {
            choices.append(line(["kind": "cleanup", "raw": c.rawValue, "id": c.id, "displayName": c.displayName, "runsModelLevels": c.runsModelLevels, "processingRoute": c.processingRoute.rawValue,
                                 "providerID": opt(c.providerID?.rawValue), "credentialID": opt(c.credentialID?.rawValue)]))
        }
        for t in TranscriptionRoute.allCases { choices.append(line(["kind": "transcription", "raw": t.rawValue])) }
        var bound = RouteSettings(); bound.customAPIBaseURL = "c"; bound.customTranscriptionAPIBaseURL = "t"
        for p in ProviderID.allCases {
            let result: Any = opt(bound.apiBaseURL(for: p))
            choices.append(line(["kind": "apiBaseURL", "provider": p.rawValue, "customAPIBaseURL": "c", "customTranscriptionAPIBaseURL": "t", "result": result]))
        }

        // Route plans.
        let keySets: [(String, Set<ProviderID>)] = [("none", []), ("all", Set(ProviderID.allCases)), ("murmurCloud", [.murmurCloud]), ("custom", [.customTranscription, .customCleanup])]
        var plans: [String] = []
        func plan(_ s: RouteSettings, sends: Bool, keys: (String, Set<ProviderID>)) {
            var asked: [ProviderID] = []
            let p = RoutePlan.make(s, cleanupSendsText: sends) { asked.append($0); return keys.1.contains($0) ? key : nil }
            plans.append(line(["settings": settingsObject(s), "sends": sends, "keys": keys.0, "asked": asked.map(\.rawValue), "transcriber": transcriber(p.transcriber), "cleaner": cleaner(p.cleaner),
                               "refusal": opt(p.refusal), "usesLocalModel": p.usesLocalModel, "stages": p.stages.map(disclosure),
                               "transcriptionSummary": opt(DisclosureText.summary(p, stage: .transcription)), "cleanupSummary": opt(DisclosureText.summary(p, stage: .cleanup))]))
        }
        for route in TranscriptionRoute.allCases {
            for (url, model) in [("", ""), ("https://speech.example/v1", "speech-model"), ("https://speech.example/v1", " "), ("http://10.0.0.5:8080", "whisper-1"),
                                 ("http://localhost:8000", " whisper-1 "), ("ftp://x", "m")] {
                for keys in keySets {
                    var s = RouteSettings(); s.transcription = route; s.customTranscriptionAPIBaseURL = url; s.customTranscriptionModel = model
                    plan(s, sends: true, keys: keys)
                }
            }
        }
        for choice in CleanupChoice.allCases {
            for (url, model) in [("", ""), ("https://cleanup.example/v1", "m"), ("http://localhost:11434", "llama3.2"), ("http://192.168.1.20:11434", "m"), ("http://192.168.1.20:11434", ""), ("ftp://x", "m")] {
                for effort in ["low", " high ", ""] {
                    for keys in keySets {
                        for sends in [true, false] {
                            if !sends && (effort != "low" || keys.0 != "all") { continue }
                            var s = RouteSettings(); s.cleanup = choice; s.customAPIBaseURL = url; s.customAPIModel = model; s.customAPIReasoningEffort = effort
                            plan(s, sends: sends, keys: keys)
                        }
                    }
                }
            }
        }
        for keys in keySets {
            var s = RouteSettings(); s.transcription = .cloud; s.customTranscriptionAPIBaseURL = "https://speech.example/v1"; s.customTranscriptionModel = "speech-model"
            s.cleanup = .customAPI; s.customAPIBaseURL = "https://cleanup.example/v1"; s.customAPIModel = "cleanup-model"
            plan(s, sends: true, keys: keys)
            s.transcription = .murmurCloud; s.cleanup = .murmurCloud
            plan(s, sends: true, keys: keys)
            s.transcription = .cloud; s.customTranscriptionAPIBaseURL = "ftp://x"; s.cleanup = .customAPI; s.customAPIModel = ""
            plan(s, sends: true, keys: keys)
        }

        // Settings load and save (no legacy keys).
        var settings: [String] = []
        typealias K = RouteSettings.Key
        let stored: [[String: String]] = [
            [:], [K.transcription: "cloud", K.cleanup: "custom-api", K.customAPIBaseURL: "https://api.example.com/v1", K.customAPIModel: "qwen",
                  K.customTranscriptionAPIBaseURL: "https://speech.example/v1", K.customTranscriptionModel: "whisper-1", K.customAPIReasoningEffort: "medium"],
            [K.transcription: "murmur-cloud", K.cleanup: "murmur-cloud"], [K.transcription: "LOCAL", K.cleanup: "Local-Rules"], [K.transcription: "", K.cleanup: ""],
            [K.cleanup: "local-model"], [K.customAPIBaseURL: "https://only-cleanup.example/v1"], [K.customAPIBaseURL: "https://a.example", K.customTranscriptionAPIBaseURL: ""],
            [K.customAPIReasoningEffort: ""], [K.cleanup: "custom-api", K.customAPIModel: " spaced "],
        ]
        for values in stored {
            let suite = "murmur-golden-routes-\(UUID().uuidString)"
            let defaults = UserDefaults(suiteName: suite)!
            for (k, v) in values { defaults.set(v, forKey: k) }
            let loaded = RouteSettings.load(from: defaults)
            let saved = UserDefaults(suiteName: suite + "-saved")!
            loaded.save(to: saved)
            var savedValues: [String: String] = [:]
            for k in [K.transcription, K.cleanup, K.customAPIBaseURL, K.customTranscriptionAPIBaseURL, K.customAPIModel, K.customTranscriptionModel, K.customAPIReasoningEffort] { savedValues[k] = saved.string(forKey: k) }
            settings.append(line(["stored": values, "loaded": settingsObject(loaded), "saved": savedValues]))
            defaults.removePersistentDomain(forName: suite); saved.removePersistentDomain(forName: suite + "-saved")
        }
        settings.append(line(["defaults": settingsObject(RouteSettings()), "placeholderAPIURL": RouteSettings.placeholderAPIURL, "placeholderModel": RouteSettings.placeholderModel]))

        // Key binding.
        var bindings: [String] = []
        for raw in ["https://FIRST.example:443/v1/", "https://first.example/v1", "http://first.example/v1", "http://first.example:80", "https://first.example:8443/v1",
                    "https://first.example/tenant/v1", "http://localhost:11434", "http://[::1]:80/v1", "https://[::1]:443", "ftp://x", "", "http://user@x"] {
            bindings.append(line(["endpoint": raw, "result": opt(try? CustomAPIKeyBinding.endpoint(raw))]))
        }
        for p in ProviderID.allCases { bindings.append(line(["preference": p.rawValue, "result": CustomAPIKeyBinding.preference(p)])) }
        for (savedAt, readAt) in [("https://FIRST.example:443/v1/", "https://first.example/v1"), ("https://first.example/v1", "https://second.example/v1"),
                                  ("https://first.example/v1", "http://first.example/v1"), ("https://first.example/v1", "https://first.example:8443/v1"),
                                  ("https://first.example/v1", "https://first.example/tenant/v1"), ("http://localhost:11434/v1", "http://LOCALHOST:11434"), (nil, "https://first.example/v1"),
                                  ("https://first.example/v1", "ftp://x")] as [(String?, String)] {
            for provider in [ProviderID.customAPI, .customCleanup, .customTranscription] {
                let suite = "murmur-golden-binding-\(UUID().uuidString)"
                let defaults = UserDefaults(suiteName: suite)!
                let store = InMemoryCredentialStore()
                var result: String
                if let savedAt { try? CustomAPIKeyBinding.save(key, at: savedAt, defaults: defaults, credentials: store, provider: provider) } else { try store.save(key, for: provider) }
                do { result = try CustomAPIKeyBinding.read(at: readAt, defaults: defaults, credentials: store, provider: provider) == key ? "key" : "nil" }
                catch let e as CredentialError { result = "\(e)" } catch { result = "invalid: " + ((error as? ProviderError)?.kind ?? "other") }
                let needs = CustomAPIKeyBinding.needsConfirmation(at: readAt, defaults: defaults, provider: provider)
                var r = RouteSettings(); r.customAPIBaseURL = readAt; r.customTranscriptionAPIBaseURL = readAt
                let viaRoutes: String
                do { viaRoutes = try CustomAPIKeyBinding.readKey(for: provider, routes: r, defaults: defaults, credentials: store) == key ? "key" : "nil" } catch { viaRoutes = "\(error)" }
                bindings.append(line(["savedAt": opt(savedAt), "readAt": readAt, "provider": provider.rawValue, "stored": opt(defaults.string(forKey: CustomAPIKeyBinding.preference(provider))),
                                      "read": result, "needsConfirmation": needs, "readKey": viaRoutes]))
                defaults.removePersistentDomain(forName: suite)
            }
        }

        // Key checks with mock transports (Apple Intelligence and the local model are Mac-only and not compared).
        var checks: [String] = []
        for provider in ProviderID.allCases where provider != .appleIntelligence && provider != .localModel {
            for (status, body) in [(200, #"{"data":[{"id":"llama3.2"}],"object":"list"}"#), (401, #"{"error":{"message":"API key not valid: \#(secret)","code":401}}"#), (402, ""), (404, "x"), (503, ""), (307, "")] {
                for base in ["http://localhost:11434", "https://api.example.com/v1", "http://192.168.1.20:8000", "", "ftp://x"] {
                    for k in [key, nil] as [APIKey?] {
                        let mock = Mock(status, body)
                        let message = await KeyCheck.run(provider, key: k, apiBaseURL: base, transport: mock)
                        checks.append(line(["provider": provider.rawValue, "status": status, "body": body, "base": base, "key": k != nil, "message": message, "requests": mock.paths]))
                    }
                }
            }
            for fail in [URLError.Code.cannotConnectToHost, .timedOut, .notConnectedToInternet] {
                for base in ["http://localhost:11434", "https://api.example.com/v1"] {
                    let mock = Mock(0, fail: fail)
                    checks.append(line(["provider": provider.rawValue, "fail": "\(fail.rawValue)", "failName": fail == .cannotConnectToHost ? "cannotConnectToHost" : fail == .timedOut ? "timedOut" : "notConnectedToInternet",
                                        "base": base, "key": true, "message": await KeyCheck.run(provider, key: key, apiBaseURL: base, transport: mock), "requests": mock.paths]))
                }
            }
        }
        for (status, body) in [(200, #"{"data":[{"id":"b"},{"id":"a"}]}"#), (200, #"{"data":[]}"#), (200, #"{"data":[{"id":"one"}]}"#), (404, ""), (401, ""), (200, "x")] {
            for base in ["http://localhost:11434", "file:///etc"] {
                let mock = Mock(status, body)
                let found = await KeyCheck.discoverModels(serverURL: base, key: key, transport: mock)
                var models: Any = NSNull(), failure: Any = NSNull()
                switch found { case .success(let m): models = m; case .failure(let e): failure = errorObject(e) }
                checks.append(line(["discover": base, "status": status, "body": body, "models": models, "error": failure, "message": KeyCheck.discoveryMessage(found), "requests": mock.paths]))
            }
        }
        for p in ProviderID.allCases { checks.append(line(["validated": p.rawValue, "message": KeyText.validated(p)])) }

        // Cloud transcription over synthetic WAVs.
        var transcriptions: [String] = []
        let audios: [(String, [[Any]], Int)] = [
            ("speech", [["lcg", 48000, 0.3]], 48_000), ("silence", [["flat", 48000, 0.001]], 48_000), ("tap", [["lcg", 4800, 0.5]], 48_000),
            ("speechThenQuiet", [["lcg", 48000, 0.3], ["lcg", 48000, 0.002]], 48_000), ("noise", [["lcg", 240000, 0.02]], 48_000),
            ("twoWindows", [["lcg", 9600, 0.3]], 48_000), ("threeWindows", [["lcg", 14400, 0.3]], 48_000), ("click", [["flat", 24000, 0.002], ["spike", 101, 0.5], ["flat", 23899, 0.002]], 48_000),
            ("speech16k", [["lcg", 16000, 0.3]], 16_000),
        ]
        let responses: [(String, String, Int, String)] = [
            ("spy", "text", 0, "  "), ("spy", "text", 0, " ."), ("spy", "text", 0, " 42."), ("spy", "text", 0, "Aakash, spaceyboi."), ("spy", "error", 0, "authentication"),
            ("spy", "error", 0, "rateLimited"), ("spy", "error", 0, "cancelled"), ("spy", "text", 0, ""),
            ("verbose", verbose([(" Send the report.", 0, 1, -0.2), (" Thank you.", 1.2, 1.9, -0.2)]), 0, ""), ("verbose", verbose([(" Thank you.", 0.3, 0.9, -0.2)]), 0, ""),
            ("verbose", verbose([(" I'm going to go ahead and get started.", 0, 5, -1.4)]), 0, ""), ("verbose", verbose([(" .", 0, 1, -0.2)]), 0, ""),
            ("verbose", verbose([(" Hello.", 0, 0.2, -0.259)]), 0, ""), ("verbose", verbose([(" Hello.", 0, 0.2, -1.17)]), 0, ""), ("verbose", verbose([(" Hello.", 0, 0.3, -0.259)]), 0, ""),
            ("verbose", verbose([(" Send the report.", 0, 1, -0.8)]), 0, ""), ("verbose", verbose([(" A.", 0, 0.5, -0.1), (" B.", 0.5, 1, -1.3), (" C.", 1, 2, -0.6)]), 0, ""),
            ("custom", #"{"text":"Synthetic transcript."}"#, 200, ""), ("custom", "{\"error\":{\"message\":\"bad key \(secret)\",\"code\":401}}", 401, ""), ("custom", #"{"wrong":1}"#, 200, ""),
            ("custom", verbose([(" Send the report.", 0, 1, -0.2), (" Thank you.", 1.2, 1.9, -0.2)]), 200, ""), ("custom", "", 503, ""),
            ("murmurCloud", #"{"text":"Send the report."}"#, 200, ""), ("murmurCloud", "", 402, ""),
        ]
        for (audioName, recipe, rate) in audios {
            let data = WAVAudio.encodePCM16(samples(recipe), sampleRate: rate)
            for (kind, payload, status, extra) in responses {
                if kind == "spy" && !["speech", "silence"].contains(audioName) { continue }
                let provider: TranscriptionProvider
                switch kind {
                case "spy":
                    let errors: [String: ProviderError] = ["authentication": .authentication(.customAPI), "rateLimited": .rateLimited(.customAPI, retryAfter: 30), "cancelled": .cancelled]
                    provider = Spy(response: payload == "text" ? .success(extra) : .failure(errors[extra]!))
                case "verbose": provider = Verbose(json: payload)
                case "custom":
                    var p = try OpenAICompatibleTranscriptionProvider(baseURL: "https://stt.example.test", model: "whisper-large-v3-turbo", apiKey: key); p.transport = Mock(status, payload); provider = p
                default:
                    var p = MurmurCloudTranscriptionProvider(apiKey: key); p.transport = Mock(status, payload); provider = p
                }
                let (outcome, detail) = await CloudTranscription.runDetailed(provider: provider, audioData: data)
                let o: [String: Any]
                switch outcome {
                case .transcript(let t): o = ["transcript": t]
                case .noSpeech(let skipped): o = ["noSpeech": skipped]
                case .failed(let message, let keep): o = ["failed": message, "keepAudio": keep]
                case .cancelled: o = ["cancelled": true]
                }
                transcriptions.append(line(["audio": audioName, "recipe": recipe, "rate": rate, "kind": kind, "payload": payload, "status": status, "extra": extra, "outcome": o,
                                            "message": opt(CloudTranscription.message(outcome)),
                                            "detail": ["clipSeconds": opt(detail.clipSeconds), "peakP50": opt(detail.peakP50.map(bits)), "peakP90": opt(detail.peakP90.map(bits)),
                                                       "silencePeak": opt(detail.silencePeak.map(bits)), "precheck": detail.precheck.rawValue, "reason": opt(detail.noSpeechReason?.rawValue),
                                                       "segments": opt(detail.segments?.map { [$0.text, $0.dropped, opt($0.loudWindows)] as [Any] }), "logLine": detail.noSpeechLogLine] as [String: Any]]))
            }
        }
        for raw in ["", "not a wav"] {
            let (outcome, detail) = await CloudTranscription.runDetailed(provider: Spy(response: .success("Hello.")), audioData: Data(raw.utf8))
            transcriptions.append(line(["raw": raw, "outcome": outcome == .transcript("Hello.") ? "transcript" : "\(outcome)", "logLine": detail.noSpeechLogLine, "precheck": detail.precheck.rawValue]))
        }
        for (name, outcome) in [("cancelled", CloudTranscriptionOutcome.cancelled), ("failedDiscard", .failed(message: "M.", keepAudio: false)), ("unreadable", .failed(message: CloudTranscription.unreadableAudio, keepAudio: true))] {
            transcriptions.append(line(["messageOf": name, "message": opt(CloudTranscription.message(outcome))]))
        }

        // The destination settings a restore keeps on this machine (Retention.swift), tied to the route keys (check_routes.swift:144-146).
        let destination = ["destinationSettings": DataBackup.destinationSettings.sorted(), "cleanupRouteKey": Prefs.cleanupRoute.key,
                           "routeKeys": [K.transcription, K.legacyCloudTranscriber, K.cleanup, K.customAPIBaseURL, K.customTranscriptionAPIBaseURL, K.legacyServerURL]] as [String: Any]

        print("{")
        for (name, lines) in [("choices", choices), ("plans", plans), ("settings", settings), ("bindings", bindings), ("keyChecks", checks), ("transcriptions", transcriptions)] {
            print("\"\(name)\": [\n" + lines.joined(separator: ",\n") + "\n],")
        }
        print("\"destination\": " + line(destination) + "\n}")
    }
}
