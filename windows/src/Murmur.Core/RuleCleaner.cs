// Port of app/RuleCleaner.swift.
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Murmur.Core;

// The deterministic local cleaner: fillers, corrections, lists, spoken commands and email layout.

/// <summary>Swift protocol <c>Cleaner</c>. <see cref="Clean"/> throws when cleanup fails.</summary>
public interface ICleaner
{
    string Clean(string raw, CleanupOptions options);
}

/// <summary>Deterministic, conservative local baseline. When a rule is unsure it leaves text unchanged.</summary>
public sealed partial class RuleCleaner : ICleaner
{
    [JsonConverter(typeof(CamelCaseEnumConverter<Cue>))]
    public enum Cue { Sorry, No, IMean, Actually, OrRather, NoWait, Scratch }

    internal sealed record CueData(string[] Words, Cue Cue);
    internal sealed record PatternData(string Pattern, bool CaseInsensitive, bool DotMatchesLineSeparators);
    internal sealed class RulesData
    {
        public string[] StutterWords { get; set; } = [];
        public string[] CommaKeepers { get; set; } = [];
        public CueData[] CuePatterns { get; set; } = [];
        public string[] ApologyLead { get; set; } = [];
        public string[] Anchors { get; set; } = [];
        public string[] Weekdays { get; set; } = [];
        public string[] Months { get; set; } = [];
        public string[] MonthsAndMay { get; set; } = [];
        public string[] RelativeTimes { get; set; } = [];
        public string[] Colors { get; set; } = [];
        public string[] StrongListCues { get; set; } = [];
        public string[] CardinalWords { get; set; } = [];
        public string[] OrdinalWords { get; set; } = [];
        public string[] CommandBlockersBefore { get; set; } = [];
        public string[] CommandBlockersAfter { get; set; } = [];
        public string[] DashBlockersBefore { get; set; } = [];
        public string[] DomainEndings { get; set; } = [];
        public Dictionary<string, int> SpokenScales { get; set; } = new();
        public string[] IndianScales { get; set; } = [];
        public PatternData InsteadFragment { get; set; } = null!;
        public PatternData SplitCue { get; set; } = null!;
        public PatternData EmailFrame { get; set; } = null!;
        public PatternData SignOff { get; set; } = null!;
        public string CorrectionOpening { get; set; } = "";
        public string IcuWord { get; set; } = "";
        public string IcuIgnorable { get; set; } = "";
        public string IcuLetter { get; set; } = "";
        public string IcuNumber { get; set; } = "";
    }

    internal static readonly RulesData Data = SharedData.Load<RulesData>("rule_cleaner");

    public string Clean(string raw, CleanupOptions options)
    {
        var text = options.Spoken ? SpokenCommands(raw) : raw;
        if (options.Fillers) text = RemoveFillers(text);
        if (options.Corrections)
        {
            if (options.Polish) text = ApplyInsteadCorrections(text);
            text = ApplyCorrections(text);
        }
        if (options.Lists) text = FormatList(text);
        if (options.Punctuation) text = CapitalizeSentences(text);
        return text;
    }

    static readonly FrozenSet<string> SentenceTitles = new[] { "mr", "mrs", "ms", "dr", "st", "vs" }.ToFrozenSet();
    static readonly Regex LoneI = IcuRegex(@"\bi(?=\b|')");

    /// <summary>Local punctuation: capitals at sentence starts and for "I". A . ? or ! ends a sentence only before whitespace,
    /// and a period after a dotted abbreviation or a title does not.</summary>
    public static string CapitalizeSentences(string text)
    {
        var @out = new StringBuilder();
        bool start = true, ended = false;
        var word = new List<string>();
        foreach (var c in SwiftText.Characters(text))
        {
            if (start && SwiftText.IsLetter(c)) { @out.Append(SwiftText.Uppercased(c)); word = [c]; start = false; ended = false; continue; }
            if (SwiftText.IsLetter(c) || SwiftText.IsNumber(c)) { start = false; ended = false; }
            else if (c == ".") { if (word.Count == 0 || word[^1] != ".") ended = !word.Contains(".") && !SentenceTitles.Contains(SwiftText.Lowercased(string.Concat(word))); }
            else if (c is "?" or "!") ended = true;
            else if (c == "\n" || (SwiftText.IsWhitespace(c) && ended)) start = true;
            if (SwiftText.IsWhitespace(c)) word = []; else word.Add(c);
            @out.Append(c);
        }
        return LoneI.Replace(@out.ToString(), "I");
    }

    public static readonly FrozenSet<string> StutterWords = Data.StutterWords.ToFrozenSet();
    /// <summary>Words after which a comma stays when a following hesitation is removed ("So, um, I think").</summary>
    public static readonly FrozenSet<string> CommaKeepers = Data.CommaKeepers.ToFrozenSet();

    public static bool IsHesitation(CleanupToken tok)
    {
        if (tok.Kind != TokenKind.Word || !CleanupText.Hesitations.Contains(tok.Lower)) return false;
        // All-caps forms such as "ER" or "MM" are abbreviations, not hesitations.
        return !(SwiftText.Count(tok.Text) > 1 && tok.Text == SwiftText.Uppercased(tok.Text));
    }

    static bool IsComma(List<CleanupToken> t, int? i) => i is { } k && t[k].IsComma;
    static void SetText(List<CleanupToken> t, int i, string text) => t[i] = new(t[i].Kind, text);

    public static string RemoveFillers(string text)
    {
        var t = CleanupText.Tokenize(text);
        bool changed = false;
        int i = 0;
        while (i < t.Count)
        {
            var mask = CleanupText.QuotedMask(t);
            if (t[i].Kind != TokenKind.Word || mask[i]) { i++; continue; }
            var w = t[i].Lower;
            var start = CleanupText.IsSentenceStart(t, i);
            var p = CleanupText.PrevNonSpace(t, i);
            var n = CleanupText.NextNonSpace(t, i);
            bool pComma = IsComma(t, p), nComma = IsComma(t, n);
            if (IsHesitation(t[i]))
            {
                int lo = i, hi = i; bool capitalize = start;
                if (pComma && nComma && p is { } pp && n is { } nn)
                {
                    int? wb = CleanupText.PrevNonSpace(t, pp) is { } x && t[x].Kind == TokenKind.Word ? x : null;
                    int? wa = CleanupText.NextNonSpace(t, nn) is { } y && t[y].Kind == TokenKind.Word ? y : null;
                    if (wb is { } b && wa is { } a && t[b].Lower == t[a].Lower && !mask[b])
                    {
                        // Restart across a hesitation: "Let's, um, let's go" keeps the second copy.
                        lo = b; hi = nn; capitalize = CleanupText.IsSentenceStart(t, b);
                    }
                    else if (wb is { } b2 && (CommaKeepers.Contains(t[b2].Lower)
                             || (Strings.FirstIsUppercase(t[b2].Text) && t[b2].Lower != "i" && CleanupText.IsSentenceStart(t, b2))))
                    {
                        hi = nn;
                    }
                    else
                    {
                        lo = pp; hi = nn;
                    }
                }
                else if (nComma && n is { } n2)
                {
                    hi = n2;
                }
                else if (start && n is { } n3 && t[n3].IsTerminal && t[n3].Text != "\n")
                {
                    hi = n3; // A hesitation that is its own sentence: "Hmm. Okay."
                }
                else if (pComma && p is { } p2 && (n is not { } n4 || t[n4].IsTerminal || t[n4].Text == ";"))
                {
                    lo = p2;
                }
                Remove(t, lo, hi, capitalize);
                changed = true; i = Math.Max(0, lo - 1); continue;
            }
            int? spanEnd = null;
            if (w == "like") spanEnd = i;
            if (w == "you" && n is { } k && k == i + 2 && t[k].Kind == TokenKind.Word && t[k].Lower == "know") spanEnd = k;
            if (spanEnd is { } e)
            {
                var ne = CleanupText.NextNonSpace(t, e);
                bool neComma = IsComma(t, ne);
                bool neEnd = ne is not { } nx || t[nx].IsTerminal;
                if (pComma && neComma && p is { } p3 && ne is { } ne1)
                {
                    Remove(t, p3, ne1, false); changed = true; i = Math.Max(0, p3 - 1); continue;
                }
                if (start && neComma && ne is { } ne2)
                {
                    Remove(t, i, ne2, true); changed = true; i = Math.Max(0, i - 1); continue;
                }
                if (w == "you" && pComma && neEnd && p is { } p4)
                {
                    Remove(t, p4, e, false); changed = true; i = Math.Max(0, p4 - 1); continue;
                }
            }
            i++;
        }
        i = 0;
        while (i < t.Count)
        {
            var mask = CleanupText.QuotedMask(t);
            if (t[i].Kind != TokenKind.Word || mask[i] || !StutterWords.Contains(t[i].Lower)
                || CleanupText.NextWord(t, i + 1) is not { } k || t[k].Lower != t[i].Lower) { i++; continue; }
            var between = t.GetRange(i + 1, k - i - 1);
            if (!between.All(x => x.Kind == TokenKind.Space || x.IsComma) || between.Count(x => x.IsComma) > 1) { i++; continue; }
            var start = CleanupText.IsSentenceStart(t, i);
            t.RemoveRange(i, k - i);
            if (start) SetText(t, i, CleanupText.Capitalized(t[i].Text));
            changed = true;
        }
        return changed ? CleanupText.Render(CleanupText.Normalize(t)) : text;
    }

    public static void Remove(List<CleanupToken> t, int lo, int hi, bool capitalizeNext)
    {
        int end = hi + 1;
        while (end < t.Count && t[end].Kind == TokenKind.Space) end++;
        t.RemoveRange(lo, end - lo);
        if (lo > 0 && lo < t.Count && t[lo - 1].Kind == TokenKind.Word && t[lo].Kind == TokenKind.Word)
            t.Insert(lo, new(TokenKind.Space, " "));
        if (capitalizeNext && CleanupText.NextWord(t, lo) is { } k && !t.Skip(lo).Take(k - lo).Any(x => x.Kind == TokenKind.Punct && !x.IsComma))
            SetText(t, k, CleanupText.Capitalized(t[k].Text));
    }

    public static readonly IReadOnlyList<(IReadOnlyList<string> Words, Cue Cue)> CuePatterns =
        Data.CuePatterns.Select(c => ((IReadOnlyList<string>)c.Words, c.Cue)).ToArray();
    public static readonly FrozenSet<string> ApologyLead = Data.ApologyLead.ToFrozenSet();
    public static readonly FrozenSet<string> Anchors = Data.Anchors.ToFrozenSet();
    public static readonly FrozenSet<string> Weekdays = Data.Weekdays.ToFrozenSet();
    public static readonly FrozenSet<string> Months = Data.Months.ToFrozenSet();
    /// <summary>"may" is a modal, so <see cref="Months"/> omits it: <see cref="Category"/> accepts only a capitalized "May", the guard's lowercase word lists accept it always.</summary>
    public static readonly FrozenSet<string> MonthsAndMay = Data.MonthsAndMay.ToFrozenSet();
    public static readonly FrozenSet<string> RelativeTimes = Data.RelativeTimes.ToFrozenSet();
    public static readonly FrozenSet<string> Colors = Data.Colors.ToFrozenSet();

    public static string? Category(IReadOnlyList<CleanupToken> t, int i)
    {
        var w = t[i].Lower;
        if (Weekdays.Contains(w)) return "weekday";
        if (Months.Contains(w) || t[i].Text == "May") return "month";
        if (RelativeTimes.Contains(w)) return "time";
        if (Colors.Contains(w)) return "color";
        if (CleanupText.Ordinals.ContainsKey(w)) return "ordinal";
        if (CleanupText.NumberValue(w) is not null) return "number";
        if (Strings.FirstIsUppercase(t[i].Text) && w != "i" && !Strings.HasPrefix(w, "i'") && !CleanupText.IsSentenceStart(t, i)) return "name";
        return null;
    }

    public static (Cue Cue, int End)? MatchCue(IReadOnlyList<CleanupToken> t, int i)
    {
        foreach (var (words, cue) in CuePatterns)
        {
            int idx = i, last = i; bool ok = true;
            for (int n = 0; n < words.Count; n++)
            {
                if (n > 0)
                {
                    if (CleanupText.NextNonSpace(t, idx) is not { } k || k != idx + 2 || t[k].Kind != TokenKind.Word) { ok = false; break; }
                    idx = k;
                }
                if (t[idx].Kind != TokenKind.Word || t[idx].Lower != words[n]) { ok = false; break; }
                last = idx;
            }
            if (ok) return (cue, last);
        }
        return null;
    }

    public static bool IsNumberWord(IReadOnlyList<CleanupToken> t, int i) =>
        t[i].Kind == TokenKind.Word && !CleanupText.Ordinals.ContainsKey(t[i].Lower) && CleanupText.NumberValue(t[i].Lower) is not null;

    static Regex Compile(PatternData p) =>
        IcuRegex(p.Pattern, (p.CaseInsensitive ? RegexOptions.IgnoreCase : 0) | (p.DotMatchesLineSeparators ? RegexOptions.Singleline : 0));

    public static readonly Regex InsteadFragment = Compile(Data.InsteadFragment);
    static readonly FrozenSet<string> InsteadCategories = new[] { "weekday", "month", "time", "color" }.ToFrozenSet();
    static readonly FrozenSet<string> InsteadBlockers = new[] { "and", "or", "but", "if", "unless", "because", "while", "instead" }.ToFrozenSet();

    /// <summary>shortcut: only one known date/time/color slot, expand only with evidence for safe scope.</summary>
    public static string ApplyInsteadCorrections(string text)
    {
        var @out = text;
        var mask = CleanupText.QuotedMask(CleanupText.Tokenize(text));
        foreach (var match in InsteadFragment.Matches(text).Reverse())
        {
            var prefix = CleanupText.Tokenize(text[..match.Groups[1].Index]);
            var tokens = CleanupText.Tokenize(match.Value);
            int offset = prefix.Count;
            if (!(offset + 3 < mask.Length) || mask[offset] || mask[offset + 3]) continue;
            if (Category(tokens, 0) is not { } category || !InsteadCategories.Contains(category)
                || category != Category(tokens, 3) || tokens[0].Lower == tokens[3].Lower) continue;
            var sentence = Enumerable.Reverse(prefix).TakeWhile(x => !x.IsTerminal).ToList();
            // Multiple alternatives, negations and subordinate clauses leave replacement scope unclear.
            if (sentence.Any(x => x.Kind == TokenKind.Word && (Category([x], 0) == category || CleanupText.IsNegation(x.Lower) || InsteadBlockers.Contains(x.Lower)))) continue;
            var replacement = category is "color" or "time" ? tokens[3].Lower : tokens[3].Text;
            @out = @out[..match.Index] + replacement + "." + @out[(match.Index + match.Length)..];
        }
        return @out;
    }

    /// <summary>Speech models often end a sentence around a cue ("Tuesday. No wait. Wednesday."). Rejoin it into the comma form
    /// the rules expect, and keep the rejoined text only if a correction applies.</summary>
    public static string ApplyCorrections(string text)
    {
        var merged = MergeSplitCues(text);
        if (merged != text)
        {
            var corrected = ApplyCommaCorrections(merged);
            if (corrected != merged) return corrected;
        }
        return ApplyCommaCorrections(text);
    }

    /// <summary>Every <see cref="CuePatterns"/> cue except "scratch that"; "wait no" may carry a comma, "actually" only before a one-word repair.</summary>
    public static readonly Regex SplitCue = Compile(Data.SplitCue);

    public static string MergeSplitCues(string text)
    {
        var @out = text;
        foreach (var m in SplitCue.Matches(text).Reverse())
        {
            // A leading interjection stays as a word ("sentence. Oh, no, I mean" -> "sentence, oh, no, I mean").
            var lead = m.Groups[1].Success ? SwiftText.Lowercased(m.Groups[1].Value) + ", " : "";
            var cue = SwiftText.Lowercased(m.Groups[2].Value).Replace(",", "");
            var word = m.Groups[3].Value;
            // Only words known to be lowercase mid-sentence; names, weekdays and months keep their capital.
            var lower = SwiftText.Lowercased(word);
            if (RelativeTimes.Contains(lower) || Colors.Contains(lower) || Anchors.Contains(lower)
                || CleanupText.Ordinals.ContainsKey(lower) || CleanupText.NumberValue(lower) is not null) word = lower;
            var replacement = $", {lead}{cue}, {word}";
            if (cue == "actually")
            {
                var prefix = CleanupText.Tokenize(text[..m.Index]);
                if (prefix.Count == 0 || CleanupText.QuotedMask(prefix)[^1]) continue;
                var fragment = prefix[^1].Text + replacement;
                if (ApplyCommaCorrections(fragment) == fragment) continue;
            }
            @out = @out[..m.Index] + replacement + @out[(m.Index + m.Length)..];
        }
        return @out;
    }

    static readonly FrozenSet<string> ParentheticalBlockers = new[] { "and", "but", "or", "so", "then", "number", "step", "item" }.ToFrozenSet();

    public static string ApplyCommaCorrections(string text)
    {
        var t = CleanupText.Tokenize(text);
        bool changed = false;
        int edits = 0, i = 0;
        while (i < t.Count && edits < 20)
        {
            var mask = CleanupText.QuotedMask(t);
            if (t[i].Kind != TokenKind.Word || mask[i] || MatchCue(t, i) is not (var cue, var cueEnd) || CleanupText.PrevNonSpace(t, i) is not { } p) { i++; continue; }
            var before = t[p];
            bool scratch = cue == Cue.Scratch;
            if (!(before.IsComma || before.IsDash || (scratch && (before.Text == "." || before.Text == "!")))) { i++; continue; }
            var q = CleanupText.NextNonSpace(t, cueEnd);
            bool qIsSep = q is { } qq && (t[qq].IsComma || t[qq].IsDash || t[qq].Text == ":");
            bool needsSep = cue is Cue.Sorry or Cue.No or Cue.NoWait or Cue.Scratch;
            if (needsSep && !qIsSep) { i++; continue; }
            // Interregnum: separators, hesitations, a secondary cue ("no, sorry,"), or a short
            // negated restatement of the reparandum ("no, not Tuesday, Thursday").
            int r = cueEnd + 1;
            while (r < t.Count)
            {
                if (t[r].Kind == TokenKind.Space || t[r].IsComma || t[r].IsDash || t[r].Text == ":") { r++; continue; }
                if (IsHesitation(t[r])) { r++; continue; }
                if (t[r].Kind == TokenKind.Word && MatchCue(t, r) is (var c2, var e2) && c2 != Cue.Actually && c2 != Cue.Scratch && c2 != Cue.No) { r = e2 + 1; continue; }
                if (t[r].Kind == TokenKind.Word && t[r].Lower == "not")
                {
                    int k2 = r + 1, words = 0;
                    while (k2 < t.Count && !t[k2].IsBoundary) { if (t[k2].Kind == TokenKind.Word) words++; k2++; }
                    if (words >= 1 && words <= 3 && k2 < t.Count && (t[k2].IsComma || t[k2].IsDash)
                        && CleanupText.NextNonSpace(t, k2) is { } after && t[after].Kind == TokenKind.Word) { r = k2 + 1; continue; }
                }
                break;
            }
            if (!(r < t.Count) || t[r].Kind != TokenKind.Word || mask[r]) { i++; continue; }
            var repair = new List<int>();
            int k = r;
            while (k < t.Count && !t[k].IsBoundary) { if (t[k].Kind == TokenKind.Word) repair.Add(k); k++; }
            if (repair.Count == 0) { i++; continue; }
            int? repairBoundary = k < t.Count ? k : null;

            var window = new List<int>();
            int w = p - 1;
            if (scratch)
            {
                while (w >= 0 && !t[w].IsTerminal) { if (t[w].Kind == TokenKind.Word) window.Insert(0, w); w--; }
                if (!before.IsTerminal || window.Count > 0)
                {
                    if (window.Count == 0 || window.Count > 30 || window.Any(x => mask[x])) { i++; continue; }
                    DeleteRange(t, window[0], r, true);
                    changed = true; edits++; i = 0; continue;
                }
                i++; continue;
            }
            while (w >= 0 && !t[w].IsBoundary) { if (t[w].Kind == TokenKind.Word) window.Insert(0, w); w--; }
            if (window.Count == 0 || window.Any(x => mask[x])) { i++; continue; }
            int lastW = window[^1];
            if (cue == Cue.Sorry && ApologyLead.Contains(t[lastW].Lower)) { i++; continue; }

            int? from = null;
            // Number run + noun restated with a new number ("twelve chairs, I mean fifteen chairs").
            if (window.Count >= 2 && !IsNumberWord(t, lastW) && IsNumberWord(t, window[^2]) && IsNumberWord(t, r))
            {
                int rk = 0;
                while (rk < repair.Count && IsNumberWord(t, repair[rk])) rk++;
                if (rk < repair.Count && t[repair[rk]].Lower == t[lastW].Lower)
                {
                    int s = window.Count - 2;
                    while (s > 0 && IsNumberWord(t, window[s - 1])) s--;
                    from = window[s];
                }
            }
            // Rule A: same-category swap ("Tuesday, sorry, Thursday"); number runs move together.
            if (from is null && Category(t, lastW) is { } cat && cat == Category(t, r) && t[lastW].Lower != t[r].Lower)
            {
                int s = window.Count - 1;
                if (cat == "number") while (s > 0 && IsNumberWord(t, window[s - 1])) s--;
                if (cat == "name" && s > 0 && CleanupText.Titles.Contains(t[window[s - 1]].Lower) && CleanupText.Titles.Contains(t[r].Lower)) s--;
                from = window[s];
            }
            if (from is null && Category(t, lastW) == "name" && CleanupText.Titles.Contains(t[r].Lower) && repair.Count >= 2
                && window.Count >= 2 && CleanupText.Titles.Contains(t[window[^2]].Lower))
            {
                from = window[^2]; // "Ms. Alvarez, no, Mr. Alvarez"
            }
            // Rule B: repair restarts a short phrase with the same anchor word ("at the office, I mean at home").
            if (from is null && Anchors.Contains(t[r].Lower) && repair.Count <= 6)
            {
                int j = window.FindLastIndex(x => t[x].Lower == t[r].Lower);
                if (j >= 0)
                {
                    int span = window.Count - j;
                    if (span >= 2 && span <= 6) from = window[j];
                }
            }
            if (from is not { } start) { i++; continue; } // Ambiguous: keep the original wording.
            // A comma closing a parenthetical repair goes too ("the design team, I mean the
            // product team, to the review"), unless it separates a clause or a list item.
            if (repairBoundary is { } b && t[b].IsComma && before.IsComma
                && CleanupText.PrevNonSpace(t, start) is { } prevOfStart && t[prevOfStart].Kind == TokenKind.Word
                && CleanupText.NextNonSpace(t, b) is { } nextWordAfter && t[nextWordAfter].Kind == TokenKind.Word
                && !ParentheticalBlockers.Contains(t[nextWordAfter].Lower)
                && !CleanupText.Ordinals.ContainsKey(t[nextWordAfter].Lower) && Strings.FirstIsLowercase(t[nextWordAfter].Text))
            {
                t.RemoveAt(b);
            }
            DeleteRange(t, start, r, CleanupText.IsSentenceStart(t, start));
            changed = true; edits++; i = 0;
        }
        return changed ? CleanupText.Render(CleanupText.Normalize(t)) : text;
    }

    public static void DeleteRange(List<CleanupToken> t, int from, int repairStart, bool capitalize)
    {
        t.RemoveRange(from, repairStart - from);
        if (capitalize && from < t.Count && t[from].Kind == TokenKind.Word) SetText(t, from, CleanupText.Capitalized(t[from].Text));
    }

    public static readonly FrozenSet<string> StrongListCues = Data.StrongListCues.ToFrozenSet();
    public static readonly IReadOnlyList<string> CardinalWords = Data.CardinalWords;
    public static readonly IReadOnlyList<string> OrdinalWords = Data.OrdinalWords;
    static readonly FrozenSet<string> MarkerNouns = new[] { "number", "step", "item" }.ToFrozenSet();

    public static int? MatchMarker(IReadOnlyList<CleanupToken> t, int i, int k)
    {
        if (!(k < OrdinalWords.Count) || t[i].Kind != TokenKind.Word) return null;
        var w = t[i].Lower;
        if (w == OrdinalWords[k] || w == OrdinalWords[k] + "ly") return i;
        if (MarkerNouns.Contains(w) && CleanupText.NextNonSpace(t, i) is { } n && n == i + 2 && t[n].Lower == CardinalWords[k]) return n;
        return null;
    }

    public static string FormatList(string text)
    {
        var t = CleanupText.Tokenize(text);
        for (int m1 = 0; m1 < t.Count; m1++)
        {
            if (MatchMarker(t, m1, 1) is not { } m1End) continue;
            // Lead-in: sentence containing the first marker, or the previous sentence if the marker starts one.
            int s = m1;
            while (s > 0 && !t[s - 1].IsTerminal) s--;
            var leadTokens = t.GetRange(s, m1 - s);
            bool leadIsPrevSentence = false;
            int leadStart = s;
            bool hasLeadWords = leadTokens.Any(x => x.Kind == TokenKind.Word);
            var cueTokens = leadTokens;
            if (!hasLeadWords)
            {
                if (!(s > 0 && (t[s - 1].Text == "." || t[s - 1].Text == ":"))) continue;
                int ps = s - 1;
                while (ps > 0 && !t[ps - 1].IsTerminal) ps--;
                cueTokens = t.GetRange(ps, s - ps);
                leadStart = ps; leadIsPrevSentence = true;
            }
            var lastNonSpace = cueTokens.FindLastIndex(x => x.Kind != TokenKind.Space);
            bool leadEndsColon = lastNonSpace >= 0 && cueTokens[lastNonSpace].Text == ":";
            // A list noun counts as a cue only near the end of the lead-in ("my to-do list for today",
            // "the steps are"), not as a modifier earlier in it ("our list price went up").
            var leadWords = cueTokens.Where(x => x.Kind == TokenKind.Word).ToList();
            bool leadHasCue = leadWords.TakeLast(4).Any(x => StrongListCues.Contains(x.Lower));
            if (!(leadEndsColon || leadHasCue)) continue;
            if (!leadIsPrevSentence && CleanupText.PrevNonSpace(t, m1) is { } pm && !(t[pm].Text == ":" || t[pm].IsComma || t[pm].Kind == TokenKind.Word)) continue;

            // Markers 2...n, each after a separator (, ; .) optionally followed by "and"/"then".
            var markers = new List<(int Start, int End, int Sep)> { (m1, m1End, m1) };
            int k = 2, j = m1End + 1, lastItemEnd = t.Count;
            var mask = CleanupText.QuotedMask(t);
            if (mask[m1]) continue;
            while (j < t.Count)
            {
                if (t[j].Text == "\n") { lastItemEnd = j; break; }
                if (MatchMarker(t, j, k) is { } e && !mask[j])
                {
                    var p = CleanupText.PrevNonSpace(t, j);
                    while (p is { } pp && t[pp].Kind == TokenKind.Word && t[pp].Lower is "and" or "then") p = CleanupText.PrevNonSpace(t, pp);
                    if (p is { } p2 && (t[p2].IsComma || t[p2].Text == ";" || t[p2].IsTerminal))
                    {
                        // A question or exclamation mark belongs to its item.
                        int sep = t[p2].Text is "?" or "!" ? p2 + 1 : p2;
                        markers.Add((j, e, sep)); k++; j = e + 1; continue;
                    }
                }
                if (t[j].IsTerminal)
                {
                    // Sentence-final punctuation ends the list unless the next marker follows it.
                    if (CleanupText.NextWord(t, j + 1) is { } n && MatchMarker(t, n, k) is not null && CleanupText.NextNonSpace(t, j) == n) { j++; continue; }
                    lastItemEnd = t[j].Text is "?" or "!" ? j + 1 : j; break;
                }
                j++;
            }
            if (markers.Count < 2) continue;
            var items = new List<string>();
            for (int n = 0; n < markers.Count; n++)
            {
                var m = markers[n];
                int end = n + 1 < markers.Count ? markers[n + 1].Sep : lastItemEnd;
                var body = t.GetRange(m.End + 1, Math.Max(0, end - m.End - 1));
                while (body.Count > 0 && (body[0].Kind == TokenKind.Space || body[0].IsComma || body[0].Text == ":" || body[0].IsDash)) body.RemoveAt(0);
                while (body.Count > 0 && (body[^1].Kind == TokenKind.Space || body[^1].IsComma || body[^1].Text == ";" || body[^1].Text == ".")) body.RemoveAt(body.Count - 1);
                int wordsInItem = body.Count(x => x.Kind == TokenKind.Word);
                if (wordsInItem < 1 || wordsInItem > 25) { items = []; break; }
                int fi = body.FindIndex(x => x.Kind == TokenKind.Word);
                if (fi >= 0) body[fi] = new(body[fi].Kind, CleanupText.Capitalized(body[fi].Text));
                items.Add($"{n + 1}. " + CleanupText.Render(body));
            }
            if (items.Count != markers.Count) continue;
            var prefix = Strings.TrimWhitespaces(CleanupText.Render(t.Take(leadStart)));
            var lead = SwiftText.Characters(Strings.TrimWhitespaces(CleanupText.Render(t.Skip(leadStart).Take((leadIsPrevSentence ? s : m1) - leadStart))));
            if (!leadIsPrevSentence || !(lead.Count > 0 && lead[^1] is "." or ":"))
            {
                while (lead.Count > 0 && lead[^1] is "," or ":" or " " or ";" or "-" or "—") lead.RemoveAt(lead.Count - 1);
                lead.Add(":");
            }
            int restStart = lastItemEnd;
            if (restStart < t.Count && t[restStart].IsTerminal && t[restStart - 1].Text != "?" && t[restStart - 1].Text != "!") restStart++;
            var rest = restStart < t.Count ? Strings.TrimWhitespaces(CleanupText.Render(t.Skip(restStart))) : "";
            var leadText = string.Concat(lead);
            var @out = prefix.Length == 0 ? leadText : prefix + " " + leadText;
            @out += "\n" + string.Join("\n", items);
            if (rest.Length > 0) @out += "\n" + rest;
            return @out;
        }
        return text;
    }

    internal static Regex IcuRegex(string pattern, RegexOptions options = RegexOptions.None) =>
        new(Icu.Translate(pattern), options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    internal static string TranslateIcu(string pattern) => Icu.Translate(pattern);

    /// <summary>NSRegularExpression (ICU) patterns on .NET. A nested class, so its tables exist before any RuleCleaner regex field is built.</summary>
    static class Icu
    {
        static (int Lo, int Hi)[] Parse(string s) => s.Split(',').Select(r => { var p = r.Split('-'); return (Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16)); }).ToArray();

        static (int Lo, int Hi)[] Subtract((int Lo, int Hi)[] a, (int Lo, int Hi)[] b)
        {
            var o = new List<(int, int)>();
            foreach (var (lo0, hi) in a)
            {
                int lo = lo0;
                foreach (var (blo, bhi) in b)
                {
                    if (bhi < lo || blo > hi) continue;
                    if (blo > lo) o.Add((lo, blo - 1));
                    lo = Math.Max(lo, bhi + 1);
                    if (lo > hi) break;
                }
                if (lo <= hi) o.Add((lo, hi));
            }
            return [.. o];
        }

        static string U(int v) => $"\\u{v:X4}";

        /// <summary>The BMP part as the inside of a character class.</summary>
        static string Bmp((int Lo, int Hi)[] ranges) =>
            string.Concat(ranges.Where(r => r.Lo <= 0xFFFF).Select(r => U(r.Lo) + "-" + U(Math.Min(r.Hi, 0xFFFF))));

        /// <summary>The astral part as surrogate-pair alternatives, one per high surrogate; empty when there is none.</summary>
        static string Astral((int Lo, int Hi)[] ranges)
        {
            var byHigh = new SortedDictionary<int, StringBuilder>();
            foreach (var (lo0, hi) in ranges.Where(r => r.Hi > 0xFFFF))
            {
                for (int lo = Math.Max(lo0, 0x10000); lo <= hi;)
                {
                    int high = 0xD800 + ((lo - 0x10000) >> 10), end = Math.Min(hi, 0x10000 + ((high - 0xD800 + 1) << 10) - 1);
                    if (!byHigh.TryGetValue(high, out var sb)) byHigh[high] = sb = new StringBuilder();
                    sb.Append(U(0xDC00 + ((lo - 0x10000) & 0x3FF))).Append('-').Append(U(0xDC00 + ((end - 0x10000) & 0x3FF)));
                    lo = end + 1;
                }
            }
            return string.Join("|", byHigh.Select(kv => $"{U(kv.Key)}[{kv.Value}]"));
        }

        /// <summary>One scalar in the ranges.</summary>
        static string One((int Lo, int Hi)[] ranges)
        {
            var astral = Astral(ranges);
            return astral.Length == 0 ? $"[{Bmp(ranges)}]" : $"(?:[{Bmp(ranges)}]|{astral})";
        }

        static readonly (int Lo, int Hi)[] Word = Parse(Data.IcuWord), Ignorable = Parse(Data.IcuIgnorable), Letter = Parse(Data.IcuLetter), Number = Parse(Data.IcuNumber);
        static readonly string LetterOne = One(Letter), NumberOne = One(Number), LetterBmp = Bmp(Letter), NumberBmp = Bmp(Number);
        static readonly string LetterAstral = Astral(Letter), NumberAstral = Astral(Number);
        // ICU's \b: never before a Grapheme_Extend or Cf character, and those are skipped when looking back for the previous character.
        // Case-insensitivity is turned off inside it: ICU's boundary ignores the flag, while .NET would case-fold the classes.
        static readonly string Boundary = BuildBoundary();
        static string BuildBoundary()
        {
            string w = One(Word), e = One(Ignorable), wNotE = One(Subtract(Word, Ignorable));
            return $"(?-i:(?!{e})(?:(?<={wNotE}{e}*)(?!{w})|(?<!{wNotE}{e}*)(?={w})))";
        }

        /// <summary>Translates an NSRegularExpression pattern to .NET: <c>\x{H}</c> escapes; <c>\b</c> with ICU's boundary rule; and
        /// <c>\p{L}</c>, <c>\p{N}</c> (bare or in a non-negated class) with ICU's tables, astral scalars included, since .NET regex
        /// sees UTF-16 units. <c>\s</c> (White_Space in both), lookarounds and <c>$</c> mean the same in both for these patterns.</summary>
        internal static string Translate(string pattern)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    char n = pattern[i + 1];
                    if (n == 'x' && i + 2 < pattern.Length && pattern[i + 2] == '{')
                    {
                        int close = pattern.IndexOf('}', i);
                        foreach (var u in char.ConvertFromUtf32(Convert.ToInt32(pattern[(i + 3)..close], 16))) sb.Append(U(u));
                        i = close; continue;
                    }
                    if (n == 'b') { sb.Append(Boundary); i++; continue; }
                    if (pattern.AsSpan(i).StartsWith(@"\p{L}")) { sb.Append(LetterOne); i += 4; continue; }
                    if (pattern.AsSpan(i).StartsWith(@"\p{N}")) { sb.Append(NumberOne); i += 4; continue; }
                    sb.Append(c).Append(n); i++; continue;
                }
                if (c == '[')
                {
                    // A class runs to the first unescaped "]" (these patterns nest no classes).
                    int j = i + 1;
                    while (j < pattern.Length && pattern[j] != ']') j += pattern[j] == '\\' ? 2 : 1;
                    var body = pattern[(i + 1)..j];
                    bool letter = body.Contains(@"\p{L}"), number = body.Contains(@"\p{N}");
                    if (body.StartsWith('^') && (letter || number)) throw new NotSupportedException(@"negated class with \p{L} or \p{N}");
                    body = Regex.Replace(body, @"\\x\{([0-9A-Fa-f]{1,4})\}", m => U(Convert.ToInt32(m.Groups[1].Value, 16))); // BMP only inside a class.
                    if (body.Contains(@"\x{")) throw new NotSupportedException(@"astral \x{} in a class");
                    body = body.Replace(@"\p{L}", LetterBmp).Replace(@"\p{N}", NumberBmp);
                    var astral = string.Join("|", new[] { letter ? LetterAstral : "", number ? NumberAstral : "" }.Where(x => x.Length > 0));
                    sb.Append(astral.Length == 0 ? $"[{body}]" : $"(?:[{body}]|{astral})");
                    i = j; continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

public sealed partial class RuleCleaner
{
    /// <summary>A word before a command that makes it an ordinary noun: "a period", "the colon", "notice period", "a new line".</summary>
    public static readonly FrozenSet<string> CommandBlockersBefore = Data.CommandBlockersBefore.ToFrozenSet();
    /// <summary>A word after a command that makes it an ordinary noun: "a period of time", "the period ends", "colon cancer".</summary>
    public static readonly FrozenSet<string> CommandBlockersAfter = Data.CommandBlockersAfter.ToFrozenSet();
    /// <summary>Words a dash or hyphen never follows as a command: "I'll dash home", "we dash off".</summary>
    public static readonly FrozenSet<string> DashBlockersBefore = Data.DashBlockersBefore.ToFrozenSet();
    public static readonly FrozenSet<string> DomainEndings = Data.DomainEndings.ToFrozenSet();
    public static readonly FrozenDictionary<string, int> SpokenScales = Data.SpokenScales.ToFrozenDictionary();
    public static readonly FrozenSet<string> IndianScales = Data.IndianScales.ToFrozenSet();

    /// <summary>Integer part of a spoken number, fed one word at a time. Only well-formed English counting is accepted.
    /// Swift Int is 64-bit, so the values are long.</summary>
    public struct SpokenNumber
    {
        public enum LastKind { None, Unit, Teen, Tens, Hundred, Scale, And }
        public long Total, Group;
        public long LastScale = long.MaxValue;
        public bool GroupHundred;
        public LastKind Last;
        /// <summary>Never mutated in place, so copies of the struct stay independent as in Swift.</summary>
        public string[] Scales = [];

        public SpokenNumber() { }

        public readonly bool HasScale => Scales.Length > 0 || GroupHundred || Last == LastKind.Hundred;
        public readonly long Value => Total + Group;

        public bool Feed(string w)
        {
            int? v = CleanupText.Cardinals.TryGetValue(w, out var x) ? x : null;
            if (v is >= 1 and <= 9)
            {
                if (!(Last is LastKind.None or LastKind.Hundred or LastKind.Scale or LastKind.And || (Last == LastKind.Tens && Group % 10 == 0))) return false;
                Group += v.Value; Last = LastKind.Unit; return true;
            }
            if (v is >= 10 and <= 19)
            {
                if (Last is not (LastKind.None or LastKind.Hundred or LastKind.Scale or LastKind.And)) return false;
                Group += v.Value; Last = LastKind.Teen; return true;
            }
            if (v is >= 20 and <= 90)
            {
                if (Last is not (LastKind.None or LastKind.Hundred or LastKind.Scale or LastKind.And)) return false;
                Group += v.Value; Last = LastKind.Tens; return true;
            }
            if (w == "hundred")
            {
                if (Last is not (LastKind.Unit or LastKind.Teen or LastKind.Tens) || GroupHundred || Group is < 1 or > 99) return false;
                Group *= 100; GroupHundred = true; Last = LastKind.Hundred; return true;
            }
            if (SpokenScales.TryGetValue(w, out var s))
            {
                if (Last is not (LastKind.Unit or LastKind.Teen or LastKind.Tens or LastKind.Hundred) || Group <= 0 || s >= LastScale) return false;
                Total += Group * s; Group = 0; GroupHundred = false; LastScale = s; Scales = [.. Scales, w]; Last = LastKind.Scale; return true;
            }
            if (w == "and")
            {
                if (Last is not (LastKind.Hundred or LastKind.Scale)) return false;
                Last = LastKind.And; return true;
            }
            return false;
        }
    }

    /// <summary>Digits with Western grouping from 10,000 ("1200", "250,000"), or Indian grouping ("2,50,000").</summary>
    public static string GroupedDigits(long v, bool indian)
    {
        var s = v.ToString(CultureInfo.InvariantCulture);
        if (v < 10_000) return s;
        var digits = s.ToList();
        var parts = new List<string> { new(digits.TakeLast(3).ToArray()) };
        digits.RemoveRange(digits.Count - 3, 3);
        int size = indian ? 2 : 3;
        while (digits.Count > 0)
        {
            parts.Insert(0, new string(digits.TakeLast(size).ToArray()));
            digits.RemoveRange(digits.Count - Math.Min(size, digits.Count), Math.Min(size, digits.Count));
        }
        return string.Join(",", parts);
    }

    static readonly FrozenSet<string> TrailingMarks = new[] { ",", ".", ";", ":", "!", "?" }.ToFrozenSet();
    static readonly FrozenSet<string> QuoteMarks = new[] { "\"", "“", "”" }.ToFrozenSet();

    /// <summary>Spoken commands, numbers and symbols, then the email layout. Conservative: a rule that is unsure leaves the words.
    /// Quoted speech is never changed. Runs first in <see cref="Clean"/>, and <c>CleanupGuard.Check</c> accepts output judged
    /// against this function's result.</summary>
    public static string SpokenCommands(string text)
    {
        var t = CleanupText.Tokenize(text);
        var lowerWords = t.Where(x => x.Kind == TokenKind.Word).Select(x => x.Lower).ToList();
        // A command said on its own writes just the mark.
        foreach (var (ws, mk) in MarkCommands)
        {
            if (!ws.SequenceEqual(lowerWords)) continue;
            if (!t.Any(x => x.Kind == TokenKind.Punct && QuoteMarks.Contains(x.Text))) return mk;
            break;
        }
        var mask = CleanupText.QuotedMask(t);
        var @out = new List<CleanupToken>();
        bool changed = false, capitalizeNext = false;
        int i = 0;
        string? Word(int k) => k < t.Count && t[k].Kind == TokenKind.Word && !mask[k] ? t[k].Lower : null;
        int? After(int k) => k + 2 < t.Count && t[k + 1].Kind == TokenKind.Space && !Strings.Contains(t[k + 1].Text, "\n") && Word(k + 2) is not null ? k + 2 : null;
        int? Phrase(int k, IReadOnlyList<string> ws)
        {
            int idx = k;
            for (int n = 0; n < ws.Count; n++)
            {
                if (n > 0) { if (After(idx) is not { } nx) return null; idx = nx; }
                if (Word(idx) != ws[n]) return null;
            }
            return idx;
        }
        string? PrevWord() { int k = @out.FindLastIndex(x => x.Kind == TokenKind.Word); return k >= 0 ? @out[k].Lower : null; }
        void TrimTrailing(bool marks)
        {
            while (@out.Count > 0 && (@out[^1].Kind == TokenKind.Space || (marks && @out[^1].Kind == TokenKind.Punct && TrailingMarks.Contains(@out[^1].Text)))) @out.RemoveAt(@out.Count - 1);
        }
        void Emit(CleanupToken tok)
        {
            if (tok.Kind == TokenKind.Word && capitalizeNext) { tok = new(tok.Kind, CleanupText.Capitalized(tok.Text)); capitalizeNext = false; }
            @out.Add(tok);
        }
        string? WordAfter(int k) => After(k) is { } a ? Word(a) : null;
        // A number run at `k`: its last token, digits, and whether it carries hundred or a scale word.
        (int End, string Digits, bool Scaled)? Number(int k)
        {
            if (Word(k) is not { } first) return null;
            var head = first.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!CleanupText.Cardinals.TryGetValue(head, out var v0) || v0 < 1 || v0 >= 100) return null;
            var n = new SpokenNumber();
            int idx = k; int? end = null; var best = n; bool failedOnNumber = false;
            while (Word(idx) is { } w)
            {
                var trial = n;
                var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);
                bool ok = true;
                foreach (var part in parts) if (!trial.Feed(part)) { ok = false; break; }
                if (!ok)
                {
                    failedOnNumber = parts.Any(x => CleanupText.Cardinals.ContainsKey(x) || SpokenScales.ContainsKey(x));
                    break;
                }
                n = trial;
                if (n.Last != SpokenNumber.LastKind.And) { end = idx; best = n; }
                if (After(idx) is not { } nx) break;
                idx = nx;
            }
            if (end is not { } e) return null;
            // Back off a run cut short by another number ("one hundred and two hundred") to before its "and".
            if (failedOnNumber)
            {
                int k2 = k; var n2 = new SpokenNumber(); (int, SpokenNumber)? lastGood = null;
                while (k2 <= e && Word(k2) is { } w)
                {
                    if (w == "and") break;
                    foreach (var part in w.Split('-', StringSplitOptions.RemoveEmptyEntries)) n2.Feed(part);
                    lastGood = (k2, n2);
                    if (After(k2) is not { } nx) break;
                    k2 = nx;
                }
                if (lastGood is not (var ge, var gs) || ge >= e || WordAfter(ge) != "and") return null;
                e = ge; best = gs;
            }
            // A decimal before a scale word or "percent": "two point five million", "two point five percent".
            if (!best.HasScale && After(e) is { } pt && Word(pt) == "point")
            {
                var digits = ""; int d = pt;
                while (After(d) is { } nx && Word(nx) is { } w && CleanupText.Cardinals.TryGetValue(w, out var dv) && dv <= 9) { digits += dv.ToString(CultureInfo.InvariantCulture); d = nx; }
                if (digits.Length > 0 && After(d) is { } s && Word(s) is { } sw && (SpokenScales.ContainsKey(sw) || sw == "percent"))
                {
                    var value = best.Value.ToString(CultureInfo.InvariantCulture);
                    return sw == "percent" ? (d, $"{value}.{digits}", true) : (s, $"{value}.{digits} {t[s].Text}", true);
                }
            }
            // "two million", "three hundred million": the scale word stays.
            if (best.Scales.Length == 1 && best.Scales[0] is "million" or "billion" && best.Group == 0)
            {
                long scale = SpokenScales[best.Scales[0]];
                return (e, $"{(best.Total / scale).ToString(CultureInfo.InvariantCulture)} {best.Scales[0]}", true);
            }
            return (e, GroupedDigits(best.Value, best.Scales.Any(IndianScales.Contains)), best.HasScale);
        }

        while (i < t.Count)
        {
            var tok = t[i];
            if (tok.Kind != TokenKind.Word || Word(i) is not { } w) { Emit(tok); i++; continue; }

            int match = -1;
            for (int c = 0; c < MarkCommands.Count; c++) if (Phrase(i, MarkCommands[c].Words) is not null) { match = c; break; }
            if (match >= 0 && Phrase(i, MarkCommands[match].Words) is { } e)
            {
                var mark = MarkCommands[match].Mark;
                string? prev = PrevWord(), next = WordAfter(e);
                bool isDash = mark is "-" or "—";
                bool allowed = prev is not null && !CommandBlockersBefore.Contains(prev)
                    && !(next is not null && (CommandBlockersAfter.Contains(next) || CleanupText.Cardinals.ContainsKey(next) || Strings.SwiftInt(next) is not null));
                if (isDash) allowed = allowed && next is not null && !DashBlockersBefore.Contains(prev!);
                if (allowed)
                {
                    changed = true;
                    int k = e + 1;
                    if (mark == "-")
                    {
                        TrimTrailing(false); @out.Add(new(TokenKind.Punct, "-"));
                        while (k < t.Count && t[k].Kind == TokenKind.Space && !Strings.Contains(t[k].Text, "\n")) k++;
                    }
                    else
                    {
                        TrimTrailing(true);
                        // Punctuation speech recognition put around the command word goes ("Hello, comma, how").
                        while (k < t.Count && ((t[k].Kind == TokenKind.Space && !Strings.Contains(t[k].Text, "\n")) || (t[k].Kind == TokenKind.Punct && TrailingMarks.Contains(t[k].Text)))) k++;
                        if (mark == "—") @out.Add(new(TokenKind.Space, " "));
                        foreach (var ch in SwiftText.Characters(mark)) @out.Add(new(TokenKind.Punct, ch));
                        if (!Strings.HasPrefix(mark, "\n")) @out.Add(new(TokenKind.Space, " "));
                        capitalizeNext = mark is "." or "?" or "!" || Strings.HasPrefix(mark, "\n");
                    }
                    i = k; continue;
                }
            }
            // "hashtag launch" -> "#launch"; "hash forty two" / "hashtag forty two" -> "#42". "hash out" stays.
            if ((w == "hash" || w == "hashtag") && After(i) is { } hn)
            {
                if (Number(hn) is { } num && !num.Digits.Contains(' '))
                {
                    Emit(new(TokenKind.Punct, "#")); @out.Add(new(TokenKind.Word, num.Digits.Replace(",", "")));
                    changed = true; i = num.End + 1; continue;
                }
                if (w == "hashtag" && CleanupText.NumberValue(Word(hn)!) is null)
                {
                    Emit(new(TokenKind.Punct, "#")); @out.Add(t[hn]);
                    changed = true; i = hn + 1; continue;
                }
            }
            // "at the rate KV" / "at sign KV" -> "@KV"; "name at the rate gmail dot com" -> "name@gmail.com".
            // Not "at the rate of", "at the rate we're going" or "at the rate things are going".
            if (w == "at" && AtPhrases.Select(ph => Phrase(i, ph)).FirstOrDefault(x => x is not null) is { } ae && After(ae) is { } x && Word(x) is { } handle
                && handle != "of" && !CleanupGuard.GrammarWords.Contains(handle) && !CleanupGuard.Persons.Any(set => set.Contains(handle))
                && !CleanupGuard.Modals.Contains(handle) && CleanupText.NumberValue(handle) is null)
            {
                var parts = new List<CleanupToken> { t[x] };
                int end = x;
                while (After(end) is { } d && Word(d) == "dot" && After(d) is { } tld && Word(tld) is { } ending && DomainEndings.Contains(ending))
                {
                    parts.Add(new(TokenKind.Punct, ".")); parts.Add(t[tld]); end = tld;
                }
                bool domain = parts.Count > 1;
                bool lastWord = After(end) is null;
                if (domain || SwiftText.Characters(t[x].Text).Any(SwiftText.IsUppercase) || lastWord)
                {
                    if (domain && @out.Count > 0 && @out[^1].Kind == TokenKind.Space && @out.Count > 1 && @out[^2].Kind == TokenKind.Word) @out.RemoveAt(@out.Count - 1);
                    @out.Add(new(TokenKind.Punct, "@")); @out.AddRange(parts);
                    changed = true; i = end + 1; continue;
                }
            }
            // "example dot com" -> "example.com".
            if (w == "dot" && After(i) is { } dn && Word(dn) is { } dEnding && DomainEndings.Contains(dEnding) && i >= 2 && t[i - 1].Kind == TokenKind.Space
                && Word(i - 2) is { } dp && !CleanupGuard.GrammarWords.Contains(dp) && @out.Count > 0 && @out[^1].Kind == TokenKind.Space)
            {
                @out.RemoveAt(@out.Count - 1); @out.Add(new(TokenKind.Punct, ".")); @out.Add(t[dn]);
                changed = true; i = dn + 1; continue;
            }
            // Digits before "percent" too, as the prompt asks: "12 percent" -> "12%".
            if (SwiftText.Characters(w).All(SwiftText.IsNumber) && After(i) is { } pp && Word(pp) == "percent")
            {
                Emit(new(TokenKind.Word, tok.Text + "%"));
                changed = true; i = pp + 1; continue;
            }
            // Numbers: with hundred or a scale word, or before "percent". "give me five minutes",
            // "a hundred times", ordinals, years in pairs and digit strings stay words.
            if (Number(i) is { } nm)
            {
                if (After(nm.End) is { } np && Word(np) == "percent" && !nm.Digits.Contains(' '))
                {
                    Emit(new(TokenKind.Word, nm.Digits + "%"));
                    changed = true; i = np + 1; continue;
                }
                if (nm.Scaled)
                {
                    Emit(new(TokenKind.Word, nm.Digits));
                    changed = true; i = nm.End + 1; continue;
                }
            }
            Emit(tok); i++;
        }
        var result = changed ? CleanupText.Render(CleanupText.Normalize(@out)) : text;
        if (EmailLayout(result) is { } email) result = email;
        return result;
    }

    public static readonly Regex EmailFrame = Compile(Data.EmailFrame);
    public static readonly Regex SignOff = Compile(Data.SignOff);

    /// <summary>"Write an email to Sarah saying … best regards, Kartik" -> "Hi Sarah," / body / "Best regards," / "Kartik".
    /// Only for that explicit frame at the start; the frame words go and "Hi" is the only word added.</summary>
    public static string? EmailLayout(string text)
    {
        var m = EmailFrame.Match(text);
        if (!m.Success) return null;
        static bool Plain(string s) => Strings.Split(s, c => c == " ").All(w =>
        {
            var l = SwiftText.Lowercased(w);
            return !CleanupGuard.GrammarWords.Contains(l) && CleanupText.NumberValue(l) is null;
        });
        static string Titled(string s) => string.Join(" ", Strings.Split(s, SwiftText.IsWhitespace).Select(CleanupText.Capitalized));
        var recipient = m.Groups[1].Value;
        var body = m.Groups[2].Value;
        string? closing = null;
        if (!Plain(recipient)) return null;
        var so = SignOff.Match(body);
        if (so.Success && Plain(so.Groups[3].Value))
        {
            var phrase = SwiftText.Lowercased(so.Groups[2].Value);
            closing = CleanupText.Capitalized(phrase) + ",\n" + Titled(so.Groups[3].Value);
            body = so.Groups[1].Value;
        }
        body = Strings.Trim(body, r => r.Value is ' ' or ',' or ';' or ':' or '—' or '-' || Strings.IsWhitespaceOrNewline(r));
        if (body.Length == 0) return null;
        body = CleanupText.Capitalized(body);
        var chars = SwiftText.Characters(body);
        if (chars.Count > 0 && chars[^1] is not ("." or "?" or "!")) body += ".";
        return $"Hi {Titled(recipient)},\n\n{body}" + (closing is null ? "" : "\n\n" + closing);
    }
}
