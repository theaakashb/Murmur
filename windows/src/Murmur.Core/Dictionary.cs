// Port of app/Dictionary.swift.
using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur.Core;

// Custom dictionary: manual and learned term preferences, applied to dictated text.

/// <summary>A message-carrying error shared by the dictionary, cue, learning and private-file stores; the controller shows its text.</summary>
public sealed class StoreError(string message) : Exception(message);

/// <summary>Swift's default <c>Date</c> coding: seconds since 2001-01-01 UTC as a number. Dates are kept to whole microseconds so a file round trip is exact.</summary>
public static class SwiftDate
{
    static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static DateTime Normalize(DateTime value) => new(value.ToUniversalTime().Ticks / 10 * 10, DateTimeKind.Utc);
    public static double ToInterval(DateTime value) => (Normalize(value) - Reference).Ticks / 10 / 1e6;
    public static DateTime FromInterval(double seconds) => Normalize(Reference.AddTicks((long)Math.Round(seconds * 1e6) * 10));
}

sealed class SwiftDateConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => SwiftDate.FromInterval(reader.GetDouble());
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteNumberValue(SwiftDate.ToInterval(value));
}

/// <summary>Swift writes <c>UUID</c> as an uppercase string; reading accepts either case.</summary>
sealed class UuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Guid.Parse(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>Compact JSON for the private stores. Property declaration order is the (sorted) key order of Swift's <c>.sortedKeys</c>.</summary>
static class StoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Encode<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options));
    // Foundation's JSONDecoder accepts a UTF-8 byte order mark (Notepad and PowerShell write one); System.Text.Json does not.
    public static T Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data is [0xEF, 0xBB, 0xBF, ..] ? data.AsSpan(3) : data, Options) ?? throw new StoreError("The file is empty");
}

[JsonConverter(typeof(CamelCaseEnumConverter<EntrySource>))]
public enum EntrySource { Manual, Learned }

public sealed record DictionaryEntry : IEquatable<DictionaryEntry>
{
    readonly DateTime createdAt;

    [JsonPropertyName("createdAt"), JsonConverter(typeof(SwiftDateConverter))]
    public DateTime CreatedAt { get => createdAt; init => createdAt = SwiftDate.Normalize(value); }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("id"), JsonConverter(typeof(UuidConverter))] public Guid Id { get; init; }
    /// <summary>The spelling written into dictated text, exactly as stored (e.g. "Akme", "iPhone").</summary>
    [JsonPropertyName("preferred")] public string Preferred { get; init; } = "";
    [JsonPropertyName("source")] public EntrySource Source { get; init; }
    /// <summary>Spoken variants or commonly mistaken forms. Matched exactly (case-insensitive, whole words).</summary>
    [JsonPropertyName("variants")] public IReadOnlyList<string> Variants { get; init; } = [];

    public bool Equals(DictionaryEntry? other) => other is not null && Id == other.Id && Preferred == other.Preferred && Variants.SequenceEqual(other.Variants)
        && Source == other.Source && CreatedAt == other.CreatedAt && Enabled == other.Enabled;
    public override int GetHashCode() => HashCode.Combine(Id, Preferred, Source, CreatedAt, Enabled);
}

/// <summary>
/// Word tokenizer shared by dictionary matching and the learning filter.
/// A word is a run of letters/digits (including combining marks); an apostrophe or hyphen
/// joins two word characters ("don't", "e-mail"), so "Acme" never matches inside
/// "Acmeville", "Acme's" or "Acme-based". Positions index the Swift <c>Character</c> (grapheme) array.
/// </summary>
public static class TermText
{
    /// <param name="Start">Index into the Character array.</param>
    /// <param name="End">Exclusive.</param>
    public readonly record struct Token(int Start, int End, string Text)
    {
        public string Folded => Fold(Text);
    }

    public static string Fold(string text) => SwiftText.Lowercased(text.Replace("’", "'"));

    public static bool IsCore(string c) => SwiftText.IsLetter(c) || SwiftText.IsNumber(c);
    public static bool IsJoiner(string c) => c is "'" or "’" or "-";

    public static List<Token> Tokens(IReadOnlyList<string> chars)
    {
        var result = new List<Token>();
        int i = 0;
        while (i < chars.Count)
        {
            if (!IsCore(chars[i])) { i++; continue; }
            int j = i + 1;
            while (j < chars.Count)
            {
                if (IsCore(chars[j])) { j++; continue; }
                if (IsJoiner(chars[j]) && j + 1 < chars.Count && IsCore(chars[j + 1])) { j += 2; continue; }
                break;
            }
            result.Add(new Token(i, j, Slice(chars, i, j)));
            i = j;
        }
        return result;
    }

    public static List<Token> Tokens(string text) => Tokens(SwiftText.Characters(text));

    public static string CollapseWhitespace(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in SwiftText.Characters(text))
        {
            if (SwiftText.IsWhitespace(c)) { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return string.Join(" ", words);
    }

    /// <summary>Folded word key for a phrase, or null when the phrase holds anything besides words and single spaces
    /// (punctuation is not part of a match key).</summary>
    public static string? PhraseKey(string phrase)
    {
        var normalized = CollapseWhitespace(phrase);
        var words = Tokens(normalized);
        if (words.Count == 0) return null;
        var key = string.Join(" ", words.Select(w => w.Folded));
        return key == Fold(normalized) ? key : null;
    }

    // Swift String/Character helpers over the grapheme array (string.Length, StartsWith and Contains would count UTF-16 units).

    /// <summary>The characters <paramref name="from"/>..&lt;<paramref name="to"/> as a string.</summary>
    public static string Slice(IReadOnlyList<string> chars, int from, int to)
    {
        var sb = new StringBuilder();
        for (int k = from; k < to; k++) sb.Append(chars[k]);
        return sb.ToString();
    }

    public static bool HasPrefix(string s, string prefix)
    {
        var a = SwiftText.Characters(s); var b = SwiftText.Characters(prefix);
        return b.Count <= a.Count && b.Select((c, i) => a[i] == c).All(x => x);
    }

    public static bool HasSuffix(string s, string suffix)
    {
        var a = SwiftText.Characters(s); var b = SwiftText.Characters(suffix);
        return b.Count <= a.Count && b.Select((c, i) => a[a.Count - b.Count + i] == c).All(x => x);
    }

    /// <summary>Swift <c>removeLast(n)</c>.</summary>
    public static string DropLast(string s, int n) { var c = SwiftText.Characters(s); return Slice(c, 0, Math.Max(0, c.Count - n)); }

    /// <summary>Swift <c>Character.isNewline</c>: decided by the first scalar.</summary>
    public static bool IsNewline(string c) => c.Length > 0 && char.ConvertToUtf32(c, 0) is (>= 0xA and <= 0xD) or 0x85 or 0x2028 or 0x2029;

    /// <summary>Foundation <c>capitalized</c> for one word: the first character in upper case, the rest lower. (Titlecase digraphs and ß differ from upper case; not modeled.)</summary>
    public static string Capitalized(string word)
    {
        var c = SwiftText.Characters(word);
        return c.Count == 0 ? word : SwiftText.Uppercased(c[0]) + SwiftText.Lowercased(Slice(c, 1, c.Count));
    }
}

/// <summary>
/// Persists entries as <c>dictionary.json</c> in <see cref="Directory"/>. Every mutation writes through.
///
/// Case policy for <see cref="Apply"/>: the preferred spelling is inserted exactly as stored,
/// because it is the user's chosen form ("iPhone", "Akme", "kubectl"). The only exception:
/// when the matched words start a sentence (text start, or after . ! ? or a newline,
/// ignoring opening quotes/brackets), the original began with an uppercase letter, and the
/// preferred spelling contains no uppercase letters at all, its first letter is capitalized.
/// </summary>
public sealed class DictionaryStore
{
    sealed class Data
    {
        public string FileName { get; set; } = "";
        public int MaxVariantWords { get; set; }
        public int MaxLength { get; set; }
        public int MaxNearMatches { get; set; }
        public string[] CommandPhrases { get; set; } = [];
    }

    static readonly Data Shared = SharedData.Load<Data>("dictionary");
    public static readonly string FileName = Shared.FileName;
    public static readonly int MaxVariantWords = Shared.MaxVariantWords;
    public static readonly int MaxLength = Shared.MaxLength;
    public static readonly int MaxNearMatches = Shared.MaxNearMatches;

    /// <summary>Spoken commands a dictionary entry may target ("Kocia Mark" -> "question mark"): <c>RuleCleaner.MarkCommands</c> and
    /// <c>AtPhrases</c> joined with spaces (exported from the Mac, and checked against the C# commands in the tests).
    /// Dictionary application runs before cleanup, so these entries apply first.</summary>
    public static readonly FrozenSet<string> CommandPhrases = Shared.CommandPhrases.ToFrozenSet();

    /// <summary>An entry whose preferred form is a spoken command: applied ahead of other entries and kept
    /// out of every transcription boost and cleanup hint (it is not a word to bias toward).</summary>
    public static bool IsCommandTarget(DictionaryEntry entry) => TermText.PhraseKey(entry.Preferred) is { } key && CommandPhrases.Contains(key);

    sealed class FileFormat
    {
        [JsonPropertyName("entries")] public List<DictionaryEntry> Entries { get; set; } = [];
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
    }

    public string Directory { get; }
    List<DictionaryEntry> entries = [];
    string StorePath => Path.Combine(Directory, FileName);

    /// <summary>A snapshot of the stored entries, in order.</summary>
    public IReadOnlyList<DictionaryEntry> Entries => entries.ToArray();

    /// <summary>The lexicon for lexicon-gated respelling and learned-variant skipping; null disables both. Set once where the store is built (VOCABULARY.md).</summary>
    public Func<string, bool>? IsCommonWord { get; set; }

    public DictionaryStore(string directory, Func<string, bool>? isCommonWord = null)
    {
        Directory = directory;
        IsCommonWord = isCommonWord;
        Reload();
    }

    /// <summary>Re-reads from disk; an absent file means an empty dictionary (e.g. after Delete all data).</summary>
    public void Reload()
    {
        PrivateFiles.PrepareDirectory(Directory);
        var data = PrivateFiles.Read(StorePath);
        if (data is null) { entries = []; return; }
        var decoded = StoreJson.Decode<FileFormat>(data);
        if (decoded.Version != 1) throw new StoreError("Unsupported dictionary version");
        entries = decoded.Entries;
    }

    void Save() => PrivateFiles.Write(StoreJson.Encode(new FileFormat { Entries = entries }), StorePath);

    public DictionaryEntry Add(string preferred, IEnumerable<string> variants, EntrySource source = EntrySource.Manual, bool enabled = true, DateTime? now = null)
    {
        var entry = new DictionaryEntry { Id = Guid.NewGuid(), Preferred = preferred, Variants = variants.ToArray(), Source = source, CreatedAt = now ?? DateTime.UtcNow, Enabled = enabled };
        var validated = Validate(entry);
        entries.Add(validated);
        Save();
        return validated;
    }

    /// <summary>Replaces the entry with the same id. <c>Source</c> and <c>CreatedAt</c> are kept from the stored entry.</summary>
    public void Update(DictionaryEntry entry)
    {
        int index = entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) throw new StoreError("Dictionary entry not found");
        var stored = entries[index];
        var merged = new DictionaryEntry { Id = stored.Id, Preferred = entry.Preferred, Variants = entry.Variants, Source = stored.Source, CreatedAt = stored.CreatedAt, Enabled = entry.Enabled };
        entries[index] = Validate(merged);
        try { Save(); } catch { entries[index] = stored; throw; } // memory keeps matching the file
    }

    public void Delete(Guid id)
    {
        int index = entries.FindIndex(e => e.Id == id);
        if (index < 0) throw new StoreError("Dictionary entry not found");
        var removed = entries[index];
        entries.RemoveAt(index);
        try { Save(); } catch { entries.Insert(index, removed); throw; } // memory keeps matching the file
    }

    /// <summary>Removes every learned entry and keeps manual ones. Returns what was removed.
    /// To also forget evidence and tombstones, call <c>CorrectionLedger.Reset</c>, which calls this.</summary>
    public IReadOnlyList<DictionaryEntry> ResetLearned()
    {
        var removed = entries.Where(e => e.Source == EntrySource.Learned).ToList();
        entries.RemoveAll(e => e.Source == EntrySource.Learned);
        Save();
        return removed;
    }

    /// <summary>The enabled entry whose variant matches <paramref name="phrase"/> (folded, whole words), if any.</summary>
    public DictionaryEntry? EntryForVariant(string phrase)
    {
        var key = TermText.PhraseKey(phrase);
        if (key is null) return null;
        return VariantTable(null).GetValueOrDefault(key); // no lexicon: stored learned variants must stay findable
    }

    DictionaryEntry Validate(DictionaryEntry entry)
    {
        var preferred = TermText.CollapseWhitespace(entry.Preferred);
        if (preferred.Length == 0 || SwiftText.Count(preferred) > MaxLength || TermText.Tokens(preferred).Count == 0)
            throw new StoreError("Preferred spelling must contain a word");
        var seen = new HashSet<string>();
        var variants = new List<string>();
        foreach (var raw in entry.Variants)
        {
            var variant = TermText.CollapseWhitespace(raw);
            if (variant.Length == 0) continue;
            string? key = null;
            if (SwiftText.Count(variant) > MaxLength || (key = TermText.PhraseKey(variant)) is null)
                throw new StoreError("Variants may contain only words separated by spaces");
            if (key.Split(' ').Length > MaxVariantWords) throw new StoreError($"Variants are limited to {MaxVariantWords} words");
            if (seen.Add(key)) variants.Add(variant);
        }
        if (entry.Enabled)
        {
            foreach (var other in entries.Where(o => o.Enabled && o.Id != entry.Id))
                foreach (var variant in other.Variants.Where(v => seen.Contains(TermText.PhraseKey(v) ?? "")))
                    throw new StoreError($"“{variant}” already belongs to “{other.Preferred}”");
        }
        return new DictionaryEntry { Id = entry.Id, Preferred = preferred, Variants = variants, Source = entry.Source, CreatedAt = entry.CreatedAt, Enabled = entry.Enabled };
    }

    /// <summary>Folded variant key -> entry. Command targets win, then manual entries over learned ones, then older entries.</summary>
    Dictionary<string, DictionaryEntry> VariantTable(Func<string, bool>? isCommonWord, bool manualOnly = false)
    {
        var table = new Dictionary<string, DictionaryEntry>();
        var ordered = entries.Where(e => e.Enabled && (!manualOnly || (e.Source == EntrySource.Manual && !IsCommandTarget(e)))).OrderByDescending(IsCommandTarget).ThenBy(e => e.Source == EntrySource.Manual ? 0 : 1).ThenBy(e => e.CreatedAt);
        foreach (var entry in ordered)
            foreach (var variant in entry.Variants)
            {
                var key = TermText.PhraseKey(variant);
                if (key is null || table.ContainsKey(key)) continue;
                if (SkipLearnedVariant(variant, entry, isCommonWord)) continue;
                table[key] = entry;
            }
        return table;
    }

    /// <summary>Keep unsafe learned aliases stored, but omit them from both replacement and model biasing.</summary>
    public static bool SkipLearnedVariant(string variant, DictionaryEntry entry, Func<string, bool>? isCommonWord)
    {
        if (entry.Source != EntrySource.Learned || isCommonWord is not { } common) return false;
        var key = TermText.PhraseKey(variant);
        if (key is null || !key.Split(' ').All(common)) return false;
        var targetWords = TermText.Tokens(entry.Preferred);
        var acronym = targetWords.All(w => SwiftText.Count(w.Text) >= 2 && SwiftText.Characters(w.Text).All(c => !SwiftText.IsLetter(c) || SwiftText.IsUppercase(c)));
        return !acronym && CorrectionFilter.EmbedsOrdinary(key, TermText.Fold(entry.Preferred));
    }

    /// <summary>
    /// Replaces whole-word variant phrases with preferred spellings. Explicit variants match
    /// exactly after case folding. With a lexicon, an unfamiliar single word may also use one
    /// unique phonetic term; names allow only a single doubled letter, and ordinary words stay
    /// unchanged. Punctuation or a line break ends a phrase. At each position a command-target
    /// phrase wins, then the longest phrase; replaced text is never matched again, so
    /// another entry cannot rewrite words inside an inserted command ("question mark" stays).
    /// </summary>
    public string Apply(string text, Func<string, bool>? isCommonWordOverride = null, bool manualVariantsOnly = false)
    {
        var isCommonWord = manualVariantsOnly ? null : isCommonWordOverride ?? IsCommonWord;
        var table = VariantTable(isCommonWord, manualVariantsOnly);
        var terms = isCommonWord is null ? new List<DictionaryEntry>()
            : entries.Where(e => e.Enabled && !IsCommandTarget(e) && SwiftText.Count(e.Preferred) >= 6 && SwiftText.Characters(e.Preferred).All(SwiftText.IsLetter)).ToList();
        if (table.Count == 0 && terms.Count == 0) return text;
        int maxWords = table.Keys.Select(k => k.Split(' ').Length).DefaultIfEmpty(1).Max();
        var chars = SwiftText.Characters(text);
        var words = TermText.Tokens(chars);
        var output = new StringBuilder();
        int cursor = 0, i = 0;
        while (i < words.Count)
        {
            var spans = new List<(int Length, DictionaryEntry Entry)>();
            for (int length = Math.Min(maxWords, words.Count - i); length >= 1; length--)
            {
                var span = words.GetRange(i, length);
                if (!SpacedPhrase(span, chars)) continue;
                var key = string.Join(" ", span.Select(w => w.Folded));
                if (table.TryGetValue(key, out var hit)) { spans.Add((length, hit)); continue; }
                if (length != 1 || SwiftText.Count(key) < 5 || !SwiftText.Characters(key).All(SwiftText.IsLetter) || isCommonWord is not { } common || common(key)) continue;
                // Recognized names allow only one doubled letter, not broader phonetic swaps.
                bool unfamiliar = SwiftText.Count(key) >= 6 && !common(TermText.Capitalized(key));
                var exact = terms.Where(t => TermText.Fold(t.Preferred) == key).ToList();
                var candidates = exact.Count == 0 ? terms.Where(term =>
                    (unfamiliar || AddsDoubledLetter(key, TermText.Fold(term.Preferred))) &&
                    CorrectionFilter.PhoneticKey(key) == CorrectionFilter.PhoneticKey(term.Preferred) &&
                    CorrectionFilter.Evaluate(words[i].Text, term.Preferred, null, common) == new CandidateVerdict.Accepted(words[i].Text, term.Preferred)).ToList() : exact;
                if (candidates.Count == 1) spans.Add((length, candidates[0]));
            }
            if (spans.Count == 0) { i++; continue; }
            var (len, entry) = spans.FirstOrDefault(s => IsCommandTarget(s.Entry)) is { Entry: not null } command ? command : spans[0];
            int start = words[i].Start, end = words[i + len - 1].End;
            output.Append(TermText.Slice(chars, cursor, start));
            output.Append(IsCommandTarget(entry) ? entry.Preferred : Cased(entry.Preferred, TermText.Slice(chars, start, end), AtSentenceStart(start, chars)));
            cursor = end;
            i += len;
        }
        output.Append(TermText.Slice(chars, cursor, chars.Count));
        return output.ToString();
    }

    static bool AddsDoubledLetter(string source, string spelling)
    {
        var chars = SwiftText.Characters(spelling);
        if (chars.Count != SwiftText.Count(source) + 1) return false;
        for (int i = 1; i < chars.Count; i++)
        {
            if (chars[i] != chars[i - 1]) continue;
            var shorter = chars.ToList(); shorter.RemoveAt(i);
            if (TermText.Slice(shorter, 0, shorter.Count) == source) return true;
        }
        return false;
    }

    /// <summary>One helper-vocabulary term: the preferred spelling and its "heard as" aliases (null when it has none).</summary>
    public sealed record HelperTerm(string Text, IReadOnlyList<string>? Aliases);

    /// <summary>The helper's vocabulary: the same enabled, non-command-target entries as <see cref="VocabularyTerms"/>, each with its
    /// "heard as" aliases minus learned variants that are ordinary words.</summary>
    public IReadOnlyList<HelperTerm> HelperVocabulary => entries.Where(e => e.Enabled && !IsCommandTarget(e)).Select(entry =>
    {
        var variants = entry.Variants.Where(v => !SkipLearnedVariant(v, entry, IsCommonWord)).ToList();
        return new HelperTerm(entry.Preferred, variants.Count == 0 ? null : variants);
    }).ToList();

    /// <summary>Enabled preferred spellings for transcription biasing, manual entries first, commas removed
    /// (callers join with ", "). Unlike <see cref="RelevantEntries"/>, this is the whole enabled dictionary,
    /// because nothing can be filtered before a transcript exists.</summary>
    public IReadOnlyList<string> VocabularyTerms
    {
        get
        {
            var seen = new HashSet<string>();
            var enabled = entries.Where(e => e.Enabled && !IsCommandTarget(e)).ToList();
            var result = new List<string>();
            foreach (var entry in enabled.Where(e => e.Source == EntrySource.Manual).Concat(enabled.Where(e => e.Source == EntrySource.Learned)))
            {
                var term = TermText.CollapseWhitespace(entry.Preferred.Replace(",", " "));
                if (seen.Add(TermText.Fold(term))) result.Add(term);
            }
            return result;
        }
    }

    /// <summary>
    /// Entries a cloud stage may receive for this text: enabled entries whose preferred spelling
    /// or a variant occurs in the text as whole words, then up to <see cref="MaxNearMatches"/> entries whose
    /// preferred spelling sounds like a 1-3 word span of the text (the learning filter's
    /// similarity rules), so a misheard term can still be corrected by cleanup. Never the whole dictionary.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> RelevantEntries(string text)
    {
        var exact = ExactEntries(text).Where(e => !IsCommandTarget(e)).ToList();
        var exactIds = exact.Select(e => e.Id).ToHashSet();
        var candidates = entries.Where(e => e.Enabled && !exactIds.Contains(e.Id) && !IsCommandTarget(e)).ToList();
        if (candidates.Count == 0) return exact;
        var chars = SwiftText.Characters(text);
        var words = TermText.Tokens(chars);
        var spans = new HashSet<string>();
        for (int i = 0; i < words.Count; i++)
            for (int length = 1; length <= Math.Min(CorrectionFilter.MaxWords, words.Count - i); length++)
            {
                var span = words.GetRange(i, length);
                if (!SpacedPhrase(span, chars)) break;
                spans.Add(CorrectionFilter.Letters(string.Concat(span.Select(w => w.Text))));
            }
        var near = new List<DictionaryEntry>();
        foreach (var entry in candidates)
        {
            if (near.Count >= MaxNearMatches) continue;
            var target = CorrectionFilter.Letters(entry.Preferred);
            if (spans.Any(s => SoundsLike(s, target))) near.Add(entry);
        }
        return exact.Concat(near).ToList();
    }

    /// <summary>The learning filter's respelling test on letters only: at least 4 letters each, and either
    /// matching phonetic keys with similarity >= 0.5, or similarity >= 0.75 for terms of 6+ letters.</summary>
    public static bool SoundsLike(string heard, string term)
    {
        int h = SwiftText.Count(heard), t = SwiftText.Count(term);
        if (h < CorrectionFilter.MinLetters || t < CorrectionFilter.MinLetters || (double)Math.Min(h, t) < Math.Max(h, t) * CorrectionFilter.PhoneticSimilarity) return false;
        var similarity = CleanupText.Similarity(heard, term);
        // Edit similarity alone is too loose for short terms ("mean" is 0.75 from "Sean").
        if (similarity >= CorrectionFilter.DirectSimilarity && t >= 6) return true;
        return similarity >= CorrectionFilter.PhoneticSimilarity && CorrectionFilter.PhoneticKey(heard) == CorrectionFilter.PhoneticKey(term);
    }

    List<DictionaryEntry> ExactEntries(string text)
    {
        var chars = SwiftText.Characters(text);
        var words = TermText.Tokens(chars);
        var present = new HashSet<string>();
        var enabled = entries.Where(e => e.Enabled).ToList();
        var phrases = enabled.SelectMany(e => e.Variants.Prepend(e.Preferred));
        int maxWords = phrases.Select(p => TermText.PhraseKey(p)?.Split(' ').Length).Where(n => n is not null).Select(n => n!.Value).DefaultIfEmpty(0).Max();
        for (int i = 0; i < words.Count; i++)
            for (int length = 1; length <= Math.Max(1, Math.Min(maxWords, words.Count - i)); length++)
            {
                if (i + length > words.Count) continue;
                var span = words.GetRange(i, length);
                if (!SpacedPhrase(span, chars)) break;
                present.Add(string.Join(" ", span.Select(w => w.Folded)));
            }
        return entries.Where(entry => entry.Enabled && entry.Variants.Prepend(entry.Preferred).Any(phrase =>
        {
            var key = TermText.PhraseKey(phrase) ?? WordKey(phrase);
            return key is not null && present.Contains(key);
        })).ToList();
    }

    /// <summary>Key for a preferred spelling that contains punctuation ("Node.js" -> "node js").</summary>
    static string? WordKey(string phrase)
    {
        var words = TermText.Tokens(phrase);
        return words.Count == 0 ? null : string.Join(" ", words.Select(w => w.Folded));
    }

    static bool SpacedPhrase(List<TermText.Token> span, IReadOnlyList<string> chars)
    {
        for (int k = 1; k < span.Count; k++)
        {
            int from = span[k - 1].End, to = span[k].Start;
            if (to <= from) return false;
            for (int c = from; c < to; c++) if (chars[c] != " " && chars[c] != "\t") return false;
        }
        return true;
    }

    static bool AtSentenceStart(int index, IReadOnlyList<string> chars)
    {
        int j = index - 1;
        while (j >= 0)
        {
            var c = chars[j];
            if (TermText.IsNewline(c)) return true;
            if (SwiftText.IsWhitespace(c) || c is "\"" or "'" or "“" or "‘" or "(" or "[" or "{") { j--; continue; }
            return c is "." or "!" or "?";
        }
        return true;
    }

    static string Cased(string preferred, string original, bool sentenceStart)
    {
        var o = SwiftText.Characters(original);
        var p = SwiftText.Characters(preferred);
        if (!sentenceStart || o.Count == 0 || !SwiftText.IsUppercase(o[0]) || p.Any(SwiftText.IsUppercase) || p.Count == 0) return preferred;
        return SwiftText.Uppercased(p[0]) + TermText.Slice(p, 1, p.Count);
    }
}

/// <summary>A word Murmur keeps hearing that is neither ordinary English nor in the dictionary, offered for
/// one-tap approval. <c>Heard</c> are the transcript spellings, most frequent first; <c>Preferred</c> is a guess.</summary>
public sealed record DictionarySuggestion(string Preferred, IReadOnlyList<string> Heard, int Dictations) : IEquatable<DictionarySuggestion>
{
    public string Id => TermText.Fold(Heard[0]);
    public bool Equals(DictionarySuggestion? other) => other is not null && Preferred == other.Preferred && Heard.SequenceEqual(other.Heard) && Dictations == other.Dictations;
    public override int GetHashCode() => HashCode.Combine(Preferred, Dictations);
}

/// <summary>
/// Learning from corrections needs a fix made after pasting, which some people never make; this needs
/// only what was said. A group of similar spellings qualifies when it spans <c>minDictations</c> dictations
/// and Whisper spelled it more than one way, or when one spelling spans <c>minSingle</c> dictations.
/// <c>Preferred</c> is the spelling cleanup wrote most often in the final text, else the most heard one.
/// </summary>
public static class DictionarySuggestions
{
    // shortcut: greedy grouping of single words by spelling; two-word forms ("True Naz") are not found.
    public static IReadOnlyList<DictionarySuggestion> Find(IReadOnlyList<(string Raw, string Final)> dictations, IEnumerable<DictionaryEntry> entries, IReadOnlySet<string> dismissed,
        Func<string, bool> isCommonWord, int minDictations = 2, int minSingle = 4, int limit = 12)
    {
        var known = new HashSet<string>();
        foreach (var entry in entries) { known.Add(TermText.Fold(entry.Preferred)); foreach (var v in entry.Variants) known.Add(TermText.Fold(v)); }
        var common = new Dictionary<string, bool>();
        (string Word, string Key)? Candidate(TermText.Token token)
        {
            var word = token.Text;
            foreach (var suffix in new[] { "'s", "’s" }) if (TermText.HasSuffix(word, suffix)) word = TermText.DropLast(word, 2);
            var key = TermText.Fold(word);
            if (SwiftText.Count(key) < 4 || !SwiftText.Characters(key).All(c => SwiftText.IsLetter(c) || c is "'" or "-") || known.Contains(key)) return null;
            if (!common.ContainsKey(key)) common[key] = isCommonWord(word) || isCommonWord(key);
            return common[key] ? null : (word, key);
        }
        var seenIn = new Dictionary<string, HashSet<int>>();
        var spelled = new Dictionary<string, Dictionary<string, int>>();
        var written = new Dictionary<string, Dictionary<string, int>>();
        static void Bump(Dictionary<string, Dictionary<string, int>> map, string key, string word)
        {
            if (!map.TryGetValue(key, out var counts)) map[key] = counts = new();
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        for (int index = 0; index < dictations.Count; index++)
        {
            foreach (var token in TermText.Tokens(dictations[index].Raw))
            {
                if (Candidate(token) is not { } found1 || dismissed.Contains(found1.Key)) continue;
                var (word, key) = found1;
                if (!seenIn.TryGetValue(key, out var set)) seenIn[key] = set = new();
                set.Add(index); Bump(spelled, key, word);
            }
            foreach (var token in TermText.Tokens(dictations[index].Final))
            {
                if (Candidate(token) is not { } found2) continue;
                var (word, key) = found2;
                Bump(written, key, word);
            }
        }
        // Swift's max(by: ($0.value, $1.key) < ($1.value, $0.key)): the highest count, then the smallest key.
        static string Top(Dictionary<string, int> counts)
        {
            KeyValuePair<string, int>? best = null;
            foreach (var e in counts)
                if (best is null || best.Value.Value < e.Value || (best.Value.Value == e.Value && string.CompareOrdinal(e.Key, best.Value.Key) < 0)) best = e;
            return best!.Value.Key;
        }
        bool Alike(string a, string b) => a == b || CleanupText.Similarity(a, b) >= 0.7 || DictionaryStore.SoundsLike(CorrectionFilter.Letters(a), CorrectionFilter.Letters(b));
        var keys = seenIn.Keys.OrderByDescending(k => seenIn[k].Count).ThenBy(k => k, StringComparer.Ordinal).ToList();
        var used = new HashSet<string>();
        var found = new List<DictionarySuggestion>();
        foreach (var key in keys)
        {
            if (used.Contains(key)) continue;
            var group = keys.Where(k => !used.Contains(k) && Alike(k, key)).ToList();
            used.UnionWith(group);
            var count = group.SelectMany(g => seenIn[g]).ToHashSet().Count;
            if (!(group.Count > 1 ? count >= minDictations : count >= minSingle)) continue;
            var heard = group.Select(g => Top(spelled[g])).ToList();
            // A spelling cleanup wrote but Whisper never heard is cleanup's correction, so it ranks first.
            (int, int, string) Rank(KeyValuePair<string, Dictionary<string, int>> item) => (group.Contains(item.Key) ? 0 : 1, item.Value.Values.Sum(), item.Key);
            KeyValuePair<string, Dictionary<string, int>>? cleaned = null;
            foreach (var w in written.Where(w => group.Any(g => Alike(w.Key, g))))
            {
                var (a0, a1, a2) = cleaned is { } c ? Rank(c) : (0, 0, "");
                var (b0, b1, b2) = Rank(w);
                if (cleaned is null || a0 < b0 || (a0 == b0 && (a1 < b1 || (a1 == b1 && string.CompareOrdinal(a2, b2) < 0)))) cleaned = w;
            }
            found.Add(new DictionarySuggestion(cleaned is { } best ? Top(best.Value) : heard[0], heard, count));
        }
        return found.OrderByDescending(s => s.Dictations).ThenBy(s => s.Id, StringComparer.Ordinal).Take(limit).ToList();
    }
}
