// AppController: API keys and the custom API provider.
import Foundation

extension AppController {
    // MARK: API keys and custom API provider

    /// Publishes which providers have a key. The key itself never leaves the Keychain store.
    func refreshKeys() { savedKeys = Set(KeyText.providers.filter { credentials.exists($0) }) }
    func customAPIKeyNeedsConfirmation(_ provider: ProviderID) -> Bool {
        guard savedKeys.contains(provider), let address = customAPIAddress(provider) else { return false }
        return CustomAPIKeyBinding.needsConfirmation(at: address, defaults: .standard, provider: provider)
    }
    private func customAPIAddress(_ provider: ProviderID) -> String? { routeSettings.apiBaseURL(for: provider) }
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
            if customAPIAddress(provider) != nil { CustomAPIKeyBinding.clear(defaults: .standard, provider: provider) }
            keyMessages[provider] = KeyText.removed
        } catch { keyMessages[provider] = KeyText.removeFailed }
        refreshKeys()
    }
    func validateKey(_ provider: ProviderID) {
        let key: APIKey?
        do { key = try CustomAPIKeyBinding.readKey(for: provider, routes: routeSettings, defaults: .standard, credentials: credentials) } catch { keyMessages[provider] = (error as? CredentialError)?.description ?? KeyText.unreadable; return }
        keyMessages[provider] = KeyText.checking
        let endpoint = customAPIAddress(provider) ?? customAPIBaseURL
        Task { @MainActor [weak self] in
            let message = await KeyCheck.run(provider, key: key, apiBaseURL: endpoint)
            self?.keyMessages[provider] = message
        }
    }
}
