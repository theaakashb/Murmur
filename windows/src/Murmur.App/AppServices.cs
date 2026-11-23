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
    public LocalTranscriber Local { get; }
    /// <summary>The downloadable models under <c>&lt;data root&gt;\Models</c> (one slot each; Settings shows them, the helpers read them).</summary>
    public ModelSlot SpeechSlot { get; }
    public ModelSlot LiveWordsSlot { get; }
    /// <summary>Parakeet TDT v3 and Whisper large-v3-turbo, for dictation in other languages. No downloader until their pins exist.</summary>
    public ModelSlot TdtSlot { get; }
    public ModelSlot WhisperSlot { get; }
    public ModelSlot CleanupSlot { get; }
    /// <summary>Speaker separation downloads two models (segmentation and embedding); the feature needs both.</summary>
    public ModelSlot SpeakerSegmentationSlot { get; }
    public ModelSlot SpeakerEmbeddingSlot { get; }
    /// <summary>Stored by the dictation-language picker (default English).</summary>
    public IReadOnlyList<string> DictationLanguageCodes => Settings.Get(Prefs.DictationLanguages);
    public LocalSpeechModel ChosenSpeechModel => DictationLanguages.Create(DictationLanguageCodes)?.LocalModel ?? LocalSpeechModel.ParakeetEnglish;
    /// <summary>The chosen model on the local route, else English (the cloud routes need no speech model).</summary>
    public LocalSpeechModel RequiredSpeechModel => RouteSettings.Load(Settings).Transcription == TranscriptionRoute.Local ? ChosenSpeechModel : LocalSpeechModel.ParakeetEnglish;
    /// <summary>The slot of a local speech model (Controller+Models.swift speechModelSlot).</summary>
    public ModelSlot SlotFor(LocalSpeechModel model) => model switch { LocalSpeechModel.ParakeetTDT => TdtSlot, LocalSpeechModel.WhisperTurbo => WhisperSlot, _ => SpeechSlot };
    /// <summary>Gemma 3 4B, the on-device meeting notes model (Settings › Meetings).</summary>
    public ModelSlot LocalNotesSlot { get; }
    /// <summary>Raised on the UI thread when "Show in taskbar" (Prefs.ShowInDock) changes; the main window re-applies it.</summary>
    public event Action? ShowInTaskbarChanged;
    public void NotifyShowInTaskbarChanged() => ShowInTaskbarChanged?.Invoke();
    public HistoryRouter<HistoryEntry> History { get; private set; } = null!;
    /// <summary>Null when the dictionary or ledger can't be read: only cleanup and the dictionary stop, and dictations are held in History (app/Controller.swift:348-351).</summary>
    public ProcessingCoordinator? Processing { get; private set; }
    public DictionaryStore? Dictionary => Processing?.Dictionary;
    public string? DataProblem { get; private set; }
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
        Processor = new CoordinatorProcessor(() => Processing);
        SpeechSlot = ModelSlot.Create(ModelFacts.Batch, Root);
        LiveWordsSlot = ModelSlot.Create(ModelFacts.LiveWords, Root);
        TdtSlot = ModelSlot.Create(ModelFacts.MultilingualTDT, Root);
        WhisperSlot = ModelSlot.Create(ModelFacts.MultilingualWhisper, Root);
        CleanupSlot = ModelSlot.Create(ModelFacts.LocalCleanup, Root);
        SpeakerSegmentationSlot = ModelSlot.Create(ModelFacts.SpeakerSegmentation, Root);
        SpeakerEmbeddingSlot = ModelSlot.Create(ModelFacts.SpeakerEmbedding, Root);
        LocalNotesSlot = ModelSlot.Create(ModelFacts.LocalNotes, Root);
        foreach (var slot in new[] { SpeechSlot, TdtSlot, WhisperSlot, LiveWordsSlot, CleanupSlot, SpeakerSegmentationSlot, SpeakerEmbeddingSlot, LocalNotesSlot }) if (slot.Downloader is { } downloader) slot.Status = downloader.Status();
        ConfigureCleanupRunner();
        // Whether a GPU can run the notes model: asked once, off the UI thread, so the no-GPU warning is known by the time Settings or the download dialog needs it.
        if (!testing) _ = NotesGpu.Available();
        Local = new LocalTranscriber(this);
        ReopenData();
        PurgeAtLaunch();
        // Hash the pinned speech model now, off the UI thread, so the first dictation does not wait for it.
        if (RouteSettings.Load(Settings).Transcription == TranscriptionRoute.Local) _ = Task.Run(Local.Verify);
    }

    /// <summary>Points the cleanup runner at this data root (it defaults to the real one, not a test run's) and checks the pinned checksums before the helper first reads the model.</summary>
    void ConfigureCleanupRunner()
    {
        LocalCleanupRunner.Shared.PreferGpu = () => Settings.Get(Prefs.UseGpuWhenAvailable);
        if (CleanupSlot is not { Downloader: { } downloader, Manifest: { } manifest }) return;
        LocalCleanupRunner.ModelDirectory = downloader.Target;
        LocalCleanupRunner.Verifier = () =>
        {
            try { ModelVerificationCache.Shared.Verify(downloader.Target, manifest); return true; }
            catch (RetentionError) { return false; }
        };
    }

    /// <summary>(Re)creates the History and dictionary stores over their folders, after the folders were deleted or replaced.</summary>
    public void ReopenData()
    {
        PrivateFiles.PrepareDirectory(Locations.RetryAudio);
        PrivateFiles.PrepareDirectory(Locations.Dictionary);
        store = new HistoryStore(Locations.History);
        History = new HistoryRouter<HistoryEntry>(new HistoryDisk<HistoryEntry>(store.Save, () => store.Entries(), store.Delete), Settings.Get(Prefs.HistoryMode).PersistsTranscripts());
        try { Processing = new ProcessingCoordinator(Locations.Dictionary, Settings, Lexicon.English.IsCommonWord); DataProblem = null; }
        catch (Exception) { Processing = null; DataProblem = "The dictionary could not be read. Dictations are held in History until it is repaired."; }
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

    public async Task<WritingMode?> SelectModeAsync(Task<FocusInfo?> focusTask, CancellationToken ct)
    {
        var focus = await focusTask.ConfigureAwait(false);
        if (focus is null or { IsPassword: true } || ct.IsCancellationRequested) return null;
        List<WritingMode> modes;
        try { modes = WritingModes.Load(Settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or StoreError) { return null; }
        var app = WritingModes.Match(modes, focus.ProcessName, null);
        if (!WritingModes.NeedsURL(modes)) return app;
        try
        {
            return await Task.Run(() => WritingModes.Match(modes, focus.ProcessName, FocusProbe.ReadBrowserPage(focus, ct)), ct)
                .WaitAsync(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return app; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
    }

    public APIKey? Key(ProviderID provider)
    {
        try { return CustomAPIKeyBinding.ReadKey(provider, RouteSettings.Load(Settings), Settings, Credentials); }
        catch (CredentialException) { return null; }
    }
}
