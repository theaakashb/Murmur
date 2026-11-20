using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

internal sealed partial class Controller
{
    sealed record ActiveEdit(VoiceEditSession Edit, IVoiceEditTarget Target, CancellationTokenSource Cancellation);
    sealed record EditProbe(VoiceEdit.SessionResult Result, ActiveEdit? Active);

    readonly EditShortcutCapture editLatch = new();
    sealed record EditBinding(bool SameKey, EditChord? Separate);
    volatile EditBinding editBinding = new(true, null);
    Task<EditProbe>? editProbe;
    bool separateEditStart;
    double editPressedAt;

    void ReloadVoiceEditShortcut()
    {
        string spec = s.Settings.Get(Prefs.VoiceEditShortcut);
        editBinding = new(spec == EditTrigger.SameKey, VoiceEditBinding.Parse(spec, dictationVk));
    }

    bool HandleVoiceEditKey(in KeyEvent e)
    {
        var action = editLatch.Handle(editBinding.Separate, new EditChord(e.VirtualKey, (int)e.Modifiers),
            VoiceEditBinding.Modifiers, e.IsDown, isRepeat: false,
            () => phase != Phase.Processing && !dictationDown);
        if (action is EditKeyAction.Press or EditKeyAction.Release)
        {
            double time = e.Timestamp / (double)Stopwatch.Frequency;
            ui.TryEnqueue(() =>
            {
                if (action == EditKeyAction.Release)
                {
                    Apply(VoiceEdit.ReleaseSeparate(machine, session?.SeparateEdit == true, time - editPressedAt));
                    return;
                }
                if (IsBusy && session?.SeparateEdit != true) return;
                editPressedAt = time;
                separateEditStart = true;
                try { Apply(VoiceEdit.PressSeparate(machine, true, IsBusy, session?.SeparateEdit == true)); }
                finally { separateEditStart = false; }
            });
        }
        return action != EditKeyAction.Pass;
    }

    EditProbe? ProbeVoiceEdit(RouteSettings routes)
    {
        if (!separateEditStart && !editBinding.SameKey) return null;
        if (!separateEditStart && AIEngines.Derive(routes.Cleanup) is null) return null;
        if (editProbe is not { IsCompleted: false })
        {
            var cancellation = new CancellationTokenSource();
            editProbe = Task.Run(() =>
            {
                var target = new FocusProbe.VoiceEditSelection(s.Paste, cancellation.Token);
                var result = VoiceEdit.CreateSession(false, routes.Cleanup, routes, s.Key, target, Environment.ProcessId);
                return cancellation.IsCancellationRequested
                    ? new EditProbe(new VoiceEdit.SessionResult.Refused("Selection check expired."), null)
                    : new EditProbe(result, result is VoiceEdit.SessionResult.Ready ready ? new(ready.Session, target, cancellation) : null);
            });
            try
            {
                if (editProbe.Wait(TimeSpan.FromMilliseconds(150)))
                {
                    var probe = editProbe.Result;
                    editProbe = null;
                    return !separateEditStart && probe.Result is VoiceEdit.SessionResult.Refused { Dictates: true } ? null : probe;
                }
            }
            catch (AggregateException)
            {
                editProbe = null;
                return new(new VoiceEdit.SessionResult.Refused("Voice edit could not check the selection."), null);
            }
            cancellation.Cancel();
        }
        // A slow provider must not let a press overwrite a selection or keep queueing UI Automation reads.
        return new(new VoiceEdit.SessionResult.Refused("The selected text could not be read in time. Try again."), null);
    }

    async Task RunVoiceEdit(Session ss, HistoryEntry entry, HistoryRouter<HistoryEntry> history)
    {
        var active = ss.VoiceEdit!;
        var edit = active.Edit;
        var (words, error) = VoiceEdit.CheckInstruction(entry.RawText);
        if (words.Length > 0) new Tips(s.Settings).Completed(Tips.ID.VoiceEdit);
        if (error is not null) { Fail(ss, "voice-edit", "invalid-instruction", error); return; }
        bubble.ShowProcessing($"Editing with {edit.EngineName} ({edit.Destination})…");

        string output;
        try { output = await edit.Run(edit.Selection, words, ss.Cts.Token); }
        catch (OperationCanceledException) when (ss.Cts.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            var message = (ex as ProviderException)?.Error switch
            {
                { Case: ProviderErrorKind.ContentBlocked } => $"{edit.EngineName} declined this text.",
                { Case: ProviderErrorKind.PayloadTooLarge } => $"The selection is too long for {edit.EngineName}.",
                { } failure => failure.UserMessage,
                _ => "The edit failed.",
            };
            Fail(ss, "voice-edit", "provider-failed", $"{message} The selection was not changed.");
            return;
        }
        if (Stale(ss)) return;

        switch (EditCheck.Verdict(edit.Selection, words, output))
        {
            case EditCheck.EditVerdict.Unchanged:
                phase = Phase.Ready;
                bubble.ShowNotice("No change. The selection already fits that.", 2);
                TestEvents.Emit("voice-edit", ("status", "unchanged"));
                break;
            case EditCheck.EditVerdict.Refused refusal:
                ShowEditCard(Phase.Error, $"{refusal.Reason} The selection was not changed.", output);
                TestEvents.Emit("voice-edit", ("status", "refused"));
                break;
            case EditCheck.EditVerdict.Apply apply:
                bubble.Hide();
                var pasted = await Task.Run(() =>
                {
                    EndLearning();
                    return VoiceEdit.ReplaceVoiceEditSelection(edit, active.Target, apply.Text);
                });
                if (Stale(ss)) return;
                if (!pasted)
                {
                    ShowEditCard(Phase.Error, "Could not replace the selection. Copy the edited text here.", apply.Text);
                    TestEvents.Emit("voice-edit", ("status", "saved-copy"));
                    return;
                }
                _ = WatchClipboardRestore();
                bool saved = true;
                try { history.Update(entry.With(apply.Text, InsertionStatus.Edited) with { EditOriginal = edit.Selection }); }
                catch (Exception) { saved = false; }
                ShowEditCard(Phase.Ready, saved
                    ? "Original selection. Press Ctrl+Z in the app to undo the edit."
                    : "Edited. History could not be updated. Copy the original here.", edit.Selection);
                TestEvents.Emit("voice-edit", ("status", "edited"), ("history-saved", saved));
                break;
        }
    }

    void ShowEditCard(Phase result, string title, string text)
    {
        lastText = text;
        phase = result;
        bubble.ShowCard(title, text);
    }
}
