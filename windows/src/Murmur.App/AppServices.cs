using Microsoft.UI.Dispatching;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>The composition root: data folder, settings, credentials, History and the dictionary. Delete All and Restore call <see cref="ReopenData"/>.</summary>
internal sealed class AppServices
{
    public string Root { get; }
    public MurmurDataLocations Locations { get; }
    public ISettingsStore Settings { get; }
    public ICredentialStore Credentials { get; }
    public ClipboardPaste Paste { get; } = new();
    public DispatcherQueue UI { get; }
    public IDictationProcessor Processor { get; }
    public HistoryRouter<HistoryEntry> History { get; private set; } = null!;
    public DictionaryStore Dictionary { get; private set; } = null!;
    HistoryStore store = null!;

    public AppServices(DispatcherQueue ui)
    {
        UI = ui;
        // MURMUR_DATA_DIR (spec/test-hooks.md) replaces the data root; its credentials stay in memory so a test run never touches the real Credential Manager.
        var overrideDir = Environment.GetEnvironmentVariable("MURMUR_DATA_DIR");
        var testing = !string.IsNullOrWhiteSpace(overrideDir);
        Root = testing ? Path.GetFullPath(overrideDir!) : MurmurDataLocations.Root;
        PrivateFiles.PrepareDirectory(Root);
        Locations = MurmurDataLocations.Standard(Root);
        TestEvents.Init(Root);
        Settings = new JsonSettingsStore(Path.Combine(Root, JsonSettingsStore.FileName));
        Credentials = testing ? new InMemoryCredentialStore() : new CredentialAdapter(new CredentialManagerStore());
        Processor = new RulesOnlyProcessor(() => Dictionary);
        ReopenData();
        PurgeAtLaunch();
    }

    /// <summary>(Re)creates the History and dictionary stores over their folders, after the folders were deleted or replaced.</summary>
    public void ReopenData()
    {
        PrivateFiles.PrepareDirectory(Locations.RetryAudio);
        PrivateFiles.PrepareDirectory(Locations.Dictionary);
        store = new HistoryStore(Locations.History);
        History = new HistoryRouter<HistoryEntry>(new HistoryDisk<HistoryEntry>(store.Save, () => store.Entries(), store.Delete), Settings.Get(Prefs.HistoryMode).PersistsTranscripts());
        Dictionary = new DictionaryStore(Locations.Dictionary);
    }

    /// <summary>Retention purge, retry audio older than 24 hours, and the transient chunks folder (spec/settings.md).</summary>
    void PurgeAtLaunch()
    {
        try { store.Purge(Settings.Get(Prefs.Retention)); } catch (Exception) { /* a purge problem must not stop the app */ }
        try
        {
            foreach (var file in Directory.EnumerateFiles(Locations.RetryAudio))
                if (!FileLinks.IsLink(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromSeconds(RetryWindow.LifetimeSeconds)) File.Delete(file);
            if (Directory.Exists(Locations.Chunks)) FileLinks.DeleteTree(Locations.Chunks);
        }
        catch (Exception) { }
    }

    /// <summary>History switches take effect for new entries; existing ones stay where they are (HistoryRouter).</summary>
    public void ApplyHistoryMode() => History.PersistsTranscripts = Settings.Get(Prefs.HistoryMode).PersistsTranscripts();

    public APIKey? Key(ProviderID provider)
    {
        try { return CustomAPIKeyBinding.ReadKey(provider, RouteSettings.Load(Settings), Settings, Credentials); }
        catch (CredentialException) { return null; }
    }
}
