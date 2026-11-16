import Foundation

// The managed local cleanup model's download facts (the provider and helper client are in LocalCleanup.swift); needs ModelText.swift.
extension ModelFacts {
    static let localCleanup = ModelFacts(
        displayName: LocalCleanupIdentity.modelName,
        repository: "heyvedu/vedu-scribe-0.8b",
        directory: LocalCleanupIdentity.directory,
        confirmName: LocalCleanupIdentity.modelName,
        summaryLead: LocalCleanupIdentity.modelName,
        summaryTail: ["English", "Apache-2.0"],
        placement: { _ in "Cleanup then runs on this Mac without the network." },
        removeTitle: "Remove the local cleanup model?",
        removeBody: { _, _ in "Cleanup falls back to Local rules until you download it again." },
        completed: "The model is downloaded and verified.",
        removed: "The local cleanup model was removed.")
}

enum LocalCleanupFacts {
    static let removedGeminiNotice = "Gemini cleanup was removed. Cleanup is now Local rules; choose Local model or Custom Provider if you want a model."
}
