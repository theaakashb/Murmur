// Port of app/Learning.swift.
using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Murmur.Core;

// Correction ledger: provisional evidence from attributed post-insertion edits, a conservative
// candidate filter, and promotion of consistent mappings into learned dictionary entries.
// Stores only short source/target strings, never surrounding document text.

/// <summary>One attributed replacement from the observation layer (<c>DictationObservation</c>).</summary>
public sealed record ObservedCorrection
{
    readonly DateTime observedAt;

    [JsonPropertyName("observedAt"), JsonConverter(typeof(SwiftDateConverter))]
    public DateTime ObservedAt { get => observedAt; init => observedAt = SwiftDate.Normalize(value); }
    [JsonPropertyName("sessionID"), JsonConverter(typeof(UuidConverter))] public Guid SessionId { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("target")] public string Target { get; init; } = "";
}

[JsonConverter(typeof(CamelCaseEnumConverter<CandidateRejection>))]
public enum CandidateRejection
{
    Empty, Identical, CaseOnly, PunctuationOnly, UnsupportedCharacters, TooManyWords,
    Deletion, Insertion, Negation, Number, Antonym, ContextualWord, TooShort, Inflection, OrdinaryWords, NotRespelling,
}

public abstract record CandidateVerdict
{
    CandidateVerdict() { }

    /// <summary>The reduced replacement (shared leading/trailing words removed), original casing kept.</summary>
    public sealed record Accepted(string Source, string Target) : CandidateVerdict;
    public sealed record Rejected(CandidateRejection Reason) : CandidateVerdict;
}

/// <summary>
/// A replacement is a learning candidate only when the target is a plausible respelling of the
/// source, i.e. the kind of error a transcriber makes on a name or term, not a change in meaning
/// (see app/Learning.swift for the full rules): structure (1...3 words of letters/digits after shared context
/// is removed), meaning guards (negation, numbers, antonyms, contextual words, inflections, and with a lexicon
/// the ordinary-words guard), respelling similarity, and the known-term waiver. When unsure, skip.
/// </summary>
public static class CorrectionFilter
{
    sealed class Data
    {
        public int MaxWords { get; set; }
        public int MinLetters { get; set; }
        public int MinLettersWithLexicon { get; set; }
        public double DirectSimilarity { get; set; }
        public double PhoneticSimilarity { get; set; }
        public string[] Negations { get; set; } = [];
        public string[] NegatingPrefixes { get; set; } = [];
        public string[] NumberWords { get; set; } = [];
        public string[] ContextualWords { get; set; } = [];
        public string[][] Antonyms { get; set; } = [];
        public string[] InflectionSuffixes { get; set; } = [];
        public string LedgerFileName { get; set; } = "";
    }

    static readonly Data Shared = SharedData.Load<Data>("learning");
    public static readonly int MaxWords = Shared.MaxWords;
    public static readonly int MinLetters = Shared.MinLetters;
    /// <summary>With a lexicon, short names and acronyms (Jon -> John, sequel -> SQL) are allowed.</summary>
    public static readonly int MinLettersWithLexicon = Shared.MinLettersWithLexicon;
    public static readonly double DirectSimilarity = Shared.DirectSimilarity;
    public static readonly double PhoneticSimilarity = Shared.PhoneticSimilarity;

    public static readonly FrozenSet<string> Negations = Shared.Negations.ToFrozenSet();
    public static readonly IReadOnlyList<string> NegatingPrefixes = Shared.NegatingPrefixes;
    public static readonly FrozenSet<string> NumberWords = Shared.NumberWords.ToFrozenSet();
    public static readonly IReadOnlyList<(string, string)> Antonyms = Shared.Antonyms.Select(p => (p[0], p[1])).ToArray();
    static readonly FrozenSet<string> AntonymSet = Antonyms.SelectMany(p => new[] { $"{p.Item1}|{p.Item2}", $"{p.Item2}|{p.Item1}" }).ToFrozenSet();
    /// <summary>Context-dependent words: never learned as a global substitution.</summary>
    public static readonly FrozenSet<string> ContextualWords = Shared.ContextualWords.ToFrozenSet();
    static readonly IReadOnlyList<string> InflectionSuffixes = Shared.InflectionSuffixes;
    public static readonly string LedgerFileName = Shared.LedgerFileName;

    static CandidateVerdict Reject(CandidateRejection reason) => new CandidateVerdict.Rejected(reason);

    /// <param name="isCommonWord">Receives one folded word and returns true for an ordinary dictionary word.</param>
    public static CandidateVerdict Evaluate(string source, string target, IReadOnlySet<string>? manualPreferred = null, Func<string, bool>? isCommonWord = null)
    {
        manualPreferred ??= FrozenSet<string>.Empty;
        var s = TermText.CollapseWhitespace(source);
        var t = TermText.CollapseWhitespace(target);
        if (s.Length == 0 && t.Length == 0) return Reject(CandidateRejection.Empty);
        if (t.Length == 0) return Reject(CandidateRejection.Deletion);
        if (s.Length == 0) return Reject(CandidateRejection.Insertion);
        if (s == t) return Reject(CandidateRejection.Identical);
        // Case (jira -> Jira), spacing (git hub -> GitHub) and short-word (Jon -> John) fixes are
        // learned only when the lexicon can tell names and terms from ordinary words.
        bool caseOnly = TermText.Fold(s) == TermText.Fold(t);
        if (caseOnly && isCommonWord is null) return Reject(CandidateRejection.CaseOnly);
        bool spacing = !caseOnly && Letters(TermText.Fold(s)) == Letters(TermText.Fold(t));
        if (spacing && (isCommonWord is null || TermText.Tokens(s).Count == TermText.Tokens(t).Count)) return Reject(CandidateRejection.PunctuationOnly);

        var sw = TermText.Tokens(s); var tw = TermText.Tokens(t);
        if (sw.Count == 0) return Reject(CandidateRejection.Insertion);
        if (tw.Count == 0) return Reject(CandidateRejection.Deletion);
        if (!WordsOnly(s, sw) || !WordsOnly(t, tw)) return Reject(CandidateRejection.UnsupportedCharacters);

        // Shared surrounding words are context, not part of the replacement.
        bool Same(TermText.Token a, TermText.Token b) => caseOnly ? a.Text == b.Text : a.Folded == b.Folded;
        while (sw.Count > 0 && tw.Count > 0 && Same(sw[0], tw[0])) { sw.RemoveAt(0); tw.RemoveAt(0); }
        while (sw.Count > 0 && tw.Count > 0 && Same(sw[^1], tw[^1])) { sw.RemoveAt(sw.Count - 1); tw.RemoveAt(tw.Count - 1); }
        if (tw.Count == 0) return Reject(CandidateRejection.Deletion);
        if (sw.Count == 0) return Reject(CandidateRejection.Insertion);
        if (sw.Count > MaxWords || tw.Count > MaxWords) return Reject(CandidateRejection.TooManyWords);

        var sf = sw.Select(w => w.Folded).ToList(); var tf = tw.Select(w => w.Folded).ToList();
        bool spacingFix = Letters(TermText.Fold(source)) == Letters(TermText.Fold(target)) && TermText.Tokens(source).Count != TermText.Tokens(target).Count;
        var separated = sf.Count == 1 ? tf : sf;
        var joined = string.Concat(sf.Count == 1 ? sf : tf);
        bool joinedAcronym = tw.All(w => SwiftText.Count(w.Text) >= 2 && SwiftText.Characters(w.Text).All(c => !SwiftText.IsLetter(c) || SwiftText.IsUppercase(c)));
        var separatedTokens = sf.Count == 1 ? tw : sw;
        bool separatedWordsAreTerms = joinedAcronym || separatedTokens.All(token =>
            SwiftText.Count(token.Text) >= 2 ||
            (SwiftText.Count(token.Text) == 1 && SwiftText.IsUppercase(SwiftText.Characters(token.Text)[0]) && !ContextualWords.Contains(token.Folded)));
        if (IsSubsequence(tf, sf)) return Reject(CandidateRejection.Deletion);
        if (IsSubsequence(sf, tf)) return Reject(CandidateRejection.Insertion);
        if (NegationFlip(sf, tf)) return Reject(CandidateRejection.Negation);
        if (sf.Concat(tf).Any(w => SwiftText.Characters(w).Any(SwiftText.IsNumber) || NumberWords.Contains(w))) return Reject(CandidateRejection.Number);
        foreach (var a in sf) foreach (var b in tf) if (AntonymSet.Contains($"{a}|{b}")) return Reject(CandidateRejection.Antonym);
        // Spelled-out letters ("a p i" -> API) are not the function words they look like.
        // A joined/split spelling can contain an ordinary word such as "boy". Allow that
        // word only for lexicon-verified spacing fixes; other multiword respellings keep
        // the contextual-word guard because their meaning may depend on the sentence.
        if ((spacingFix ? separated : sf.Concat(tf).ToList()).Any(w => ContextualWords.Contains(w)))
        {
            bool verifiedSpacing = spacingFix && isCommonWord is { } common0 && separatedWordsAreTerms && (!common0(joined) || joinedAcronym);
            if (!verifiedSpacing) return Reject(CandidateRejection.ContextualWord);
        }

        var sLetters = Letters(string.Concat(sf)); var tLetters = Letters(string.Concat(tf));
        int floor = isCommonWord is null ? MinLetters : MinLettersWithLexicon;
        if (SwiftText.Count(sLetters) < floor || SwiftText.Count(tLetters) < floor) return Reject(CandidateRejection.TooShort);
        if (!caseOnly && sf.Count == 1 && tf.Count == 1 && InflectionOnly(sf[0], tf[0])) return Reject(CandidateRejection.Inflection);

        var reducedSource = string.Join(" ", sw.Select(w => w.Text)); var reducedTarget = string.Join(" ", tw.Select(w => w.Text));
        // A shared possessive ending is not part of the respelling ("Buzari's" -> "Buzzati's").
        if (TermText.HasSuffix(TermText.Fold(reducedSource), "'s") && TermText.HasSuffix(TermText.Fold(reducedTarget), "'s"))
        {
            reducedSource = TermText.DropLast(reducedSource, 2); reducedTarget = TermText.DropLast(reducedTarget, 2);
        }
        CandidateVerdict reduced = new CandidateVerdict.Accepted(reducedSource, reducedTarget);
        // An all-capitals target (API, SQL, JSON) is a deliberate acronym, not an ordinary word.
        bool acronym = joinedAcronym;
        if (isCommonWord is { } common1 && spacingFix)
        {
            if (!(separated.Count > 1 && separatedWordsAreTerms && (!common1(joined) || acronym))) return Reject(CandidateRejection.OrdinaryWords);
        }
        else if (isCommonWord is { } common2 && !acronym && sf.Concat(tf).All(common2))
        {
            return Reject(CandidateRejection.OrdinaryWords);
        }
        // An ordinary word extended into a coined term ("Cloud" -> "clouddev") is a one-off context fix, and a
        // manual preferred spelling does not make an ordinary source word a universal replacement.
        bool ordinarySource = isCommonWord is { } common3 && !acronym && !spacingFix && sf.All(common3);
        if (ordinarySource && EmbedsOrdinary(string.Concat(sf), string.Concat(tf))) return Reject(CandidateRejection.OrdinaryWords);
        if (!ordinarySource && manualPreferred.Contains(string.Join(" ", tf))) return reduced;
        double similarity = CleanupText.Similarity(sLetters, tLetters);
        if (similarity >= DirectSimilarity) return reduced;
        if (similarity >= PhoneticSimilarity && PhoneticKey(sLetters) == PhoneticKey(tLetters)) return reduced;
        // With a lexicon, one unfamiliar word retyped as another is a garbled name or product even
        // when its consonants shift ("ChadJuT" -> ChatGPT, "Buzari" -> Buzzati, History October 2026).
        // Folded, so a mixed-case garble the spell checker waves through still counts as unfamiliar.
        if (isCommonWord is { } common4 && sf.Count == 1 && tf.Count == 1 && SwiftText.Count(sLetters) >= 5 && similarity >= PhoneticSimilarity
            && !common4(sf[0]) && !common4(tf[0])) return reduced;
        return Reject(CandidateRejection.NotRespelling);
    }

    /// <summary>True when a folded target merely extends (or is a fragment of) an ordinary source ("cloud" -> "clouddev").
    /// Shared by learning and <c>DictionaryStore.Apply</c>, so a pair is learnable exactly when it would apply.</summary>
    public static bool EmbedsOrdinary(string source, string target)
    {
        var s = Letters(source); var t = Letters(target);
        // Foundation's contains(_:) is false for an empty needle, unlike string.Contains.
        return s != t && ((s.Length > 0 && t.Contains(s, StringComparison.Ordinal)) || (t.Length > 0 && s.Contains(t, StringComparison.Ordinal)));
    }

    public sealed record Change(string Source, IReadOnlyList<string> Targets) : IEquatable<Change>
    {
        public bool Equals(Change? other) => other is not null && Source == other.Source && Targets.SequenceEqual(other.Targets);
        public override int GetHashCode() => HashCode.Combine(Source, Targets.Count);
    }

    /// <summary>Swift <c>Result&lt;[Change], CandidateRejection&gt;</c>: either <see cref="Changes"/> or <see cref="Failure"/>.</summary>
    public readonly record struct ReplacementsResult(IReadOnlyList<Change>? Changes, CandidateRejection? Failure);

    static ReplacementsResult Failed(CandidateRejection reason) => new(null, reason);

    /// <summary>
    /// The replaced word runs from <paramref name="original"/> to <paramref name="current"/>, aligned by longest common
    /// subsequence of words (case-sensitive, so jira -> Jira is a change). Words added after the span, or anywhere with
    /// nothing replaced, are ignored, so a fix followed by more typing still counts, and two fixes in one dictation are two changes.
    /// Fails for an edit that deletes words, or replaces more than half of a long span (a rewrite).
    /// </summary>
    public static ReplacementsResult Replacements(string original, string current)
    {
        var a = TermText.Tokens(original); var b = TermText.Tokens(current);
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                dp[i, j] = a[i].Text == b[j].Text ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var changes = new List<Change>();
        int replaced = 0, ii = 0, jj = 0;
        while (ii < a.Count || jj < b.Count)
        {
            if (ii < a.Count && jj < b.Count && a[ii].Text == b[jj].Text) { ii++; jj++; continue; }
            int i0 = ii, j0 = jj;
            while ((ii < a.Count || jj < b.Count) && !(ii < a.Count && jj < b.Count && a[ii].Text == b[jj].Text))
            {
                if (jj < b.Count && (ii == a.Count || dp[ii, jj + 1] >= dp[ii + 1, jj])) jj++; else ii++;
            }
            if (ii == i0) continue;                                // words added: not a correction
            if (jj == j0) return Failed(CandidateRejection.Deletion);
            replaced += ii - i0;
            var words = b.GetRange(j0, jj - j0).Select(w => w.Text).ToList();
            var whole = string.Join(" ", words);
            // A run that grew past the end of the span may hold the fix plus typing after it.
            var targets = ii == a.Count && words.Count > ii - i0
                ? Enumerable.Range(1, words.Count - 1).Take(MaxWords).Select(n => string.Join(" ", words.Take(n))).Append(whole).ToList()
                : [whole];
            changes.Add(new Change(string.Join(" ", a.GetRange(i0, ii - i0).Select(w => w.Text)), targets));
        }
        // shortcut: the half-the-span rewrite cut-off is a guess; tune with real correction logs.
        if (replaced > Math.Max(MaxWords, a.Count / 2)) return Failed(CandidateRejection.TooManyWords);
        if (changes.Count > 0) return new(changes, null);
        if (b.Count > a.Count) return Failed(CandidateRejection.Insertion);
        // Same words: name the case, punctuation or spacing change.
        if (Evaluate(original, current) is CandidateVerdict.Rejected r) return Failed(r.Reason);
        return Failed(CandidateRejection.Identical);
    }

    /// <summary>
    /// Of <paramref name="candidates"/> (what a span might now read, when the caret position is unknown), the one
    /// reading whose every replaced run is an accepted respelling of the original, preferring the
    /// reading that changes the fewest words. Readings that imply the same fix count as one.
    /// Null when none, or several equally small but different fixes, qualify.
    /// </summary>
    public static string? PlausibleEdit(string original, IEnumerable<string> candidates, Func<string, bool>? isCommonWord = null)
    {
        static bool Accepted(IReadOnlyList<Change> changes, Func<string, bool>? lexicon) =>
            changes.All(change => Evaluate(change.Source, change.Targets[^1], null, lexicon) is CandidateVerdict.Accepted);
        // The spell checker is slow, so it only sees readings that pass every other rule.
        var passing = new List<(int Cost, string Reading, IReadOnlyList<Change> Changes)>();
        var a = SwiftText.Characters(original);
        foreach (var candidate in candidates.Distinct())
        {
            if (candidate == original) continue;
            // Each guess differs only around its caret: diff just that stretch, widened to whole words.
            var b = SwiftText.Characters(candidate);
            int start = 0, tail = 0;
            while (start < Math.Min(a.Count, b.Count) && a[start] == b[start]) start++;
            while (tail < Math.Min(a.Count, b.Count) - start && a[a.Count - 1 - tail] == b[b.Count - 1 - tail]) tail++;
            while (start > 0 && (TermText.IsCore(a[start - 1]) || TermText.IsJoiner(a[start - 1]))) start--;
            while (tail > 0 && (TermText.IsCore(a[a.Count - tail]) || TermText.IsJoiner(a[a.Count - tail]))) tail--;
            var result = Replacements(TermText.Slice(a, start, a.Count - tail), TermText.Slice(b, start, b.Count - tail));
            // "Nothing is an ordinary word" accepts a superset of what the real lexicon does
            // (it only adds rejections), so case and spacing fixes survive to the real check.
            if (result.Changes is not { } changes || !Accepted(changes, isCommonWord is null ? null : _ => false)) continue;
            passing.Add((changes.Sum(c => Math.Max(TermText.Tokens(c.Source).Count, TermText.Tokens(c.Targets[^1]).Count)), candidate, changes));
        }
        (int Cost, IReadOnlyList<Change> Fix, string? Reading)? best = null;
        foreach (var item in passing.OrderBy(p => p.Cost).ThenBy(p => p.Reading, StringComparer.Ordinal))
        {
            if (best is { } b0 && item.Cost > b0.Cost) break;
            if (isCommonWord is not null && !Accepted(item.Changes, isCommonWord)) continue;
            if (best is null) best = (item.Cost, item.Changes, item.Reading);
            else if (!best.Value.Fix.SequenceEqual(item.Changes)) best = best.Value with { Reading = null };
        }
        return best?.Reading;
    }

    /// <summary>Lowercased letters and digits only.</summary>
    public static string Letters(string text) => SwiftText.Lowercased(string.Concat(SwiftText.Characters(text).Where(TermText.IsCore)));

    /// <summary>Soundex-style consonant classes over the whole word (no truncation), with c/k/q/s/z in one
    /// class so Acme/Akme and Kubernetes/Coopernetties share a key. Vowels only separate repeats;
    /// a leading vowel is marked "V".</summary>
    public static string PhoneticKey(string word)
    {
        var text = Letters(word).Replace("ph", "f", StringComparison.Ordinal);
        static char Code(string c) => c switch
        {
            "b" or "f" or "p" or "v" => '1',
            "c" or "g" or "j" or "k" or "q" or "s" or "x" or "z" => '2',
            "d" or "t" => '3',
            "l" => '4',
            "m" or "n" => '5',
            "r" => '6',
            _ => '0',
        };
        var key = new System.Text.StringBuilder();
        char last = ' ';
        var chars = SwiftText.Characters(text);
        for (int index = 0; index < chars.Count; index++)
        {
            char value = Code(chars[index]);
            if (index == 0 && value == '0') key.Append('V');
            if (value != last && value != '0') key.Append(value);
            last = value;
        }
        return key.ToString();
    }

    static bool WordsOnly(string text, List<TermText.Token> words) => string.Join(" ", words.Select(w => w.Folded)) == TermText.Fold(text);

    static bool IsSubsequence(List<string> needle, List<string> haystack)
    {
        if (needle.Count >= haystack.Count) return false;
        int i = 0;
        foreach (var word in haystack) if (i < needle.Count && word == needle[i]) i++;
        return i == needle.Count;
    }

    static bool NegationFlip(List<string> a, List<string> b)
    {
        static HashSet<string> Negated(List<string> words) => words.Where(w => Negations.Contains(w) || TermText.HasSuffix(w, "n't")).ToHashSet();
        if (!Negated(a).SetEquals(Negated(b))) return true;
        foreach (var x in a) foreach (var y in b) foreach (var prefix in NegatingPrefixes) if (y == prefix + x || x == prefix + y) return true;
        return false;
    }

    static bool InflectionOnly(string a, string b)
    {
        var ca = SwiftText.Characters(a); var cb = SwiftText.Characters(b);
        var (shortWord, longWord) = ca.Count <= cb.Count ? (a, b) : (b, a);
        var cs = SwiftText.Characters(shortWord); var cl = SwiftText.Characters(longWord);
        if (TermText.HasPrefix(longWord, shortWord) && InflectionSuffixes.Contains(TermText.Slice(cl, cs.Count, cl.Count))) return true;
        // y -> ies/ied (party/parties)
        if (TermText.HasSuffix(shortWord, "y") && TermText.HasPrefix(longWord, TermText.DropLast(shortWord, 1))
            && TermText.Slice(cl, cs.Count - 1, cl.Count) is "ies" or "ied") return true;
        // A vowel-only change in a short word is usually tense/number (ran/run), not a respelling.
        static bool Vowel(string c) => c is "a" or "e" or "i" or "o" or "u" or "y";
        if (ca.Count <= 5 && cb.Count <= 5 && ca.Count == cb.Count && ca.Where(c => !Vowel(c)).SequenceEqual(cb.Where(c => !Vowel(c)))) return true;
        return false;
    }
}

/// <summary>
/// Persists <c>ledger.json</c> in <see cref="Directory"/>. Lifecycle per dictation session:
/// <c>Record</c> (provisional, replaces that session's earlier evidence) while the observation window
/// is open, then <c>Settle</c> when it closes; <c>Revert</c> removes the session's evidence at any time
/// (and withdraws a promotion it supported). One settled fix is enough: it adds the target with the source as a variant,
/// so the source is replaced from the next dictation on, or adds the source as another variant of
/// the learned entry for that spelling. A conflicting settled target suppresses the source until <c>Reset</c>.
/// A learned entry the user deletes is tombstoned with its evidence cleared, and a tombstoned target is never learned again
/// from any source; <c>Reset</c> clears tombstones.
/// </summary>
public sealed class CorrectionLedger
{
    public static readonly string FileName = CorrectionFilter.LedgerFileName;

    public readonly record struct RecordOutcome(RecordKind Kind, CandidateRejection? Rejection = null)
    {
        public static RecordOutcome LearningDisabled => new(RecordKind.LearningDisabled);
        public static RecordOutcome SessionClosed => new(RecordKind.SessionClosed);
        public static RecordOutcome Provisional => new(RecordKind.Provisional);
        public static RecordOutcome Suppressed => new(RecordKind.Suppressed);
        public static RecordOutcome Tombstoned => new(RecordKind.Tombstoned);
        public static RecordOutcome Rejected(CandidateRejection reason) => new(RecordKind.Rejected, reason);
    }

    public enum RecordKind { LearningDisabled, SessionClosed, Provisional, Suppressed, Tombstoned, Rejected }

    /// <summary>Folded source and target, except that a case-only fix keeps the target's casing
    /// (jira -> Jira), since folding would make it identical to its source.</summary>
    public readonly record struct Pair([property: JsonPropertyName("source")] string Source, [property: JsonPropertyName("target")] string Target) : IComparable<Pair>
    {
        public static Pair Of(string source, string target)
        {
            var folded = TermText.Fold(source);
            return new(folded, TermText.Fold(target) == folded ? target : TermText.Fold(target));
        }

        /// <summary>The target as other keys see it (preferred spellings and tombstones compare folded).</summary>
        [JsonIgnore] public string Folded => TermText.Fold(Target);

        public int CompareTo(Pair other) => string.CompareOrdinal(Source, other.Source) is var c and not 0 ? c : string.CompareOrdinal(Target, other.Target);
    }

    sealed class EvidenceItem
    {
        [JsonPropertyName("correction")] public ObservedCorrection Correction { get; set; } = new();
        [JsonPropertyName("settled")] public bool Settled { get; set; }
        [JsonIgnore] public Pair Pair => Pair.Of(Correction.Source, Correction.Target);
    }

    [JsonConverter(typeof(CamelCaseEnumConverter<PromotionKind>))]
    enum PromotionKind { Hint, Variant }

    sealed class Promotion
    {
        [JsonPropertyName("entryID"), JsonConverter(typeof(UuidConverter))] public Guid EntryId { get; set; }
        /// <summary>Null: created the entry with <c>Pair.Source</c> as its variant (also every version-1 promotion written before tiers).
        /// Hint: created the entry with no variant (old first tier, read only; no longer written).
        /// Variant: added <c>Pair.Source</c> to an entry another promotion created.</summary>
        [JsonPropertyName("kind")] public PromotionKind? Kind { get; set; }
        [JsonPropertyName("pair")] public Pair Pair { get; set; }
        [JsonPropertyName("sessions")] public List<Guid> Sessions { get; set; } = [];
        [JsonIgnore] public bool Replaces => Kind != PromotionKind.Hint;
    }

    sealed class StateFile
    {
        [JsonPropertyName("evidence")] public List<EvidenceItem> Items { get; set; } = [];
        [JsonPropertyName("learningEnabled")] public bool LearningEnabled { get; set; } = true;
        [JsonPropertyName("promotions")] public List<Promotion> Promotions { get; set; } = [];
        [JsonPropertyName("suppressed")] public List<string> Suppressed { get; set; } = [];
        [JsonPropertyName("tombstones")] public List<Pair> Tombstones { get; set; } = [];
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
    }

    /// <summary>What learning will not touch, for Settings: a misheard form whose fixes conflicted or
    /// that a fix reversed (<c>Heard</c>), and a spelling the user deleted, undid or reversed (<c>Spelling</c>).</summary>
    public abstract record BlockedItem
    {
        BlockedItem() { }
        public sealed record Heard(string Source) : BlockedItem;
        public sealed record Spelling(string Target) : BlockedItem;
    }

    public string Directory { get; }
    public DictionaryStore Dictionary { get; }
    /// <summary>The lexicon for the ordinary-words guard (see <see cref="CorrectionFilter"/>): the dictionary store's, so it is injected once, where the store is built.</summary>
    public Func<string, bool>? IsCommonWord { get => Dictionary.IsCommonWord; set => Dictionary.IsCommonWord = value; }

    List<EvidenceItem> evidence = [];
    bool learningEnabled = true;
    HashSet<string> suppressed = [];
    HashSet<Pair> tombstones = [];
    List<Promotion> promotions = [];

    string StorePath => Path.Combine(Directory, FileName);

    public CorrectionLedger(string directory, DictionaryStore dictionary)
    {
        Directory = directory;
        Dictionary = dictionary;
        Reload();
    }

    public void Reload()
    {
        PrivateFiles.PrepareDirectory(Directory);
        var data = PrivateFiles.Read(StorePath);
        if (data is null) { Clear(); return; }
        var decoded = StoreJson.Decode<StateFile>(data);
        if (decoded.Version != 1) throw new StoreError("Unsupported ledger version");
        evidence = decoded.Items; learningEnabled = decoded.LearningEnabled; promotions = decoded.Promotions;
        suppressed = decoded.Suppressed.ToHashSet(); tombstones = decoded.Tombstones.ToHashSet();
    }

    void Clear()
    {
        evidence = []; learningEnabled = true; suppressed = []; tombstones = []; promotions = [];
    }

    void Save() => PrivateFiles.Write(StoreJson.Encode(new StateFile
    {
        Items = evidence, LearningEnabled = learningEnabled, Promotions = promotions,
        Suppressed = suppressed.Order(StringComparer.Ordinal).ToList(), Tombstones = tombstones.Order().ToList(),
    }), StorePath);

    public bool LearningEnabled => learningEnabled;
    public IReadOnlyList<ObservedCorrection> Evidence => evidence.Select(e => e.Correction).ToList();
    public IReadOnlySet<Guid> ProvisionalSessions => evidence.Where(e => !e.Settled).Select(e => e.Correction.SessionId).ToHashSet();
    public IReadOnlySet<string> SuppressedSources => suppressed.ToHashSet();
    public IReadOnlySet<Pair> Tombstones => tombstones.ToHashSet();

    public IReadOnlyList<BlockedItem> Blocked
    {
        get
        {
            var seen = new HashSet<string>();
            var spellings = tombstones.Select(t => t.Target).Order(StringComparer.Ordinal).Where(t => seen.Add(TermText.Fold(t))).ToList();
            return suppressed.Order(StringComparer.Ordinal).Select(s => (BlockedItem)new BlockedItem.Heard(s)).Concat(spellings.Select(s => new BlockedItem.Spelling(s))).ToList();
        }
    }

    /// <summary>Lets learning use a blocked form or spelling again (old evidence for it was already cleared).</summary>
    public void Unblock(BlockedItem item)
    {
        switch (item)
        {
            case BlockedItem.Heard h: suppressed.Remove(h.Source); break;
            case BlockedItem.Spelling s: tombstones = tombstones.Where(t => t.Folded != TermText.Fold(s.Target)).ToHashSet(); break;
        }
        Save();
    }

    /// <summary>Off records nothing and drops in-flight provisional evidence. Existing learned entries keep
    /// applying until the user deletes or resets them.</summary>
    public void SetLearningEnabled(bool enabled)
    {
        learningEnabled = enabled;
        if (!enabled) evidence.RemoveAll(e => !e.Settled);
        Save();
    }

    /// <summary>Feed the latest attributed replacement for a session while its window is open. A session
    /// that already settled early (stable text) is reconciled pair by pair: a settled pair
    /// missing from the new reading is withdrawn with whatever it promoted, a settled pair still
    /// present stays as it is (so one session never counts twice), and only new pairs are recorded.</summary>
    public RecordOutcome Record(ObservedCorrection observed)
    {
        if (!learningEnabled) return RecordOutcome.LearningDisabled;
        Reconcile();
        var session = observed.SessionId;
        var (fresh, outcome) = Candidates(observed);
        // A promotion consumes its evidence, so its session list also marks a settled pair.
        var settled = evidence.Where(e => e.Correction.SessionId == session && e.Settled).Select(e => e.Pair).ToHashSet();
        settled.UnionWith(promotions.Where(p => p.Sessions.Contains(session)).Select(p => p.Pair));
        var gone = settled.Except(fresh.Select(f => Pair.Of(f.Source, f.Target))).ToHashSet();
        // shortcut: withdrawing a promotion that several old (two-tier) sessions supported loses the
        // others' consumed evidence (as Revert always has); keep promotion evidence if that proves common.
        foreach (var promotion in promotions.Where(p => p.Sessions.Contains(session) && gone.Contains(p.Pair)).ToList()) Withdraw(promotion, tombstone: false);
        evidence.RemoveAll(e => e.Correction.SessionId == session && (!e.Settled || gone.Contains(e.Pair)));
        var added = fresh.Where(f => !settled.Contains(Pair.Of(f.Source, f.Target))).ToList();
        evidence.AddRange(added.Select(c => new EvidenceItem { Correction = c, Settled = false }));
        Save();
        return settled.Count > 0 && gone.Count == 0 && added.Count == 0 ? RecordOutcome.SessionClosed : outcome;
    }

    /// <summary>The filter's accepted, reduced replacements for one snapshot, and the outcome to report.</summary>
    (List<ObservedCorrection> Fresh, RecordOutcome Outcome) Candidates(ObservedCorrection observed)
    {
        var fresh = new List<ObservedCorrection>();
        RecordOutcome? outcome = null;
        var rejection = CandidateRejection.Identical;
        var result = CorrectionFilter.Replacements(observed.Source, observed.Target);
        if (result.Failure is { } failure) rejection = failure;
        else
        {
            foreach (var change in result.Changes!)
            {
                // A run that grew past the end of the span may hold the fix plus typing after it:
                // the shortest accepted reading wins.
                CandidateVerdict verdict = new CandidateVerdict.Rejected(CandidateRejection.NotRespelling);
                foreach (var target in change.Targets)
                {
                    verdict = CorrectionFilter.Evaluate(change.Source, target, ManualPreferredKeys(), IsCommonWord);
                    if (verdict is CandidateVerdict.Accepted) break;
                }
                switch (verdict)
                {
                    case CandidateVerdict.Rejected r:
                        if (outcome is null && rejection == CandidateRejection.Identical) rejection = r.Reason;
                        break;
                    case CandidateVerdict.Accepted a:
                        var pair = Pair.Of(a.Source, a.Target);
                        if (tombstones.Any(t => t.Folded == pair.Folded)) { outcome ??= RecordOutcome.Tombstoned; }
                        else if (suppressed.Contains(pair.Source)) { outcome ??= RecordOutcome.Suppressed; }
                        else
                        {
                            fresh.Add(new ObservedCorrection { SessionId = observed.SessionId, Source = a.Source, Target = a.Target, ObservedAt = observed.ObservedAt });
                            outcome = RecordOutcome.Provisional;
                        }
                        break;
                }
            }
        }
        return (fresh, outcome ?? RecordOutcome.Rejected(rejection));
    }

    /// <summary>The session's observation window closed, or its text has been stable long enough:
    /// commit its evidence and promote if consistent. Settling again without new evidence does nothing.
    /// Returns the entries that gained a replacement: new ones, and existing ones given a variant.</summary>
    public IReadOnlyList<DictionaryEntry> Settle(Guid sessionId)
    {
        Reconcile();
        if (!learningEnabled)
        {
            evidence.RemoveAll(e => e.Correction.SessionId == sessionId && !e.Settled);
            Save();
            return [];
        }
        var pairs = new List<Pair>();
        foreach (var e in evidence.Where(e => e.Correction.SessionId == sessionId && !e.Settled).ToList())
        {
            e.Settled = true;
            if (!pairs.Contains(e.Pair)) pairs.Add(e.Pair);
        }
        if (pairs.Count == 0) return [];
        var promoted = new List<DictionaryEntry>();

        foreach (var pair in pairs)
        {
            var reversed = promotions.FirstOrDefault(p => p.Pair != pair && p.Pair.Source == pair.Folded && p.Pair.Folded == pair.Source);
            if (reversed is not null)
            {
                // The user changed a learned replacement back: withdraw it and never re-learn it.
                Withdraw(reversed, tombstone: true);
                suppressed.Add(pair.Source);
                evidence.RemoveAll(e => e.Correction.SessionId == sessionId && e.Pair == pair);
                continue;
            }
            var settled = evidence.Where(e => e.Settled && e.Pair.Source == pair.Source).ToList();
            var targets = settled.Select(e => e.Pair.Target).Concat(promotions.Where(p => p.Pair.Source == pair.Source).Select(p => p.Pair.Target)).ToHashSet();
            if (targets.Count > 1)
            {
                suppressed.Add(pair.Source);
                evidence.RemoveAll(e => e.Pair.Source == pair.Source);
                foreach (var promotion in promotions.Where(p => p.Pair.Source == pair.Source).ToList()) Withdraw(promotion, tombstone: false);
            }
            else if (promotions.Any(p => p.Pair == pair && p.Replaces))
            {
                // Already replaced: a repeat fix confirms it and adds nothing.
                evidence.RemoveAll(e => e.Pair == pair);
            }
            else if (settled.Count > 0)
            {
                var latest = settled[0];
                foreach (var e in settled.Skip(1)) if (latest.Correction.ObservedAt < e.Correction.ObservedAt) latest = e; // the first of equal dates
                if (CorrectionFilter.Evaluate(latest.Correction.Source, latest.Correction.Target, ManualPreferredKeys(), IsCommonWord) is CandidateVerdict.Rejected)
                {
                    evidence.RemoveAll(e => e.Pair.Source == pair.Source);
                    continue;
                }
                var sessions = settled.Select(e => e.Correction.SessionId).Distinct().ToList();
                var existing = Dictionary.EntryForVariant(latest.Correction.Source);
                if (existing is not null)
                {
                    // A manual or other entry already owns this form; consume matching evidence only.
                    if (TermText.Fold(existing.Preferred) == pair.Folded) evidence.RemoveAll(e => e.Pair.Source == pair.Source);
                }
                else
                {
                    // One fix: the misheard form is replaced from now on, as another variant of
                    // the learned entry for this spelling when there is one (including an old hint).
                    var owner = Dictionary.Entries.FirstOrDefault(e => e.Source == EntrySource.Learned && TermText.Fold(e.Preferred) == pair.Folded);
                    if (owner is not null)
                    {
                        owner = owner with { Variants = [.. owner.Variants, latest.Correction.Source] };
                        Dictionary.Update(owner);
                        promotions.Add(new Promotion { EntryId = owner.Id, Pair = pair, Sessions = sessions, Kind = PromotionKind.Variant });
                        promoted.Add(owner);
                    }
                    else
                    {
                        var entry = Dictionary.Add(latest.Correction.Target, [latest.Correction.Source], EntrySource.Learned);
                        promotions.Add(new Promotion { EntryId = entry.Id, Pair = pair, Sessions = sessions });
                        promoted.Add(entry);
                    }
                    // Evidence is consumed; the promotion keeps only session IDs for revert.
                    evidence.RemoveAll(e => e.Pair.Source == pair.Source);
                }
            }
        }
        Save();
        return promoted;
    }

    /// <summary>The user corrected a dictation's final text in History. The edit is one more
    /// observation (a fresh session, settled at once) through the same filter, ledger, thresholds,
    /// tombstones and conflicts; it adds to automatic learning and never replaces it.
    /// Returns the entries that gained a replacement.</summary>
    public IReadOnlyList<DictionaryEntry> LearnFromEdit(string original, string edited, DateTime? at = null)
    {
        if (original == edited) return [];
        var session = Guid.NewGuid();
        Record(new ObservedCorrection { SessionId = session, Source = original, Target = edited, ObservedAt = at ?? DateTime.UtcNow });
        return Settle(session);
    }

    /// <summary>Undo/reversion: removes the session's evidence. If a promotion relied on this session, its
    /// learned entry is withdrawn (not tombstoned; fresh evidence may promote it again).</summary>
    public void Revert(Guid sessionId)
    {
        Reconcile();
        evidence.RemoveAll(e => e.Correction.SessionId == sessionId);
        foreach (var promotion in promotions.Where(p => p.Sessions.Contains(sessionId)).ToList()) Withdraw(promotion, tombstone: false);
        Save();
    }

    /// <summary>The notice's Undo: withdraws the latest promotion on the entry (its variant, or the entry it
    /// created) and tombstones it. Does nothing if that was already withdrawn or deleted.</summary>
    public void UndoLearned(Guid entryId)
    {
        Reconcile();
        var promotion = promotions.LastOrDefault(p => p.EntryId == entryId);
        if (promotion is null) return;
        Withdraw(promotion, tombstone: true);
        Save();
    }

    /// <summary>Delete a learned entry: it stops applying and is tombstoned so old evidence cannot
    /// re-promote it. Deleting through <c>DictionaryStore.Delete</c> directly has the same effect at
    /// the ledger's next operation (see <see cref="Reconcile"/>).</summary>
    public void DeleteLearned(Guid id)
    {
        var entry = Dictionary.Entries.FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.Source != EntrySource.Learned) throw new StoreError("Not a learned entry");
        Dictionary.Delete(id);
        Reconcile();
        // Learned entries without a ledger promotion (e.g. ledger file lost) still get tombstones.
        foreach (var variant in entry.Variants)
        {
            tombstones.Add(new Pair(TermText.Fold(variant), TermText.Fold(entry.Preferred)));
            evidence.RemoveAll(e => e.Pair.Source == TermText.Fold(variant));
        }
        Save();
    }

    /// <summary>Forget all evidence, suppressions and tombstones, and remove all learned entries.
    /// Manual entries and the learning toggle are kept.</summary>
    public void Reset()
    {
        var enabled = learningEnabled;
        Clear();
        learningEnabled = enabled;
        Dictionary.ResetLearned();
        Save();
    }

    /// <summary>A learned entry missing from the dictionary was deleted by the user: tombstone its mapping.</summary>
    public void Reconcile()
    {
        bool changed = false;
        foreach (var promotion in promotions.ToList())
        {
            if (Dictionary.Entries.Any(e => e.Id == promotion.EntryId)) continue;
            Forget(promotion, tombstone: true);
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>Undoes what a promotion added: its variant, or its whole entry (and every promotion on it).
    /// Throws when the dictionary cannot be saved: the promotion is then kept, so the word is never left applying with no record of why.</summary>
    void Withdraw(Promotion promotion, bool tombstone)
    {
        if (promotion.Kind == PromotionKind.Variant)
        {
            if (Dictionary.Entries.FirstOrDefault(e => e.Id == promotion.EntryId) is { } entry)
                Dictionary.Update(entry with { Variants = entry.Variants.Where(v => TermText.Fold(v) != promotion.Pair.Source).ToList() });
            Forget(promotion, tombstone);
        }
        else
        {
            if (Dictionary.Entries.Any(e => e.Id == promotion.EntryId)) Dictionary.Delete(promotion.EntryId);
            foreach (var other in promotions.Where(p => p.EntryId == promotion.EntryId).ToList()) Forget(other, tombstone);
        }
    }

    void Forget(Promotion promotion, bool tombstone)
    {
        promotions.RemoveAll(p => p.EntryId == promotion.EntryId && p.Pair == promotion.Pair);
        if (!tombstone) return;
        tombstones.Add(promotion.Pair);
        evidence.RemoveAll(e => e.Pair.Source == promotion.Pair.Source);
    }

    IReadOnlySet<string> ManualPreferredKeys() => Dictionary.Entries.Where(e => e.Enabled && e.Source == EntrySource.Manual)
        .Select(e => TermText.Tokens(e.Preferred)).Where(words => words.Count > 0).Select(words => string.Join(" ", words.Select(w => w.Folded))).ToHashSet();
}
