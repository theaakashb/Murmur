// Port of app/ModelFlow.swift.
// The download, cancel and remove flow every downloadable model shares: the owner supplies the model's ModelSlot and the few steps that differ
// (refusals, what to refresh before and after). The Swift file shows NSAlerts; here the window supplies <see cref="IModelFlowDialogs"/>.
namespace Murmur.Core;

/// <summary>The two confirmations the flow asks the user for. Each returns true when the user chose the button that goes ahead.</summary>
public interface IModelFlowDialogs
{
    /// <summary>"Download" or "Cancel": the license and size are shown before any byte is fetched.</summary>
    bool ConfirmDownload(string title, string body);
    /// <summary>"Remove" or "Cancel" (a destructive confirmation).</summary>
    bool ConfirmRemove(string title, string body);
}

public sealed class ModelFlow(ModelSlot slot, IModelFlowDialogs dialogs)
{
    public ModelSlot Slot { get; } = slot;
    /// <summary>Why a download or removal may not start now (<see cref="ModelGate"/>); null means no gate for this model.</summary>
    public Func<string?> DownloadGate { get; init; } = () => null;
    public Func<string?> RemoveGate { get; init; } = () => null;
    /// <summary>After the state shows the download starting.</summary>
    public Action WillDownload { get; init; } = () => { };
    /// <summary>Also show the downloader's byte progress as it arrives (otherwise the owner samples it).</summary>
    public bool PushProgress { get; init; }
    /// <summary>Null error means success.</summary>
    public Action<ModelDownloadError?> DidDownload { get; init; } = _ => { };
    /// <summary>Runs the removal after the user confirmed; release what holds the files first, then call <c>go</c>.</summary>
    public Action<Action> Release { get; init; } = go => go();
    public Action DidRemove { get; init; } = () => { };
    /// <summary>Raised after every change to the slot's state, on the UI thread when <see cref="Post"/> marshals there.</summary>
    public Action Changed { get; init; } = () => { };
    /// <summary>Runs an action on the UI thread. Download progress and completion arrive on a worker thread and go through this.</summary>
    public Action<Action> Post { get; init; } = a => a();

    void Update(Action<ModelSlot> change) { change(Slot); Changed(); }

    public void Download()
    {
        if (Slot.Downloader is not { } downloader) { Update(s => s.Message = ModelDownloadError.InvalidManifest.UserMessage); return; }
        if (DownloadGate() is { } refusal) { Update(s => s.Message = refusal); return; }
        if (Slot.InFlight) return;
        var text = Slot.Facts.Confirmation(downloader.DownloadSize, downloader.License, downloader.Target);
        if (!dialogs.ConfirmDownload(text.Title, text.Body)) return;
        // A dictation may have started while the dialog was open.
        if (DownloadGate() is { } late) { Update(s => s.Message = late); return; }
        Update(s => { s.InFlight = true; s.Message = ""; });
        WillDownload();
        Update(s => s.Status = ModelStatus.Downloading(new(0, downloader.DownloadSize)));
        var facts = Slot.Facts;
        downloader.Start(PushProgress ? progress => Post(() => { if (Slot.InFlight) Update(s => s.Status = ModelStatus.Downloading(progress)); }) : null, error => Post(() =>
        {
            Update(s => { s.InFlight = false; s.Status = downloader.Status(); s.Message = facts.CompletionMessage(error); });
            DidDownload(error);
        }));
    }

    public void Cancel() => Slot.Downloader?.Cancel();

    public void Remove()
    {
        if (Slot.Downloader is not { } downloader) return;
        if (RemoveGate() is { } refusal) { Update(s => s.Message = refusal); return; }
        if (Slot.InFlight) return;
        var facts = Slot.Facts;
        if (!dialogs.ConfirmRemove(facts.RemoveTitle, facts.RemoveBody(downloader.Target, downloader.DownloadSize))) return;
        if (RemoveGate() is { } late) { Update(s => s.Message = late); return; }
        Release(() =>
        {
            try { downloader.Remove(); Update(s => s.Message = facts.Removed); }
            catch (ModelDownloadException e) { Update(s => s.Message = e.Error.UserMessage); }
            Update(s => s.Status = downloader.Status());
            DidRemove();
        });
    }
}
