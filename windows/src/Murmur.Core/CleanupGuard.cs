// Port of app/CleanupGuard.swift.
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur.Core;

// The bound checks between raw text and rewritten text: `CleanupGuard` (cleanup) and `TransformGuard` (mode rewrites).

/// <summary>Bound check between raw transcript and cleanup output. Rejects output that answers or obeys the dictation,
/// adds facts, or makes edits the enabled options cannot explain.</summary>
public static partial class CleanupGuard
{
    /// <summary>Closed-class words a polish grammar fix may add or drop. Not negations, numbers or modals.</summary>
    public static readonly FrozenSet<string> GrammarWords = CleanupText.Data.GrammarWords.ToFrozenSet();

    public sealed class Word
    {
        public string Text = "";
        public string Lower = "";
        public bool SentenceStart;
        public bool CommaBefore;
        public bool CommaAfter;
        public bool EndAfter;
        public bool Capitalized => Strings.FirstIsUppercase(Text);
        public bool IsName => Capitalized && !SentenceStart && Lower != "i" && !Strings.HasPrefix(Lower, "i'");
    }

    internal sealed class GuardData
    {
        public string[] AssistantPhrases { get; set; } = [];
        public string[] AnswerPhrases { get; set; } = [];
        public string[] OrdinalPhraseNouns { get; set; } = [];
        public string[] PolishFillers { get; set; } = [];
        public string[][] PolishFillerPairs { get; set; } = [];
        public string[] MeaningfulLikeAfter { get; set; } = [];
        public string[] DroppableGrammarWords { get; set; } = [];
        public string[] Modals { get; set; } = [];
        public string[][] Persons { get; set; } = [];
        public string[][] CorrectionCues { get; set; } = [];
        public string[][] LooseCorrectionCues { get; set; } = [];
        public string[][] CommaRequiredCues { get; set; } = [];
        public string[] DeclarativeAfterCue { get; set; } = [];
        public string[] NoQuantified { get; set; } = [];
        public string[][] ExplicitRepairCues { get; set; } = [];
        public string[][] SentenceRepairCues { get; set; } = [];
    }

    static readonly GuardData Data = SharedData.Load<GuardData>("cleanup_guard");

    /// <summary>Assistant-specific phrases that rarely occur in dictation.</summary>
    public static readonly IReadOnlyList<string> AssistantPhrases = Data.AssistantPhrases;
    public static readonly IReadOnlyList<string> AnswerPhrases = Data.AnswerPhrases;

    static string Bare(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in SwiftText.Characters(s))
        {
            var x = SwiftText.IsWhitespace(c) ? " " : c;
            if (SwiftText.IsLetter(x) || SwiftText.IsNumber(x) || x == "'" || x == " ") sb.Append(x);
        }
        return sb.ToString();
    }

    /// <summary>The output contains an answer phrase the speaker did not say. Compared without punctuation on the raw side:
    /// added punctuation alone ("let's make sure" -> "Let's make sure.") is not an answer.</summary>
    public static bool Answers(string raw, string cleaned)
    {
        var rawBare = Bare(SwiftText.Lowercased(raw).Replace("’", "'"));
        var outLower = SwiftText.Lowercased(cleaned).Replace("’", "'");
        return AnswerPhrases.Any(p => Strings.Contains(outLower, p) && !Strings.Contains(rawBare, Bare(p)));
    }

    /// <summary>Nouns that make a preceding ordinal part of a phrase, not a list marker ("first impressions", "second nature").</summary>
    public static readonly FrozenSet<string> OrdinalPhraseNouns = Data.OrdinalPhraseNouns.ToFrozenSet();
    /// <summary>Discourse fillers polish may remove without commas, with the fillers option.</summary>
    public static readonly FrozenSet<string> PolishFillers = Data.PolishFillers.ToFrozenSet();
    public static readonly IReadOnlyList<IReadOnlyList<string>> PolishFillerPairs = Data.PolishFillerPairs;
    public static readonly FrozenSet<string> MeaningfulLikeAfter = Data.MeaningfulLikeAfter.ToFrozenSet();
    /// <summary>Grammar words polish may drop: not pronouns or the joins and, or, but, if ("salt and pepper").</summary>
    public static readonly FrozenSet<string> DroppableGrammarWords = Data.DroppableGrammarWords.ToFrozenSet();
    public static readonly FrozenSet<string> Modals = Data.Modals.ToFrozenSet();
    /// <summary>A grammar fix may change a pronoun's case ("him" -> "he") but not whom it refers to. Index 4 is "it".</summary>
    public static readonly IReadOnlyList<FrozenSet<string>> Persons = Data.Persons.Select(p => p.ToFrozenSet()).ToArray();

    static int PersonIndex(string w)
    {
        for (int k = 0; k < Persons.Count; k++) if (Persons[k].Contains(w)) return k;
        return -1;
    }
    static bool IsPronoun(string w) => PersonIndex(w) >= 0;

    static readonly FrozenSet<string> FlipPrefixes = new[] { "", "in", "de", "ex", "un", "dis", "non", "im", "il", "ir", "mis", "anti", "up", "down", "over", "under", "pre", "post" }.ToFrozenSet();

    /// <summary>Words that differ only by an opposite prefix ("increase"/"decrease", "include"/"exclude", "like"/"dislike").</summary>
    public static bool PolarityFlip(string x, string y)
    {
        var cx = SwiftText.Characters(x); var cy = SwiftText.Characters(y);
        int common = 0;
        while (common < cx.Count && common < cy.Count && SwiftText.CharEquals(cx[cx.Count - 1 - common], cy[cy.Count - 1 - common])) common++;
        if (common < 3) return false;
        var px = string.Concat(cx.Take(cx.Count - common)); var py = string.Concat(cy.Take(cy.Count - common));
        return px != py && FlipPrefixes.Contains(px) && FlipPrefixes.Contains(py);
    }

    /// <summary>A polish replacement: a respelling or misheard word, never a different modal or person.</summary>
    public static bool PolishSwap(string heard, string written)
    {
        if (Modals.Contains(heard) && Modals.Contains(written)) return false;
        int a = PersonIndex(heard), b = PersonIndex(written);
        if (a >= 0 || b >= 0) return a == b;
        return CleanupText.Similarity(heard, written) >= 0.5;
    }

    /// <summary>The rule engine's cues plus "actually no", which the rules reach through "actually" and "no" separately.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> CorrectionCues = Data.CorrectionCues;

    public static List<Word> Words(string s)
    {
        var tokens = CleanupText.Tokenize(s.Replace("’", "'"));
        var @out = new List<Word>();
        bool start = true, lineStart = true, commaPending = false;
        int i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Word)
            {
                // A list marker "3." / "3)" at line start is formatting, not content.
                if (lineStart && SwiftText.Characters(t.Text).All(SwiftText.IsNumber) && CleanupText.NextNonSpace(tokens, i) is { } n
                    && (tokens[n].Text == "." || tokens[n].Text == ")"))
                {
                    i = n + 1; start = true; lineStart = false; continue;
                }
                // "250,000", "2,50,000" and "250000" are the same word.
                var lc = SwiftText.Characters(t.Lower);
                var lower = lc.Count > 0 && SwiftText.IsNumber(lc[0]) && lc.All(c => SwiftText.IsNumber(c) || c == "," || c == ".") ? t.Lower.Replace(",", "") : t.Lower;
                // "42-dollar" is the two spoken words "42 dollar".
                var parts = Strings.Split(lower, c => c == "-", maxSplits: 1);
                if (parts.Count == 2 && SwiftText.Characters(parts[0]).All(SwiftText.IsNumber) && Strings.FirstIsLetter(parts[1]))
                {
                    @out.Add(new Word { Text = parts[0], Lower = parts[0], SentenceStart = start, CommaBefore = commaPending });
                    @out.Add(new Word { Text = string.Concat(SwiftText.Characters(t.Text).Skip(SwiftText.Count(parts[0]) + 1)), Lower = parts[1], SentenceStart = false, CommaBefore = false });
                }
                else
                {
                    @out.Add(new Word { Text = t.Text, Lower = lower, SentenceStart = start, CommaBefore = commaPending });
                }
                start = false; lineStart = false; commaPending = false;
            }
            else if (t.Kind == TokenKind.Punct)
            {
                if (t.IsComma || t.IsDash)
                {
                    if (@out.Count > 0) @out[^1].CommaAfter = true;
                    commaPending = true;
                }
                else commaPending = false;
                if (t.IsTerminal && @out.Count > 0) @out[^1].EndAfter = true;
                if (t.IsTerminal || t.Text == ":") start = true;
                if (t.Text == "\n") lineStart = true;
            }
            i++;
        }
        return @out;
    }

    /// <summary>Swift <c>line.drop(while: " ")</c>, its leading digits, and whether "." or ")" follows them.</summary>
    static (string Digits, bool Marked) LineMarker(string line)
    {
        var c = SwiftText.Characters(line);
        int k = 0;
        while (k < c.Count && c[k] == " ") k++;
        int d = k;
        while (d < c.Count && SwiftText.IsNumber(c[d])) d++;
        return (string.Concat(c.Skip(k).Take(d - k)), d < c.Count && c[d] is "." or ")");
    }

    public static HashSet<string> ListMarkerValues(string s)
    {
        var values = new HashSet<string>();
        foreach (var line in Strings.Split(s, c => c == "\n"))
        {
            var (digits, marked) = LineMarker(line);
            if (digits.Length > 0 && marked) values.Add(digits);
        }
        return values;
    }

    public static int NonListLineCount(string s) => Strings.Split(s, c => c == "\n").Count(line =>
    {
        var (digits, marked) = LineMarker(line);
        return digits.Length == 0 || !marked;
    });

    /// <summary>Cues professional accepts without a comma: unambiguous, unlike "sorry", "no" or "I mean".</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> LooseCorrectionCues = Data.LooseCorrectionCues;
    /// <summary>The ordinary words that are corrections only after a comma (or dash).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> CommaRequiredCues = Data.CommaRequiredCues;
    /// <summary>After a cue these make it a statement, not a repair: "I meant it", "I mean business", "what I meant was".</summary>
    public static readonly FrozenSet<string> DeclarativeAfterCue = Data.DeclarativeAfterCue.ToFrozenSet();
    /// <summary>"no" before one of these quantifies it ("no one", "no longer") and is not a correction cue.</summary>
    public static readonly FrozenSet<string> NoQuantified = Data.NoQuantified.ToFrozenSet();

    static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b);
    static bool AnyIn(List<CleanupToken> t, int from, int to, Func<CleanupToken, bool> test)
    {
        for (int k = from; k < to; k++) if (test(t[k])) return true;
        return false;
    }
    static int CommonPrefix(string x, string y)
    {
        var a = SwiftText.Characters(x); var b = SwiftText.Characters(y);
        int n = 0;
        while (n < a.Count && n < b.Count && SwiftText.CharEquals(a[n], b[n])) n++;
        return n;
    }

    static readonly FrozenSet<string> MeanDeterminers = new[] { "the", "a", "an", "this", "that" }.ToFrozenSet();
    static readonly FrozenSet<string> DeclarativeBefore = new[] { "what", "which", "who", "how", "whatever", "all", "if" }.ToFrozenSet();
    static readonly FrozenSet<string> BareDeclarativeAfter = DeclarativeAfterCue.Union(["that", "by", "for"]).ToFrozenSet();
    static readonly FrozenSet<string> MeantLeads = new[] { "wait", "no", "know", "oh", "sorry", "actually", "um", "uh" }.ToFrozenSet();
    static readonly FrozenSet<string> InterregnumWords = new[] { "wait", "no", "know", "oh", "ah", "sorry", "actually" }.ToFrozenSet();
    static readonly FrozenSet<string> Questions = new[] { "what", "which", "who", "how", "when", "where", "why" }.ToFrozenSet();
    static readonly FrozenSet<string> Joins = new[] { "so", "and", "but", "then" }.ToFrozenSet();
    static readonly FrozenSet<string> PhraseStarts = new[] { "on", "at", "in", "to", "for", "with", "by", "from", "the", "a", "an", "my", "your", "our", "their", "this", "that" }.ToFrozenSet();

    /// <summary>Raw word indices that a spoken correction could legitimately delete: the cue itself plus up to eight preceding
    /// words in the same sentence (whole sentence for "scratch that"). With <paramref name="loose"/>, a loose cue needs no comma,
    /// but then covers only the preceding words whose content words all recur in the next words, so it never frees an
    /// unrelated name, number or negation.</summary>
    public static HashSet<int> CorrectionWindow(string raw, bool loose = false, bool commaFreeOnly = false)
    {
        var tokens = CleanupText.Tokenize(raw.Replace("’", "'"));
        var tokenOfWord = new List<int>();
        for (int ti = 0; ti < tokens.Count; ti++) if (tokens[ti].Kind == TokenKind.Word) tokenOfWord.Add(ti);
        string Word(int x) => tokens[tokenOfWord[x]].Lower;
        var window = new HashSet<int>();
        for (int wi = 0; wi < tokenOfWord.Count; wi++)
        {
            int ti = tokenOfWord[wi];
            foreach (var cue in CorrectionCues)
            {
                if (wi + cue.Count > tokenOfWord.Count) continue;
                bool match = true;
                for (int c = 0; c < cue.Count; c++) if (Word(wi + c) != cue[c]) { match = false; break; }
                if (!match) continue;
                if (CleanupText.PrevNonSpace(tokens, ti) is not { } p) continue;
                bool scratch = Same(cue, ["scratch", "that"]);
                bool commaFree = !(tokens[p].IsComma || tokens[p].IsDash || (scratch && tokens[p].IsTerminal));
                if (!(!commaFree || (loose && LooseCorrectionCues.Any(x => Same(x, cue)) && tokens[p].Kind == TokenKind.Word))) continue;
                if (commaFreeOnly && !commaFree) continue;
                bool iMean = Same(cue, ["i", "mean"]);
                if (commaFree && iMean)
                {
                    if (!(wi + 2 < tokenOfWord.Count && MeanDeterminers.Contains(Word(wi + 2)))) continue;
                }
                // Declarative uses are not repairs: "what I meant was", "I meant to", "I meant it".
                if (commaFree)
                {
                    // "no" quantifying the next word is not a cue: "wait no one blamed Bob".
                    if (cue[^1] == "no" && wi + cue.Count < tokenOfWord.Count && NoQuantified.Contains(Word(wi + cue.Count))) continue;
                    var before = wi > 0 ? Word(wi - 1) : "";
                    var after0 = wi + cue.Count < tokenOfWord.Count ? Word(wi + cue.Count) : "";
                    // A bare comma-free cue is also a statement before "that", "by" or "for" ("I mean that", "I meant by").
                    if (DeclarativeBefore.Contains(before) || BareDeclarativeAfter.Contains(after0)) continue;
                    // A bare "I meant" repairs only right after a cue word ("wait no I meant", "actually I meant").
                    if (Same(cue, ["i", "meant"]) && !MeantLeads.Contains(before)) continue;
                }
                // A repair must follow the cue before the sentence ends.
                int lastCueToken = tokenOfWord[wi + cue.Count - 1];
                int j = lastCueToken + 1;
                bool hasRepair = false;
                while (j < tokens.Count && !tokens[j].IsTerminal)
                {
                    if (tokens[j].Kind == TokenKind.Word) { hasRepair = true; break; }
                    j++;
                }
                if (!hasRepair) continue;
                for (int k = 0; k < cue.Count; k++) window.Add(wi + k);
                // Interregnum hesitations directly after the cue.
                int after = wi + cue.Count;
                while (after < tokenOfWord.Count && CleanupText.Hesitations.Contains(Word(after))) { window.Add(after); after++; }
                // A short negated restatement before the repair ("no, not Tuesday, Thursday").
                if (after < tokenOfWord.Count && Word(after) == "not")
                {
                    int end = after + 1;
                    while (end < tokenOfWord.Count && end - after <= 3 && !AnyIn(tokens, tokenOfWord[end - 1] + 1, tokenOfWord[end], x => x.IsBoundary)) end++;
                    if (end < tokenOfWord.Count && end - after <= 4 && AnyIn(tokens, tokenOfWord[end - 1] + 1, tokenOfWord[end], x => x.IsComma || x.IsDash))
                        for (int x = after; x < end; x++) window.Add(x);
                }
                // Preceding words in the same sentence. "Scratch that" after a period reaches back across that one period.
                int limit = scratch ? 30 : 8;
                if (commaFree)
                {
                    // The repair restates right away: within its first four words, not later in the sentence.
                    int repairLo = wi + cue.Count, repairHi = Math.Min(tokenOfWord.Count, wi + cue.Count + 4);
                    // Interregnum words, including "know" misheard for "no" ("wait I know I meant"), do not count.
                    static bool Interregnum(string w) => InterregnumWords.Contains(w) || CleanupText.Hesitations.Contains(w);
                    // Restated means the same word, an inflection of it, or the same kind of word ("Friday" -> "Monday").
                    static bool Restates(string x, string y) =>
                        x == y || CommonPrefix(x, y) >= 4
                        || new[] { RuleCleaner.Weekdays, RuleCleaner.MonthsAndMay, RuleCleaner.RelativeTimes, RuleCleaner.Colors }.Any(k => k.Contains(x) && k.Contains(y))
                        || (CleanupText.NumberValue(x) is not null && CleanupText.NumberValue(y) is not null);
                    bool Recurs(string w)
                    {
                        if (GrammarWords.Contains(w)) return true;
                        for (int x = repairLo; x < repairHi; x++) if (Restates(w, Word(x))) return true;
                        return false;
                    }
                    limit = 0;
                    int reach = 8; bool restated = false;
                    while (limit < reach && wi - 1 - limit >= 0)
                    {
                        var w = Word(wi - 1 - limit);
                        if (Interregnum(w)) reach++;
                        else if (!Recurs(w)) break;
                        else if (!GrammarWords.Contains(w) && !Questions.Contains(w)) restated = true;
                        limit++;
                    }
                    // Remove only as many words before the restated word as the repair puts before it.
                    var content = Enumerable.Range(wi - limit, limit).Where(x => !GrammarWords.Contains(Word(x)) && !Interregnum(Word(x))).ToList();
                    int? e = null;
                    foreach (var x in content) { for (int q = repairLo; q < repairHi; q++) if (Restates(Word(x), Word(q))) { e = x; break; } if (e is not null) break; }
                    int? r = null;
                    for (int q = repairLo; q < repairHi && r is null; q++) if (content.Any(x => Restates(Word(x), Word(q)))) r = q;
                    if (e is { } ee && r is { } rr) limit = wi - Math.Max(wi - limit, ee - (rr - repairLo));
                    // Without a restated content word it is not a correction, except "I mean the ..." clarifying, where only the cue goes.
                    // A leading join stays ("go so we are not ... I meant he is not" keeps "so").
                    while (limit > 0 && Joins.Contains(Word(wi - limit))) limit--;
                    // Only two repairs are safe to apply here: a whole clause after a join or a sentence start, or a phrase
                    // restarted with the same preposition or determiner. Anything else is left for the model.
                    if (limit > 0 && !iMean)
                    {
                        int start = wi - limit;
                        bool clause = start == 0 || Joins.Contains(Word(start - 1)) || AnyIn(tokens, tokenOfWord[start - 1], tokenOfWord[start], x => x.IsTerminal);
                        bool phrase = Word(start) == Word(wi + cue.Count) && PhraseStarts.Contains(Word(start));
                        if (!clause && !phrase)
                        {
                            for (int k = 0; k < cue.Count; k++) window.Remove(wi + k);
                            continue;
                        }
                    }
                    if (iMean) limit = 0;
                    else if (!restated)
                    {
                        for (int k = 0; k < cue.Count; k++) window.Remove(wi + k);
                        continue;
                    }
                }
                int kk = wi - 1, count = 0;
                bool allowCross = scratch && tokens[p].IsTerminal;
                while (kk >= 0 && count < limit)
                {
                    if (AnyIn(tokens, tokenOfWord[kk] + 1, tokenOfWord[kk + 1], x => x.IsTerminal))
                    {
                        if (allowCross && count == 0) allowCross = false; else break;
                    }
                    window.Add(kk); count++; kk--;
                }
            }
        }
        return window;
    }

    /// <summary>Professional applies comma-free corrections before the model sees the text. Removes only what <see cref="Check"/> would allow.</summary>
    public static string ApplyCommaFreeCorrections(string text)
    {
        var window = CorrectionWindow(text, loose: true, commaFreeOnly: true);
        if (window.Count == 0) return text;
        var t = CleanupText.Tokenize(text.Replace("’", "'"));
        int word = -1;
        t.RemoveAll(tok =>
        {
            if (tok.Kind != TokenKind.Word) return false;
            word++;
            return window.Contains(word);
        });
        return CleanupText.Render(CleanupText.Normalize(t));
    }

    /// <summary>"No I mean" / "No I meant" prefix the ordinary cues; "actually no" and "scratch that" are shared; "let me start again" is its own.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> ExplicitRepairCues = Data.ExplicitRepairCues;

    static readonly FrozenSet<string> RepairInterregnum = CleanupText.Hesitations.Union(["wait", "oh", "no", "actually", "sorry"]).ToFrozenSet();
    static readonly FrozenSet<string> RepairOpeners = CleanupText.Hesitations.Union(["hey", "okay", "ok", "well", "oh", "so", "yeah", "alright"]).ToFrozenSet();
    static readonly FrozenSet<string> ClauseJoins = new[] { "so", "and", "but" }.ToFrozenSet();
    static readonly FrozenSet<string> QuantifiedOrDeclarative = NoQuantified.Union(DeclarativeAfterCue).ToFrozenSet();

    /// <summary>Polish and Professional: <paramref name="raw"/> with each explicitly cued repair applied ("um what time is it no I mean
    /// what day is it" -> "um what day is it"). <see cref="Check"/> judges output against this text too.
    /// ponytail: word lists and a length bound, not a parser; Strict and the cleaner itself never use it.</summary>
    public static string ApplyExplicitRepairs(string raw)
    {
        var tokens = CleanupText.Tokenize(raw.Replace("’", "'"));
        var tokenOfWord = Enumerable.Range(0, tokens.Count).Where(k => tokens[k].Kind == TokenKind.Word).ToList();
        string Word(int x) => tokens[tokenOfWord[x]].Lower;
        bool Between(int x, int y, Func<CleanupToken, bool> test) => AnyIn(tokens, tokenOfWord[x] + 1, tokenOfWord[y], test);
        var cuts = new List<(int Lo, int Hi)>();
        int LastCut() => cuts.Count > 0 ? cuts[^1].Hi : -1;
        for (int wi = 0; wi < tokenOfWord.Count; wi++)
        {
            IReadOnlyList<string>? cue = null;
            foreach (var c in ExplicitRepairCues)
            {
                if (wi + c.Count > tokenOfWord.Count) continue;
                bool ok = true;
                for (int k = 0; k < c.Count; k++) if (Word(wi + k) != c[k]) { ok = false; break; }
                for (int k = wi; ok && k < wi + c.Count - 1; k++) if (Between(k, k + 1, x => x.IsTerminal)) ok = false;
                if (ok) { cue = c; break; }
            }
            if (cue is null) continue;
            int start = wi, end = wi + cue.Count;
            while (start > LastCut() + 1 && RepairInterregnum.Contains(Word(start - 1)) && !Between(start - 1, start, x => x.IsTerminal)) start--;
            while (end < tokenOfWord.Count && CleanupText.Hesitations.Contains(Word(end)) && !Between(end - 1, end, x => x.IsTerminal)) end++;
            // A declarative or quantifying use is not a cue: "no I mean it", "actually no one came".
            if (!(end < tokenOfWord.Count) || Between(end - 1, end, x => x.IsTerminal) || QuantifiedOrDeclarative.Contains(Word(end))
                || !(start > LastCut() + 1)
                // "No, I mean" may follow a question it corrects; a bare "No," there stays an answer.
                || !(cue[0] == "no" || !Between(start - 1, start, x => x.IsTerminal))) continue;
            int s = start - 1;
            while (s > 0 && !Between(s - 1, s, x => x.IsBoundary) && !ClauseJoins.Contains(Word(s - 1)) && start - s < 8) s--;
            if (!(s == 0 || Between(s - 1, s, x => x.IsBoundary) || ClauseJoins.Contains(Word(s - 1)))) continue;
            while (s < start && RepairOpeners.Contains(Word(s))) s++;
            int repairEnd = end;
            while (repairEnd + 1 < tokenOfWord.Count && !Between(repairEnd, repairEnd + 1, x => x.IsTerminal)) repairEnd++;
            int clauseLo = s, clauseHi = start, repairLo = end, repairHi = Math.Min(repairEnd + 1, end + 4);
            // Each number and name the clause loses has one of its kind in the repair.
            int Count(int lo, int hi, Func<int, bool> test) { int n = 0; for (int x = lo; x < hi; x++) if (test(x)) n++; return n; }
            bool Number(int x) => CleanupText.NumberValue(Word(x)) is not null;
            bool Name(int x) => Strings.FirstIsUppercase(tokens[tokenOfWord[x]].Text) && x > 0 && !Between(x - 1, x, y => y.IsTerminal) && !Strings.HasPrefix(Word(x), "i'") && Word(x) != "i";
            bool shared = false;
            for (int x = clauseLo; x < clauseHi && !shared; x++)
            {
                if (GrammarWords.Contains(Word(x))) continue;
                for (int q = repairLo; q < repairHi; q++) if (Word(q) == Word(x) || CommonPrefix(Word(q), Word(x)) >= 4) { shared = true; break; }
            }
            if (!(clauseHi > clauseLo) || !(repairEnd + 1 - end >= clauseHi - clauseLo)
                || !(Count(clauseLo, clauseHi, Number) <= Count(end, repairEnd + 1, Number)) || !(Count(clauseLo, clauseHi, Name) <= Count(end, repairEnd + 1, Name))
                || !shared) continue;
            cuts.Add((s, end - 1)); wi = end - 1;
        }
        for (int c = cuts.Count - 1; c >= 0; c--)
        {
            int lo = tokenOfWord[cuts[c].Lo], hi = tokenOfWord[cuts[c].Hi];
            tokens.RemoveRange(lo, hi - lo + 1);
        }
        return cuts.Count == 0 ? raw : CleanupText.Render(CleanupText.Normalize(tokens));
    }

    /// <summary>Cues that open a sentence correcting the one before it ("…on Tuesday. Actually, I mean Wednesday.").</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> SentenceRepairCues = Data.SentenceRepairCues;

    /// <summary>Words of each sentence that the following sentence replaces, for the trusted path's truncation check only.
    /// ponytail: word overlap, not meaning; a parallel sentence looks the same, and keeping it is left to the model.</summary>
    public static (HashSet<int> Cued, HashSet<int> Restated, List<(int Lo, int Hi)> Cuts) ReplacedSentences(List<Word> r)
    {
        var starts = Enumerable.Range(0, r.Count).Where(k => k == 0 || r[k].SentenceStart).Append(r.Count).ToList();
        HashSet<string> Content(int lo, int hi) => Enumerable.Range(lo, hi - lo).Select(k => r[k].Lower)
            .Where(w => !GrammarWords.Contains(w) && !CleanupText.Hesitations.Contains(w)).Select(w => SwiftText.Prefix(w, 5)).ToHashSet();
        var cued = new HashSet<int>(); var restated = new HashSet<int>(); var cuts = new List<(int, int)>();
        for (int k = 0; k < starts.Count - 2; k++)
        {
            int firstLo = starts[k], firstHi = starts[k + 1], secondLo = starts[k + 1], secondHi = starts[k + 2];
            int open = secondLo;
            while (open < secondHi && CleanupText.Hesitations.Contains(r[open].Lower)) open++;
            // Cues may stack ("Actually, I mean …").
            int after = open;
            while (true)
            {
                var cue = SentenceRepairCues.FirstOrDefault(c => after + c.Count < secondHi && Enumerable.Range(0, c.Count).All(x => r[after + x].Lower == c[x]));
                if (cue is null) break;
                after += cue.Count;
            }
            var was = Content(firstLo, firstHi); var now = Content(after, secondHi);
            int shared = was.Intersect(now).Count();
            if (after > open)
            {
                if (shared >= 1) { cued.UnionWith(Enumerable.Range(firstLo, firstHi - firstLo)); cuts.Add((firstLo, after - 1)); }
                continue;
            }
            int firstCount = firstHi - firstLo, secondCount = secondHi - secondLo;
            bool sameOpening = firstCount >= 2 && secondCount >= 2 && r[firstLo].Lower == r[secondLo].Lower && r[firstLo + 1].Lower == r[secondLo + 1].Lower;
            if (sameOpening || (was.Count >= 2 && shared * 5 >= was.Count * 3)) restated.UnionWith(Enumerable.Range(firstLo, firstCount));
        }
        return (cued, restated, cuts);
    }

    /// <summary>The transcript without each sentence that the next one corrects after a cue, and without the cue.</summary>
    public static string ApplyCuedSentenceRepairs(string raw)
    {
        var cuts = ReplacedSentences(Words(raw)).Cuts;
        if (cuts.Count == 0) return raw;
        var tokens = CleanupText.Tokenize(raw.Replace("’", "'"));
        var tokenOfWord = Enumerable.Range(0, tokens.Count).Where(k => tokens[k].Kind == TokenKind.Word).ToList();
        for (int c = cuts.Count - 1; c >= 0; c--)
        {
            int lo = tokenOfWord[cuts[c].Lo], hi = tokenOfWord[cuts[c].Hi];
            tokens.RemoveRange(lo, hi - lo + 1);
        }
        return CleanupText.Render(CleanupText.Normalize(tokens));
    }

    static readonly FrozenSet<string> Restrictions = new[] { "unless", "until", "except" }.ToFrozenSet();

    /// <summary>Trusted output that lost substance: under 60% of the spoken words, a vanished tail of 3+ content words, or every negation gone.
    /// shortcut: length and tail floors, not a semantic check; a same-length meaning change passes, upgrade with a verifier.</summary>
    public static bool Truncated(string raw, string cleaned)
    {
        // Judge the comma form of a split cue ("Tuesday. No, Wednesday." -> "Tuesday, no, Wednesday").
        raw = RuleCleaner.MergeSplitCues(raw);
        var r = Words(raw); var o = Words(cleaned);
        var a = r.Select(w => w.Lower).ToList(); var b = o.Select(w => w.Lower).ToList();
        // A sentence the next one replaces is not substance either. Only a cued replacement frees its negations.
        var replaced = ReplacedSentences(r);
        var cued = CorrectionWindow(raw, loose: true); cued.UnionWith(replaced.Cued);
        var window = new HashSet<int>(cued); window.UnionWith(replaced.Restated);
        // Words a correction replaces are not substance, so they do not count toward the length floor.
        int spoken = Words(RuleCleaner.RemoveFillers(raw)).Count - window.Count;
        if (spoken >= 8 && o.Count < spoken * 0.6) return true;
        var matched = Align(a, b).Raw;
        var kept = b.Select(w => SwiftText.Prefix(w, 5)).ToHashSet();
        int tail = 0, k = a.Count - 1;
        while (k >= 0 && !matched.Contains(k)) k--;
        for (int i = k + 1; i < a.Count; i++)
            if (!window.Contains(i) && !GrammarWords.Contains(a[i]) && !CleanupText.Hesitations.Contains(a[i]) && !kept.Contains(SwiftText.Prefix(a[i], 5))) tail++;
        if (tail >= 3) return true;
        // A restriction connector ("unless QA signs off") outside a correction must survive somewhere.
        for (int i = 0; i < a.Count; i++)
            if (!window.Contains(i) && Restrictions.Contains(a[i]) && !b.Contains(a[i])) return true;
        int rawNeg = Enumerable.Range(0, a.Count).Count(i => CleanupText.IsNegation(a[i]) && !cued.Contains(i));
        return rawNeg > 0 && !b.Any(w => CleanupText.IsNegation(w) || w == "without");
    }

    /// <summary>LCS alignment of two word lists: the matched indices on each side. On ties, the later raw copy matches.</summary>
    public static (HashSet<int> Raw, HashSet<int> Out) Align(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count;
        if (n == 0 || m == 0) return ([], []);
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var ma = new HashSet<int>(); var mb = new HashSet<int>();
        int x = 0, y = 0;
        // On ties, skip the earlier raw copy: a correction's reparandum precedes its repair, so a
        // repeated word ("at the office, sorry, at the cafe") should match the later copy.
        while (x < n && y < m)
        {
            if (a[x] == b[y] && dp[x + 1, y] < dp[x, y]) { ma.Add(x); mb.Add(y); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) x++; else y++;
        }
        return (ma, mb);
    }

    static readonly Regex HorizontalSpace = new(@"\p{Zs}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary><paramref name="soundsLike"/>(heard, term) (lowercased letters only) lets a term replace 1–3 raw words that sound like it.
    /// With spoken, output that fails against the raw text may instead match the raw text after <c>RuleCleaner.SpokenCommands</c>.
    /// ponytail: a model must make the same conversions as the rules, or its output falls back.</summary>
    public static CleanupGuardResult Check(string raw, string cleaned, CleanupOptions options, IReadOnlyList<string>? allowedTerms = null,
                                           Func<string, string, bool>? soundsLike = null)
    {
        allowedTerms ??= [];
        soundsLike ??= (_, _) => false;
        // Compare equivalent horizontal spaces without changing the text returned for insertion.
        raw = HorizontalSpace.Replace(raw, " ");
        cleaned = HorizontalSpace.Replace(cleaned, " ");
        var result = CheckWords(raw, cleaned, options, allowedTerms, soundsLike);
        if (!result.IsPass && options.Corrections && options.Polish)
        {
            foreach (var repaired in new[] { RuleCleaner.ApplyInsteadCorrections(raw), ApplyExplicitRepairs(raw), ApplyCuedSentenceRepairs(raw) })
                if (repaired != raw && CheckWords(repaired, cleaned, options, allowedTerms, soundsLike).IsPass) return CleanupGuardResult.Pass;
        }
        if (result.IsPass || !options.Spoken) return result;
        var spoken = RuleCleaner.SpokenCommands(raw);
        if (spoken == raw) return result;
        return Check(spoken, cleaned, options with { Spoken = false }, allowedTerms, soundsLike).IsPass ? CleanupGuardResult.Pass : result;
    }

    static readonly string[] StructureMarkers = ["**", "##", "```", "\n- ", "\n* ", "•"];
    static readonly FrozenSet<string> MarkerNouns = new[] { "number", "step", "item" }.ToFrozenSet();
    static readonly FrozenSet<string> AdjectiveDeterminers = new[] { "the", "a", "an", "my", "our", "your", "his", "her", "their", "its", "this", "that" }.ToFrozenSet();
    static readonly FrozenSet<string> OrdinalSubjects = new[] { "i", "we", "you", "he", "she", "they", "it", "let's", "lets" }.ToFrozenSet();
    static readonly FrozenSet<string> OneDeterminers = new[] { "the", "this", "that", "which", "each", "every", "another", "a", "any" }.ToFrozenSet();
    static readonly FrozenSet<string> CoordinationJoins = new[] { "and", "or" }.ToFrozenSet();
    static readonly FrozenSet<string> MeaningfulAfterPair = new[] { "the", "a", "an", "what", "that", "it", "how", "where", "why", "who", "business", "to", "this", "him", "her", "them", "his", "everything", "nothing", "exactly" }.ToFrozenSet();
    static readonly FrozenSet<string> PairJoins = new[] { "and", "but", "so" }.ToFrozenSet();
    static readonly FrozenSet<string> PairSubjects = new[] { "i", "we", "you", "he", "she", "they", "it's", "we're", "you're", "they're" }.ToFrozenSet();
    static readonly FrozenSet<string> LikeDeterminers = new[] { "the", "a", "an", "this", "that", "these", "those" }.ToFrozenSet();
    static readonly string[] InflectionEndings = ["s", "es", "ed", "d", "ing"];
    static readonly FrozenSet<string> QuestionAuxiliaries = new[] { "do", "does", "did", "is", "are", "was", "were", "can", "could", "should", "would", "will", "shall", "may", "might", "must", "have", "has", "had" }.ToFrozenSet();
    static readonly FrozenSet<string> Prepositions = new[] { "to", "from", "for", "with", "in", "on", "at", "by", "of", "into", "off", "up", "out", "about", "than", "as" }.ToFrozenSet();
    static readonly FrozenSet<string> RelationalPrepositions = new[] { "for", "from", "with", "by", "at", "on", "in", "into", "about" }.ToFrozenSet();
    static readonly FrozenSet<string>[] Tenses = [new[] { "was", "were", "had", "did" }.ToFrozenSet(), new[] { "is", "are", "am", "has", "have", "do", "does" }.ToFrozenSet(), new[] { "will", "shall" }.ToFrozenSet()];
    static readonly FrozenSet<string> Particles = new[] { "up", "off", "out", "more", "most", "down", "over", "back", "away", "too", "very" }.ToFrozenSet();
    static readonly FrozenSet<string> InformalForms = new[] { "gotta", "wanna", "kinda", "sorta" }.ToFrozenSet();
    static readonly FrozenSet<string> Interjections = new[] { "hey", "okay", "ok", "well", "oh", "alright", "anyway" }.ToFrozenSet();
    static readonly FrozenSet<string> DroppedJoins = new[] { "and", "so", "but", "because", "anyway" }.ToFrozenSet();
    static readonly FrozenSet<string> Auxiliaries = Modals.Union(["has", "have", "had", "been", "be", "is", "are", "was", "were", "am", "do", "does", "did", "going", "gonna", "gotta", "wanna"]).ToFrozenSet();
    static readonly FrozenSet<string> InsertJoins = new[] { "and", "but", "or", "so", "if", "then", "than" }.ToFrozenSet();
    static readonly FrozenSet<string> Adverbs = new[] { "also", "just", "really", "very", "too", "more", "most", "only", "even", "still", "already" }.ToFrozenSet();

    static int TenseIndex(string w)
    {
        for (int k = 0; k < Tenses.Length; k++) if (Tenses[k].Contains(w)) return k;
        return -1;
    }

    static int LongestRun(IEnumerable<int> xs)
    {
        int best = 0, run = 0; long last = long.MinValue;
        foreach (var x in xs.OrderBy(v => v)) { run = x == last + 1 ? run + 1 : 1; best = Math.Max(best, run); last = x; }
        return best;
    }

    static int Increment(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    static CleanupGuardResult CheckWords(string raw, string cleaned, CleanupOptions options, IReadOnlyList<string> allowedTerms, Func<string, string, bool> soundsLike)
    {
        static CleanupGuardResult Fail(CleanupFailureReason r) => CleanupGuardResult.Fail(r);
        // Judge corrections against the comma form the cleaner rewrote ("Tuesday. No wait." -> "Tuesday, no wait,").
        if (options.Corrections) raw = RuleCleaner.MergeSplitCues(raw);
        var rawWords = Words(raw);
        var outWords = Words(cleaned);
        if (rawWords.Count == 0)
        {
            var o = Strings.TrimWhitespaceAndNewlines(cleaned);
            return o.Length == 0 || o == Strings.TrimWhitespaceAndNewlines(raw) ? CleanupGuardResult.Pass : Fail(CleanupFailureReason.InsertedWords);
        }
        if (outWords.Count == 0) return Fail(CleanupFailureReason.EmptyOutput);
        if (Answers(raw, cleaned)) return Fail(CleanupFailureReason.AnswerPattern); // Inserted words are caught below.
        foreach (var marker in StructureMarkers)
            if (Strings.Contains(cleaned, marker) && !Strings.Contains(raw, marker)) return Fail(CleanupFailureReason.StructureAdded);
        // No new symbols or emphasis: only list/sentence punctuation may increase.
        var mayIncrease = new HashSet<string> { ".", ",", ":", "'", "’", "-", "\n", " " };
        if (options.Punctuation) mayIncrease.Add("?");
        if (options.Professional) mayIncrease.Add(";");
        var rawChars = new Dictionary<string, int>(); var outChars = new Dictionary<string, int>();
        foreach (var c in SwiftText.Characters(raw)) if (!SwiftText.IsLetter(c) && !SwiftText.IsNumber(c)) Increment(rawChars, c);
        foreach (var c in SwiftText.Characters(cleaned)) if (!SwiftText.IsLetter(c) && !SwiftText.IsNumber(c)) Increment(outChars, c);
        foreach (var (c, n) in outChars)
            if (n > rawChars.GetValueOrDefault(c) && !mayIncrease.Contains(c)) return Fail(CleanupFailureReason.StructureAdded);
        // Quoted speech is kept verbatim unless a correction removes the whole quote.
        var rawTokens = CleanupText.Tokenize(raw);
        var mask = CleanupText.QuotedMask(rawTokens);
        int? spanStart = null;
        for (int i = 0; i < rawTokens.Count; i++)
        {
            bool isMark = rawTokens[i].Kind == TokenKind.Punct && rawTokens[i].Text is "\"" or "“" or "”";
            if (isMark && spanStart is null && mask[i]) { spanStart = i; continue; }
            if (spanStart is { } s0 && (isMark || i == rawTokens.Count - 1))
            {
                var inner = Strings.TrimWhitespaces(CleanupText.Render(rawTokens.Skip(s0 + 1).Take((isMark ? i : i + 1) - s0 - 1)));
                spanStart = null;
                if (inner.Length == 0 || Strings.Contains(cleaned, inner)) continue;
                // A correction may drop a whole quoted reparandum; partial edits are never allowed.
                var outSet = outWords.Select(w => w.Lower).ToHashSet();
                if (options.Corrections && CorrectionWindow(raw).Count > 0 && Words(inner).All(w => !outSet.Contains(w.Lower))) continue;
                return Fail(CleanupFailureReason.QuoteEdited);
            }
        }
        int rawLines = Strings.Split(raw, c => c == "\n").Count;
        // With punctuation on, paragraph breaks change no words, so only other line breaks count as added structure.
        var lines = options.Punctuation ? CleanupText.JoiningParagraphs(cleaned) : cleaned;
        if (options.Lists)
        {
            if (NonListLineCount(lines) > rawLines + 2) return Fail(CleanupFailureReason.StructureAdded);
        }
        else if (Strings.Split(lines, c => c == "\n").Count > rawLines) return Fail(CleanupFailureReason.StructureAdded);

        double rawLen = SwiftText.Count(raw), outLen = SwiftText.Count(cleaned);
        double minRatio = 0.9;
        if (options.Lists) minRatio = Math.Min(minRatio, 0.5); // "number three, " becomes "3. "
        if (options.Fillers) minRatio = Math.Min(minRatio, 0.4);
        if (options.Corrections) minRatio = Math.Min(minRatio, 0.35);
        if (options.Fillers && options.Corrections) minRatio = 0.25;
        double maxRatio = options.Lists ? 1.35 : 1.1;
        if (outLen < rawLen * minRatio - 10 || outLen > rawLen * maxRatio + 10) return Fail(CleanupFailureReason.LengthRatio);

        // Word-level edit distance (insertions + deletions in an LCS alignment), by option.
        var a = rawWords.Select(w => w.Lower).ToList(); var b = outWords.Select(w => w.Lower).ToList();
        var (matchedRaw, matchedOut) = Align(a, b);
        int deletions = a.Count - matchedRaw.Count, insertions = b.Count - matchedOut.Count;
        double maxEdit = 0.15;
        if (options.Fillers) maxEdit += 0.45;
        if (options.Corrections) maxEdit += 0.45;
        if (options.Lists) maxEdit += 0.35;
        maxEdit = Math.Min(maxEdit, 0.9);
        // Polish and professional ask for grammar fixes: short utterances may swap two words, or three reordered in professional.
        // ponytail: flat floor, not a grammar-aware diff; a short full rewrite relies on the later checks.
        double editFloor = options.Professional ? 6.0 : options.Polish ? 4 : 0;
        if (deletions + insertions > Math.Max(maxEdit * a.Count + 1, editFloor)) return Fail(CleanupFailureReason.EditDistance);

        HashSet<int> window = options.Corrections ? CorrectionWindow(raw, loose: options.Professional) : [];
        // A correction is applied whole or not at all: a dropped cue takes the word it corrects with it,
        // so "for 2, no, 3 people" never becomes "for 2, 3 people". A cue that reaches no word before it drops alone.
        var cueSpans = new List<(int Lo, int Hi)>();
        for (int i = 0; i < a.Count; i++)
            foreach (var cue in CorrectionCues)
                if (i + cue.Count <= a.Count && a.Skip(i).Take(cue.Count).SequenceEqual(cue) && Enumerable.Range(i, cue.Count).All(window.Contains))
                    cueSpans.Add((i, i + cue.Count));
        var cueWords = cueSpans.SelectMany(s => Enumerable.Range(s.Lo, s.Hi - s.Lo)).ToHashSet();
        foreach (var span in cueSpans)
        {
            if (!Enumerable.Range(span.Lo, span.Hi - span.Lo).Any(x => !matchedRaw.Contains(x))) continue;
            int k = span.Lo - 1, m = span.Hi;
            while (k >= 0 && (cueWords.Contains(k) || CleanupText.Hesitations.Contains(a[k]))) k--;
            while (m < a.Count && (cueWords.Contains(m) || CleanupText.Hesitations.Contains(a[m]))) m++;
            if (k >= 0 && m < a.Count && window.Contains(k) && matchedRaw.Contains(k) && matchedRaw.Contains(m)) return Fail(CleanupFailureReason.UnexplainedDeletion);
        }
        HashSet<string> markerValues = options.Lists ? ListMarkerValues(cleaned) : [];
        // Professional may write spoken markers ("item one milk item two eggs") as an inline list:
        // they count as markers when the raw counts 1, 2, ... with them.
        if (options.Professional && options.Lists)
        {
            var spokenMarkers = Enumerable.Range(0, a.Count - 1).Where(k => MarkerNouns.Contains(a[k]))
                .Select(k => CleanupText.Cardinals.TryGetValue(a[k + 1], out var v) ? v : Strings.SwiftInt(a[k + 1])).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (spokenMarkers.Count >= 2 && spokenMarkers.SequenceEqual(Enumerable.Range(1, spokenMarkers.Count).Select(v => (long)v)))
                markerValues.UnionWith(spokenMarkers.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }
        // Stutters and restarts ("I I", "don't, uh, don't") that filler removal may collapse.
        var repeats = new HashSet<int>();
        if (options.Fillers)
        {
            for (int i = 0; i < a.Count; i++)
            {
                int j = i + 1;
                while (j < a.Count && j <= i + 3)
                {
                    if (a[j] == a[i]) { repeats.Add(i); repeats.Add(j); break; }
                    if (!CleanupText.Hesitations.Contains(a[j])) break;
                    j++;
                }
            }
            // Restarted phrases, often split by a chunk cut: only the first copy of an immediately repeated 2–6 word run may go.
            for (int n = 2; n <= 6; n++)
                for (int i = 0; i < a.Count; i++)
                    if (i + 2 * n <= a.Count && a.Skip(i).Take(n).SequenceEqual(a.Skip(i + n).Take(n))) repeats.UnionWith(Enumerable.Range(i, n));
        }
        bool DropAllowed(int i) => window.Contains(i) || repeats.Contains(i);
        // Polish and Professional: a short sentence that the next, longer one restarts with the same two opening words may go whole.
        // ponytail: opening words can't tell an abandoned sentence from a parallel one; the prompt asks the model to keep those.
        var restarts = new HashSet<int>();
        if (options.Corrections && options.Polish)
        {
            var starts = new List<int> { 0 };
            starts.AddRange(Enumerable.Range(1, Math.Max(0, rawWords.Count - 1)).Where(k => rawWords[k].SentenceStart));
            starts.Add(a.Count);
            for (int k = 0; k < starts.Count - 2; k++)
            {
                int s = starts[k], t = starts[k + 1], next = starts[k + 2];
                if (t - s is >= 3 and <= 12 && t - s < next - t && a[s] == a[t] && a[s + 1] == a[t + 1]) restarts.UnionWith(Enumerable.Range(s, t - s));
            }
        }
        // Filler words the fillers option may delete: hesitations anywhere; "like" and "you know"
        // only when set off by commas or sentence-initial (never "I like you", "you know the answer").
        bool FillerDeletable(int i)
        {
            var w = rawWords[i];
            if (CleanupText.Hesitations.Contains(w.Lower)) return true;
            if (w.Lower == "like") return (w.CommaBefore || w.SentenceStart) && w.CommaAfter;
            if (w.Lower == "you" && i + 1 < a.Count && a[i + 1] == "know")
            {
                var know = rawWords[i + 1];
                return (w.CommaBefore && (know.CommaAfter || know.EndAfter || i + 2 == a.Count)) || (w.SentenceStart && know.CommaAfter);
            }
            if (w.Lower == "know" && i > 0 && a[i - 1] == "you") return FillerDeletable(i - 1);
            return false;
        }
        bool hasListMarkers = markerValues.Count > 0;
        // A list starts only where the speaker announces one: a list noun before the first marker in its sentence,
        // a colon just before that sentence, or a list noun in the sentence before it.
        bool Announced(int i)
        {
            int start = i;
            while (start > 0 && !rawWords[start].SentenceStart) start--;
            for (int x = start; x < i; x++) if (RuleCleaner.StrongListCues.Contains(a[x])) return true;
            if (!(start > 0)) return false;
            if (!rawWords[start - 1].EndAfter) return true;
            if (start != i) return false;
            int lead = start - 1;
            while (lead > 0 && !rawWords[lead].SentenceStart) lead--;
            for (int x = lead; x < start; x++) if (RuleCleaner.StrongListCues.Contains(a[x])) return true;
            return false;
        }
        // Professional also takes digits, as speech recognition often writes them ("item 1 milk").
        long? SpokenMarker(int i)
        {
            if (!(i > 0) || !MarkerNouns.Contains(a[i - 1])) return null;
            return CleanupText.Cardinals.TryGetValue(a[i], out var v) ? v : options.Professional ? Strings.SwiftInt(a[i]) : null;
        }
        // Ordinals inside a fixed phrase are prose. ponytail: a closed list; an unlisted phrase can still pass as a list.
        long? OrdinalMarker(int i)
        {
            if (!CleanupText.Ordinals.TryGetValue(a[i], out var n)) return null;
            if (i + 1 < a.Count && !rawWords[i].CommaAfter && OrdinalPhraseNouns.Contains(a[i + 1])) return null;
            // A date's day ("May first", "June second") is never a list marker.
            if (i > 0 && RuleCleaner.MonthsAndMay.Contains(a[i - 1])) return null;
            return n;
        }
        // A marker counts inside a counted sequence ("first ... second") that starts at an announced first marker.
        bool InSequence(int i, Func<int, long?> marker, int markerWords, bool announce)
        {
            if (marker(i) is not { } n) return false;
            var values = Enumerable.Range(0, a.Count).Select(marker).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (!(values.Contains(n + 1) || values.Contains(n - 1))) return false;
            return !announce || Enumerable.Range(0, i + 1).Any(x => marker(x) == 1 && Announced(x - markerWords));
        }
        // An ordinal modifying a noun is prose even in Professional. ponytail: a heuristic.
        bool Adjectival(int i)
        {
            if (i > 0 && AdjectiveDeterminers.Contains(a[i - 1])) return true;
            if (!(i + 1 < a.Count) || rawWords[i].CommaAfter || OrdinalSubjects.Contains(a[i + 1])) return false;
            return Enumerable.Range(0, a.Count).Any(x => x != i && x + 1 < a.Count && CleanupText.Ordinals.ContainsKey(a[x]) && a[x + 1] == a[i + 1]);
        }
        // Enumeration words a list may replace with "N." markers: ordinals, "number/step/item" plus a cardinal, and an "and"/"then" joining the next marker.
        bool IsMarkerWord(int i)
        {
            if (!(options.Lists && hasListMarkers)) return false;
            var w = a[i];
            if (CleanupText.Ordinals.ContainsKey(w)) return InSequence(i, OrdinalMarker, 0, !options.Professional || Adjectival(i));
            if (SpokenMarker(i) is not null) return InSequence(i, SpokenMarker, 1, !options.Professional);
            if (MarkerNouns.Contains(w) && i + 1 < a.Count && SpokenMarker(i + 1) is not null) return IsMarkerWord(i + 1);
            if ((w == "and" || w == "then") && i + 1 < a.Count && IsMarkerWord(i + 1)) return true;
            // Professional: also across a filler ("eggs and yeah item three").
            if (options.Professional && (w == "and" || w == "then") && i + 2 < a.Count
                && (CleanupText.Hesitations.Contains(a[i + 1]) || PolishFillers.Contains(a[i + 1])) && IsMarkerWord(i + 2)) return true;
            return false;
        }
        // A new "N." line needs a spoken enumeration of at least two items that reaches N.
        var addedValues = ListMarkerValues(cleaned); addedValues.ExceptWith(ListMarkerValues(raw));
        var addedMarkers = addedValues.Select(Strings.SwiftInt).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (addedMarkers.Count > 0)
        {
            int spokenCount = Enumerable.Range(0, a.Count).Count(k => (CleanupText.Ordinals.ContainsKey(a[k]) || CleanupText.Cardinals.ContainsKey(a[k]) || Strings.SwiftInt(a[k]) is not null) && IsMarkerWord(k));
            if (spokenCount < 2 || addedMarkers.Max() > spokenCount) return Fail(CleanupFailureReason.StructureAdded);
        }

        // Professional: "one" as a pronoun ("the quarterly one not the monthly one") may go.
        bool PronounOne(int i)
        {
            if (!options.Professional || a[i] != "one" || !(i > 0)
                || !(OneDeterminers.Contains(a[i - 1]) || (i > 1 && OneDeterminers.Contains(a[i - 2]) && CleanupText.NumberValue(a[i - 1]) is null))) return false;
            return i + 1 == a.Count || GrammarWords.Contains(a[i + 1]) || CleanupText.IsNegation(a[i + 1]);
        }
        // Numbers: nothing new; drops only inside a correction or as a list marker.
        var rawNumbers = new Dictionary<string, int>(); var outNumbers = new Dictionary<string, int>(); var allowedNumberDrops = new Dictionary<string, int>();
        for (int i = 0; i < rawWords.Count; i++)
        {
            if (CleanupText.NumberValue(rawWords[i].Lower) is not { } v) continue;
            Increment(rawNumbers, v);
            if (PronounOne(i) || DropAllowed(i) || (IsMarkerWord(i) && markerValues.Contains(v) && !matchedRaw.Contains(i))) Increment(allowedNumberDrops, v);
        }
        foreach (var w in outWords) if (CleanupText.NumberValue(w.Lower) is { } v) Increment(outNumbers, v);
        foreach (var (v, c) in outNumbers) if (c > rawNumbers.GetValueOrDefault(v)) return Fail(CleanupFailureReason.NumberAdded);
        foreach (var (v, c) in rawNumbers) if (outNumbers.GetValueOrDefault(v) < c - allowedNumberDrops.GetValueOrDefault(v)) return Fail(CleanupFailureReason.NumberDropped);

        // Negation: count may not rise, and may drop only inside a correction.
        int rawNeg = rawWords.Count(w => CleanupText.IsNegation(w.Lower));
        int outNeg = outWords.Count(w => CleanupText.IsNegation(w.Lower));
        int negInWindow = Enumerable.Range(0, rawWords.Count).Count(i => DropAllowed(i) && CleanupText.IsNegation(rawWords[i].Lower));
        if (outNeg > rawNeg || outNeg < rawNeg - negInWindow) return Fail(CleanupFailureReason.NegationChanged);

        var rawSet = a.ToHashSet();
        var termWords = allowedTerms.SelectMany(t => Words(t).Select(w => w.Lower)).ToHashSet();
        // Cleanup never inserts words: every unaligned output word must be a supplied dictionary term.
        // A correction may restate a word from its corrected span in a new position; such a word consumes that unmatched raw word.
        var consumedRaw = new HashSet<int>(matchedRaw);
        // Matches keep order: the k-th matched raw word pairs with the k-th matched output word.
        var pairedRaw = matchedRaw.OrderBy(x => x).ToList(); var pairedOut = matchedOut.OrderBy(x => x).ToList();
        IEnumerable<int> Gap(int j)
        {
            int before = pairedOut.Count(x => x < j);
            int lo = before == 0 ? 0 : pairedRaw[before - 1] + 1, hi = before < pairedRaw.Count ? pairedRaw[before] : rawWords.Count;
            return Enumerable.Range(lo, hi - lo);
        }
        // Gap widened by one kept word on each side.
        IEnumerable<int> NearGap(int j)
        {
            int before = pairedOut.Count(x => x < j);
            int lo = before < 2 ? 0 : pairedRaw[before - 2] + 1, hi = before + 1 < pairedRaw.Count ? pairedRaw[before + 1] : rawWords.Count;
            return Enumerable.Range(lo, hi - lo);
        }
        int? First(IEnumerable<int> xs, Func<int, bool> test) { foreach (var x in xs) if (test(x)) return x; return null; }
        static string Letters(string s) => string.Concat(SwiftText.Characters(s).Where(SwiftText.IsLetter));
        static string Form(string t)
        {
            if (CleanupText.Ordinals.ContainsKey(t)) return "ordinal";
            var c = SwiftText.Characters(t);
            if (c.Count > 0 && SwiftText.IsNumber(c[0])) return string.Concat(c.SkipWhile(x => SwiftText.IsNumber(x) || x == "." || x == ","));
            return "";
        }
        static bool NotFamily(string t) => t == "not" || Strings.HasSuffix(t, "n't") || CleanupText.TruncatedNegations.ContainsKey(t);
        static bool Pairs(IReadOnlyList<string> ws, int k) =>
            (CoordinationJoins.Contains(ws[k]) && ((k > 0 && IsPronoun(ws[k - 1])) || (k + 1 < ws.Count && IsPronoun(ws[k + 1]))))
            || (k > 1 && CoordinationJoins.Contains(ws[k - 1]) && IsPronoun(ws[k - 2]))
            || (k + 2 < ws.Count && CoordinationJoins.Contains(ws[k + 1]) && IsPronoun(ws[k + 2]))
            || (IsPronoun(ws[k]) && ((k > 0 && CoordinationJoins.Contains(ws[k - 1])) || (k + 1 < ws.Count && CoordinationJoins.Contains(ws[k + 1]))));
        var polishInserted = new List<int>();
        for (int j = 0; j < outWords.Count; j++)
        {
            var w = outWords[j];
            if (matchedOut.Contains(j) || termWords.Contains(w.Lower)) continue;
            if (First(Enumerable.Range(0, rawWords.Count), x => window.Contains(x) && !consumedRaw.Contains(x) && a[x] == w.Lower) is { } i0) { consumedRaw.Add(i0); continue; }
            // A negative contraction may complete one speech recognition truncated in the same place, with the same auxiliary.
            if (First(Gap(j), x => !consumedRaw.Contains(x) && CleanupText.TruncatedNegations.TryGetValue(a[x], out var c) && c.Contains(w.Lower)) is { } i1) { consumedRaw.Add(i1); continue; }
            if (w.IsName && !rawSet.Contains(w.Lower))
            {
                // Polish and Professional: a name may respell one lowercase raw word in its place (similarity 0.7 or soundsLike).
                // A capitalized antonym is not a respelling ("include" -> "Exclude").
                if (!options.Polish || First(Gap(j), x => !consumedRaw.Contains(x) && !rawWords[x].IsName && !GrammarWords.Contains(a[x])
                        && !IsPronoun(a[x]) && CleanupText.NumberValue(a[x]) is null && !CleanupText.IsNegation(a[x]) && !PolarityFlip(a[x], w.Lower)
                        && (CleanupText.Similarity(a[x], w.Lower) >= 0.7 || soundsLike(Letters(a[x]), Letters(w.Lower)))) is not { } i2) return Fail(CleanupFailureReason.NameAdded);
                consumedRaw.Add(i2); continue;
            }
            // Professional: a number or negation never moves; a new one replaces one in its own gap.
            if (options.Professional && CleanupText.NumberValue(w.Lower) is { } v)
            {
                // Same value and same form: "one" never becomes "first", nor "12pm" "12am" or "1" "1st".
                if (First(Gap(j), x => !consumedRaw.Contains(x) && CleanupText.NumberValue(a[x]) == v && Form(a[x]) == Form(w.Lower)) is not { } i3) return Fail(CleanupFailureReason.NumberAdded);
                consumedRaw.Add(i3); continue;
            }
            if (options.Professional && CleanupText.IsNegation(w.Lower))
            {
                // Only within the not/n't family ("don't" -> "doesn't", "not" -> "won't"); never/no/nothing stay exact.
                if (First(Gap(j), x => !consumedRaw.Contains(x) && CleanupText.IsNegation(a[x]) && (a[x] == w.Lower || (NotFamily(a[x]) && NotFamily(w.Lower)))) is not { } i4) return Fail(CleanupFailureReason.NegationChanged);
                consumedRaw.Add(i4); continue;
            }
            // Professional may reorder locally: a moved word consumes its raw copy within one kept word of its gap,
            // only next to "and"/"or" with a pronoun conjunct ("me and John" -> "John and I").
            int jj = j;
            if (options.Professional && First(NearGap(j), x => !consumedRaw.Contains(x) && a[x] == w.Lower && (Pairs(a, x) || Pairs(b, jj))) is { } i5) { consumedRaw.Add(i5); continue; }
            if (options.Polish) { polishInserted.Add(j); continue; }
            return Fail(CleanupFailureReason.InsertedWords);
        }
        var outNameCounts = new Dictionary<string, int>();
        foreach (var w in outWords) Increment(outNameCounts, w.Lower);
        var rawNameCounts = new Dictionary<string, int>(); var allowedNameDrops = new Dictionary<string, int>();
        for (int i = 0; i < rawWords.Count; i++)
        {
            if (!rawWords[i].IsName) continue;
            Increment(rawNameCounts, rawWords[i].Lower);
            if (DropAllowed(i) || restarts.Contains(i)) Increment(allowedNameDrops, rawWords[i].Lower);
        }
        foreach (var (n, c) in rawNameCounts)
        {
            if (!(outNameCounts.GetValueOrDefault(n) < c - allowedNameDrops.GetValueOrDefault(n))) continue;
            if (termWords.Count == 0) return Fail(CleanupFailureReason.NameDropped);
            // A dictionary respelling may replace a misheard name.
            if (!outWords.Any(w => termWords.Contains(w.Lower))) return Fail(CleanupFailureReason.NameDropped);
        }

        // Every deleted word must be explained by an enabled option, or by a dictionary respelling. Each unaligned term word must
        // replace its own run of 1–3 adjacent raw words in its alignment gap that spell it, are spelled at least half like it, or sound like it.
        static string Spelled(string text) => string.Concat(SwiftText.Characters(text).Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)));
        for (int j = 0; j < outWords.Count; j++)
        {
            var w = outWords[j];
            if (matchedOut.Contains(j) || !termWords.Contains(w.Lower)) continue;
            var target = Spelled(w.Lower);
            var gap = Gap(j).ToList();
            (int Lo, int Hi)? replaced = null;
            for (int length = 3; length >= 1 && replaced is null; length--)
            {
                if (gap.Count == 0) break;
                int gapHi = gap[^1] + 1;
                foreach (var start in gap)
                {
                    if (start + length > gapHi) continue;
                    if (Enumerable.Range(start, length).Any(consumedRaw.Contains)) continue;
                    var heard = Spelled(string.Concat(Enumerable.Range(start, length).Select(x => a[x])));
                    if (heard == target || CleanupText.Similarity(heard, target) >= 0.5 || soundsLike(heard, target)) { replaced = (start, start + length); break; }
                }
            }
            if (replaced is not { } rep) return Fail(CleanupFailureReason.InsertedWords);
            consumedRaw.UnionWith(Enumerable.Range(rep.Lo, rep.Hi - rep.Lo));
        }
        // Polish: grammar fixes swap a few words in place; fillers and false starts may go.
        // ponytail: closed word lists and similarity, not a grammar model; strict mode stays the meaning-safe choice.
        bool Filler(int i)
        {
            bool Pair(int x)
            {
                if (!(x >= 0 && x + 2 < a.Count && PolishFillerPairs.Any(p => p[0] == a[x] && p[1] == a[x + 1]))) return false;
                // Professional: also at a sentence start or after another filler. Never before a meaningful complement.
                if (options.Professional && !MeaningfulAfterPair.Contains(a[x + 2]) && (rawWords[x].SentenceStart || (x > 0 && PolishFillers.Contains(a[x - 1]))
                    || PairJoins.Contains(a[x + 2]))) return true;
                // Without punctuation, only a following personal subject clearly starts a new thought.
                return PairSubjects.Contains(a[x + 2]);
            }
            if (a[i] == "like")
                return i > 0 && i + 1 < a.Count && !MeaningfulLikeAfter.Contains(a[i - 1]) && !LikeDeterminers.Contains(a[i + 1]);
            return CleanupText.Hesitations.Contains(a[i]) || PolishFillers.Contains(a[i]) || Pair(i) || Pair(i - 1);
        }
        CleanupGuardResult PolishCheck(List<int> unexplained, List<int> inserted)
        {
            // New words: a few, inside the text (never a prefix like "Sure," or an appended answer), no long run.
            if (inserted.Count > Math.Max(2, a.Count * 8 / 100) || LongestRun(inserted) > 3) return Fail(CleanupFailureReason.InsertedWords);
            if (pairedOut.Count > 0 && inserted.Any(x => x < pairedOut[0] || x > pairedOut[^1])) return Fail(CleanupFailureReason.InsertedWords);
            // Each new word replaces a word in its gap that it respells, or is a grammar word.
            var deleted = new HashSet<int>(unexplained);
            foreach (var j in inserted)
            {
                var w = outWords[j].Lower;
                if (First(Gap(j), x => deleted.Contains(x) && !rawWords[x].IsName && PolishSwap(a[x], w)) is { } i) { deleted.Remove(i); continue; }
                if (!GrammarWords.Contains(w)) return Fail(CleanupFailureReason.InsertedWords);
            }
            // Removed words: fillers (with the fillers option), grammar words, or a false start that the speaker restates
            // within the next four words (with the corrections option).
            var dropped = new List<int>();
            foreach (var i in deleted.OrderBy(x => x))
            {
                if (rawWords[i].IsName) return Fail(CleanupFailureReason.NameDropped);
                if (Filler(i)) { if (options.Fillers) continue; else return Fail(CleanupFailureReason.UnexplainedDeletion); }
                dropped.Add(i);
            }
            if (dropped.Count > Math.Max(4, a.Count / 10) || LongestRun(dropped) > 6) return Fail(CleanupFailureReason.UnexplainedDeletion);
            int k = 0;
            while (k < dropped.Count)
            {
                int end = k;
                while (end + 1 < dropped.Count && dropped[end + 1] == dropped[end] + 1) end++;
                var run = dropped.GetRange(k, end - k + 1);
                int afterLo = run[^1] + 1, afterHi = Math.Min(a.Count, run[^1] + 5);
                // Every content word of the dropped phrase must come back, not just a shared "the".
                var content = run.Where(x => !GrammarWords.Contains(a[x])).ToList();
                bool restated = content.Count > 0 && content.All(x => Enumerable.Range(afterLo, afterHi - afterLo).Any(y => CleanupText.Similarity(a[x], a[y]) >= 0.5));
                if (!run.All(x => DroppableGrammarWords.Contains(a[x])) && !(options.Corrections && restated)) return Fail(CleanupFailureReason.UnexplainedDeletion);
                k = end + 1;
            }
            return CleanupGuardResult.Pass;
        }
        var unexplained = new List<int>();
        for (int i = 0; i < rawWords.Count; i++)
        {
            if (consumedRaw.Contains(i)) continue;
            if (options.Fillers && (FillerDeletable(i) || repeats.Contains(i))) continue;
            if (options.Corrections && window.Contains(i)) continue;
            if (restarts.Contains(i)) continue;
            if (IsMarkerWord(i)) continue;
            unexplained.Add(i);
        }
        // Professional: budgets only. ponytail: a reworded word can change meaning ("accept" -> "decline"); names, numbers,
        // negations, answers and quotes are checked above. `long` is `short` with a regular English ending.
        static bool Inflection(string shortWord, string longWord)
        {
            var sc = SwiftText.Characters(shortWord);
            if (sc.Count < 2 || !(SwiftText.Count(longWord) > sc.Count) || PolarityFlip(shortWord, longWord)) return false;
            var dropLast = string.Concat(sc.Take(sc.Count - 1));
            var stems = new[] { shortWord, dropLast, dropLast + "i", shortWord + sc[^1] };
            return stems.Any(stem => InflectionEndings.Any(e => stem + e == longWord));
        }
        if (options.Professional)
        {
            // A kept number or negation keeps its context: a content word dropped on one side of it may not come back on the other.
            for (int k = 0; k < pairedRaw.Count; k++)
            {
                if (CleanupText.NumberValue(a[pairedRaw[k]]) is null && !CleanupText.IsNegation(a[pairedRaw[k]])) continue;
                int i = pairedRaw[k], j = pairedOut[k];
                int rbLo = k > 0 ? pairedRaw[k - 1] + 1 : 0, obLo = k > 0 ? pairedOut[k - 1] + 1 : 0;
                int raHi = k + 1 < pairedRaw.Count ? pairedRaw[k + 1] : a.Count, oaHi = k + 1 < pairedOut.Count ? pairedOut[k + 1] : b.Count;
                bool Crosses(int fromLo, int fromHi, int toLo, int toHi) =>
                    Enumerable.Range(fromLo, fromHi - fromLo).Any(x => !GrammarWords.Contains(a[x]) && !Filler(x) && Enumerable.Range(toLo, toHi - toLo).Any(y => CleanupText.Similarity(a[x], b[y]) >= 0.5));
                if (Crosses(rbLo, i, j + 1, oaHi) || Crosses(i + 1, raHi, obLo, j)) return Fail(CleanupFailureReason.NumberAdded);
            }
            var inserted = polishInserted.Select(x => outWords[x].Lower).ToList();
            if (inserted.Count > Math.Max(4, a.Count / 4)) return Fail(CleanupFailureReason.InsertedWords);
            if (pairedOut.Count > 0 && polishInserted.Any(x => x < pairedOut[0] || x > pairedOut[^1])) return Fail(CleanupFailureReason.InsertedWords);
            // Names keep their order ("Alice paid Bob" never becomes "Bob paid Alice"), counting a capitalized sentence start that is
            // not a grammar word. Every occurrence counts, whatever its case.
            var nameSet = rawWords.Where(w => w.IsName || (w.Capitalized && !GrammarWords.Contains(w.Lower))).Select(w => w.Lower).ToHashSet();
            var rawNames = rawWords.Select(w => w.Lower).Where(nameSet.Contains).ToList();
            int rest = 0;
            foreach (var n in outWords.Select(w => w.Lower).Where(nameSet.Contains))
            {
                int k = rawNames.IndexOf(n, rest);
                if (k < 0) return Fail(CleanupFailureReason.NameDropped);
                rest = k + 1;
            }
            // Pronoun persons keep their order ("I told him" never becomes "he told me"); "it" may come and go.
            static List<int> PersonSequence(IEnumerable<string> ws) => ws.Select(PersonIndex).Where(x => x >= 0 && x != 4).ToList();
            var restPersons = PersonSequence(Enumerable.Range(0, rawWords.Count).Where(x => !DropAllowed(x) && !Filler(x)).Select(x => a[x]));
            int restP = 0;
            foreach (var person in PersonSequence(b))
            {
                int k = restPersons.IndexOf(person, restP);
                if (k < 0) return Fail(CleanupFailureReason.UnexplainedDeletion);
                restP = k + 1;
            }
            // Removed words: fillers (with that option), droppable grammar words, a pronoun replaced in the same person,
            // or a content word replaced by a similar new word ("table" -> "tables").
            var replacements = polishInserted.Where(x => !GrammarWords.Contains(b[x])).ToList();
            int droppedCount = 0;
            foreach (var i in unexplained)
            {
                if (rawWords[i].IsName) return Fail(CleanupFailureReason.NameDropped);
                if (Filler(i)) { if (options.Fillers) continue; else return Fail(CleanupFailureReason.UnexplainedDeletion); }
                droppedCount++;
                // A question's leading auxiliary stays ("do you accept" never becomes "You accept").
                if (rawWords[i].SentenceStart && QuestionAuxiliaries.Contains(a[i])) return Fail(CleanupFailureReason.UnexplainedDeletion);
                // A preposition is never swapped for another ("to Paris" -> "from Paris").
                if (Prepositions.Contains(a[i]) && polishInserted.Any(x => Gap(x).Contains(i) && Prepositions.Contains(b[x]) && b[x] != a[i])) return Fail(CleanupFailureReason.UnexplainedDeletion);
                // Nor is a relational one dropped: "This is for Bob" never becomes "This is Bob".
                if (RelationalPrepositions.Contains(a[i])) return Fail(CleanupFailureReason.UnexplainedDeletion);
                // A tense word may change only within its tense ("was" -> "were", never "was" -> "is").
                int tense = TenseIndex(a[i]);
                if (tense >= 0 && polishInserted.Any(x => Gap(x).Contains(i) && TenseIndex(b[x]) is var bt && bt >= 0 && bt != tense)) return Fail(CleanupFailureReason.UnexplainedDeletion);
                // Particles carry meaning ("turn it up", "take it off"); they are never dropped.
                if (Particles.Contains(a[i])) return Fail(CleanupFailureReason.UnexplainedDeletion);
                // Informal forms the rewrite spells out ("gotta" -> "have to").
                if (DroppableGrammarWords.Contains(a[i]) || InformalForms.Contains(a[i]) || PronounOne(i)) continue;
                // A sentence-opening interjection ("hey do you know" -> "Do you know").
                if (rawWords[i].SentenceStart && Interjections.Contains(a[i])) continue;
                // A join dropped where the rewrite ends a sentence ("by Thursday, and I" -> "by Thursday. I").
                if (DroppedJoins.Contains(a[i]))
                {
                    int k = pairedRaw.FindLastIndex(x => x < i);
                    if (k >= 0 && outWords[pairedOut[k]].EndAfter) continue;
                }
                int person = PersonIndex(a[i]);
                if (person >= 0)
                {
                    if (Enumerable.Range(0, outWords.Count).Any(x => !matchedOut.Contains(x) && Persons[person].Contains(outWords[x].Lower))) continue;
                    return Fail(CleanupFailureReason.UnexplainedDeletion);
                }
                // The same word elsewhere is a move, not a replacement, and moves are limited above.
                // A replacement is an inflection of the same word in the same place ("table" -> "tables").
                int r = replacements.FindIndex(x => Gap(x).Contains(i) && (Inflection(a[i], b[x]) || Inflection(b[x], a[i])));
                if (r < 0) return Fail(CleanupFailureReason.UnexplainedDeletion);
                replacements.RemoveAt(r);
            }
            // Every new content word or modal replaces a dropped word; a pronoun contraction ("he's") is the only free one.
            // An inserted auxiliary or join takes the place of a dropped word of the same kind; an adverb is never added.
            foreach (var j in polishInserted)
            {
                var w = b[j];
                if (Adverbs.Contains(w)) return Fail(CleanupFailureReason.InsertedWords);
                var kind = Auxiliaries.Contains(w) ? Auxiliaries : InsertJoins.Contains(w) ? InsertJoins : null;
                if (kind is not null && !Gap(j).Any(x => unexplained.Contains(x) && kind.Contains(a[x]))) return Fail(CleanupFailureReason.InsertedWords);
            }
            // A bare pronoun is new only as a same-person swap in its place ("me" -> "I"), never an added object.
            foreach (var j in polishInserted)
            {
                if (Strings.Contains(b[j], "'")) continue;
                int person = PersonIndex(b[j]);
                if (person < 0) continue;
                if (!NearGap(j).Any(x => unexplained.Contains(x) && Persons[person].Contains(a[x]))) return Fail(CleanupFailureReason.InsertedWords);
            }
            // An inflection of a dropped word in the same place also counts ("do" -> "doing"); a contraction is the only free word.
            if (replacements.Any(j => !(Strings.Contains(b[j], "'") && IsPronoun(b[j])) && !Gap(j).Any(x => unexplained.Contains(x) && Inflection(a[x], b[j]))))
                return Fail(CleanupFailureReason.InsertedWords);
            return droppedCount > Math.Max(4, a.Count / 4) ? Fail(CleanupFailureReason.UnexplainedDeletion) : CleanupGuardResult.Pass;
        }
        if (options.Polish) return PolishCheck(unexplained, polishInserted);
        return unexplained.Count == 0 ? CleanupGuardResult.Pass : Fail(CleanupFailureReason.UnexplainedDeletion);
    }
}

/// <summary>A coarse check for a rewrite, which cannot use the cleanup guard's word-level rules: it must keep the digits and
/// negations, stay in a sane length range, and not leak the prompt's tags.</summary>
public static class TransformGuard
{
    // A correction cue's "no" ("actually no, make it Monday") is not a negation the rewrite must keep.
    static readonly Regex CueNo = RuleCleaner.IcuRegex(@"(?i)\b(actually|oh|wait|sorry),? no\b|\bno,? (wait|sorry|i mean|i meant|actually|scratch that)\b");

    public static bool Accepts(string input, string output)
    {
        var @out = Strings.TrimWhitespaceAndNewlines(output);
        if (@out.Length == 0 || Strings.Contains(@out, "transcript-") || Strings.Contains(@out, "instructions-")) return false;
        int inWords = Strings.Split(input, SwiftText.IsWhitespace).Count, outWords = Strings.Split(@out, SwiftText.IsWhitespace).Count;
        if (!(outWords <= inWords * 2 + 40) || !(inWords < 20 || outWords * 5 >= inWords * 2)) return false;
        static HashSet<string> Digits(string t) => Strings.Split(t, c => !SwiftText.IsNumber(c)).ToHashSet();
        if (!Digits(input).IsSubsetOf(Digits(@out))) return false;
        static bool Negates(string t) => Strings.Split(SwiftText.Lowercased(t), c => !SwiftText.IsLetter(c) && c != "'" && c != "’")
            .Any(w => CleanupText.BasicNegations.Contains(w) || w == "without" || Strings.HasSuffix(w, "n't") || Strings.HasSuffix(w, "n’t"));
        var said = CueNo.Replace(input, " ");
        return !Negates(said) || Negates(@out);
    }
}

/// <summary>Swift <c>String</c> operations the ports need: Character-based (grapheme cluster, canonical equivalence) search and splitting.</summary>
internal static class Strings
{
    static string? First(string s) => s.Length == 0 ? null : s[..StringInfo.GetNextTextElementLength(s)];
    public static bool FirstIsUppercase(string s) => First(s) is { } c && SwiftText.IsUppercase(c);
    public static bool FirstIsLowercase(string s) => First(s) is { } c && SwiftText.IsLowercase(c);
    public static bool FirstIsLetter(string s) => First(s) is { } c && SwiftText.IsLetter(c);
    public static bool FirstIsNumber(string s) => First(s) is { } c && SwiftText.IsNumber(c);

    // Ordinal search equals Swift's when both strings are ASCII without "\r" (each char is then its own Character).
    static bool Simple(string s) { foreach (var c in s) if (c > 0x7F || c == '\r') return false; return true; }

    /// <summary>Swift <c>String.contains(_ other: String)</c>: a run of whole Characters, compared by canonical equivalence.</summary>
    public static bool Contains(string hay, string needle)
    {
        if (Simple(hay) && Simple(needle)) return hay.Contains(needle, StringComparison.Ordinal);
        var h = SwiftText.Characters(hay); var n = SwiftText.Characters(needle);
        for (int i = 0; i + n.Count <= h.Count; i++)
        {
            int k = 0;
            while (k < n.Count && SwiftText.CharEquals(h[i + k], n[k])) k++;
            if (k == n.Count) return true;
        }
        return false;
    }

    public static bool HasPrefix(string s, string p)
    {
        if (Simple(s) && Simple(p)) return s.StartsWith(p, StringComparison.Ordinal);
        var a = SwiftText.Characters(s); var b = SwiftText.Characters(p);
        return b.Count <= a.Count && b.Select((c, i) => SwiftText.CharEquals(a[i], c)).All(x => x);
    }

    public static bool HasSuffix(string s, string p)
    {
        if (Simple(s) && Simple(p)) return s.EndsWith(p, StringComparison.Ordinal);
        var a = SwiftText.Characters(s); var b = SwiftText.Characters(p);
        return b.Count <= a.Count && b.Select((c, i) => SwiftText.CharEquals(a[a.Count - b.Count + i], c)).All(x => x);
    }

    /// <summary>Swift <c>split(whereSeparator:)</c> over Characters, omitting empty pieces.</summary>
    public static List<string> Split(string s, Func<string, bool> isSeparator, int maxSplits = int.MaxValue)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int splits = 0;
        foreach (var c in SwiftText.Characters(s))
        {
            if (splits < maxSplits && isSeparator(c))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); splits++; }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    // Foundation's sets, listed on macOS 27: Zs, U+200B ZERO WIDTH SPACE (no longer Zs in Unicode, still in Foundation's set) and tab;
    // whitespacesAndNewlines adds U+000A-U+000D, U+0085, U+2028 and U+2029.
    /// <summary>Foundation <c>CharacterSet.whitespaces</c> membership of a scalar.</summary>
    public static bool IsWhitespace(Rune r) => r.Value is 0x9 or 0x200B || Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator;

    /// <summary>Foundation <c>CharacterSet.whitespacesAndNewlines</c> membership of a scalar.</summary>
    public static bool IsWhitespaceOrNewline(Rune r) => IsWhitespace(r) || r.Value is 0x85 or 0x2028 or 0x2029 || (r.Value is >= 0xA and <= 0xD);

    /// <summary>Foundation <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>. Not <c>SwiftText.TrimWhitespaceAndNewlines</c>, which misses U+200B.</summary>
    public static string TrimWhitespaceAndNewlines(string s) => Trim(s, IsWhitespaceOrNewline);

    /// <summary>Foundation <c>trimmingCharacters(in:)</c> for a scalar predicate.</summary>
    public static string Trim(string s, Func<Rune, bool> trim)
    {
        var runes = s.EnumerateRunes().ToList();
        int a = 0, b = runes.Count;
        while (a < b && trim(runes[a])) a++;
        while (b > a && trim(runes[b - 1])) b--;
        return string.Concat(runes.Skip(a).Take(b - a).Select(r => r.ToString()));
    }

    /// <summary>Foundation <c>trimmingCharacters(in: .whitespaces)</c>: not newlines.</summary>
    public static string TrimWhitespaces(string s) => Trim(s, IsWhitespace);

    /// <summary>Swift <c>Int(String)</c>: optional sign and ASCII digits, 64-bit, nothing else.</summary>
    public static long? SwiftInt(string s)
    {
        if (s.Length == 0) return null;
        int i = s[0] is '+' or '-' ? 1 : 0;
        if (i == s.Length) return null;
        for (int k = i; k < s.Length; k++) if (s[k] is < '0' or > '9') return null;
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
