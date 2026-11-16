import Foundation

// The managed local cleanup model's Settings text (the provider and helper client are in LocalCleanup.swift); needs ModelDownload.swift and AppServices.swift.
enum LocalCleanupFacts {
    static let repository = "heyvedu/vedu-scribe-0.8b"
    static func directory(home: URL) -> URL { LocalCleanupIdentity.directory(home: home) }
    static let modelName = LocalCleanupIdentity.modelName
    static func summary(downloadSize: Int64) -> String {
        "\(modelName) · \(ModelFacts.formatBytes(downloadSize)) download · English · Apache-2.0 · \(ModelFacts.location)"
    }
    static let explanation = "Cleans up each dictation on this Mac; the text never leaves it. It does not use screen context, custom writing instructions or rewrite presets, and it can miss numbered lists. Those cases fall back to Local rules."
    static let removedGeminiNotice = "Gemini cleanup was removed. Cleanup is now Local rules; choose Local model or Custom Provider if you want a model."
    static func confirmation(downloadSize: Int64, license: ModelLicenseInfo) -> (title: String, body: String) {
        ("Download \(modelName) (\(ModelFacts.formatBytes(downloadSize)))?",
         "Murmur downloads \(ModelFacts.formatBytes(downloadSize)) from Hugging Face and checks every file against its pinned checksum. Cleanup then runs on this Mac without the network.\n\nLicense:\n\(ModelFacts.licenseText(license))")
    }
    static let removeTitle = "Remove the local cleanup model?"
    static let removeBody = "Cleanup falls back to Local rules until you download it again."
}
