using Murmur.Core;

namespace Murmur.App;

/// <summary>The local speech models and the one this setup needs (app/Controller+Models.swift, "speechModelSlot" and the required-model members).
/// English keeps its folder choice; Parakeet TDT v3 and Whisper large-v3-turbo live in their slots' pinned folders. Settings and the welcome guide bind to the public members.</summary>
internal sealed partial class Controller
{
    /// <summary>Stored by the dictation-language picker (Prefs.dictationLanguages on the Mac; default English).</summary>
    IReadOnlyList<string> DictationLanguageCodes => s.Settings.Get("dictationLanguages") is IReadOnlyList<string> codes ? codes : DictationLanguages.English.Codes.ToArray();

    public ModelSlot SpeechModelSlot(LocalSpeechModel model) => s.SlotFor(model);

    public bool SpeechModelDownloading => s.SpeechSlot.InFlight || s.TdtSlot.InFlight || s.WhisperSlot.InFlight;

    /// <summary>The model the chosen languages need, whatever the route.</summary>
    public LocalSpeechModel ChosenSpeechModel => DictationLanguages.Create(DictationLanguageCodes)?.LocalModel ?? LocalSpeechModel.ParakeetEnglish;

    /// <summary>The model this setup needs on this PC: the chosen one on the local route, else English (the cloud routes need no speech model).</summary>
    public LocalSpeechModel RequiredSpeechModel => RouteSettings.Load(s.Settings).Transcription == TranscriptionRoute.Local ? ChosenSpeechModel : LocalSpeechModel.ParakeetEnglish;

    public ModelSlot RequiredSpeechSlot => SpeechModelSlot(RequiredSpeechModel);

    /// <summary>English: the chosen model folder; the others: the pinned folder, or null while the model has no pins.</summary>
    public string? SpeechModelPath(LocalSpeechModel model) => s.Local.DirectoryFor(model);

    /// <summary>True when the model's files are on disk. English counts the chosen folder; the others need a downloaded, pinned slot.</summary>
    public bool SpeechModelReady(LocalSpeechModel model) => model == LocalSpeechModel.ParakeetEnglish ? s.Local.ModelFilesPresent : SpeechModelSlot(model).Row.IsReady && s.Local.FilesPresent(model);

    /// <summary>The flow of a speech model's row: the same gates and release as the Settings rows, plus a prewarm once the required model arrives.</summary>
    ModelFlow SpeechFlow(LocalSpeechModel model, IModelFlowDialogs dialogs, Action? changed)
    {
        var slot = SpeechModelSlot(model);
        void Forget() { if (slot.Downloader is { } downloader) ModelVerificationCache.Shared.Forget(downloader.Target); }
        return new ModelFlow(slot, dialogs)
        {
            PushProgress = true,
            Post = a => s.UI.TryEnqueue(() => a()),
            Changed = () => changed?.Invoke(),
            DownloadGate = () => ModelGate.DownloadRefusal(IsBusy, false, false),
            RemoveGate = () => ModelGate.RemoveRefusal(IsBusy, false, slot.InFlight),
            DidDownload = error => { Forget(); if (error is null && RequiredSpeechModel == model) PrewarmSpeech(); },
            Release = go => { s.Local.Release(); go(); },
            DidRemove = Forget,
        };
    }

    /// <summary>Asks <paramref name="dialogs"/> for the license-and-size confirmation, then downloads the required model.</summary>
    public void DownloadRequiredSpeechModel(IModelFlowDialogs dialogs, Action? changed = null) => SpeechFlow(RequiredSpeechModel, dialogs, changed).Download();

    public void CancelRequiredSpeechModel() => RequiredSpeechSlot.Downloader?.Cancel();

    public void RemoveRequiredSpeechModel(IModelFlowDialogs dialogs, Action? changed = null) => SpeechFlow(RequiredSpeechModel, dialogs, changed).Remove();

    /// <summary>Loads the required model into the warm helper in the background, so the next dictation does not wait for it. Does nothing until the model's files are there.</summary>
    public void PrewarmSpeech()
    {
        var model = RequiredSpeechModel;
        if (!SpeechModelReady(model)) return;
        _ = s.Local.PrewarmAsync(model);
    }
}
