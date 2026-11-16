// AppController: API keys and the custom API provider.
import Foundation

extension AppController {
    // MARK: API keys (R5) and custom API provider

    /// Publishes which providers have a key. The key itself never leaves the Keychain store.
    func refreshKeys() { savedKeys = Set(KeyText.providers.filter { credentials.exists($0) }) }
    func customAPIKeyNeedsConfirmation(_ provider: ProviderID) -> Bool {
        let address = provider == .customTranscription ? customTranscriptionAPIBaseURL : customAPIBaseURL
        guard savedKeys.contains(provider), let destination = try? CustomAPIKeyBinding.endpoint(address) else { return false }
        return UserDefaults.standard.string(forKey: CustomAPIKeyBinding.preference(provider)) != destination
    }
    private func customAPIAddress(_ provider: ProviderID) -> String? {
        switch provider {
        case .customTranscription: return customTranscriptionAPIBaseURL
        case .customCleanup: return customAPIBaseURL
        default: return nil
        }
    }
    @discardableResult
    func saveKey(_ provider: ProviderID, _ raw: String) -> Bool {
        guard let key = APIKey(raw) else { keyMessages[provider] = KeyText.invalid; return false }
        do {
            if let address = customAPIAddress(provider) { try CustomAPIKeyBinding.save(key, at: address, defaults: .standard, credentials: credentials, provider: provider) }
            else { try credentials.save(key, for: provider) }
            keyMessages[provider] = KeyText.saved
        }
        catch { keyMessages[provider] = KeyText.storeFailed; return false }
        refreshKeys(); return true
    }
    func removeKey(_ provider: ProviderID) {
        do {
            try credentials.delete(provider)
            if customAPIAddress(provider) != nil { UserDefaults.standard.removeObject(forKey: CustomAPIKeyBinding.preference(provider)) }
            keyMessages[provider] = KeyText.removed
        } catch { keyMessages[provider] = KeyText.removeFailed }
        refreshKeys()
    }
    func validateKey(_ provider: ProviderID) {
        let key: APIKey?
        do {
            if let address = customAPIAddress(provider) {
                key = try CustomAPIKeyBinding.read(at: address, defaults: .standard, credentials: credentials, provider: provider)
            } else { key = try credentials.read(provider) }
        } catch { keyMessages[provider] = (error as? CredentialError)?.description ?? KeyText.unreadable; return }
        keyMessages[provider] = KeyText.checking
        let endpoint = customAPIAddress(provider) ?? customAPIBaseURL
        Task { @MainActor [weak self] in
            let message = await KeyCheck.run(provider, key: key, apiBaseURL: endpoint)
            self?.keyMessages[provider] = message
        }
    }
}
