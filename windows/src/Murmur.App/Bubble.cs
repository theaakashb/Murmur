using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>
/// The dictation indicator: a topmost, non-activating layered window that draws the five bars and the cards itself (Murmur.Platform.PillWindow),
/// so the pixels outside the shapes are truly transparent. Only a card with buttons accepts clicks. The state it shows is set from the UI thread.
/// </summary>
internal sealed partial class Bubble
{
    readonly PillWindow pill = new();
    readonly DispatcherQueueTimer hideTimer;
    readonly Windows.UI.ViewManagement.UISettings animations = new();
    bool isListening, liveWordsActive;
    string? cardText, chipOriginal, liveShown;
    DisplayArea? display;

    /// <summary>Opens History (the chip's click, as on the Mac).</summary>
    public Action? OpenHistory { get; set; }

    /// <summary>True when the dictation indicator sits at the top of the screen.</summary>
    public Func<bool>? TopPlacement { get; set; }

    /// <summary>Development demo only: Copy does not touch the clipboard, and the theme can be forced.</summary>
    internal bool Demo { get; set; }
    internal bool? ForceDark { get; set; }

    public Bubble(DispatcherQueue ui)
    {
        hideTimer = ui.CreateTimer();
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) => Hide();
        pill.Clicked = hit =>
        {
            switch (pill.Model?.Kind, hit)
            {
                case (PillKind.Copy, PillHit.Copy): if (cardText is not null && !Demo) Native.CopyText(cardText); Hide(); break;
                case (PillKind.Copy, PillHit.Dismiss): Hide(); break;
                case (PillKind.Changes, PillHit.Open): Hide(); OpenHistory?.Invoke(); break;
                case (PillKind.Changes, PillHit.Copy): if (chipOriginal is not null && !Demo) Native.CopyText(chipOriginal); Hide(); break;
            }
        };
    }

    public void ShowListening() => Present(Model(PillKind.Dots), dismissAfter: null, listening: true);

    public void ShowProcessing(string? message = null) =>
        Present(message is null ? Model(PillKind.Dots) with { Processing = true } : Model(PillKind.DotsLabel) with { Text = message, Processing = true }, null);

    public void ShowError(string message, double dismissAfter) => Present(Model(PillKind.Error) with { Text = message }, dismissAfter);
    public void ShowNotice(string message, double dismissAfter) => Present(Model(PillKind.Notice) with { Text = message }, dismissAfter);

    /// <summary>The paste-failure (or saved, not pasted) card: text with Copy, 30 s.</summary>
    public void ShowCard(string title, string copyText, double dismissAfter = 30)
    {
        var heading = title.StartsWith("Could not paste", StringComparison.Ordinal) ? "Couldn’t paste here" : title;
        Present(Model(PillKind.Copy) with { Title = heading, Preview = copyText }, dismissAfter);
        cardText = copyText;
    }

    /// <summary>The latest normalized RMS level, written by the capture thread.</summary>
    public void SetLevel(float level) => pill.Level = level;

    /// <summary>Mac parity (LiveWordsBubble): when live words run, the card is up from the start with a dim "Listening…" until the first words arrive.</summary>
    public void ShowLiveWords()
    {
        if (!isListening) return;
        liveWordsActive = true;
        RenderLive("");
    }

    /// <summary>Shows live words on the UI thread while the indicator is listening: settled words in the ink colour, the last word (it can still grow) dimmed.</summary>
    public void SetPartial(string? text)
    {
        if (!isListening) return;
        var words = string.IsNullOrWhiteSpace(text) ? "" : text;
        if (!liveWordsActive && words.Length == 0) return;
        liveWordsActive = true;
        RenderLive(words);
    }

    void RenderLive(string words)
    {
        if (liveShown == words) return;
        liveShown = words;
        var (settled, guess) = words.Length == 0 ? ("", "") : Murmur.Core.LiveWordsFacts.Split(words);
        Place(Model(PillKind.LiveWords) with { Settled = settled, Guess = guess });
    }

    /// <summary>The Mac's changes chip, after a dictation cleanup or a mode changed: the mode icon when a writing mode shaped it (else the cleanup one), "N changes", 8 s.</summary>
    public void ShowChanges(int changes, bool usedMode, string original)
    {
        Present(Model(PillKind.Changes) with { Text = $"{changes} change{(changes == 1 ? "" : "s")}", UsedMode = usedMode }, 8);
        chipOriginal = original;
    }

    public void Hide()
    {
        hideTimer.Stop();
        isListening = false;
        liveWordsActive = false; liveShown = null;
        cardText = null;
        pill.Hide();
    }

    PillModel Model(PillKind kind) => new(kind) { Dark = ForceDark ?? Application.Current.RequestedTheme == ApplicationTheme.Dark, Motion = animations.AnimationsEnabled };

    void Present(PillModel model, double? dismissAfter, bool listening = false)
    {
        hideTimer.Stop();
        isListening = listening;
        liveWordsActive = false; liveShown = null;
        cardText = null;
        var foreground = Native.GetForegroundWindow();
        display = foreground != 0
            ? DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Primary)
            : DisplayArea.Primary;
        Place(model);
        if (dismissAfter is { } seconds) { hideTimer.Interval = TimeSpan.FromSeconds(seconds); hideTimer.Start(); }
    }

    [DllImport("user32.dll")] static extern nint MonitorFromPoint(long point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    /// <summary>The scale of the monitor the pill appears on (the window is not there yet, so its own DPI would be the old monitor's).</summary>
    static double MonitorScale(int x, int y)
    {
        var monitor = MonitorFromPoint((uint)x | (long)y << 32, 2);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi >= 96 ? dpi / 96.0 : 1;
    }

    /// <summary>Sizes the pill for its monitor and puts it at the Mac's spot: centred, 24 pt above the bottom of the visible area (or below the top).</summary>
    void Place(PillModel model)
    {
        var work = (display ?? DisplayArea.Primary).WorkArea;
        var scale = MonitorScale(work.X + work.Width / 2, work.Y + work.Height / 2);
        var (width, height) = pill.Measure(model, scale);
        int x = work.X + (work.Width - width) / 2;
        int margin = (int)(24 * scale);
        int y = TopPlacement?.Invoke() == true ? work.Y + margin : work.Y + work.Height - height - margin;
        pill.Show(model, scale, x, y);
    }
}
