using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Murmur.Core;

namespace Murmur.App;

/// <summary>One row of Settings > Models (app/SettingsPaneViews.swift model rows): title, summary, status, license, progress and the Download / Cancel / Retry / Remove buttons.
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
    readonly ModelFlow flow;
    readonly Answer answer = new();
    readonly Func<XamlRoot?> root;
    readonly Action changed;
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
    readonly ProgressBar bar = new() { Maximum = 1, Visibility = Visibility.Collapsed };
    readonly Dictionary<ModelRowAction, Button> buttons = [];
    public StackPanel Panel { get; } = new() { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };

    /// <param name="release">Stops what holds the model's files open, before they are deleted.</param>
    /// <param name="changed">After the slot's state changed (the page re-checks what that enables).</param>
    public ModelRow(AppServices services, ModelSlot slot, Controller controller, Func<XamlRoot?> root, Action<Action> release, Action changed)
    {
        s = services; this.slot = slot; this.root = root; this.changed = changed;
        var facts = slot.Facts;
        flow = new ModelFlow(slot, answer)
        {
            PushProgress = true,
            Post = a => s.UI.TryEnqueue(() => a()),
            Changed = Refresh,
            DownloadGate = () => ModelGate.DownloadRefusal(controller.IsBusy, false, false),
            RemoveGate = () => ModelGate.RemoveRefusal(controller.IsBusy, false, slot.InFlight),
            DidDownload = _ => { Forget(); changed(); },
            Release = go => release(go),
            DidRemove = () => { Forget(); changed(); },
        };
        Panel.Children.Add(new TextBlock { Text = facts.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Panel.Children.Add(Small(slot.Summary));
        if (!string.IsNullOrEmpty(facts.Explanation)) Panel.Children.Add(Small(facts.Explanation));
        Panel.Children.Add(Small(slot.License is { } license ? "License: " + string.Join(", ", license.Declarations.Select(d => d.Name).DefaultIfEmpty("not recorded")) + ". Shown in full before any download." : ""));
        Panel.Children.Add(status);
        Panel.Children.Add(bar);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (action, label, click) in new (ModelRowAction, string, Func<Task>)[] { (ModelRowAction.Download, "Download", Download), (ModelRowAction.Retry, "Retry", Download), (ModelRowAction.Cancel, "Cancel", () => { flow.Cancel(); return Task.CompletedTask; }), (ModelRowAction.Remove, "Remove", Remove) })
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => await click();
            buttons[action] = button; row.Children.Add(button);
        }
        Panel.Children.Add(row);
        Panel.Children.Add(message);
        Refresh();
    }

    static TextBlock Small(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7, Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible };

    void Forget() { if (slot.Downloader is { } downloader) ModelVerificationCache.Shared.Forget(downloader.Target); }

    /// <summary>A staged model exists on disk only as a side-loaded test copy: it is built but not uploaded, so Download says so instead of failing.</summary>

    public void Refresh()
    {
        var row = slot.Row;
        status.Text = row.StatusText;
        bar.Visibility = row.IsWorking ? Visibility.Visible : Visibility.Collapsed;
        bar.IsIndeterminate = row.IsWorking && row.Progress is null;
        if (row.Progress is { } fraction) bar.Value = fraction;
        foreach (var (action, button) in buttons)
        {
            button.Visibility = row.Actions.Contains(action) ? Visibility.Visible : Visibility.Collapsed;
        }
        message.Text = slot.Message;
        message.Visibility = slot.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task Download()
    {
        if (slot.Downloader is not { } downloader || slot.InFlight) return;
        var text = slot.Facts.Confirmation(downloader.DownloadSize, downloader.License, downloader.Target);
        answer.Yes = await Ask(text.Title, text.Body, "Download");
        flow.Download();
        answer.Yes = false;
    }

    async Task Remove()
    {
        if (slot.Downloader is not { } downloader || slot.InFlight) return;
        answer.Yes = await Ask(slot.Facts.RemoveTitle, slot.Facts.RemoveBody(downloader.Target, downloader.DownloadSize), "Remove");
        flow.Remove();
        answer.Yes = false;
    }

    async Task<bool> Ask(string title, string body, string action)
    {
        if (root() is not { } xaml) return false;
        var dialog = new ContentDialog { XamlRoot = xaml, Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 360 }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
