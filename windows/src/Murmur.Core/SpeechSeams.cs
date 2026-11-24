using System.Text;

namespace Murmur.Core;

/// <summary>A stretch of silence found by the VAD, in sample indexes (End exclusive).</summary>
public readonly record struct Silence(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>Where long recordings are cut for the multilingual local models, and how the decoded pieces are joined (Windows only; no Swift original).
/// Pure functions so the Core tests can reach them; Murmur.Speech calls them.</summary>
public static class SpeechSeams
{
    /// <summary>The longest run of words (or characters, for scripts without spaces) compared at a seam.</summary>
    public const int MaxOverlap = 6;
    /// <summary>Characters needed before a no-space-script overlap is dropped: one character is too often a legitimate repeat.</summary>
    const int MinCharOverlap = 2;

    // ---- Cut selection ----------------------------------------------------------------------------------------------------------

    /// <summary>The cut index for the piece that starts at <paramref name="start"/>: the middle of the longest pause that lies in
    /// [start + minLength, start + maxLength]. Each piece then keeps only the silence on its own side of the cut. A pause is clipped to the window first,
    /// and a tie goes to the later pause. With no pause of at least <paramref name="minPause"/> samples, the quietest short frame in the last
    /// <paramref name="fallbackWindow"/> samples of the window is used (the cut may then split a word; <see cref="Join"/> repairs that).</summary>
    public static int ChooseCut(ReadOnlySpan<float> samples, IReadOnlyList<Silence> pauses, int start, int minLength, int maxLength, int minPause, int fallbackWindow, int frame)
    {
        int lo = start + minLength, hi = Math.Min(start + maxLength, samples.Length);
        int best = -1, bestLength = 0;
        foreach (var p in pauses)
        {
            int from = Math.Max(p.Start, lo), to = Math.Min(p.End, hi);
            int length = to - from;
            if (length < minPause || length < bestLength) continue;
            best = from + length / 2; bestLength = length; // `<` above, so a tie goes to the later pause
        }
        if (best >= 0) return best;
        int scanFrom = Math.Max(start + 1, hi - fallbackWindow), step = Math.Max(1, frame / 2);
        double quietest = double.MaxValue;
        int cut = hi;
        for (int i = scanFrom; i + frame <= hi; i += step)
        {
            double energy = 0;
            for (int j = i; j < i + frame; j++) energy += (double)samples[j] * samples[j];
            if (energy <= quietest) { quietest = energy; cut = i + frame / 2; }
        }
        return cut;
    }

    // ---- Joining ----------------------------------------------------------------------------------------------------------------

    /// <summary>Joins the pieces' texts. At each join it drops the longest run of up to <see cref="MaxOverlap"/> words that ends the text so far
    /// and also starts the next piece (case and punctuation ignored); it keeps the earlier copy, so the kept text keeps its original casing and punctuation.
    /// Text that does not match is never dropped. For scripts without spaces (Chinese, Japanese, Thai, Lao, Khmer, Myanmar) it compares characters
    /// instead, needs at least two, and joins without a space. Empty and whitespace-only pieces are skipped.</summary>
    public static string Join(IEnumerable<string?> pieces)
    {
        var result = new StringBuilder();
        foreach (var raw in pieces)
        {
            var piece = raw?.Trim() ?? "";
            if (piece.Length == 0) continue;
            if (result.Length == 0) { result.Append(piece); continue; }
            var previous = result.ToString();
            bool characters = EndsInNoSpaceScript(previous) && StartsInNoSpaceScript(piece);
            piece = characters ? DropCharacterOverlap(previous, piece) : DropWordOverlap(previous, piece);
            if (piece.Length == 0) continue;
            if (!(EndsInNoSpaceScript(previous) && StartsInNoSpaceScript(piece))) result.Append(' ');
            result.Append(piece);
        }
        return result.ToString();
    }

    static string DropWordOverlap(string previous, string next)
    {
        var tail = Tokens(previous, fromEnd: true);
        var head = Tokens(next, fromEnd: false);
        for (int k = Math.Min(MaxOverlap, Math.Min(tail.Count, head.Count)); k >= 1; k--)
        {
            bool same = true;
            for (int i = 0; i < k && same; i++)
            {
                var a = tail[tail.Count - k + i].Folded;
                same = a.Length > 0 && a == head[i].Folded;
            }
            if (same) return next[head[k - 1].End..].TrimStart();
        }
        return next;
    }

    readonly record struct Token(string Folded, int End);

    /// <summary>Up to MaxOverlap words from one end of the text. A word is a whitespace-separated token; a token with no letters or digits
    /// (a dash, an ellipsis) belongs to the word before it. Folded is the lowercase letters and digits only. End is the token's end index in the text.</summary>
    static List<Token> Tokens(string text, bool fromEnd)
    {
        var all = new List<(int Start, int End)>();
        for (int i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > s) all.Add((s, i));
        }
        var words = new List<Token>();
        foreach (var (s, e) in all)
        {
            var folded = Fold(text.AsSpan(s, e - s));
            if (folded.Length == 0 && words.Count > 0) words[^1] = words[^1] with { End = e };
            else words.Add(new Token(folded, e));
            if (!fromEnd && words.Count > MaxOverlap + 1) break;
        }
        if (fromEnd && words.Count > MaxOverlap) words.RemoveRange(0, words.Count - MaxOverlap);
        return words;
    }

    static string Fold(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.ToString().EnumerateRunes())
            if (Rune.IsLetterOrDigit(r)) sb.Append(Rune.ToLowerInvariant(r).ToString());
        return sb.ToString();
    }

    static string DropCharacterOverlap(string previous, string next)
    {
        var tail = new List<Rune>();
        var runes = previous.EnumerateRunes().ToList();
        for (int i = runes.Count - 1; i >= 0 && tail.Count < MaxOverlap; i--)
            if (Rune.IsLetterOrDigit(runes[i])) tail.Insert(0, Rune.ToLowerInvariant(runes[i]));
        var head = new List<(Rune Rune, int End)>();
        int index = 0;
        foreach (var r in next.EnumerateRunes())
        {
            index += r.Utf16SequenceLength;
            if (Rune.IsLetterOrDigit(r)) head.Add((Rune.ToLowerInvariant(r), index));
            if (head.Count == MaxOverlap) break;
        }
        for (int k = Math.Min(tail.Count, head.Count); k >= MinCharOverlap; k--)
        {
            bool same = true;
            for (int i = 0; i < k && same; i++) same = tail[tail.Count - k + i] == head[i].Rune;
            if (!same) continue;
            int end = head[k - 1].End;
            // Punctuation that closed the dropped run is dropped with it; the earlier copy already carries its own.
            while (end < next.Length && (char.IsPunctuation(next[end]) || char.IsSymbol(next[end]))) end++;
            return next[end..].TrimStart();
        }
        return next;
    }

    static bool EndsInNoSpaceScript(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            int j = char.IsLowSurrogate(text[i]) && i > 0 ? i - 1 : i;
            if (!Rune.TryGetRuneAt(text, j, out var r)) continue;
            if (Rune.IsLetterOrDigit(r)) return NoSpaceScript(r.Value);
            if (Rune.IsWhiteSpace(r)) return false;
            i = j;
        }
        return false;
    }

    static bool StartsInNoSpaceScript(string text)
    {
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r)) return NoSpaceScript(r.Value);
            if (Rune.IsWhiteSpace(r)) return false;
        }
        return false;
    }

    /// <summary>Han, Hiragana, Katakana, Thai, Lao, Myanmar and Khmer: scripts written without spaces between words.</summary>
    static bool NoSpaceScript(int c) =>
        c is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
          or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF or >= 0x20000 and <= 0x2FA1F;
}
