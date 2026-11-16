import Foundation

// Checks for LocalCleanup.swift: the provider's request, the helper client (a fake helper script stands in for the model:
// ready line, one reply per request, serialized, one child, timeouts, cancellation) and the guard through ProcessingCoordinator.
//
//   swiftc app/Cleanup.swift app/Dictionary.swift app/Learning.swift app/Retention.swift app/Providers.swift \
//     app/Credentials.swift app/Processing.swift app/Routes.swift app/AppleCleanup.swift app/LocalCleanup.swift spike/Observation.swift \
//     app/check_local_cleanup.swift -o /tmp/murmur-local-cleanup-check && /tmp/murmur-local-cleanup-check

private var failures = 0
private var passes = 0
private func check(_ condition: Bool, _ label: String, line: Int = #line) {
    if condition { passes += 1 } else { failures += 1; print("FAIL [line \(line)]: \(label)") }
}

private final class Recorder: @unchecked Sendable {
    private let lock = NSLock()
    private var _requests: [LocalCleanupRequest] = []
    var requests: [LocalCleanupRequest] { lock.withLock { _requests } }
    func add(_ r: LocalCleanupRequest) { lock.withLock { _requests.append(r) } }
}

private func provider(_ recorder: Recorder, status: OnDeviceModelUnavailable? = nil, _ reply: @escaping @Sendable (String) -> String) -> LocalModelCleanupProvider {
    var p = LocalModelCleanupProvider()
    p.status = { status }
    p.generate = { recorder.add($0); return reply($0.text) }
    return p
}

private func script(_ body: String, in dir: URL, name: String) throws -> URL {
    let url = dir.appendingPathComponent(name)
    try ("#!/bin/sh\n" + body).write(to: url, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
    return url
}

private func alive(_ pid: Int32) -> Bool { kill(pid, 0) == 0 }

@main struct LocalCleanupChecks {
    static func main() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("murmur-local-cleanup-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let suite = "murmur-local-cleanup-check-\(UUID())"
        guard let defaults = UserDefaults(suiteName: suite) else { print("FAIL: no defaults suite"); exit(1) }
        defer { try? FileManager.default.removeItem(at: root); defaults.removePersistentDomain(forName: suite) }

        // Provider: typed input only, never the built prompt; refusals are content-free.
        let rec = Recorder()
        var pro = CleanupOptions.standard; pro.professional = true; pro.polish = true; pro.trustModel = true
        let terms = (1...15).map { "Term\($0)" }
        let built = CleanupPrompt.build(transcript: "hello there", options: pro, dictionaryTerms: terms, context: ScreenContext(selected: "SECRET-SCREEN-TEXT", clipboard: "", window: ""), instructions: "STYLE-NOTE")
        _ = try await provider(rec) { $0 }.cleanup(built)
        check(rec.requests.count == 1 && rec.requests[0].text == "hello there" && rec.requests[0].tone == "formal", "Professional sends the typed transcript with the formal tone")
        check(rec.requests[0].terms == Array(terms.prefix(12)), "at most 12 relevant terms")
        let casual = CleanupPrompt.build(transcript: "hello there", options: .standard)
        _ = try await provider(rec) { $0 }.cleanup(casual)
        check(rec.requests[1].tone == "casual", "other styles use the default tone")
        do { _ = try await provider(rec) { $0 }.cleanup(CleanupMessages(system: "rewrite", user: "x")); check(false, "a rewrite preset must be refused") }
        catch { check(error as? ProviderError == .invalidConfiguration(.localModel), "messages without typed input (rewrite presets, notes) are refused") }
        do { _ = try await provider(rec, status: .modelNotDownloaded) { $0 }.cleanup(casual); check(false, "missing model must throw") }
        catch { check(error as? ProviderError == .modelUnavailable(.localModel, .modelNotDownloaded), "a missing model is reported, never downloaded") }
        do { _ = try await provider(rec) { $0 }.cleanup(CleanupPrompt.build(transcript: String(repeating: "word ", count: 1_000), options: .standard)); check(false, "long input must throw") }
        catch { check(error as? ProviderError == .payloadTooLarge(.localModel, limitBytes: nil), "oversized input is refused before the helper") }
        check(provider(rec) { $0 }.disclosure.staysOnThisMac && LocalModelCleanupProvider().id.rawValue == "local-model", "stays on this Mac, own provider ID")

        // Helper client with a fake helper: one child reused, replies matched, nothing carried over.
        let echo = try script("""
        echo $$ > "$1/pid"
        echo '{"ready":true}'
        while read line; do
          id=$(echo "$line" | sed -n 's/.*"id":\\([0-9]*\\).*/\\1/p')
          case "$line" in
            *toolong*) echo "{\\"id\\":$id,\\"error\\":\\"too-long\\"}";;
            *hang*) sleep 30;;
            *slow*) sleep 1; echo "{\\"id\\":$id,\\"text\\":\\"reply $$\\"}";;
            *broken*) echo "{\\"id\\":$id,\\"error\\":\\"failed\\"}";;
            *) echo "{\\"id\\":$id,\\"text\\":\\"reply $$\\"}";;
          esac
        done
        """, in: root, name: "echo.sh")
        let runner = LocalCleanupRunner(executable: echo, directory: root, startupSeconds: 10, requestSeconds: 1.5, idleSeconds: 60)
        let first = try await runner.clean(LocalCleanupRequest(text: "one", tone: "casual", terms: []))
        let second = try await runner.clean(LocalCleanupRequest(text: "two", tone: "formal", terms: ["A"]))
        check(first == second && first.hasPrefix("reply "), "consecutive requests reuse one warm helper")
        let both = try await withThrowingTaskGroup(of: String.self) { group in
            for text in ["a", "b", "c"] { group.addTask { try await runner.clean(LocalCleanupRequest(text: text, tone: "casual", terms: [])) } }
            var out: [String] = []; for try await r in group { out.append(r) }; return out
        }
        check(both.count == 3 && Set(both).count == 1, "concurrent requests are serialized on the one helper")
        do { _ = try await runner.clean(LocalCleanupRequest(text: "toolong", tone: "casual", terms: [])); check(false, "too-long must throw") }
        catch { check(error as? ProviderError == .payloadTooLarge(.localModel, limitBytes: nil), "a too-long reply becomes payloadTooLarge (local rules fallback)") }
        do { _ = try await runner.clean(LocalCleanupRequest(text: "broken", tone: "casual", terms: [])); check(false, "failed must throw") }
        catch { check(error as? ProviderError == .malformedResponse(.localModel), "a helper failure becomes a provider error") }
        let pid1 = Int32(try String(contentsOf: root.appendingPathComponent("pid")).trimmingCharacters(in: .whitespacesAndNewlines))!
        check(alive(pid1), "helper still alive after errors it reported")
        // Deadline: a hung request ends only this child; the next request starts a fresh one.
        do { _ = try await runner.clean(LocalCleanupRequest(text: "hang", tone: "casual", terms: [])); check(false, "hang must time out") }
        catch { check(error as? ProviderError == .timeout(.localModel), "a hung request times out") }
        try await Task.sleep(for: .milliseconds(300))
        check(!alive(pid1), "the timed-out child was terminated")
        let third = try await runner.clean(LocalCleanupRequest(text: "after", tone: "casual", terms: []))
        let pid2 = Int32(try String(contentsOf: root.appendingPathComponent("pid")).trimmingCharacters(in: .whitespacesAndNewlines))!
        check(pid2 != pid1 && third == "reply \(pid2)", "a fresh helper serves the next request")
        // Cancellation terminates the child and reports cancelled.
        let task = Task { try await runner.clean(LocalCleanupRequest(text: "hang", tone: "casual", terms: [])) }
        try await Task.sleep(for: .milliseconds(300)); task.cancel()
        do { _ = try await task.value; check(false, "cancel must throw") } catch { check(error as? ProviderError == .cancelled, "cancellation reports cancelled: \(error)") }
        try await Task.sleep(for: .milliseconds(300))
        check(!alive(pid2), "the cancelled child was terminated")
        await runner.shutdown()
        // Cancelling a queued request must not touch the active one.
        let active = Task { try await runner.clean(LocalCleanupRequest(text: "slow", tone: "casual", terms: [])) }
        try await Task.sleep(for: .milliseconds(250))
        let queued = Task { try await runner.clean(LocalCleanupRequest(text: "queued", tone: "casual", terms: [])) }
        try await Task.sleep(for: .milliseconds(250)); queued.cancel()
        do { _ = try await queued.value; check(false, "queued cancel must throw") } catch { check(error as? ProviderError == .cancelled, "a queued request cancels alone") }
        let finished = try? await active.value
        check(finished?.hasPrefix("reply ") == true, "the active request was not terminated by a queued cancel: \(String(describing: finished))")
        // Damaged files: the first launch is refused when the pinned checksums fail, and nothing starts.
        LocalCleanupRunner.verifier = { false }
        let damaged = LocalCleanupRunner(executable: echo, directory: root)
        do { _ = try await damaged.clean(LocalCleanupRequest(text: "x", tone: "casual", terms: [])); check(false, "damaged model must throw") }
        catch { check(error as? ProviderError == .modelUnavailable(.localModel, .modelDamaged), "unverified model files are refused") }
        LocalCleanupRunner.verifier = nil
        // Missing helper: a content-free failure, no crash.
        let missing = LocalCleanupRunner(executable: root.appendingPathComponent("nope"), directory: root)
        do { _ = try await missing.clean(LocalCleanupRequest(text: "x", tone: "casual", terms: [])); check(false, "missing helper must throw") }
        catch { check(error as? ProviderError == .malformedResponse(.localModel), "a helper that cannot start fails cleanly") }
        await runner.shutdown()
        // Idle unload.
        let idle = LocalCleanupRunner(executable: echo, directory: root, startupSeconds: 10, requestSeconds: 5, idleSeconds: 0.5)
        _ = try await idle.clean(LocalCleanupRequest(text: "x", tone: "casual", terms: []))
        let idlePid = Int32(try String(contentsOf: root.appendingPathComponent("pid")).trimmingCharacters(in: .whitespacesAndNewlines))!
        try await Task.sleep(for: .seconds(1.2))
        check(!alive(idlePid), "an idle helper exits and releases the model")

        // Guard through the pipeline: the full guard applies even with the global trust option on.
        let p = try ProcessingCoordinator(directory: root.appendingPathComponent("p"), defaults: defaults)
        var settings = p.settings; settings.route = .cloud; settings.options = .standard
        settings.options.polish = true; settings.options.professional = true; settings.options.trustModel = true
        try p.update(settings, persist: false)
        let deadlineRaw = "can you send me the report by 5 the quarterly one not the monthly one"
        p.cloudCleaner = provider(rec) { _ in "Can you send me the quarterly report, not the monthly one?" }
        let dropped = await p.processAsync(deadlineRaw)
        if case .insert(let text) = dropped.outcome { check(text.contains("5"), "a model output that drops the deadline is not inserted: \(text)") } else { check(false, "inserts") }
        check(dropped.fallback != nil && dropped.cleanupPath != .modelAccepted, "dropped deadline is recorded as a rejection")
        p.cloudCleaner = provider(rec) { _ in "Can you send me the report by 5, the quarterly one, not the monthly one?" }
        let ok = await p.processAsync(deadlineRaw)
        check(ok.fallback == nil && ok.promptHash == "local-fixed", "a faithful output is accepted and the fixed prompt is recorded")

        let fridayRaw = "me and Priya was thinking we could ship on Friday actually no on Monday"
        p.cloudCleaner = provider(rec) { _ in "Priya and I were thinking we could ship on Monday." }
        let friday = await p.processAsync(fridayRaw)
        check(friday.outcome == .insert("Priya and I were thinking we could ship on Monday.") && friday.fallback == nil, "the correct \"actually no\" repair is accepted")
        p.cloudCleaner = provider(rec) { _ in "Priya and I were thinking we could ship on Friday and Monday." }
        let both2 = await p.processAsync(fridayRaw)
        check(both2.fallback != nil, "keeping both versions of a repair is rejected")
        let notRaw = "please do not send the contract today"
        p.cloudCleaner = provider(rec) { _ in "Please send the contract today." }
        let negation = await p.processAsync(notRaw)
        if case .insert(let text) = negation.outcome { check(text.lowercased().contains("not"), "a dropped negation is not inserted: \(text)") } else { check(false, "inserts") }
        p.cloudCleaner = provider(rec) { _ in "Thanks, Shreya Raman. Send 42.50 dollars." }
        let names = await p.processAsync("thanks shreya raman send 42.50 dollars")
        check(names.fallback == nil, "names and numbers kept pass")
        // A rewrite preset cannot run on the fixed-prompt model: it is never sent, ordinary cleanup runs instead.
        var preset = WritingMode(); preset.transform = .email
        let presetRec = Recorder()
        p.cloudCleaner = provider(presetRec) { _ in "Dear team, ship Monday." }
        let presetResult = await p.processAsync("ship friday no monday", style: DictationStyle(mode: preset, settings: p.settings))
        check(presetResult.fallback == .cleanerFailed || presetRec.requests.count == 1, "a rewrite preset never reaches the local model as a rewrite")
        check(presetRec.requests.allSatisfy { $0.text == "ship friday no monday" || !$0.text.contains("email") }, "no preset instructions are sent")
        // Providers that are not the managed model keep the global trust behaviour: only the local route is forced strict.
        check(ProviderID.localModel.displayName == "Local model", "display name")

        // Long input is split at sentence ends and a repair stays with the sentence it repairs.
        let filler = "we reviewed the plan and agreed on the next steps for the team."
        let longText = Array(repeating: filler, count: 14).joined(separator: " ") + " We ship on Friday. Actually no, on Monday. " + Array(repeating: filler, count: 14).joined(separator: " ")
        let sections = CleanupSections.split(longText, target: 150, keepingCorrectionsAttached: true)
        check(sections.count > 1 && sections.allSatisfy { !$0.hasPrefix("Actually no") }, "a correction is never cut from its antecedent")
        check(sections.joined(separator: " ") == longText, "splitting loses no text")
        // Unsplittable oversized input falls back to local rules instead of truncating.
        let huge = Array(repeating: "word", count: 3_000).joined(separator: " ")
        var helperLimit = LocalModelCleanupProvider(); helperLimit.status = { nil }; helperLimit.generate = { _ in throw ProviderError.payloadTooLarge(.localModel, limitBytes: nil) }
        p.cloudCleaner = helperLimit
        let tooBig = await p.processAsync(huge)
        if case .insert(let text) = tooBig.outcome { check(text.split(separator: " ").count >= 3_000, "oversized text is kept in full by the rules fallback") } else { check(false, "inserts") }
        check(tooBig.providerError == .payloadTooLarge(.localModel, limitBytes: nil) && tooBig.fallback == .cleanerFailed, "the fallback and its reason are recorded")

        print("\(passes) checks passed, \(failures) failed")
        if failures > 0 { exit(1) }
    }
}
