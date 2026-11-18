// End-to-end latency after key release, per stage, on synthetic speech (macOS `say`, 16 kHz WAV). Reuses the app's own
// WarmRunner (local Parakeet helper), ProcessingCoordinator (local rules, Apple Intelligence, Murmur Cloud cleanup) and
// MurmurCloudTranscriptionProvider. Insertion is not measured (it needs a focused app); see LATENCY_BENCHMARK.md.
// Prints numbers only: never the synthetic or transcribed text, never a key. Murmur Cloud is skipped without a stored key.
//
//   app/tools/build.sh measure_latency   # prints the executable path, called /tmp/ml below
//   [ML_N=20] [ML_COLD=3] /tmp/ml HELPER_EXECUTABLE PARAKEET_MODEL_DIR [local,apple,cloud]
import Foundation

let buckets: [(name: String, words: Int)] = [("short", 17), ("medium", 56), ("long", 166)]
let pool = [
    "um so I wanted to follow up on the schedule for the next release", "we should uh move the review meeting to Thursday afternoon",
    "the invoice for the second quarter is like twenty five percent higher than we expected", "I think we can finish the draft by the end of the week",
    "actually no make that Friday morning instead", "please send the updated numbers to the whole team before lunch",
    "the new onboarding flow reduced support requests by about a third", "you know the main risk is that the vendor might not deliver on time",
    "let's keep the budget under fifty thousand dollars and revisit it in March", "uh can you check whether the backup finished last night",
    "the customer asked for two changes to the proposal and one new section", "so the plan is to test it on a small group first and then roll it out",
    "I don't think we need another round of approvals for this", "remind me to call the dentist tomorrow about the appointment",
    "the report covers three regions and the west coast numbers look strongest", "we are waiting on legal to review the contract terms",
]

func quantile(_ xs: [Double], _ q: Double) -> Double { let s = xs.sorted(); return s.isEmpty ? .nan : s[max(0, Int((q * Double(s.count)).rounded(.up)) - 1)] }
func ms(_ x: Double) -> String { x.isNaN ? "-" : String(format: "%.0f", x * 1000) }
func run(_ exe: String, _ args: [String]) throws {
    let p = Process(); p.executableURL = URL(fileURLWithPath: exe); p.arguments = args
    try p.run(); p.waitUntilExit(); guard p.terminationStatus == 0 else { throw NSError(domain: exe, code: Int(p.terminationStatus)) }
}
func now() -> Double { ProcessInfo.processInfo.systemUptime }
func cell(_ label: String, _ bucket: String, _ xs: [Double], failed: Int, fallbacks: Int = 0) {
    print("| \(label) | \(bucket) | \(xs.count) | \(ms(quantile(xs, 0.5))) | \(ms(quantile(xs, 0.95))) | \(ms(xs.max() ?? .nan)) | \(failed) | \(fallbacks) |")
}
func asr(_ warm: WarmRunner, _ helper: URL, _ launch: HelperLaunch, _ wav: URL) async -> (Double, Bool) {
    let t = now()
    let line: Data? = await withCheckedContinuation { c in warm.transcribe(wav, helper: helper, launch: launch) { c.resume(returning: $0) } }
    let text = line.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }?["text"] as? String
    return (now() - t, text.map { !$0.isEmpty } ?? false)
}

@main enum MeasureLatency {
    static func main() async throws {
        setvbuf(stdout, nil, _IOLBF, 0)
        let a = Array(CommandLine.arguments.dropFirst())
        guard a.count >= 2 else { print("usage: ml HELPER MODEL_DIR [local,apple,cloud]"); exit(2) }
        let helper = URL(fileURLWithPath: a[0]), models = URL(fileURLWithPath: a[1])
        let stages = Set((a.count > 2 ? a[2] : "local,apple,cloud").split(separator: ",").map(String.init))
        let n = Int(ProcessInfo.processInfo.environment["ML_N"] ?? "") ?? 20, coldRuns = Int(ProcessInfo.processInfo.environment["ML_COLD"] ?? "") ?? 3
        let launch = HelperLaunch(models: models, dictionaryArguments: [], key: "bench")
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-latency-\(UUID())")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        // Synthetic clips: n per bucket, each a different deterministic mix of the sentence pool.
        var clips: [String: [(url: URL, seconds: Double)]] = [:]
        for b in buckets {
            for i in 0..<n {
                var words: [String] = [], k = i * 5
                while words.count < b.words { words += pool[k % pool.count].split(separator: " ").map(String.init); k += 1 }
                let text = words.prefix(b.words).joined(separator: " ") + "."
                let aiff = dir.appendingPathComponent("\(b.name)-\(i).aiff"), wav = dir.appendingPathComponent("\(b.name)-\(i).wav")
                try run("/usr/bin/say", ["-o", aiff.path, text])
                try run("/usr/bin/afconvert", ["-f", "WAVE", "-d", "LEI16@16000", "-c", "1", aiff.path, wav.path])
                let d = try Data(contentsOf: wav); clips[b.name, default: []].append((wav, Double(d.count - 44) / 32000))
            }
            let s = clips[b.name]!.map(\.seconds); print("clips \(b.name): n=\(s.count), duration s median \(String(format: "%.1f", quantile(s, 0.5))), range \(String(format: "%.1f-%.1f", s.min()!, s.max()!))")
        }
        print("\n| Stage | Length | n | Median ms | P95 ms | Max ms | Failed |\n|---|---|---:|---:|---:|---:|---:|---:|")

        let warm = WarmRunner(); defer { warm.stopNow() }
        var transcripts: [String: [String]] = [:]
        if stages.contains("local") || stages.contains("apple") {
            // Cold: a new helper process (Core ML caches on disk are warm) to its first answer for a short clip.
            var cold: [Double] = [], coldFailed = 0
            for _ in 0..<coldRuns {
                let r = WarmRunner(); let t = now()
                let line: Data? = await withCheckedContinuation { c in r.transcribe(clips["short"]![0].url, helper: helper, launch: launch) { c.resume(returning: $0) } }
                if line == nil { coldFailed += 1 } else { cold.append(now() - t) }
                r.stopNow()
            }
            cell("Local ASR, cold (launch + load + first clip)", "short", cold, failed: coldFailed)
            _ = await asr(warm, helper, launch, clips["short"]![0].url)  // load once; not recorded
            for b in buckets {
                var xs: [Double] = [], failed = 0
                for c in clips[b.name]! { let (t, ok) = await asr(warm, helper, launch, c.url); if ok { xs.append(t) } else { failed += 1 } }
                cell("Local ASR, warm", b.name, xs, failed: failed)
            }
        }
        // Text for the cleanup stages comes from the local ASR so cleanup sees realistic transcripts; it is never printed.
        if stages.contains("local") || stages.contains("apple") {
            for b in buckets {
                for c in clips[b.name]! {
                    let line: Data? = await withCheckedContinuation { k in warm.transcribe(c.url, helper: helper, launch: launch) { k.resume(returning: $0) } }
                    if let t = (line.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] })?["text"] as? String, !t.isEmpty { transcripts[b.name, default: []].append(t) }
                }
            }
        }

        let root = dir.appendingPathComponent("p"), suite = "murmur-latency-\(UUID())", defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let p = try ProcessingCoordinator(directory: root, defaults: defaults)
        // Times processAsync (dictionary, rules, optional model call, guard) for each text. A fallback or provider error is counted as failed.
        func cleanup(_ label: String, route: CleanupRoute, provider: CleanupProvider?, level: String, texts: [String: [String]], first: Bool = false) async {
            let o = CleanupOptions(fillers: true, corrections: true, lists: true, punctuation: true, polish: level != "strict", professional: level == "professional", spoken: true)
            var s = p.settings; s.route = route; s.options = o; try? p.update(s, persist: false); p.cloudCleaner = provider
            for b in buckets {
                var xs: [Double] = [], failed = 0, fallbacks = 0
                for (i, t) in (texts[b.name] ?? []).enumerated() {
                    let start = now(); let r = await p.processAsync(t); let dt = now() - start
                    if r.providerError != nil { failed += 1 } else if first && b.name == "short" && i == 0 { cell(label + ", first call (cold)", b.name, [dt], failed: 0) } else { xs.append(dt); if provider != nil && r.fallback != nil { fallbacks += 1 } }
                }
                cell(label, b.name, xs, failed: failed, fallbacks: fallbacks)
            }
        }
        if stages.contains("local") { await cleanup("Local rules cleanup (Strict)", route: .local, provider: nil, level: "strict", texts: transcripts) }
        if stages.contains("apple") {
            let apple = AppleCleanupProvider()
            if let why = apple.status() { print("| Apple Intelligence | - | 0 | - | - | - | - | unavailable: \(why.userMessage) |") } else {
                for level in ["polish", "professional"] { await cleanup("Apple on-device cleanup (\(level.capitalized))", route: .cloud, provider: apple, level: level, texts: transcripts, first: level == "polish") }
            }
        }
        if stages.contains("cloud") {
            guard let key = try? KeychainCredentialStore().read(.murmurCloud) else { print("| Murmur Cloud | - | 0 | - | - | - | - | skipped: no stored key |"); return }
            let tr = MurmurCloudTranscriptionProvider(apiKey: key)
            var cloudTexts: [String: [String]] = [:]
            for b in buckets {
                var xs: [Double] = [], prep: [Double] = [], failed = 0
                for (i, c) in clips[b.name]!.enumerated() {
                    let t0 = now(); let prepared = CloudAudio.prepare(try Data(contentsOf: c.url.deletingPathExtension().appendingPathExtension("wav"))); prep.append(now() - t0)
                    try? await Task.sleep(nanoseconds: 2_000_000_000)  // dictations are sparse; do not trip a rate limit
                    let t = now()
                    var res: TranscriptionResult? = nil
                    do { res = try await tr.transcribeDetailed(prepared.payload) } catch { print("  cloud ASR error \(b.name): \(Mirror(reflecting: error).children.first?.label ?? "\(error)")") }
                    if let r = res, !r.text.isEmpty {
                        let dt = now() - t
                        if i == 0 && b.name == "short" { cell("Murmur Cloud ASR, first request (cold)", b.name, [dt], failed: 0) } else { xs.append(dt) }
                        cloudTexts[b.name, default: []].append(r.text)
                    } else { failed += 1 }
                }
                cell("Murmur Cloud ASR upload+transcribe", b.name, xs, failed: failed); cell("Murmur Cloud audio prepare (local)", b.name, prep, failed: 0)
            }
            await cleanup("Murmur Cloud cleanup (Polish)", route: .cloud, provider: MurmurCloudCleanupProvider(apiKey: key), level: "polish", texts: cloudTexts)
        }
    }
}
