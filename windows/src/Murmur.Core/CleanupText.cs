// Port of app/CleanupText.swift.
using System.Collections.Frozen;
using System.Text;

namespace Murmur.Core;

// Tokenizer, normalization, similarity and the negation/number lexicons shared by the guards and rules.

/// <summary>Swift <c>CleanupToken.Kind</c>.</summary>
public enum TokenKind { Word, Punct, Space }

public struct CleanupToken : IEquatable<CleanupToken>
{
    public TokenKind Kind;
    public string Text;

    public CleanupToken(TokenKind kind, string text) { Kind = kind; Text = text; }

    public readonly string Lower => SwiftText.Lowercased(Text).Replace("’", "'");
    public readonly bool IsTerminal => Kind == TokenKind.Punct && Text is "." or "?" or "!" or "\n";
    public readonly bool IsDash => Kind == TokenKind.Punct && Text is "—" or "–" or "-" or "--";
    public readonly bool IsComma => Kind == TokenKind.Punct && Text == ",";
    /// <summary>Any punctuation that bounds a phrase (comma, dash, colon, semicolon, terminal).</summary>
    public readonly bool IsBoundary => Kind == TokenKind.Punct && (IsTerminal || IsComma || IsDash || Text is ";" or ":");

    public readonly bool Equals(CleanupToken o) => Kind == o.Kind && Text == o.Text;
    public override readonly bool Equals(object? o) => o is CleanupToken t && Equals(t);
    public override readonly int GetHashCode() => HashCode.Combine(Kind, Text);
    public static bool operator ==(CleanupToken a, CleanupToken b) => a.Equals(b);
    public static bool operator !=(CleanupToken a, CleanupToken b) => !a.Equals(b);
}

public static class CleanupText
{
    internal sealed class TextData
    {
        public string[] FunctionWords { get; set; } = [];
        public string[] Hesitations { get; set; } = [];
        public string[] BasicNegations { get; set; } = [];
        public string[] NegationCore { get; set; } = [];
        public string[] NegationStems { get; set; } = [];
        public string[] NegationWords { get; set; } = [];
        public string[] ApostropheNegations { get; set; } = [];
        public Dictionary<string, string[]> TruncatedNegations { get; set; } = new();
        public Dictionary<string, int> Cardinals { get; set; } = new();
        public Dictionary<string, int> Ordinals { get; set; } = new();
        public string[] Abbreviations { get; set; } = [];
        public string[] Titles { get; set; } = [];
        public string[] GrammarWords { get; set; } = [];
        public string[] ScreenPrivateTypes { get; set; } = [];
        public int SelectedLimit { get; set; }
        public int ClipboardLimit { get; set; }
        public int WindowLimit { get; set; }
        public int InstructionsLimit { get; set; }
        public int SectioningDefaultTarget { get; set; }
        public int ParagraphMinWords { get; set; }
    }

    internal static readonly TextData Data = SharedData.Load<TextData>("cleanup_text");

    /// <summary>Words a paragraph needs before its closing line break counts as a paragraph break.</summary>
    public static readonly int ParagraphMinWords = Data.ParagraphMinWords;

    /// <summary>Closed-class words shared by <c>CleanupGuard.GrammarWords</c> and the learning filter.</summary>
    public static readonly FrozenSet<string> FunctionWords = Data.FunctionWords.ToFrozenSet();
    public static readonly FrozenSet<string> Hesitations = Data.Hesitations.ToFrozenSet();
    public static readonly FrozenSet<string> BasicNegations = Data.BasicNegations.ToFrozenSet();
    public static readonly FrozenSet<string> NegationCore = Data.NegationCore.ToFrozenSet();
    /// <summary>Stems of negative contractions: stem + "t" ("dont") or stem + "'t" ("don't").</summary>
    public static readonly IReadOnlyList<string> NegationStems = Data.NegationStems;
    public static readonly FrozenSet<string> NegationWords = Data.NegationWords.ToFrozenSet();
    public static readonly FrozenSet<string> ApostropheNegations = Data.ApostropheNegations.ToFrozenSet();
    /// <summary>Negative contractions speech recognition sometimes returns without "'t", with the completions cleanup may write.</summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> TruncatedNegations =
        Data.TruncatedNegations.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet());
    public static readonly FrozenDictionary<string, int> Cardinals = Data.Cardinals.ToFrozenDictionary();
    public static readonly FrozenDictionary<string, int> Ordinals = Data.Ordinals.ToFrozenDictionary();
    public static readonly FrozenSet<string> Abbreviations = Data.Abbreviations.ToFrozenSet();
    public static readonly FrozenSet<string> Titles = Data.Titles.ToFrozenSet();

    /// <summary>1 - Levenshtein distance / longer length, over Characters (canonical equivalence).</summary>
    public static double Similarity(string a, string b)
    {
        var x = SwiftText.Characters(a).Select(c => string.Join(",", SwiftText.CanonicalDecomposition(c))).ToArray();
        var y = SwiftText.Characters(b).Select(c => string.Join(",", SwiftText.CanonicalDecomposition(c))).ToArray();
        if (x.Length == 0 || y.Length == 0) return x.Length == 0 && y.Length == 0 ? 1 : 0;
        var previous = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) previous[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            var current = new int[y.Length + 1];
            current[0] = i;
            for (int j = 1; j <= y.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - (double)previous[y.Length] / Math.Max(x.Length, y.Length);
    }

    /// <summary>Swift <c>isWordChar(Character)</c>; <paramref name="c"/> is one grapheme cluster.</summary>
    public static bool IsWordChar(string c) => SwiftText.IsLetter(c) || SwiftText.IsNumber(c) || c == "'" || c == "’";

    public static List<CleanupToken> Tokenize(string s)
    {
        var chars = SwiftText.Characters(s);
        var @out = new List<CleanupToken>();
        int i = 0;
        while (i < chars.Count)
        {
            var c = chars[i];
            if (c == "\n")
            {
                @out.Add(new(TokenKind.Punct, "\n")); i++;
            }
            else if (SwiftText.IsWhitespace(c))
            {
                int j = i;
                while (j < chars.Count && SwiftText.IsWhitespace(chars[j]) && chars[j] != "\n") j++;
                @out.Add(new(TokenKind.Space, string.Concat(chars.Skip(i).Take(j - i)))); i = j;
            }
            else if (IsWordChar(c) || (c == "$" && i + 1 < chars.Count && SwiftText.IsNumber(chars[i + 1])))
            {
                int j = i + 1;
                while (j < chars.Count)
                {
                    var d = chars[j];
                    if (IsWordChar(d)) { j++; continue; }
                    if (d == "%" && SwiftText.IsNumber(chars[j - 1])) { j++; continue; }
                    if (j + 1 < chars.Count)
                    {
                        var prev = chars[j - 1]; var next = chars[j + 1];
                        if (d == "-" && IsWordChar(prev) && IsWordChar(next)) { j++; continue; }
                        if ((d == "." || d == "," || d == ":") && SwiftText.IsNumber(prev) && SwiftText.IsNumber(next)) { j++; continue; }
                    }
                    break;
                }
                // Honorifics keep their period so "Ms. Alvarez" is not read as a sentence break.
                if (j < chars.Count && chars[j] == "." && Abbreviations.Contains(SwiftText.Lowercased(string.Concat(chars.Skip(i).Take(j - i))))) j++;
                @out.Add(new(TokenKind.Word, string.Concat(chars.Skip(i).Take(j - i)))); i = j;
            }
            else if (c == "-")
            {
                int j = i;
                while (j < chars.Count && chars[j] == "-") j++;
                @out.Add(new(TokenKind.Punct, j - i > 1 ? "--" : "-")); i = j;
            }
            else
            {
                @out.Add(new(TokenKind.Punct, c)); i++;
            }
        }
        return @out;
    }

    public static string Render(IEnumerable<CleanupToken> tokens) => string.Concat(tokens.Select(t => t.Text));

    /// <summary>Spacing/punctuation repair applied only after an edit has been made.</summary>
    public static List<CleanupToken> Normalize(IEnumerable<CleanupToken> tokens)
    {
        var t = new List<CleanupToken>();
        foreach (var tok in tokens)
        {
            if (tok.Kind == TokenKind.Space)
            {
                if (t.Count == 0 || t[^1].Kind == TokenKind.Space || t[^1].Text == "\n") continue;
                t.Add(new(TokenKind.Space, " "));
                continue;
            }
            if (tok.Kind == TokenKind.Punct && tok.Text is "," or "." or "?" or "!" or ";" or ":")
            {
                while (t.Count > 0 && t[^1].Kind == TokenKind.Space) t.RemoveAt(t.Count - 1);
                if (tok.Text == ",")
                {
                    if (t.Count == 0 || t[^1].IsTerminal || t[^1].IsComma || t[^1].Text == ":") continue;
                }
                else if (t.Count > 0 && t[^1].IsComma)
                {
                    t.RemoveAt(t.Count - 1);
                }
            }
            if (tok.Text == "\n") while (t.Count > 0 && t[^1].Kind == TokenKind.Space) t.RemoveAt(t.Count - 1);
            t.Add(tok);
        }
        while (t.Count > 0 && t[^1].Kind == TokenKind.Space) t.RemoveAt(t.Count - 1);
        while (t.Count > 0 && (t[0].Kind == TokenKind.Space || t[0].IsComma)) t.RemoveAt(0);
        return t;
    }

    public static string Capitalized(string word)
    {
        var chars = SwiftText.Characters(word);
        if (chars.Count == 0 || !SwiftText.IsLowercase(chars[0])) return word;
        // Leave mixed-case words such as "iPhone" or "eBay" alone.
        if (chars.Skip(1).Any(SwiftText.IsUppercase)) return word;
        return SwiftText.Uppercased(chars[0]) + string.Concat(chars.Skip(1));
    }

    public static int? PrevNonSpace(IReadOnlyList<CleanupToken> t, int i)
    {
        for (int j = i - 1; j >= 0; j--) if (t[j].Kind != TokenKind.Space) return j;
        return null;
    }

    public static int? NextNonSpace(IReadOnlyList<CleanupToken> t, int i)
    {
        for (int j = i + 1; j < t.Count; j++) if (t[j].Kind != TokenKind.Space) return j;
        return null;
    }

    public static bool IsSentenceStart(IReadOnlyList<CleanupToken> t, int i)
    {
        var p = PrevNonSpace(t, i);
        return p is null || t[p.Value].IsTerminal;
    }

    public static int? NextWord(IReadOnlyList<CleanupToken> t, int from)
    {
        for (int j = from; j < t.Count; j++) if (t[j].Kind == TokenKind.Word) return j;
        return null;
    }

    /// <summary>True for tokens inside quotation marks. Quoted speech is never edited; an unbalanced straight quote protects everything after it.</summary>
    public static bool[] QuotedMask(IReadOnlyList<CleanupToken> t)
    {
        var mask = new bool[t.Count];
        bool inStraight = false; int curlyDepth = 0;
        for (int i = 0; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Kind == TokenKind.Punct && tok.Text == "\"") { inStraight = !inStraight; mask[i] = true; continue; }
            if (tok.Kind == TokenKind.Punct && tok.Text == "“") { curlyDepth++; mask[i] = true; continue; }
            if (tok.Kind == TokenKind.Punct && tok.Text == "”") { curlyDepth = Math.Max(0, curlyDepth - 1); mask[i] = true; continue; }
            mask[i] = inStraight || curlyDepth > 0;
        }
        return mask;
    }

    /// <summary><paramref name="text"/> with its paragraph breaks joined into spaces: line breaks right after a sentence end (a . ? or ! token, so not "Dr.") that close at least <see cref="ParagraphMinWords"/> words.</summary>
    public static string JoiningParagraphs(string text)
    {
        var t = Tokenize(text);
        var @out = new StringBuilder();
        int words = 0, i = 0;
        while (i < t.Count)
        {
            if (t[i].Text == "\n")
            {
                if (words >= ParagraphMinWords && PrevNonSpace(t, i) is { } p && t[p].Kind == TokenKind.Punct && t[p].Text is "." or "?" or "!")
                {
                    while (i < t.Count && (t[i].Text == "\n" || t[i].Kind == TokenKind.Space)) i++;
                    @out.Append(' '); words = 0; continue;
                }
                words = 0;
            }
            else if (t[i].Kind == TokenKind.Word) words++;
            @out.Append(t[i].Text); i++;
        }
        return @out.ToString();
    }

    public static bool IsNegation(string lower) =>
        NegationWords.Contains(lower) || lower.EndsWith("n't", StringComparison.Ordinal) || TruncatedNegations.ContainsKey(lower);

    /// <summary>A normalized numeric value for a word, or null when the word carries no number.</summary>
    public static string? NumberValue(string lower)
    {
        if (Cardinals.TryGetValue(lower, out var v) || Ordinals.TryGetValue(lower, out v)) return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chars = SwiftText.Characters(lower);
        if (chars.Contains("-"))
        {
            // "sixty-eight", "twenty-first"
            var parts = lower.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var values = parts.Select(p => Cardinals.TryGetValue(p, out var x) || Ordinals.TryGetValue(p, out x) ? (int?)x : null).Where(x => x is not null).Select(x => x!.Value).ToList();
            if (values.Count == parts.Length && values.Count > 0) return values.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (!chars.Any(SwiftText.IsNumber)) return null;
        var s = lower.Replace("$", "").Replace("%", "");
        if (!s.Contains(':')) s = s.Replace(",", "");
        // Strip trailing letters such as "3pm", "1st", "20th".
        var rest = SwiftText.Characters(s);
        while (rest.Count > 0 && (SwiftText.IsLetter(rest[^1]) || rest[^1] == "'")) rest.RemoveAt(rest.Count - 1);
        return rest.Count == 0 ? null : string.Concat(rest);
    }
}
