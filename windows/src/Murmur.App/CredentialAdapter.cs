using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>Core's <see cref="ICredentialStore"/> over Credential Manager: the account name is the provider's raw id (spec/settings.md).</summary>
internal sealed class CredentialAdapter(CredentialManagerStore store) : ICredentialStore
{
    static T Wrap<T>(Func<T> work)
    {
        try { return work(); }
        catch (CredentialStoreException) { throw new CredentialException(CredentialError.Keychain(0)); }
    }

    public APIKey? Read(ProviderID provider) => Wrap(() => store.Read(provider.RawValue()) is { } secret ? APIKey.Create(secret) : null);
    public void Save(APIKey key, ProviderID provider) => Wrap(() => { store.Save(provider.RawValue(), key.Secret); return 0; });
    public void Delete(ProviderID provider) => Wrap(() => { store.Delete(provider.RawValue()); return 0; });
    public void DeleteAll() => Wrap(() => { store.DeleteAll(); return 0; });
    public bool Exists(ProviderID provider) => Wrap(() => store.Exists(provider.RawValue()));
}
