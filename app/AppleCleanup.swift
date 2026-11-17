import Foundation
#if canImport(FoundationModels)
import FoundationModels
#endif

// Apple's on-device system model through
// FoundationModels. Nothing to download, no key, and nothing leaves this Mac, so the route has no
// disclosure. Prompts come from `CleanupPrompt.build` unchanged. ProcessingCoordinator guards the
// output and falls back to local rules on any error, as for every provider route.
// The context is about 4,096 tokens, so ProcessingCoordinator cleans text of 600 words or more in
// sections of about 400. The Professional prompt is about 3,300 characters. Measured October 14,
// 2026: a single 900-word unpunctuated request did not exceed the context, but long text is slow
// (about 3 s per 100 words), and a context error would only fall back to local rules. Without FoundationModels (Linux checks) the model reports itself unavailable.

struct AppleCleanupProvider: CleanupProvider {
    let id = ProviderID.appleIntelligence
    /// Nil when the model can run. Checks inject a fixed status.
    var status: @Sendable () -> OnDeviceModelUnavailable? = AppleSystemModel.status
    /// One generation, no retry. Checks inject a fake model.
    var generate: @Sendable (CleanupMessages) async throws -> String = AppleSystemModel.respond
    /// ponytail: fixed caps keep prompt + a 400-word section inside Apple's ~4K-token window; size by token count if it still overflows.
    var shape: CleanupShape { CleanupShape(contextCaps: .init(selected: 400, clipboard: 400, window: 1_000), sectionTarget: 400) }
    var disclosure: DataDisclosure {
        DataDisclosure(provider: id, stage: .cleanup, destinationHost: "Apple's on-device model", leavesDevice: [],
                       staysOnDevice: ["the transcript"], retentionNote: "")
    }

    func cleanup(_ messages: CleanupMessages) async throws -> String {
        if let reason = status() { throw ProviderError.modelUnavailable(id, reason) }
        return try await generate(messages)
    }

    func validate() async throws {
        if let reason = status() { throw ProviderError.modelUnavailable(id, reason) }
    }

    /// Loads the model ahead of the first request (route selected, recording started).
    func prewarm() { if status() == nil { AppleSystemModel.prewarm() } }
}

enum AppleSystemModel {
    /// FoundationModels needs macOS 26; on older systems the route is unavailable and hidden.
    static var isSupported: Bool {
#if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return true }
#endif
        return false
    }

    @Sendable static func status() -> OnDeviceModelUnavailable? {
#if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return Native.status() }
#endif
        return .deviceNotEligible
    }

    static func prewarm() {
#if canImport(FoundationModels)
        if #available(macOS 26.0, *) { Native.prewarm() }
#endif
    }

    @Sendable static func respond(_ messages: CleanupMessages) async throws -> String {
#if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return try await Native.respond(messages) }
#endif
        throw ProviderError.modelUnavailable(.appleIntelligence, .deviceNotEligible)
    }

    @Sendable static func notes(system: String, user: String, lecture: Bool = false) async throws -> String {
#if canImport(FoundationModels)
        if #available(macOS 26.0, *) { return try await Native.notes(system: system, user: user, lecture: lecture) }
#endif
        throw ProviderError.modelUnavailable(.appleIntelligence, .deviceNotEligible)
    }

    static let maximumNotesTokens = 1_200
}

#if canImport(FoundationModels)
@available(macOS 26.0, *)
private enum Native {
    private static let lock = NSLock()
    /// Kept alive so the prewarm is not cancelled with its session.
    nonisolated(unsafe) private static var warm: LanguageModelSession?

    @Sendable static func status() -> OnDeviceModelUnavailable? {
        switch SystemLanguageModel.default.availability {
        case .available: return nil
        case .unavailable(.deviceNotEligible): return .deviceNotEligible
        case .unavailable(.appleIntelligenceNotEnabled): return .appleIntelligenceOff
        case .unavailable: return .modelNotReady
        }
    }

    static func prewarm() {
        let session = LanguageModelSession()
        session.prewarm()
        lock.withLock { warm = session }
    }

    /// A fresh session per call: a session keeps its transcript, which would fill the context.
    @Sendable static func respond(_ messages: CleanupMessages) async throws -> String {
        let session = LanguageModelSession(instructions: messages.system)
        do {
            return try await session.respond(to: messages.user, options: GenerationOptions(sampling: .greedy)).content
        } catch let error as LanguageModelSession.GenerationError {
            throw providerError(error)
        } catch is CancellationError {
            throw ProviderError.cancelled
        }
    }

    /// Meeting notes with guided generation: the on-device model breaks free-form formats, so a
    /// schema fixes the shape. The result is rendered in the notes reply format (MeetingNotes.swift),
    /// so grounding is the same on every route. A fresh session per call, like cleanup.
    /// Notes for one chunk are short. Without a cap, a few replies ran on for over 3 minutes
    /// (measured October 15, 2026). With a 5,000-character request (about 1,700 tokens) and the
    /// instructions, 1,200 still fits the 4,096-token context.
    static let maximumNotesTokens = AppleSystemModel.maximumNotesTokens

    @Sendable static func notes(system: String, user: String, lecture: Bool = false) async throws -> String {
        let int = DynamicGenerationSchema(type: Int.self), text = DynamicGenerationSchema(type: String.self)
        let cites = DynamicGenerationSchema.Property(name: "lines", description: "The L numbers of the lines it rests on", schema: DynamicGenerationSchema(arrayOf: int))
        let item = DynamicGenerationSchema(name: "Item", properties: [cites, .init(name: "text", schema: text)])
        let action = DynamicGenerationSchema(name: "Action", properties: [
            cites,
            .init(name: "owner", description: lecture ? "Leave empty" : "Who took it on, as named in the lines, or Me, or the other side’s speaker label as written in the lines (Them, Speaker 2 or a name)", schema: text, isOptional: true),
            .init(name: "due", description: lecture ? "Leave empty" : "When it is due, only if said", schema: text, isOptional: true),
            .init(name: "text", description: lecture ? "The perspective expressed" : "The task", schema: text),
        ])
        let root = DynamicGenerationSchema(name: "Notes", properties: [
            .init(name: "title", description: "At most 8 words, in sentence case: only the first word and names capitalized", schema: text),
            .init(name: "summary", description: "2 to 4 sentences", schema: text),
            // Generated in this order: decisions and action items before key points, so a reply that
            // reaches the token cap loses points, not commitments.
            .init(name: "decisions", description: lecture ? "Examples actually given in the lecture" : "Choices the group agreed on", schema: DynamicGenerationSchema(arrayOf: DynamicGenerationSchema(referenceTo: "Item"), maximumElements: 10)),
            .init(name: "actions", description: lecture ? "Perspectives explicitly expressed in the lecture" : "Tasks someone committed to or accepted", schema: DynamicGenerationSchema(arrayOf: DynamicGenerationSchema(referenceTo: "Action"), maximumElements: 12)),
            .init(name: "points", description: lecture ? "Concepts explained in the lecture" : "Key points, each a short phrase", schema: DynamicGenerationSchema(arrayOf: DynamicGenerationSchema(referenceTo: "Item"), maximumElements: 8)),
        ])
        let session = LanguageModelSession(instructions: system)
        let content: GeneratedContent
        do {
            content = try await session.respond(to: user, schema: try GenerationSchema(root: root, dependencies: [item, action]),
                                           options: GenerationOptions(sampling: .greedy, maximumResponseTokens: maximumNotesTokens)).content
        } catch let error as LanguageModelSession.GenerationError {
            throw providerError(error)
        } catch is CancellationError {
            throw ProviderError.cancelled
        } catch {
            throw ProviderError.malformedResponse(.appleIntelligence)
        }
        func one(_ value: String?) -> String { (value ?? "").replacingOccurrences(of: "|", with: "/").replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces) }
        func lines(_ entry: GeneratedContent) -> String { ((try? entry.value([Int].self, forProperty: "lines")) ?? []).map { "L\($0)" }.joined(separator: " ") }
        func entries(_ key: String) -> [GeneratedContent] { (try? content.value([GeneratedContent].self, forProperty: key)) ?? [] }
        var out: [String] = []
        if let title = try? content.value(String.self, forProperty: "title") { out.append("TITLE | " + one(title)) }
        if let summary = try? content.value(String.self, forProperty: "summary") { out.append("SUMMARY | " + one(summary)) }
        for (key, kind) in [("decisions", "DECISION"), ("points", "POINT")] {
            for entry in entries(key) { out.append("\(kind) | \(lines(entry)) | " + one(try? entry.value(String.self, forProperty: "text"))) }
        }
        for entry in entries("actions") {
            let owner = one((try? entry.value(String?.self, forProperty: "owner")) ?? nil), due = one((try? entry.value(String?.self, forProperty: "due")) ?? nil)
            out.append("ACTION | \(lines(entry)) | \(owner.isEmpty ? "-" : owner) | \(due.isEmpty ? "-" : due) | " + one(try? entry.value(String.self, forProperty: "text")))
        }
        return out.joined(separator: "\n")
    }

    static func providerError(_ error: LanguageModelSession.GenerationError) -> ProviderError {
        let id = ProviderID.appleIntelligence
        switch error {
        case .exceededContextWindowSize: return .payloadTooLarge(id, limitBytes: nil)
        case .assetsUnavailable: return .modelUnavailable(id, .modelNotReady)
        case .guardrailViolation, .refusal: return .contentBlocked(id)
        case .rateLimited, .concurrentRequests: return .rateLimited(id, retryAfter: nil)
        default: return .malformedResponse(id)
        }
    }
}
#endif
