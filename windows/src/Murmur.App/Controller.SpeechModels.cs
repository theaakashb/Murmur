using Murmur.Core;

namespace Murmur.App;

/// <summary>The local speech models and the one this setup needs (app/Controller+Models.swift, "speechModelSlot" and the required-model members).
/// English keeps its folder choice; Parakeet TDT v3 and Whisper large-v3-turbo live in their slots' pinned folders. Settings and the welcome guide bind to the public members.</summary>
internal sealed partial class Controller
{

    public ModelSlot SpeechModelSlot(LocalSpeechModel model) => s.SlotFor(model);

    public bool SpeechModelDownloading => s.SpeechSlot.InFlight || s.TdtSlot.InFlight || s.WhisperSlot.InFlight;

    /// <summary>The model the chosen languages need, whatever the route.</summary>
    public LocalSpeechModel ChosenSpeechModel => s.ChosenSpeechModel;

    /// <summary>The model this setup needs on this PC: the chosen one on the local route, else English (the cloud routes need no speech model).</summary>
    public LocalSpeechModel RequiredSpeechModel => s.RequiredSpeechModel;

    public ModelSlot RequiredSpeechSlot => SpeechModelSlot(RequiredSpeechModel);

    /// <summary>English: the chosen model folder; the others: the pinned folder, or null while the model has no pins.</summary>
    public string? SpeechModelPath(LocalSpeechModel model) => s.Local.DirectoryFor(model);

    /// <summary>True when the model's files are on disk. English counts the chosen folder; the others need a downloaded, pinned slot.</summary>
    public bool SpeechModelReady(LocalSpeechModel model) => model == LocalSpeechModel.ParakeetEnglish ? s.Local.EnglishFilesPresent : SpeechModelSlot(model).Row.IsReady && s.Local.FilesPresent(model);

    /// <summary>The confirmation dialogs the UI supplies (the license-and-size and remove confirmations); without them a download or remove does not start.</summary>
    public IModelFlowDialogs? SpeechModelDialogs { get; set; }

    /// <summary>Raised after a speech model slot changes (progress, completion, removal), on the UI thread.</summary>
    public Action? SpeechModelChanged { get; set; }

    /// <summary>The flow of a speech model's row: the same gates and release as the Settings rows, plus a prewarm once the required model arrives.</summary>
    ModelFlow SpeechFlow(LocalSpeechModel model)
    {
        var slot = SpeechModelSlot(model);
        void Forget() { if (slot.Downloader is { } downloader) ModelVerificationCache.Shared.Forget(downloader.Target); }
        return new ModelFlow(slot, SpeechModelDialogs ?? NoDialogs.Instance)
        {
            PushProgress = true,
            Post = a => s.UI.TryEnqueue(() => a()),
            Changed = () => SpeechModelChanged?.Invoke(),
            DownloadGate = () => ModelGate.DownloadRefusal(IsBusy, false, false),
            RemoveGate = () => ModelGate.RemoveRefusal(IsBusy, false, slot.InFlight),
            DidDownload = error => { Forget(); if (error is null && RequiredSpeechModel == model) PrewarmSpeech(); },
            Release = go => { s.Local.Release(); go(); },
            DidRemove = Forget,
        };
    }

    /// <summary>Asks <see cref="SpeechModelDialogs"/> for the license-and-size confirmation, then downloads the required model.</summary>
    public void DownloadRequiredSpeechModel() => SpeechFlow(RequiredSpeechModel).Download();

    public void CancelRequiredSpeechModel() => RequiredSpeechSlot.Downloader?.Cancel();

    public void RemoveRequiredSpeechModel() => SpeechFlow(RequiredSpeechModel).Remove();

    sealed class NoDialogs : IModelFlowDialogs
    {
        public static readonly NoDialogs Instance = new();
        public bool ConfirmDownload(string title, string body) => false;
        public bool ConfirmRemove(string title, string body) => false;
    }

    /// <summary>Loads the required model into the warm helper in the background, so the next dictation does not wait for it. Does nothing until the model's files are there.</summary>
    public void PrewarmSpeech()
    {
        var model = RequiredSpeechModel;
        if (!SpeechModelReady(model)) return;
        _ = s.Local.PrewarmAsync(model);
    }
}
