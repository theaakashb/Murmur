using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Murmur.Harness.Driver;

/// <summary>One clipboard change: format names only, plus a classification computed in memory (text is never stored or logged).</summary>
record ClipChange(DateTime Utc, string[] Formats, string Kind); // Kind: sentinel, dictation, other-text, no-text, unreadable

sealed class ClipboardWatcher : IDisposable
{
    readonly List<ClipChange> changes = new();
    readonly Native.ClipboardListener listener;
    public string? Sentinel;
    public List<string> DictationTexts = new(); // in-memory only

    public ClipboardWatcher() { listener = new Native.ClipboardListener(OnUpdate); }

    void OnUpdate(IntPtr hwnd)
    {
        if (!Native.Open(hwnd)) { Add(new ClipChange(DateTime.UtcNow, Array.Empty<string>(), "unreadable")); return; }
        string[] formats; string? text;
        try { formats = Native.FormatNamesOpen().ToArray(); text = Native.ReadTextOpen(); } finally { Native.Close(); }
        var kind = text is null ? "no-text"
            : text == Sentinel ? "sentinel"
            : DictationTexts.Any(d => d.Length > 0 && text.Contains(d, StringComparison.Ordinal)) ? "dictation"
            : "other-text";
        Add(new ClipChange(DateTime.UtcNow, formats, kind));
    }
    void Add(ClipChange c) { lock (changes) changes.Add(c); }
    public ClipChange[] Snapshot() { lock (changes) return changes.ToArray(); }
    public void Clear() { lock (changes) changes.Clear(); }
    public void Dispose() => listener.Dispose();

    /// <summary>The dictation item, if staged, must carry all three exclusion formats.</summary>
    public static (bool staged, string[] missing) CheckExclusions(IEnumerable<ClipChange> cs)
    {
        var staged = cs.Where(c => c.Kind == "dictation").ToArray();
        if (staged.Length == 0) return (false, Native.ExclusionFormats);
        var missing = Native.ExclusionFormats.Where(f => staged.Any(c => !c.Formats.Contains(f))).ToArray();
        return (true, missing);
    }
}

/// <summary>Windows clipboard history (Win+V) via the WinRT API.</summary>
static class ClipboardHistory
{
    /// <returns>(verifiable, texts, note). Not verifiable when history is off or access is denied.</returns>
    public static async Task<(bool ok, List<string> texts, string note)> ReadAsync()
    {
        try
        {
            if (!Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled()) return (false, new(), "clipboard history is off on this machine");
            var r = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
            if (r.Status != Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success) return (false, new(), "GetHistoryItemsAsync status " + r.Status);
            var texts = new List<string>();
            foreach (var item in r.Items)
                if (item.Content.Contains("Text")) texts.Add(await item.Content.GetTextAsync());
            return (true, texts, $"{r.Items.Count} items");
        }
        catch (Exception e) { return (false, new(), "history API unavailable: " + e.GetType().Name + " " + e.Message); }
    }
}
