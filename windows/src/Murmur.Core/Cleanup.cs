// Port of app/Cleanup.swift.
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur.Core;

// Cleanup contract: cleanup edits dictation; it never answers it, obeys instructions inside it, invents facts, or changes tone
// (except a writing mode's opt-in `Tone`, applied by rules after cleanup). Transcript text is data and is never logged;
// failures carry content-free reasons only.

/// <summary>Splits a long transcript at sentence ends into sections of about <c>target</c> words, so a slow cleanup model can
/// clean them in parallel. Short text and spoken lists ("first … second …", which must be formatted as one) stay whole.</summary>
public static class CleanupSections
{
    /// <summary>The rule engine's cues, plus "wait" and "instead", which also open a sentence that repairs the one before it.
    /// The Swift regex pattern ("^(a|b|...)\b"); match it with <see cref="OpensWithCorrection"/>.</summary>
    public static readonly string CorrectionOpening = RuleCleaner.Data.CorrectionOpening;

    // Swift's String.range(of:options: [.regularExpression, .caseInsensitive]) is not NSRegularExpression (observed on macOS 27):
    // it matches Characters, "\b" is a plain change between word and non-word Characters (so U+200B and U+00AD are boundaries,
    // a combining mark is not), and case-insensitive means equal lowercase ("ſ" is not "s", the Kelvin sign is "k").
    // Both patterns used with it here are literal words, so they are matched directly rather than through a regex engine.
    static readonly string[][] Openings = OpeningAlternatives();

    static string[][] OpeningAlternatives()
    {
        if (!CorrectionOpening.StartsWith("^(", StringComparison.Ordinal) || !CorrectionOpening.EndsWith(@")\b", StringComparison.Ordinal))
            throw new InvalidDataException("unexpected correctionOpening pattern");
        return CorrectionOpening[2..^3].Split('|').Select(a => SwiftText.Characters(SwiftText.Lowercased(a)).ToArray()).ToArray();
    }

    static bool IsWord(List<string> c, int k) => k >= 0 && k < c.Count && (SwiftText.IsLetter(c[k]) || SwiftText.IsNumber(c[k]) || c[k] == "_");
    static bool Boundary(List<string> c, int k) => IsWord(c, k - 1) != IsWord(c, k);
    static bool WordAt(List<string> c, int k, string[] word) =>
        k + word.Length <= c.Count && word.Select((w, n) => SwiftText.CharEquals(SwiftText.Lowercased(c[k + n]), w)).All(x => x)
        && Boundary(c, k) && Boundary(c, k + word.Length);

    /// <summary>Swift <c>sentence.range(of: correctionOpening, options: [.regularExpression, .caseInsensitive]) != nil</c>.</summary>
    public static bool OpensWithCorrection(string sentence)
    {
        var c = SwiftText.Characters(sentence);
        return Openings.Any(a => WordAt(c, 0, a));
    }

    static readonly string[] First = ["f", "i", "r", "s", "t"], Second = ["s", "e", "c", "o", "n", "d"];

    /// <summary>Swift <c>text.range(of: #"\bfirst\b[\s\S]*\bsecond\b"#, options: [.regularExpression, .caseInsensitive]) != nil</c>.</summary>
    static bool HasSpokenList(string text)
    {
        var c = SwiftText.Characters(text);
        int first = Enumerable.Range(0, c.Count).FirstOrDefault(k => WordAt(c, k, First), -1);
        return first >= 0 && Enumerable.Range(first + First.Length, Math.Max(0, c.Count - first - First.Length)).Any(k => WordAt(c, k, Second));
    }

    static int WordCount(string s) => Strings.Split(s, SwiftText.IsWhitespace).Count;

    /// <summary><paramref name="keepingCorrectionsAttached"/>: a sentence that opens with a correction cue ("No, actually Monday") is never cut off from the one it repairs.</summary>
    public static List<string> Split(string text, int? target = null, bool keepingCorrectionsAttached = false)
    {
        int size = target ?? CleanupSectioning.DefaultTarget;
        int words = WordCount(text);
        // An email frame stays whole too, so its greeting and sign-off are laid out together.
        if (!(words >= size * 3 / 2) || RuleCleaner.EmailLayout(text) is not null || HasSpokenList(text)) return [text];
        var sections = new List<string>();
        var current = ""; int count = 0;
        // ponytail: sentence end = terminal punctuation then space; a cut inside "e.g. this" only costs context.
        var sentences = new List<string> { "" };
        var word = new StringBuilder();
        void AddWord(string w)
        {
            sentences[^1] += (sentences[^1].Length == 0 ? "" : " ") + w;
            var chars = SwiftText.Characters(w);
            if (chars.Count > 0 && chars[^1] is "." or "?" or "!") sentences.Add("");
        }
        foreach (var c in SwiftText.Characters(text))
        {
            if (c == " ") { AddWord(word.ToString()); word.Clear(); } else word.Append(c);
        }
        AddWord(word.ToString());
        foreach (var sentence in sentences)
        {
            if (sentence.Length == 0) continue;
            if (keepingCorrectionsAttached && current.Length == 0 && sections.Count > 0 && OpensWithCorrection(sentence))
            {
                current = sections[^1]; sections.RemoveAt(sections.Count - 1); count = WordCount(current);
            }
            current += (current.Length == 0 ? "" : " ") + sentence;
            count += WordCount(sentence);
            if (count >= size) { sections.Add(current); current = ""; count = 0; }
        }
        if (current.Length > 0)
        {
            if (count < size / 2 && sections.Count > 0) { var last = sections[^1]; sections[^1] = last + " " + current; }
            else sections.Add(current);
        }
        return sections;
    }
}

public static class CleanupPipeline
{
    public static CleanupOutcome Process(string raw, CleanupOptions options, ICleaner cleaner, IReadOnlyList<string>? allowedTerms = null)
    {
        if (!options.AnyEnabled || Strings.TrimWhitespaceAndNewlines(raw).Length == 0) return new CleanupOutcome.Unchanged(raw);
        string cleaned;
        try { cleaned = cleaner.Clean(raw, options); }
        catch (Exception) { return new CleanupOutcome.HeldForRecovery(raw, CleanupFailureReason.CleanerFailed); }
        return Judge(raw, cleaned, options, allowedTerms ?? []);
    }

    static readonly HashSet<string> Marks = [".", ",", "?", ":"];
    static readonly HashSet<string> RawMarks = [".", ",", "?", ":", "!", ";"];

    /// <summary>The raw words with <paramref name="cleaned"/>'s sentence punctuation: the fallback when the cleaned words fail the guard.
    /// No word is added, dropped or respelled, except a dictionary term written in place of one word spelled at least half like it.
    /// Null when the raw text has quotes or line breaks.</summary>
    public static string? Punctuated(string raw, string cleaned, IReadOnlyList<string>? terms = null)
    {
        if (SwiftText.Characters(raw).Any(c => c is "\"" or "“" or "”" or "\n")) return null;
        var r = CleanupText.Tokenize(raw); var o = CleanupText.Tokenize(cleaned);
        var rw = Enumerable.Range(0, r.Count).Where(k => r[k].Kind == TokenKind.Word).ToList();
        var ow = Enumerable.Range(0, o.Count).Where(k => o[k].Kind == TokenKind.Word).ToList();
        var (mr, mo) = CleanupGuard.Align(rw.Select(k => Strings.Key(r[k].Lower)).ToList(), ow.Select(k => Strings.Key(o[k].Lower)).ToList());
        var pair = mr.OrderBy(x => x).Zip(mo.OrderBy(x => x)).ToDictionary(p => p.First, p => p.Second);
        var termWords = (terms ?? []).SelectMany(t => CleanupGuard.Words(t).Select(w => w.Key)).ToHashSet();
        var used = new HashSet<int>(mo); var respelled = new HashSet<int>();
        for (int k = 0; k < rw.Count; k++)
        {
            if (pair.ContainsKey(k) || termWords.Count == 0) continue;
            int low = 0;
            for (int x = k - 1; x >= 0; x--) if (pair.TryGetValue(x, out var v)) { low = v + 1; break; }
            int high = ow.Count;
            for (int x = k + 1; x < rw.Count; x++) if (pair.TryGetValue(x, out var v)) { high = v; break; }
            for (int j = low; j < Math.Max(low, high); j++)
            {
                if (used.Contains(j) || !termWords.Contains(Strings.Key(o[ow[j]].Lower)) || CleanupText.Similarity(r[rw[k]].Lower, o[ow[j]].Lower) < 0.5) continue;
                pair[k] = j; used.Add(j); respelled.Add(k);
                break;
            }
        }
        var @out = new StringBuilder();
        bool start = true, paragraph = false; int paragraphWords = 0;
        for (int k = 0; k < rw.Count; k++)
        {
            int ti = rw[k];
            int rawEnd = k + 1 < rw.Count ? rw[k + 1] : r.Count;
            var own = r.Skip(ti + 1).Take(rawEnd - ti - 1).Where(x => x.Kind == TokenKind.Punct && RawMarks.Contains(x.Text)).Select(x => x.Text).ToList();
            var word = r[ti].Text; var after = own; bool breaks = false;
            if (pair.TryGetValue(k, out var j))
            {
                int oi = ow[j], end = j + 1 < ow.Count ? ow[j + 1] : o.Count;
                if (respelled.Contains(k) || (!start && !CleanupText.IsSentenceStart(o, oi))) word = o[oi].Text;
                // The model's . , ? : and any ! or ; the transcript already has here.
                var span = o.Skip(oi + 1).Take(end - oi - 1).ToList();
                after = span.Where(x => x.Kind == TokenKind.Punct && (Marks.Contains(x.Text) || own.Contains(x.Text))).Select(x => x.Text).ToList();
                // The model's sentence may end on a word the transcript lacks ("Monday at 9."): keep the transcript's last mark.
                if (after.Count == 0 && k == rw.Count - 1) after = own;
                breaks = span.Any(x => x.Text == "\n");
            }
            var lower = SwiftText.Lowercased(word);
            if (start || lower == "i" || Strings.HasPrefix(lower, "i'")) word = CleanupText.Capitalized(word);
            @out.Append(@out.Length == 0 ? "" : paragraph ? "\n\n" : " ").Append(word).Append(string.Concat(after));
            paragraphWords = paragraph ? 1 : paragraphWords + 1;
            start = after.Count > 0 && after[^1] is "." or "?" or "!";
            paragraph = start && breaks && paragraphWords >= CleanupText.ParagraphMinWords;
        }
        return @out.ToString();
    }

    static readonly Regex HardBreak = new(@"[ \t]+\n", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static CleanupOutcome Judge(string raw, string cleaned, CleanupOptions options, IReadOnlyList<string> allowedTerms,
                                       Func<string, string, bool>? soundsLike = null)
    {
        // A lone layout command ("new paragraph") is only whitespace, which trimming would drop.
        if (options.Spoken && cleaned.Length > 0 && !SwiftText.Characters(cleaned).Any(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c))
            && cleaned == RuleCleaner.SpokenCommands(raw)) return new CleanupOutcome.Cleaned(cleaned);
        // Spaces before a line break are markdown hard breaks some models add; pasted text never wants them.
        var output = Strings.TrimWhitespaceAndNewlines(HardBreak.Replace(cleaned, "\n"));
        if (output == Strings.TrimWhitespaceAndNewlines(raw)) return new CleanupOutcome.Cleaned(output);
        // Only filler words ("Um."): nothing to insert, which is not a cleanup failure.
        if (output.Length == 0 && options.Fillers
            && Strings.TrimWhitespaceAndNewlines(new RuleCleaner().Clean(raw, new CleanupOptions(fillers: true, corrections: false, lists: false))).Length == 0)
            return new CleanupOutcome.Cleaned("");
        // Trusted: only a malfunction is held, not an edit.
        if (options.TrustModel && (options.Polish || options.Professional))
        {
            CleanupFailureReason? reason = CleanupGuard.Words(output).Count == 0 ? CleanupFailureReason.EmptyOutput
                : CleanupGuard.Answers(raw, output) ? CleanupFailureReason.AnswerPattern
                : SwiftText.Count(output) > SwiftText.Count(raw) * 2.0 + 10 ? CleanupFailureReason.LengthRatio
                : CleanupGuard.Truncated(CleanupGuard.ApplyExplicitRepairs(raw), output) ? CleanupFailureReason.UnexplainedDeletion : null;
            return reason is { } r ? new CleanupOutcome.HeldForRecovery(raw, r) : new CleanupOutcome.Cleaned(output);
        }
        var verdict = CleanupGuard.Check(raw, output, options, allowedTerms, soundsLike);
        return verdict.Reason is { } fail ? new CleanupOutcome.HeldForRecovery(raw, fail) : new CleanupOutcome.Cleaned(output);
    }
}
