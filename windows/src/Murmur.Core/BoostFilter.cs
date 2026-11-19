// Port of module-one/models/Runner/Sources/ModelBench/BoostFilter.swift.
using System.Text.RegularExpressions;

namespace Murmur.Core;

/// <summary>
/// Decides which dictionary replacements from CTC rescoring to keep.
///
/// Rescoring swaps transcript words for dictionary terms that sound alike, and on its own it also
/// turns ordinary speech into terms ("nothing" -> "Notion", "remind" -> "Gemini"). A replaced span
/// is kept only when one of these holds: its letters equal the term's ("open router" -> "OpenRouter"),
/// it is a "heard as" alias the user listed for that term, or at least one replaced word is not an ordinary
/// dictionary word and the span is spelled at least half like the term, so a name is never swallowed with its neighbours.
/// The output is the raw transcript with only the kept replacements applied, so punctuation the
/// rescorer drops around a replacement ("I mean," -> "I Sean") is never lost.
/// </summary>
public static class BoostFilter
{
    sealed class Data { public string Unknown { get; set; } = ""; }

    /// <summary>The model's unknown-token piece. Parakeet's vocabulary has no accented letters, so "El Niño" decodes as
    /// "El Ni&lt;unk&gt;o" and the letter cannot be recovered. The piece is dropped with the gap it would leave.</summary>
    public static readonly string Unknown = SharedData.Load<Data>("boost_filter").Unknown;

    static readonly Regex Spaces = new(" {2,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex SpaceBeforePunctuation = new(" ([.,!?;:])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string WithoutUnknown(string text)
    {
        if (!text.Contains(Unknown, StringComparison.Ordinal)) return text;
        var cleaned = text.Replace(Unknown, "", StringComparison.Ordinal);
        cleaned = Spaces.Replace(cleaned, " ");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        // Foundation CharacterSet.whitespaces: Unicode Zs and tab (not newlines).
        static bool Blank(string c) => c == "\t" || c.EnumerateRunes().All(r => System.Text.Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.SpaceSeparator);
        var chars = SwiftText.Characters(cleaned);
        int a = 0, b = chars.Count;
        while (a < b && Blank(chars[a])) a++;
        while (b > a && Blank(chars[b - 1])) b--;
        return TermText.Slice(chars, a, b);
    }

    /// <param name="Start">UTF-16 offset into the text.</param>
    /// <param name="End">UTF-16 offset, exclusive.</param>
    public readonly record struct Word(int Start, int End, string Folded);

    public static List<Word> Words(string text)
    {
        var chars = SwiftText.Characters(text);
        var offsets = new int[chars.Count + 1];
        for (int k = 0; k < chars.Count; k++) offsets[k + 1] = offsets[k] + chars[k].Length;
        static bool Core(string c) => SwiftText.IsLetter(c) || SwiftText.IsNumber(c);
        var result = new List<Word>();
        int i = 0;
        while (i < chars.Count)
        {
            if (!Core(chars[i])) { i++; continue; }
            int j = i + 1;
            while (j < chars.Count)
            {
                if (Core(chars[j])) { j++; continue; }
                if (chars[j] is "'" or "’" or "-" && j + 1 < chars.Count && Core(chars[j + 1])) { j += 2; continue; }
                break;
            }
            result.Add(new Word(offsets[i], offsets[j], Fold(TermText.Slice(chars, i, j))));
            i = j;
        }
        return result;
    }

    public static string Fold(string text) => SwiftText.Lowercased(text.Replace("’", "'"));
    public static string Letters(string text) => string.Concat(SwiftText.Characters(Fold(text)).Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)));

    /// <summary>A kept replacement: UTF-16 range and text in the raw transcript, and the rescored text.</summary>
    public readonly record struct Replacement(int Start, int End, string From, string To);

    /// <param name="aliases">Folded alias phrases ("cube cuddle").</param>
    /// <param name="isCommonWord">Receives one folded word.</param>
    public static string Filter(string raw, string rescored, IReadOnlySet<string> aliases, Func<string, bool> isCommonWord)
    {
        var output = raw;
        foreach (var kept in Replacements(raw, rescored, aliases, isCommonWord).AsEnumerable().Reverse())
            output = output[..kept.Start] + kept.To + output[kept.End..];
        return output;
    }

    /// <summary>The kept replacements, in order: range and <c>From</c> in <c>raw</c>, <c>To</c> from <c>rescored</c>.</summary>
    public static List<Replacement> Replacements(string raw, string rescored, IReadOnlySet<string> aliases, Func<string, bool> isCommonWord)
    {
        var kept = new List<Replacement>();
        if (raw == rescored) return kept;
        var a = Words(raw); var b = Words(rescored);
        // LCS over folded words; unmatched runs between matches are the replaced spans.
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int x = a.Count - 1; x >= 0; x--)
            for (int y = b.Count - 1; y >= 0; y--)
                dp[x, y] = a[x].Folded == b[y].Folded ? dp[x + 1, y + 1] + 1 : Math.Max(dp[x + 1, y], dp[x, y + 1]);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (i < a.Count && j < b.Count && a[i].Folded == b[j].Folded) { i++; j++; continue; }
            int i0 = i, j0 = j;
            while ((i < a.Count || j < b.Count) && !(i < a.Count && j < b.Count && a[i].Folded == b[j].Folded))
            {
                if (j < b.Count && (i == a.Count || dp[i, j + 1] >= dp[i + 1, j])) j++; else i++;
            }
            // A pure insertion or deletion keeps the raw words: rescoring only replaces spans.
            if (i <= i0 || j <= j0) continue;
            var rawSpan = raw[a[i0].Start..a[i - 1].End]; var newSpan = rescored[b[j0].Start..b[j - 1].End];
            var rawWords = a.GetRange(i0, i - i0).Select(w => w.Folded).ToList();
            bool keep = Letters(rawSpan) == Letters(newSpan) || aliases.Contains(string.Join(" ", rawWords))
                || (rawWords.Any(w => !isCommonWord(w)) && Similarity(Letters(rawSpan), Letters(newSpan)) >= 0.5);
            if (keep) kept.Add(new Replacement(a[i0].Start, a[i - 1].End, rawSpan, newSpan));
        }
        return kept;
    }

    /// <summary>1 - Levenshtein distance / longer length (the learning filter's measure).</summary>
    public static double Similarity(string a, string b) => CleanupText.Similarity(a, b);
}
