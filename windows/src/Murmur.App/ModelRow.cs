using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>A model card in Settings: title, summary, status, license, progress and the Download / Cancel / Retry / Remove buttons.
/// The flow, texts and gates are Murmur.Core's (ModelFlow, ModelText); this class only draws them and shows the license-and-size confirmation in a ContentDialog.</summary>
internal sealed class ModelRow
{
    /// <summary>The flow asks synchronously, a ContentDialog answers asynchronously: the click handler awaits the dialog first and the flow then reads that answer.</summary>
    sealed class Answer : IModelFlowDialogs
    {
        public bool Yes;
        public bool ConfirmDownload(string title, string body) => Yes;
        public bool ConfirmRemove(string title, string body) => Yes;
    }

    readonly AppServices s;
    readonly ModelSlot slot;
    readonly ModelFlow? flow;
    readonly Controller? controller;
    readonly Action? changedHandler;
    readonly bool onboarding;
    /// <summary>The speech model a controller-owned row (the required model, or the meetings' model) acts on; null for rows that own their flow.</summary>
    readonly Func<LocalSpeechModel>? speechModel;
    Button? reinstall;
    readonly TextBlock hint = MurmurStyle.Text("", "MurmurCaption");

    /// <summary>Stops a required-model row listening to the controller; the owner calls it before dropping the row.</summary>
    public void Detach() { if (controller is not null && changedHandler is not null) controller.SpeechModelChanged -= changedHandler; }
    readonly Answer answer = new();
    readonly Func<XamlRoot?> root;
    readonly TextBlock status = MurmurStyle.Text("", "BodyStrongTextBlockStyle");
    readonly InfoBar message = new();
    readonly ProgressBar bar = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    readonly Dictionary<ModelRowAction, Button> buttons = [];
    public StackPanel Panel { get; } = new() { Spacing = 8 };

    /// <param name="release">Stops what holds the model's files open, before they are deleted.</param>
    /// <param name="changed">After the slot's state changed (the page re-checks what that enables).</param>
    /// <param name="onboarding">The Welcome step: a filled "Download name (size)" button and a line saying what happens next.</param>
    public ModelRow(AppServices services, ModelSlot slot, Controller controller, Func<XamlRoot?> root, Action<Action> release, Action changed, bool required = false, bool onboarding = false, Func<LocalSpeechModel>? speechModel = null)
    {
        s = services; this.slot = slot; this.root = root; this.onboarding = onboarding;
        var facts = slot.Facts;
        if (required) speechModel = () => controller.RequiredSpeechModel;
        if (speechModel is not null)
        {
            this.speechModel = speechModel;
            // The controller owns the flow of the required speech model (it prewarms after a download); this row supplies its dialogs and redraws on its changes.
            this.controller = controller;
            changedHandler = () => { Refresh(); changed(); };
            controller.SpeechModelChanged += changedHandler;
            Panel.Unloaded += (_, _) => Detach();
        }
        else
        flow = new ModelFlow(slot, answer)
        {
            PushProgress = true,
            Post = a => s.UI.TryEnqueue(() => a()),
            Changed = Refresh,
            DownloadGate = () => ModelGate.DownloadRefusal(controller.IsBusy, false, false),
            RemoveGate = () => ModelGate.RemoveRefusal(controller.IsBusy, false, slot.InFlight),
            DidDownload = error => { Forget(); changed(); if (slot == s.CleanupSlot) controller.ReportCleanupDownload(slot.Facts.DisplayName, error); },
            Release = go => release(go),
            DidRemove = () => { Forget(); changed(); },
        };
        Panel.Children.Add(MurmurStyle.Text(facts.Title, "BodyStrongTextBlockStyle"));
        MurmurStyle.Named(bar, $"{facts.Title} download progress");
        MurmurStyle.Named(message, $"{facts.Title} status");
        Panel.Children.Add(Small(slot.Summary));
        if (!string.IsNullOrEmpty(facts.Explanation)) Panel.Children.Add(Small(facts.Explanation));
        Panel.Children.Add(Small(slot.License is { } license ? "License: " + string.Join(", ", license.Declarations.Select(d => d.Name).Distinct().DefaultIfEmpty("not recorded")) + ". Shown in full before any download." : ""));
        Panel.Children.Add(status);
        Panel.Children.Add(bar);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var size = slot.Downloader is { } sized ? ModelFacts.FormatBytes(sized.DownloadSize) : "";
        foreach (var (action, label, click) in new (ModelRowAction, string, Func<Task>)[] { (ModelRowAction.Download, onboarding ? OnboardingText.DownloadButton(facts.DisplayName, size) : "Download", Download), (ModelRowAction.Retry, onboarding ? "Retry download" : "Retry", Download), (ModelRowAction.Cancel, "Cancel", () => { if (controller is not null) controller.CancelSpeechModel(speechModel!()); else flow!.Cancel(); return Task.CompletedTask; }), (ModelRowAction.Remove, "Remove", Remove) })
        {
            // The Welcome step has one obvious action: the accent-filled download button (the Mac's InkButtonStyle "Download…").
            var button = onboarding && action is ModelRowAction.Download or ModelRowAction.Retry
                ? WelcomeWindow.Filled(label, () => { })
                : MurmurStyle.Named(new Button { Content = label }, $"{label} {facts.Title}");
            button.Click += async (_, _) => await click();
            buttons[action] = button; row.Children.Add(button);
        }
        // Reinstall (the Mac's ModelRowAction.reinstall): a ready model can be downloaded again; it is replaced only after every file verifies.
        if (!onboarding)
        {
            reinstall = MurmurStyle.Named(new Button { Content = "Reinstall" }, $"Reinstall {facts.Title}");
            reinstall.Click += async (_, _) => await Download();
            row.Children.Insert(row.Children.Count - 1, reinstall);
        }
        Panel.Children.Add(row);
        if (onboarding) Panel.Children.Add(hint);
        Panel.Children.Add(message);
        Refresh();
    }

    static TextBlock Small(string text)
    {
        var block = MurmurStyle.Text(text, "MurmurCaption");
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return block;
    }

    void Forget() { if (slot.Downloader is { } downloader) ModelVerificationCache.Shared.Forget(downloader.Target); }

    /// <summary>A staged model exists on disk only as a side-loaded test copy: it is built but not uploaded, so Download says so instead of failing.</summary>

    public void Refresh()
    {
        var row = slot.Row;
        status.Text = row.StatusText;
        status.Visibility = row.StatusText == slot.Message ? Visibility.Collapsed : Visibility.Visible;
        bar.Visibility = row.IsWorking ? Visibility.Visible : Visibility.Collapsed;
        bar.IsIndeterminate = row.IsWorking && row.Progress is null;
        if (row.Progress is { } fraction) bar.Value = fraction;
        foreach (var (action, button) in buttons)
        {
            button.Visibility = row.Actions.Contains(action) ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = !slot.InFlight || action == ModelRowAction.Cancel;
        }
        if (reinstall is not null) { reinstall.Visibility = row.IsReady ? Visibility.Visible : Visibility.Collapsed; reinstall.IsEnabled = !slot.InFlight; }
        hint.Text = row.IsReady ? OnboardingText.ReadyHint : row.IsWorking ? OnboardingText.DownloadingHint : row.Actions.Contains(ModelRowAction.Download) ? OnboardingText.DownloadHint : "";
        hint.Visibility = hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        message.Message = slot.Message;
        message.Severity = slot.Status.Error is { } error && error != ModelDownloadError.Cancelled
            ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        message.IsOpen = slot.Message.Length != 0;
    }

    async Task Download()
    {
        if (slot.Downloader is not { } downloader || slot.InFlight) return;
        var text = slot.Facts.Confirmation(downloader.DownloadSize, downloader.License, downloader.Target);
        answer.Yes = await Ask(text.Title, text.Body, "Download");
        if (controller is not null) { controller.SpeechModelDialogs = answer; controller.DownloadSpeechModel(speechModel!()); } else flow!.Download();
        answer.Yes = false;
    }

    async Task Remove()
    {
        if (slot.Downloader is not { } downloader || slot.InFlight) return;
        answer.Yes = await Ask(slot.Facts.RemoveTitle, slot.Facts.RemoveBody(downloader.Target, downloader.DownloadSize), "Remove");
        if (controller is not null) { controller.SpeechModelDialogs = answer; controller.RemoveSpeechModel(speechModel!()); } else flow!.Remove();
        answer.Yes = false;
    }

    async Task<bool> Ask(string title, string body, string action)
    {
        if (root() is not { } xaml) return false;
        var dialog = new ContentDialog { XamlRoot = xaml, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 360 }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await MurmurStyle.Named(dialog, title).ShowAsync() == ContentDialogResult.Primary;
    }
}
