// Runs app/meeting-notes-corpus.json through a meeting notes route with the real writer (prompt,
// map-reduce, grounding check) and prints the notes measures. `apple` uses Apple's on-device
// model; `custom` reads Murmur's key from the Keychain (never printed) and costs API calls.
// `--long` also times notes for synthetic 1-hour and 2-hour meetings built from the corpus.
//
// "Unsupported" is judged against the corpus labels, not by the grounding check (which would make it
// 0 by construction): a decision or action item is supported only if its cited lines overlap a
// labelled agreed item of the same kind; any item is unsupported if it contains a trap (an injected
// or never-agreed name, number or claim). "Invented" is checked independently of the guard: a digit
// run, a number word or a mid-sentence capitalized word in an item that appears nowhere in the
// meeting's text or the user's notes, plus traps that are names or numbers.
//
//   app/tools/build.sh measure_meeting_notes   # prints the executable path, called /tmp/mmn below
//   /tmp/mmn apple|custom|llama [--long] [--30-minute] [--60-minute] [--verbose]
// The llama route uses a local llama.cpp server on MMN_LLAMA_PORT (default 18085).
// Set MMN_LLAMA_MAX_TOKENS to test a different reply budget (default 768).
import Foundation

struct Corpus: Decodable {
    struct Labelled: Decodable { var lines: [Int]; var owner: String? }
    struct Entry: Decodable { var id: String; var myNotes: String; var lines: [[String]]; var decisions: [[Int]]; var actions: [Labelled]; var traps: [String] }
    var meetings: [Entry]
}

func meeting(_ entry: Corpus.Entry, offset: Double = 0, into base: Meeting? = nil, spacing: Double = 20) -> Meeting {
    var m = base ?? Meeting(id: UUID().uuidString, recordedAt: Date(), source: MeetingSource(name: "Zoom", bundleID: "us.zoom.xos"))
    // Leave a gap so neighbouring lines do not count as echo of each other.
    for (i, line) in entry.lines.enumerated() {
        let start = offset + Double(i) * spacing
        m.insert(stream: line[0] == "me" ? .me : .them, start: start, end: start + spacing / 2, text: line[1])
    }
    m.myNotes = base == nil ? entry.myNotes : m.myNotes
    return m
}

func llamaNotes(system: String, user: String) async throws -> String {
    struct Reply: Decodable {
        struct Choice: Decodable {
            struct Message: Decodable { let content: String? }
            let message: Message
            let finish_reason: String
        }
        let choices: [Choice]
    }
    let env = ProcessInfo.processInfo.environment
    let port = UInt16(env["MMN_LLAMA_PORT"] ?? "18085") ?? 18085
    let maxTokens = Int(env["MMN_LLAMA_MAX_TOKENS"] ?? "768") ?? 768
    var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/v1/chat/completions")!, timeoutInterval: 600)
    request.httpMethod = "POST"
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    request.httpBody = try JSONSerialization.data(withJSONObject: [
        "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
        "temperature": 0, "seed": 1234, "max_tokens": maxTokens
    ])
    let (data, response) = try await URLSession.shared.data(for: request)
    guard let status = (response as? HTTPURLResponse)?.statusCode, status == 200 else {
        let error = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        if (error?["error"] as? [String: Any])?["type"] as? String == "exceed_context_size_error" {
            throw NotesFailure.tooLarge
        }
        throw NotesFailure.failed("llama.cpp request failed.")
    }
    guard let choice = try JSONDecoder().decode(Reply.self, from: data).choices.first,
          choice.finish_reason == "stop", let content = choice.message.content, !content.isEmpty else {
        throw NotesFailure.failed("llama.cpp did not return a complete reply.")
    }
    return content
}

nonisolated(unsafe) var lastReplies: [String] = []

@main enum MeasureMeetingNotes {
    static func main() async throws {
        setvbuf(stdout, nil, _IOLBF, 0)
        var args = Array(CommandLine.arguments.dropFirst())
        let verbose = args.contains("--verbose"), long = args.contains("--long")
        let halfHour = args.contains("--30-minute"), hour = args.contains("--60-minute")
        args.removeAll { $0.hasPrefix("--") }
        let routeArg = args.first ?? "apple"
        let run: MeetingNotesWriter.Generate, route: MeetingNotesRoute
        switch routeArg {
        case "apple":
            let apple = AppleCleanupProvider()
            if let reason = apple.status() { print("Apple Intelligence unavailable: \(reason.userMessage)"); exit(2) }
            run = { try await AppleSystemModel.notes(system: $0, user: $1) }
            route = .appleIntelligence
        case "custom":
            let d = UserDefaults(suiteName: "cc.clouddev.dictation.development")!
            guard let base = d.string(forKey: "customAPIBaseURL"), let model = d.string(forKey: "customAPIModel"),
                  let custom = makeCleanupProvider(.customAPI, key: try CustomAPIKeyBinding.read(at: base, defaults: d, credentials: KeychainCredentialStore(), provider: .customCleanup),
                                                   apiBaseURL: base, model: model) else { print("No Custom Provider cleanup configured"); exit(2) }
            run = { try await custom.cleanup(CleanupMessages(system: $0, user: $1)) }
            route = .customAPI
        case "llama":
            run = { try await llamaNotes(system: $0, user: $1) }
            route = .appleIntelligence
        default: print("route must be apple, custom or llama"); exit(2)
        }
        let generate: MeetingNotesWriter.Generate = { system, user in
            do {
                let reply = try await run(system, user)
                // Corpus text is synthetic, so an unreadable reply may be shown.
                if verbose && Draft.parse(reply) == nil { print("  UNREADABLE REPLY: \(reply.prefix(600))") }
                if verbose { lastReplies.append(reply) }
                return reply
            }
            catch ProviderError.payloadTooLarge { throw NotesFailure.tooLarge }
            catch let error as ProviderError { throw NotesFailure.failed(error.userMessage) }
        }
        let corpus = try JSONDecoder().decode(Corpus.self, from: Data(contentsOf: URL(fileURLWithPath: ProcessInfo.processInfo.environment["MMN_CORPUS"] ?? "app/meeting-notes-corpus.json")))
        print("route \(routeArg), \(corpus.meetings.count) meetings")

        var unsupported = 0, invented = 0, missedDecisions = 0, missedActions = 0, labelledDecisions = 0, labelledActions = 0
        var items = 0, dropped = 0, failures = 0, seconds: [Double] = []
        var reasons: [String: Int] = [:]
        let only = ProcessInfo.processInfo.environment["MMN_ONLY"]
        for entry in corpus.meetings where only == nil || only == entry.id {
            let m = meeting(entry)
            let number = Dictionary(uniqueKeysWithValues: MeetingNotesPrompt.number(m).map { ($0.line.id, $0.number) })
            let start = Date()
            lastReplies = []
            let outcome: MeetingNotesWriter.Outcome
            do { outcome = try await MeetingNotesWriter.write(m, route: route, routeName: routeArg, structured: routeArg == "apple", generate: generate) }
            catch { failures += 1; print("\(entry.id): FAILED \(error)"); continue }
            seconds.append(Date().timeIntervalSince(start))
            let notes = outcome.notes
            dropped += notes.dropped
            for (reason, count) in outcome.reasons { reasons[reason.rawValue, default: 0] += count }
            let said = (entry.lines.map { $0[1] }.joined(separator: " ") + " " + entry.myNotes).lowercased()
            func cited(_ item: NoteItem) -> Set<Int> { Set(item.lines.compactMap { number[$0] }) }
            func trapped(_ item: NoteItem) -> String? {
                let text = [item.text, item.owner ?? "", item.due ?? ""].joined(separator: " ").lowercased()
                return entry.traps.first { text.contains($0.lowercased()) }
            }
            func newFact(_ item: NoteItem) -> String? {
                let text = [item.text, item.owner ?? "", item.due ?? ""].joined(separator: ". ")
                for n in NotesGrounding.numbers(text) where !NotesGrounding.numbers(said).contains(n) { return n }
                for sentence in text.components(separatedBy: CharacterSet(charactersIn: ".!?:;")) {
                    let words = NotesGrounding.tokens(sentence)
                    for (i, w) in words.enumerated() where i > 0 && w.first?.isUppercase == true && !["I", "Me", "Them"].contains(w) {
                        let bare = w.lowercased().hasSuffix("'s") ? String(w.lowercased().dropLast(2)) : w.lowercased()
                        if !said.contains(bare) { return w }
                    }
                }
                return nil
            }
            var notesLog: [String] = []
            for (kind, list) in [("point", notes.keyPoints), ("decision", notes.decisions), ("action", notes.actions)] {
                for item in list {
                    items += 1
                    var flags: [String] = []
                    if let trap = trapped(item) { flags.append("trap:\(trap)") }
                    if kind == "decision", !entry.decisions.contains(where: { !cited(item).isDisjoint(with: $0) }) { flags.append("not-a-labelled-decision") }
                    if kind == "action", !entry.actions.contains(where: { !cited(item).isDisjoint(with: $0.lines) }) { flags.append("not-a-labelled-action") }
                    if !flags.isEmpty { unsupported += 1 }
                    if let fact = newFact(item) { invented += 1; flags.append("invented:\(fact)") }
                    else if let trap = trapped(item), NotesGrounding.numbers(trap).count + NotesGrounding.names(". x " + trap).count > 0 { invented += 1 }
                    notesLog.append("  \(kind) L\(cited(item).sorted()) \(item.owner.map { "[\($0)] " } ?? "")\(item.text)\(flags.isEmpty ? "" : "  <-- " + flags.joined(separator: ", "))")
                }
            }
            labelledDecisions += entry.decisions.count; labelledActions += entry.actions.count
            let missD = entry.decisions.filter { d in !notes.decisions.contains { !cited($0).isDisjoint(with: d) } }.count
            let missA = entry.actions.filter { a in !notes.actions.contains { !cited($0).isDisjoint(with: a.lines) } }.count
            missedDecisions += missD; missedActions += missA
            print("\(entry.id): \(String(format: "%.1f", seconds.last!)) s, items \(notes.keyPoints.count)/\(notes.decisions.count)/\(notes.actions.count), dropped \(notes.dropped), missed \(missD)d \(missA)a\(verbose ? "" : "")")
            if verbose {
                let proposed = lastReplies.compactMap(Draft.parse).last
                print("  title: \(notes.title ?? "(fallback: \(proposed?.title ?? "none"))") | summary: \(notes.summary ?? "(none: \(proposed?.summary ?? "none"))")")
                notesLog.forEach { print($0) }
                // What the check dropped, and why (synthetic text).
                let source = NotesGrounding.source(MeetingNotesPrompt.number(m), myNotes: m.myNotes)
                for draft in lastReplies.compactMap(Draft.parse) {
                    for (kind, list) in [(NotesGrounding.Kind.point, draft.points), (.decision, draft.decisions), (.action, draft.actions)] {
                        for item in list { if let why = NotesGrounding.check(item, kind: kind, in: source) { print("  dropped \(kind) \(item.lines) [\(item.owner ?? "-")|\(item.due ?? "-")] \(item.text)  <-- \(why)") } }
                    }
                }
            }
        }
        let n = Double(corpus.meetings.count - failures)
        let sorted = seconds.sorted()
        print("""

        RESULT route=\(routeArg) meetings=\(corpus.meetings.count) failed=\(failures)
          items kept \(items), dropped by the check \(dropped) \(reasons.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: " "))
          unsupported after the check: \(unsupported) (\(String(format: "%.2f", Double(unsupported) / max(n, 1))) per meeting; bar 0.2)
          invented names or numbers after the check: \(invented) (bar 0)
          missed decisions \(missedDecisions)/\(labelledDecisions), missed action items \(missedActions)/\(labelledActions)
          seconds per meeting: median \(String(format: "%.1f", sorted.isEmpty ? 0 : sorted[sorted.count / 2])), max \(String(format: "%.1f", sorted.last ?? 0))
        """)

        guard long || halfHour || hour else { return }
        // Repeat the synthetic corpus to approximate 150 spoken words per minute.
        var lengths: [(String, Int, Double?)] = []
        if halfHour { lengths.append(("30 minutes", 4_500, 1_800)) }
        if hour { lengths.append(("60 minutes", 9_000, 3_600)) }
        if long { lengths += [("1 hour", 9_000, nil), ("2 hours", 18_000, nil)] }
        for (label, words, duration) in lengths {
            var m = Meeting(id: UUID().uuidString, recordedAt: Date(), source: MeetingSource(name: "Zoom", bundleID: "us.zoom.xos"))
            var entries: [Corpus.Entry] = []
            var count = 0
            while count < words {
                for entry in corpus.meetings where count < words {
                    entries.append(entry)
                    count += entry.lines.reduce(0) { $0 + $1[1].split(separator: " ").count }
                }
            }
            let spacing = duration.map { $0 / (Double(entries.reduce(0) { $0 + $1.lines.count }) - 0.5) } ?? 20
            var offset = 0.0
            for entry in entries {
                m = meeting(entry, offset: offset, into: m, spacing: spacing)
                offset += Double(entry.lines.count) * spacing + (duration == nil ? 20 : 0)
            }
            if let duration { precondition(abs((m.lines.last?.end ?? 0) - duration) < 0.01) }
            let chunks = MeetingNotesWriter.chunks(MeetingNotesPrompt.number(m), budget: route.chunkCharacters).count
            var calls = 0, overflows = 0
            let counting: MeetingNotesWriter.Generate = { system, user in
                calls += 1
                do { return try await generate(system, user) } catch NotesFailure.tooLarge { overflows += 1; throw NotesFailure.tooLarge }
            }
            let start = Date()
            do {
                let notes = try await MeetingNotesWriter.write(m, route: route, routeName: routeArg, structured: routeArg == "apple", generate: counting).notes
                print("LONG \(label): \(count) words, \(m.lines.count) lines, \(chunks) chunks, \(calls) requests, \(overflows) context overflows (retried), \(String(format: "%.0f", Date().timeIntervalSince(start))) s; items \(notes.keyPoints.count)/\(notes.decisions.count)/\(notes.actions.count), dropped \(notes.dropped)")
            } catch {
                print("LONG \(label): FAILED after \(String(format: "%.0f", Date().timeIntervalSince(start))) s, \(calls) requests, \(overflows) overflows: \(error)")
            }
        }
    }
}
