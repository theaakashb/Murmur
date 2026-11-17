import Foundation

// API key UI strings and key validation.

enum KeyText {
    static let saved = "Key saved"
    static let none = "No key saved"
    static let invalid = "That doesn't look like an API key: it is empty or has spaces or unsupported characters. Nothing was saved."
    static let storeFailed = "The key could not be saved to the Keychain."
    static let removeFailed = "The key could not be removed from the Keychain."
    static let removed = "Key removed"
    static let checking = "Checking…"
    static let missing = "Save a key first."
    static let unreadable = "The saved key could not be read from the Keychain."

    /// Only active provider credentials appear in Settings; legacy accounts remain available for migration.
    static let providers: [ProviderID] = [.customTranscription, .customCleanup]

    static func validated(_ provider: ProviderID) -> String {
        provider == .localServer ? "The local model server answered." : provider == .customTranscription || provider == .customCleanup || provider == .customAPI ? "The custom API endpoint answered." : "\(provider.displayName) accepted the key."
    }
}

enum KeyCheck {
    /// One validation request (no tokens used). Returns a message with no key or response body.
    static func run(_ provider: ProviderID, key: APIKey?, apiBaseURL: String, transport: HTTPTransport? = nil) async -> String {
        do {
            switch provider {
            case .customAPI, .customTranscription, .customCleanup:
                var p = try OpenAICompatibleCleanupProvider(baseURL: apiBaseURL, model: "validation", apiKey: key, provider: .customAPI)
                if let transport { p.transport = transport }
                try await p.validate()
            case .localServer:
                var p = try OpenAICompatibleCleanupProvider(baseURL: apiBaseURL, model: "validation", apiKey: key); if let transport { p.transport = transport }
                try await p.validate()
            case .appleIntelligence:
                return AppleCleanupProvider().status()?.userMessage ?? "Apple Intelligence is ready."
            case .localModel:
                return LocalModelCleanupProvider().status()?.userMessage ?? "The local cleanup model is ready."
            case .cerebras, .openrouter:
                // Legacy key accounts only; the migration copies them to the custom endpoint.
                return ProviderError.invalidConfiguration(provider).userMessage
            }
            return KeyText.validated(provider)
        } catch let error as ProviderError {
            return error.userMessage
        } catch {
            return ProviderError.network(provider).userMessage
        }
    }

    /// Lists a local server's models. Sends no transcript.
    static func discoverModels(serverURL: String, key: APIKey?, transport: HTTPTransport? = nil) async -> Result<[String], ProviderError> {
        do {
            var p = try OpenAICompatibleCleanupProvider(baseURL: serverURL, model: "discovery", apiKey: key)
            if let transport { p.transport = transport }
            return .success(try await p.discoverModels())
        } catch let error as ProviderError {
            return .failure(error)
        } catch {
            return .failure(.network(.localServer))
        }
    }

    static func discoveryMessage(_ result: Result<[String], ProviderError>) -> String {
        switch result {
        case .success(let models) where models.isEmpty: return "The server answered but lists no models. Load or pull a model, then try again."
        case .success(let models): return "Found \(models.count) model\(models.count == 1 ? "" : "s")."
        case .failure(let error): return error.userMessage
        }
    }
}
