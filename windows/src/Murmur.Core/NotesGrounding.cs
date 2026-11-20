// Port of the grounding checks in app/MeetingNotes.swift.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Murmur.Core;

public static class NotesGrounding
{
    static MeetingNotesData Data => MeetingNotesData.Shared;
    static readonly Regex TokenSeparators = new(@"[^\p{L}\p{M}\p{N}']+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public enum Reason { NoCitation, UnknownLine, AddressedToNotes, NoSharedWord, Invented, PolarityChanged, UnknownOwner, NoCommitment, NoAgreement }
    public enum Kind { Point, Decision, Action }

    public sealed record Source(IReadOnlyDictionary<int, (string Text, string Speaker)> Lines, string MyNotes)
    {
        internal string WithoutLabels(string text) => Lines.Values.Select(line => line.Speaker).Distinct()
            .OrderByDescending(SwiftText.Count).Aggregate(text, (current, label) => MeetingSpeakers.Replace(label, " ", current));
    }

    public static Source From(IReadOnlyList<MeetingNotesPrompt.Numbered> lines, string myNotes) =>
        new(lines.ToDictionary(line => line.Number, line => (line.Line.VisibleText, line.Speaker)), myNotes);

    public static Reason? Check(Draft.Item item, Kind kind, Source source)
    {
        if (item.Lines.Count == 0) return Reason.NoCitation;
        if (item.Lines.Any(id => !source.Lines.ContainsKey(id))) return Reason.UnknownLine;
        var cited = item.Lines.Select(id => source.Lines[id]).Where(line => !AddressedToNotes(line.Text)).ToArray();
        if (cited.Length == 0) return Reason.AddressedToNotes;
        var citedText = string.Join('\n', cited.Select(line => line.Text));
        var theirs = ContentWords(citedText).ToHashSet();
        if (!ContentWords(item.Text).Any(word => theirs.Any(other => Related(word, other)))) return Reason.NoSharedWord;
        var said = citedText + "\n" + source.MyNotes;
        if (!Facts(source.WithoutLabels(string.Join(". ", item.Text, item.Owner ?? "", item.Due ?? "")), said)) return Reason.Invented;
        if (PolarityChanged(item.Text, citedText)) return Reason.PolarityChanged;
        if (kind == Kind.Decision && !Has(Data.Agreements, citedText)) return Reason.NoAgreement;
        if (kind == Kind.Action)
        {
            if (item.Owner is { } owner && !OwnerSupported(owner, cited)) return Reason.UnknownOwner;
            if (!Has(Data.Commitments, citedText)) return Reason.NoCommitment;
        }
        return null;
    }

    public static bool AddressedToNotes(string text) => Has(Data.Addressed, text);

    public static bool FreeText(string? text, Source source, bool title = false)
    {
        if (text is null) return false;
        var said = string.Join('\n', source.Lines.Values.Where(line => !AddressedToNotes(line.Text)).Select(line => line.Text)) + "\n" + source.MyNotes;
        var unlabelled = source.WithoutLabels(text);
        if (!title) return Facts(unlabelled, said);
        var words = ContentWords(said).Concat(Tokens(SwiftText.Lowercased(said))).ToArray();
        return Names(unlabelled).All(name => words.Any(word => Related(name, word))) && Facts(SwiftText.Lowercased(unlabelled), said);
    }

    public static string[] Tokens(string text) => TokenSeparators.Split(text.Replace('’', '\''))
        .Select(token => token.Trim('\'')).Where(token => token.Length > 0).ToArray();

    static string Bare(string word) => word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2] : word;

    static IEnumerable<string> ContentWords(string text) => Tokens(SwiftText.Lowercased(text)).Select(Bare)
        .Where(word => SwiftText.Count(word) >= 3 && !Data.Stopwords.Contains(word));

    static bool Related(string a, string b)
    {
        if (SwiftText.CharEquals(a, b)) return true;
        var x = SwiftText.Characters(a);
        var y = SwiftText.Characters(b);
        int count = Math.Min(x.Count, y.Count);
        if (count < 4) return false;
        return Enumerable.Range(0, Math.Min(count, 5)).All(i => SwiftText.CharEquals(x[i], y[i]));
    }

    static IEnumerable<List<(string Word, bool Negated)>> Clauses(string text)
    {
        foreach (var piece in SwiftText.Lowercased(text).Split(['.', ',', ';', ':', '!', '?', '(', ')', '\n']))
        {
            var clause = new List<(string Word, bool Negated)>();
            bool negated = false;
            foreach (var token in Tokens(piece))
            {
                if (CleanupText.IsNegation(token)) { negated = true; continue; }
                if (Data.ClauseBreaks.Contains(token))
                {
                    if (clause.Count > 0) yield return clause;
                    clause = [];
                    negated = false;
                    continue;
                }
                clause.AddRange(ContentWords(token).Select(word => (word, negated)));
            }
            if (clause.Count > 0) yield return clause;
        }
    }

    public static bool PolarityChanged(string item, string cited)
    {
        var mine = Tokens(SwiftText.Lowercased(item)).ToHashSet();
        var theirs = Tokens(SwiftText.Lowercased(cited)).ToHashSet();
        bool Opposes(HashSet<string> a, HashSet<string> b) =>
            mine.Overlaps(a) && !mine.Overlaps(b) && theirs.Overlaps(b) && !theirs.Overlaps(a);
        if (Data.Opposites.Any(pair => Opposes(pair[0], pair[1]) || Opposes(pair[1], pair[0]))) return true;

        static bool Reverses(List<(string Word, bool Negated)>[] one, List<(string Word, bool Negated)>[] other)
        {
            var words = other.SelectMany(clause => clause).ToArray();
            return one.Any(clause => clause.Count >= 2 && clause.Any(word => word.Negated) && clause.All(word =>
            {
                var uses = words.Where(use => Related(word.Word, use.Word)).ToArray();
                return uses.Length > 0 && (!word.Negated || uses.All(use => !use.Negated));
            }));
        }
        var ours = Clauses(item).ToArray();
        var said = Clauses(cited).ToArray();
        return Reverses(ours, said) || Reverses(said, ours);
    }

    public static HashSet<string> Numbers(string text)
    {
        var found = new HashSet<string>();
        var current = new StringBuilder();
        bool lastNumber = false;
        foreach (var c in SwiftText.Characters(text.Replace(",", "", StringComparison.Ordinal)).Append(" "))
        {
            bool number = SwiftText.IsNumber(c);
            if (number || c == "." && lastNumber) current.Append(c);
            else if (current.Length > 0)
            {
                string digits = current.ToString().TrimEnd('.');
                if (!digits.Contains('.'))
                    found.Add(long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long whole)
                        ? whole.ToString(CultureInfo.InvariantCulture) : "0");
                else if (double.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double fraction))
                    found.Add(fraction.ToString("0.0################", CultureInfo.InvariantCulture));
                else found.Add("0.0");
                current.Clear();
            }
            lastNumber = number;
        }
        long total = 0, part = 0;
        bool inRun = false;
        void Flush()
        {
            if (inRun) found.Add((total + part).ToString(CultureInfo.InvariantCulture));
            total = part = 0;
            inRun = false;
        }
        var words = SwiftText.Lowercased(text).Split(['.', ',', ';', ':', '!', '?', '\n']);
        foreach (var sentence in words)
        {
            foreach (var word in Tokens(sentence))
            {
                if (Data.Units.TryGetValue(word, out int value)) part += value;
                else if (Data.Scales.TryGetValue(word, out int scale))
                {
                    if (scale == 100) part = Math.Max(part, 1) * 100;
                    else { total += Math.Max(part, 1) * scale; part = 0; }
                }
                else { if (word != "and" || !inRun) Flush(); continue; }
                inRun = true;
            }
            Flush();
        }
        return found;
    }

    public static HashSet<string> Names(string text)
    {
        var found = new HashSet<string>();
        foreach (var sentence in text.Split(['.', '!', '?', ':', ';', '\n']))
        {
            var words = Tokens(sentence);
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                var first = StringInfo.GetNextTextElement(word);
                if (!SwiftText.IsUppercase(first) || !SwiftText.IsLetter(first)) continue;
                var lower = SwiftText.Lowercased(word);
                var bare = Bare(lower);
                if (Data.NotNames.Contains(bare) || Data.DateWords.Contains(bare) || Data.Months.Contains(bare)) continue;
                if (i == 0 && !lower.EndsWith("'s", StringComparison.Ordinal) &&
                    (i + 1 == words.Length || !Data.SubjectCues.Contains(SwiftText.Lowercased(words[i + 1])))) continue;
                found.Add(bare);
            }
        }
        return found;
    }

    public static HashSet<string> Dates(string text) => Tokens(text).Where(word =>
    {
        var lower = SwiftText.Lowercased(word);
        return Data.DateWords.Contains(lower) || Data.Months.Contains(lower) &&
            (lower is not ("may" or "march") || SwiftText.IsUppercase(StringInfo.GetNextTextElement(word)));
    }).Select(SwiftText.Lowercased).ToHashSet();

    static bool Facts(string text, string said)
    {
        var words = Tokens(SwiftText.Lowercased(said)).Select(Bare).ToHashSet();
        var dates = Dates(said);
        dates.UnionWith(words.Where(word => Data.DateWords.Contains(word) || Data.Months.Contains(word)));
        return Names(text).IsSubsetOf(words) && Numbers(text).IsSubsetOf(Numbers(said)) && Dates(text).IsSubsetOf(dates);
    }

    static bool OwnerSupported(string owner, IReadOnlyList<(string Text, string Speaker)> cited)
    {
        var lower = SwiftText.TrimWhitespaceAndNewlines(SwiftText.Lowercased(owner));
        if (lower is "me" or "i" or "myself") return cited.Any(line => line.Speaker.Equals("me", StringComparison.OrdinalIgnoreCase));
        if (cited.Any(line => line.Speaker.Equals(lower, StringComparison.OrdinalIgnoreCase))) return true;
        var words = cited.SelectMany(line => Tokens(SwiftText.Lowercased(line.Text))).ToHashSet();
        var parts = Tokens(lower).Where(word => word != "and").ToArray();
        return parts.Length > 0 && (parts.All(word => words.Contains(word) || words.Contains(word + "'s")) ||
            cited.Any(line => parts.All(Tokens(SwiftText.Lowercased(line.Speaker)).ToHashSet().Contains)));
    }

    static bool Has(IEnumerable<string> phrases, string text)
    {
        var words = " " + string.Join(' ', Tokens(SwiftText.Lowercased(text).Replace('-', ' '))) + " ";
        return phrases.Any(phrase => words.Contains(" " + phrase.Replace('-', ' ') + " ", StringComparison.Ordinal));
    }
}
