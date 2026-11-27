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
    /// <summary>Post sampled download and verification progress to the owner, up to ten times a second.</summary>
    public bool PushProgress { get; init; }
    /// <summary>Null error means success.</summary>
    public Action<ModelDownloadError?> DidDownload { get; init; } = _ => { };
    /// <summary>On a worker thread, release what holds the files after confirmation, then call <c>go</c>.</summary>
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
        Update(s => { s.InFlight = true; s.Message = ""; s.Status = ModelStatus.Downloading(new(0, downloader.DownloadSize)); });
        WillDownload();
        var facts = Slot.Facts;
        var pending = 0;
        var finished = false;
        var progress = PushProgress ? new Timer(_ =>
        {
            if (downloader.ActiveStatus is null || Interlocked.Exchange(ref pending, 1) != 0) return;
            Post(() =>
            {
                try
                {
                    if (!finished && downloader.ActiveStatus is { } latest && latest != Slot.Status)
                        Update(s => s.Status = latest);
                }
                finally { Interlocked.Exchange(ref pending, 0); }
            });
        }, null, 100, 100) : null;
        downloader.Start(null, error =>
        {
            if (progress is not null) progress.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var status = downloader.Status();
            // The new model is verified: an earlier release's folder is no longer needed.
            if (error is null) foreach (var old in Slot.RetiredFolders()) { try { System.IO.Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Post(() =>
            {
                finished = true;
                Update(s => { s.InFlight = false; s.Status = status; s.Message = facts.CompletionMessage(error); });
                DidDownload(error);
            });
        });
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
        Update(s => { s.InFlight = true; s.Message = ""; });
        _ = Task.Run(() =>
        {
            void Complete(ModelDownloadError? error)
            {
                var status = downloader.Status();
                Post(() =>
                {
                    Update(s => { s.InFlight = false; s.Status = status; s.Message = error?.UserMessage ?? facts.Removed; });
                    DidRemove();
                });
            }

            try
            {
                Release(() =>
                {
                    downloader.Remove();
                    Complete(null);
                });
            }
            catch (Exception e) { Complete(ModelDownloadError.FromWrite(e)); }
        });
    }
}
