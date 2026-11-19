// Port of app/Observation.swift.
// Pure snapshots only: the caller owns UI Automation access, monotonic time, focus identity and polling.
using System.Globalization;
using System.Text;

namespace Murmur.Core;

/// <summary>Swift <c>NSRange</c>: a range in UTF-16 code units, as UI Automation and the Mac's accessibility API report text ranges.</summary>
public readonly record struct Utf16Range(int Location, int Length)
{
    public int End => Location + Length;
}

/// <summary>Watches one dictated span after a verified insertion, in a nonsecure field with a readable value and selection.</summary>
public sealed class DictationObservation
{
    /// <summary>Why observation ended; the raw values are logged and stored in History's <c>AfterPaste.endReason</c>.</summary>
    public enum StopReason { AccessLost, InvalidClock, FocusDeparted, WindowEnded, OutsideAnchorsChanged, SelectionLeftSpan, SpanCleared, Finished }

    public Guid SessionId { get; }
    /// <summary>The dictated span as inserted, original casing.</summary>
    public string Initial { get; }
    readonly string prefix, suffix;
    readonly double start;
    public Utf16Range Range { get; private set; }
    public bool Stopped { get; private set; }
    public StopReason? Reason { get; private set; }
    /// <summary>The span's current text, original casing; null once attribution is lost. The app feeds <see cref="Initial"/> to this to the ledger.</summary>
    public string? Edited { get; private set; }

    DictationObservation(Guid sessionId, string value, Utf16Range inserted, double now)
    {
        SessionId = sessionId;
        Initial = value.Substring(inserted.Location, inserted.Length);
        prefix = value[..inserted.Location];
        suffix = value[inserted.End..];
        Range = inserted;
        start = now;
    }

    /// <summary>Swift's failable initializer: null for an invalid range or clock, or a span that cuts a word.</summary>
    public static DictationObservation? Create(string value, Utf16Range insertedRange, double now, Guid? sessionId = null)
    {
        if (!double.IsFinite(now) || insertedRange.Location < 0 || insertedRange.Length <= 0 || insertedRange.Location > value.Length
            || insertedRange.Length > value.Length - insertedRange.Location || !ValidBoundaries(insertedRange, value)) return null;
        // A span cutting a word cannot establish a whole-word correction.
        var before = SwiftText.Characters(value[..insertedRange.Location]);
        var inserted = SwiftText.Characters(value.Substring(insertedRange.Location, insertedRange.Length));
        var after = SwiftText.Characters(value[insertedRange.End..]);
        if (before.Count > 0 && SwiftText.IsLetter(before[^1]) && SwiftText.IsLetter(inserted[0])) return null;
        if (after.Count > 0 && SwiftText.IsLetter(inserted[^1]) && SwiftText.IsLetter(after[0])) return null;
        return new DictationObservation(sessionId ?? Guid.NewGuid(), value, insertedRange, now);
    }

    /// <summary>Call even on unchanged snapshots so expiry, focus and permission changes stop observation.</summary>
    public void Observe(string value, Utf16Range selection, double now, bool sameFocusedField, bool readableNonsecure)
    {
        if (Stopped) return;
        if (!readableNonsecure) { Stop(StopReason.AccessLost); return; }
        if (!double.IsFinite(now) || now < start) { Stop(StopReason.InvalidClock); return; }
        if (!sameFocusedField) { Stop(StopReason.FocusDeparted); return; }
        if (!(now - start < 60)) { Stop(StopReason.WindowEnded); return; }
        // shortcut: exact outside anchors reject even harmless outside edits; richer alignment needs live evidence.
        if (value.Length < prefix.Length + suffix.Length || !SwiftText.CharEquals(value[..prefix.Length], prefix)
            || !SwiftText.CharEquals(value[(value.Length - suffix.Length)..], suffix)) { Stop(StopReason.OutsideAnchorsChanged); return; }
        var updated = new Utf16Range(prefix.Length, value.Length - prefix.Length - suffix.Length);
        if (!ValidBoundaries(updated, value) || selection.Location < updated.Location || selection.Length < 0 || selection.Location > updated.End
            || selection.Length > updated.End - selection.Location || !ValidBoundaries(selection, value)) { Stop(StopReason.SelectionLeftSpan); return; }
        // Text typed after the span stays in it; the ledger's word diff ignores added words.
        var current = value.Substring(updated.Location, updated.Length);
        // A chat composer empties when Return sends it: keep the fix made before that.
        if (SwiftText.TrimWhitespaceAndNewlines(current).Length == 0) { Stop(StopReason.SpanCleared); return; }
        Range = updated;
        Edited = current;
    }

    /// <summary>Finish after a final snapshot; evidence remains reversible by session ID in the ledger.</summary>
    public void Finish() { Stopped = true; Reason ??= StopReason.Finished; }

    void Stop(StopReason reason)
    {
        Stopped = true;
        Reason = reason;
        if (!reason.KeepsEdit()) Edited = null;
    }

    /// <summary>Both ends of the range fall on character (grapheme cluster) boundaries, counted in UTF-16 units.</summary>
    static bool ValidBoundaries(Utf16Range range, string value)
    {
        // shortcut: linear boundary scan; indexed boundaries if large-field polling proves expensive.
        var boundaries = new HashSet<int> { 0 };
        int offset = 0;
        foreach (var c in SwiftText.Characters(value)) { offset += c.Length; boundaries.Add(offset); }
        return boundaries.Contains(range.Location) && boundaries.Contains(range.End);
    }
}

public static class StopReasonExtensions
{
    public static string RawValue(this DictationObservation.StopReason reason) => reason switch
    {
        DictationObservation.StopReason.AccessLost => "access or security lost",
        DictationObservation.StopReason.InvalidClock => "invalid clock",
        DictationObservation.StopReason.FocusDeparted => "focus departed",
        DictationObservation.StopReason.WindowEnded => "window ended",
        DictationObservation.StopReason.OutsideAnchorsChanged => "outside anchors changed",
        DictationObservation.StopReason.SelectionLeftSpan => "selection left dictated span",
        DictationObservation.StopReason.SpanCleared => "span cleared",
        _ => "finished",
    };

    /// <summary>A valid prior edit survives these (the text it was read from is unchanged); any other reason removes it.</summary>
    public static bool KeepsEdit(this DictationObservation.StopReason reason) => reason is DictationObservation.StopReason.WindowEnded
        or DictationObservation.StopReason.FocusDeparted or DictationObservation.StopReason.SelectionLeftSpan or DictationObservation.StopReason.SpanCleared;
}

/// <summary>Stand-in for a field with no readable text (a code editor's stub, web canvases): a virtual copy of the pasted span, edited by the keys
/// typed after it, starting with the caret at its end. Its <see cref="Value"/>/<see cref="Selection"/> feed <see cref="DictationObservation"/> as
/// snapshots with no text around the span. <c>Lost</c> (a Ctrl chord such as undo, or an edit outside the span) drops the evidence; <c>Departed</c>
/// (Return, Tab, vertical caret keys, or the caret leaving the span) keeps it, like focus departure. After a click the caret could be anywhere:
/// every caret position and every selection of 1-3 whole words is followed at once, and the span reads as the single edit <see cref="Resolve"/> finds plausible.
/// shortcut: the target app's own autocorrect is invisible here; the ledger's filter is the backstop. Read the field instead wherever UI Automation exposes it.</summary>
public sealed class KeystrokeSpan
{
    public abstract record Key
    {
        Key() { }
        public sealed record Text(string Value) : Key;
        public sealed record Backspace(bool Word) : Key;
        public sealed record ForwardDelete : Key;
        public sealed record Left(bool Word, bool Extend) : Key;
        public sealed record Right(bool Word, bool Extend) : Key;
        public sealed record Other : Key;
    }

    public enum SpanState { Editing, Departed, Lost }

    List<string> chars;
    public SpanState State { get; private set; } = SpanState.Editing;
    int anchor, caret;
    /// <summary>Where the caret might be after a click; empty while its position is known.</summary>
    List<KeystrokeSpan> guesses = [];
    bool typedSinceClick, unresolved;
    /// <summary>Picks the plausible reading among the guesses (the ledger's filter in the app).</summary>
    public Func<IReadOnlyList<string>, string?> Resolve { get; set; } = _ => null;

    public KeystrokeSpan(string text) { chars = SwiftText.Characters(text); anchor = chars.Count; caret = chars.Count; }
    KeystrokeSpan(List<string> chars, int anchor, int caret) { this.chars = [.. chars]; this.anchor = anchor; this.caret = caret; }

    /// <summary>Swift value semantics: an independent copy.</summary>
    KeystrokeSpan Copy() => new(chars, anchor, caret) { State = State, guesses = [.. guesses], typedSinceClick = typedSinceClick, unresolved = unresolved, Resolve = Resolve };

    public string Value => string.Concat(chars);

    /// <summary>UTF-16 range, as UI Automation reports selections. Unknown after a click: the span start stands in.</summary>
    public Utf16Range Selection
    {
        get
        {
            if (guesses.Count > 0) return new Utf16Range(0, 0);
            int Utf16(int index) => chars.Take(index).Sum(c => c.Length);
            int low = Math.Min(anchor, caret), high = Math.Max(anchor, caret);
            return new Utf16Range(Utf16(low), Utf16(high) - Utf16(low));
        }
    }

    public void Depart() { if (State == SpanState.Editing) State = SpanState.Departed; }

    /// <summary>Takes the plausible reading of the guesses as <see cref="Value"/>. Resolving runs the ledger's filter (tens of ms on a long span),
    /// so the app calls this on its poll, not per keystroke.</summary>
    public void Refresh()
    {
        if (!unresolved) return;
        unresolved = false;
        if (Resolve(guesses.Select(g => g.Value).ToList()) is { } reading) chars = SwiftText.Characters(reading);
    }

    /// <summary>A click in the app (a double-click is two). A click after typing that still has no plausible reading ends the watch;
    /// otherwise the guesses restart from the current text.
    /// shortcut: one guess per caret position and word, O(span length) work per key; bound the span if very long dictations make keystrokes lag.</summary>
    public void Click()
    {
        if (State != SpanState.Editing) return;
        if (typedSinceClick)
        {
            if (Resolve(guesses.Select(g => g.Value).ToList()) is not { } reading) { State = SpanState.Departed; return; }
            chars = SwiftText.Characters(reading);
        }
        typedSinceClick = false; unresolved = false;
        var words = Enumerable.Range(0, chars.Count).Where(i => IsWord(chars[i]) && (i == 0 || !IsWord(chars[i - 1]))).ToList();
        // Selections of 1-3 whole words cover a double-click and a mouse drag across a spacing fix (git hub -> GitHub); the ledger learns at most 3 words per side.
        var next = Enumerable.Range(0, chars.Count + 1).Select(i => new KeystrokeSpan(chars, i, i)).ToList();
        foreach (var wordStart in words)
        {
            int end = wordStart;
            for (int k = 0; k < 3; k++)
            {
                end = WordEnd(end);
                if (end <= chars.Count) next.Add(new KeystrokeSpan(chars, wordStart, end));
            }
        }
        guesses = next;
    }

    public void Apply(Key key)
    {
        if (State != SpanState.Editing) return;
        if (guesses.Count > 0)
        {
            if (key is Key.Other) { State = SpanState.Lost; return; }
            typedSinceClick = true;
            var seen = new HashSet<string>();
            var next = new List<KeystrokeSpan>();
            foreach (var guess in guesses)
            {
                var copy = guess.Copy();
                copy.Apply(key);
                if (copy.State == SpanState.Editing && seen.Add($"{copy.anchor} {copy.caret} {copy.Value}")) next.Add(copy);
            }
            // Return or a caret key ends the edit: settle what was typed before it.
            if (next.Count == 0) { Refresh(); State = SpanState.Departed; return; }
            guesses = next;
            unresolved = true;
            return;
        }
        int low = Math.Min(anchor, caret), high = Math.Max(anchor, caret);
        switch (key)
        {
            case Key.Text t:
                // Return, Tab, Escape (control) and arrow/Home/End/Page keys (private-use U+F700...) end the edit without undoing it.
                if (t.Value.Length == 0) { State = SpanState.Lost; return; }
                if (t.Value.EnumerateRunes().Any(EndsEdit)) { State = SpanState.Departed; return; }
                var typed = SwiftText.Characters(t.Value);
                chars.RemoveRange(low, high - low); chars.InsertRange(low, typed);
                caret = low + typed.Count; anchor = caret;
                break;
            case Key.Backspace b:
            {
                if (low < high) { Remove(low, high); return; }
                int from = b.Word ? WordStart(caret) : caret - 1;
                if (from < 0 || caret <= 0) { State = SpanState.Lost; return; }
                Remove(from, caret);
                break;
            }
            case Key.ForwardDelete:
                if (low < high) { Remove(low, high); return; }
                if (caret >= chars.Count) { State = SpanState.Lost; return; }
                Remove(caret, caret + 1);
                break;
            case Key.Left l:
            {
                int next = !l.Extend && low < high ? low : caret;
                if (l.Word || l.Extend || low == high) next = l.Word ? WordStart(next) : next - 1;
                if (next < 0) { State = SpanState.Departed; return; }
                caret = next; if (!l.Extend) anchor = next;
                break;
            }
            case Key.Right r:
            {
                int next = !r.Extend && low < high ? high : caret;
                if (r.Word || r.Extend || low == high) next = r.Word ? WordEnd(next) : next + 1;
                if (next > chars.Count) { State = SpanState.Departed; return; }
                caret = next; if (!r.Extend) anchor = next;
                break;
            }
            default:
                State = SpanState.Lost;
                break;
        }
    }

    /// <summary>Foundation's <c>controlCharacters</c> (Cc and Cf), <c>newlines</c>, and the private-use function-key range.</summary>
    static bool EndsEdit(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format
        || r.Value is (>= 0xA and <= 0xD) or 0x85 or 0x2028 or 0x2029 || r.Value is >= 0xF700 and <= 0xF8FF;

    void Remove(int from, int to) { chars.RemoveRange(from, to - from); caret = from; anchor = caret; }
    static bool IsWord(string c) => SwiftText.IsLetter(c) || SwiftText.IsNumber(c) || c is "'" or "’";

    /// <summary>Word-left: skip separators, then the word. -1 when that would leave the span.</summary>
    int WordStart(int index)
    {
        int i = index;
        while (i > 0 && !IsWord(chars[i - 1])) i--;
        if (i <= 0) return -1;
        while (i > 0 && IsWord(chars[i - 1])) i--;
        return i;
    }

    int WordEnd(int index)
    {
        int i = index;
        while (i < chars.Count && !IsWord(chars[i])) i++;
        if (i >= chars.Count) return chars.Count + 1;
        while (i < chars.Count && IsWord(chars[i])) i++;
        return i;
    }
}
