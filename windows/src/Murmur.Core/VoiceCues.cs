// Port of app/VoiceCues.swift.
using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Murmur.Core;

// Voice cues: a cue is a spoken phrase and the text to insert in its place, stored as cues.json beside
// dictionary.json. Matching works on the raw transcript, before the dictionary, so a phrase is stored the way
// speech recognition writes it. The expansion is never processed (README, Cues).

public sealed record VoiceCue
{
    readonly DateTime createdAt;

    [JsonPropertyName("createdAt"), JsonConverter(typeof(SwiftDateConverter))]
    public DateTime CreatedAt { get => createdAt; init => createdAt = SwiftDate.Normalize(value); }
    [JsonPropertyName("id"), JsonConverter(typeof(UuidConverter))] public Guid Id { get; init; }
    [JsonPropertyName("phrase")] public string Phrase { get; init; } = "";
    /// <summary>Inserted exactly as stored, newlines included.</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

/// <summary>A dictation cut at its cue commands.</summary>
public abstract record VoiceCuePiece
{
    VoiceCuePiece() { }

    /// <summary>Spoken words between commands, still raw; processed like any dictation.</summary>
    public sealed record Text(string Raw) : VoiceCuePiece;
    /// <summary>The cue text, and the ", . ! ? ; :" that followed the command.</summary>
    public sealed record Expansion(string Value, string Closing) : VoiceCuePiece;
}

public static class VoiceCueText
{
    sealed class Data
    {
        public string FileName { get; set; } = "";
        public int MaxPhraseLength { get; set; }
        public int MaxTextLength { get; set; }
        public string[] LeadWords { get; set; } = [];
        public string[] Reserved { get; set; } = [];
        public string[] Closing { get; set; } = [];
    }

    internal static readonly string FileName;
    internal static readonly int MaxPhraseLength, MaxTextLength;
    public static readonly FrozenSet<string> LeadWords, Reserved, Closing;

    static VoiceCueText()
    {
        var data = SharedData.Load<Data>("voice_cues");
        FileName = data.FileName;
        MaxPhraseLength = data.MaxPhraseLength; MaxTextLength = data.MaxTextLength;
        LeadWords = data.LeadWords.ToFrozenSet(); Reserved = data.Reserved.ToFrozenSet(); Closing = data.Closing.ToFrozenSet();
    }

    /// <summary>A phrase's comparable words: case, punctuation, apostrophes and hyphens ignored ("John's" = "Johns").</summary>
    public static List<string> Words(string phrase) => TermText.Tokens(phrase).Select(Word).ToList();

    static string Word(TermText.Token token) => string.Concat(SwiftText.Characters(token.Folded).Where(c => c != "'" && c != "-"));

    /// <summary>
    /// Null when no cue fires. A whole dictation that is just a phrase becomes the cue alone; otherwise
    /// <c>insert &lt;phrase&gt;</c> or <c>paste &lt;phrase&gt;</c>, optionally followed by <c>here</c>, is replaced in full. A sentence end
    /// inside the command breaks it ("insert my link. Here we go" keeps "Here"). The longest phrase wins.
    /// </summary>
    public static List<VoiceCuePiece>? Split(string raw, IEnumerable<VoiceCue> cues)
    {
        // A phrase with no words can never match (Swift would trap on its empty range).
        var phrases = cues.Select(c => (Words: Words(c.Phrase), c.Text)).Where(p => p.Words.Count > 0).OrderByDescending(p => p.Words.Count).ToList();
        var chars = SwiftText.Characters(raw);
        var tokens = TermText.Tokens(chars);
        var spoken = tokens.Select(Word).ToList();
        if (phrases.Count == 0 || tokens.Count == 0) return null;
        var whole = phrases.FirstOrDefault(p => p.Words.SequenceEqual(spoken));
        if (whole.Words is not null) return [new VoiceCuePiece.Expansion(whole.Text, "")];
        bool Joined(int from, int to)
        {
            for (int k = tokens[from].End; k < tokens[to].Start; k++) if (chars[k] is "." or "!" or "?") return false;
            return true;
        }
        var pieces = new List<VoiceCuePiece>();
        int cursor = 0, i = 0;
        while (i < tokens.Count)
        {
            if (!LeadWords.Contains(spoken[i])) { i++; continue; }
            var phrase = phrases.FirstOrDefault(p => i + p.Words.Count < tokens.Count && spoken.Skip(i + 1).Take(p.Words.Count).SequenceEqual(p.Words) && Joined(i, i + p.Words.Count));
            if (phrase.Words is null) { i++; continue; }
            int last = i + phrase.Words.Count;
            if (last + 1 < tokens.Count && spoken[last + 1] == "here" && Joined(last, last + 1)) last++;
            int end = tokens[last].End;
            int k = end;
            while (k < chars.Count && chars[k] is " " or "\t") k++;
            int run = k;
            while (run < chars.Count && Closing.Contains(chars[run])) run++;
            var punctuation = TermText.Slice(chars, k, run);
            if (run > k) end = run;
            var before = TermText.Slice(chars, cursor, tokens[i].Start);
            if (!SwiftText.Characters(before).All(SwiftText.IsWhitespace)) pieces.Add(new VoiceCuePiece.Text(SwiftText.TrimWhitespaceAndNewlines(before)));
            pieces.Add(new VoiceCuePiece.Expansion(phrase.Text, punctuation));
            cursor = end; i = last + 1;
        }
        if (pieces.Count == 0) return null;
        var rest = TermText.Slice(chars, cursor, chars.Count);
        if (!SwiftText.Characters(rest).All(SwiftText.IsWhitespace)) pieces.Add(new VoiceCuePiece.Text(SwiftText.TrimWhitespaceAndNewlines(rest)));
        return pieces;
    }

    /// <summary>
    /// The finished text. <c>processed[i]</c> is the processed form of <c>Text</c> piece i. One space joins neighbours
    /// unless one side already has whitespace or the next starts with closing punctuation. Words after an
    /// expansion that did not end a sentence keep the case they were spoken in (a separately cleaned segment
    /// would otherwise start with a capital, unless <c>keepsCase</c> says a dictionary spelling made it), and a
    /// segment before an expansion loses a period only cleanup added.
    /// </summary>
    public static string Join(IReadOnlyList<VoiceCuePiece> pieces, IReadOnlyList<string> processed, Func<string, bool>? keepsCase = null)
    {
        keepsCase ??= _ => false;
        var output = new System.Text.StringBuilder();
        bool midSentence = false;
        void Glue(string text)
        {
            var first = SwiftText.Characters(text);
            var last = SwiftText.Characters(output.ToString());
            if (first.Count == 0 || last.Count == 0) { output.Append(text); return; }
            output.Append(SwiftText.IsWhitespace(last[^1]) || SwiftText.IsWhitespace(first[0]) || Closing.Contains(first[0]) ? text : " " + text);
        }
        for (int index = 0; index < pieces.Count; index++)
        {
            switch (pieces[index])
            {
                case VoiceCuePiece.Expansion e:
                    Glue(e.Value); output.Append(e.Closing);
                    var tail = SwiftText.Characters(e.Value);
                    midSentence = e.Closing.Length == 0 ? !(tail.Count > 0 && TermText.IsNewline(tail[^1])) : !SwiftText.Characters(e.Closing).Any(c => c is "." or "!" or "?");
                    break;
                case VoiceCuePiece.Text t:
                    var text = SwiftText.TrimWhitespaceAndNewlines(processed[index]);
                    if (text.Length == 0) continue;
                    if (midSentence) text = UnCapitalized(text, t.Raw, keepsCase);
                    if (TermText.HasSuffix(text, ".") && !TermText.HasSuffix(text, "..") && !TermText.HasSuffix(t.Raw, ".") && index + 1 < pieces.Count) text = TermText.DropLast(text, 1);
                    Glue(text); midSentence = false;
                    break;
            }
        }
        return output.ToString();
    }

    static string UnCapitalized(string text, string spoken, Func<string, bool> keepsCase)
    {
        var textTokens = TermText.Tokens(text);
        var spokenChars = SwiftText.Characters(spoken);
        var spokenTokens = TermText.Tokens(spokenChars);
        if (textTokens.Count == 0 || spokenTokens.Count == 0) return text;
        var first = textTokens[0]; var heard = spokenTokens[0];
        var firstChars = SwiftText.Characters(first.Text);
        if (first.Start != 0 || !SwiftText.IsLowercase(spokenChars[heard.Start]) || heard.Folded != first.Folded
            || !SwiftText.IsUppercase(firstChars[0]) || firstChars.Skip(1).Any(SwiftText.IsUppercase)
            || first.Folded == "i" || TermText.HasPrefix(first.Folded, "i'") || keepsCase(heard.Text)) return text;
        var all = SwiftText.Characters(text);
        return SwiftText.Lowercased(all[0]) + TermText.Slice(all, 1, all.Count);
    }
}

/// <summary>
/// Persists cues as <c>cues.json</c> in <see cref="Directory"/> (the dictionary's folder). Every mutation writes through.
/// A file that cannot be read leaves the store empty and read-only, so dictation still works and a save
/// cannot overwrite the damaged file.
/// </summary>
public sealed class VoiceCueStore
{
    public static readonly string FileName = VoiceCueText.FileName;
    public static readonly int MaxPhraseLength = VoiceCueText.MaxPhraseLength;
    public static readonly int MaxTextLength = VoiceCueText.MaxTextLength;

    sealed class FileFormat
    {
        [JsonPropertyName("cues")] public List<VoiceCue> Cues { get; set; } = [];
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
    }

    public string Directory { get; }
    List<VoiceCue> cues = [];
    public bool Unreadable { get; private set; }
    string StorePath => Path.Combine(Directory, FileName);

    public IReadOnlyList<VoiceCue> Cues => cues.ToArray();

    public VoiceCueStore(string directory)
    {
        Directory = directory;
        try { Reload(); Unreadable = false; } catch (Exception) { Unreadable = true; }
    }

    /// <summary>Re-reads from disk; an absent file means no cues (e.g. after Delete all data).</summary>
    public void Reload()
    {
        cues = [];
        PrivateFiles.PrepareDirectory(Directory);
        var data = PrivateFiles.Read(StorePath);
        if (data is null) return;
        var decoded = StoreJson.Decode<FileFormat>(data);
        if (decoded.Version != 1) throw new StoreError("Unsupported cues version");
        cues = decoded.Cues;
    }

    void Save() => PrivateFiles.Write(StoreJson.Encode(new FileFormat { Cues = cues }), StorePath);

    public VoiceCue Add(string phrase, string text, DateTime? now = null)
    {
        var cue = Validate(new VoiceCue { Id = Guid.NewGuid(), Phrase = phrase, Text = text, CreatedAt = now ?? DateTime.UtcNow });
        cues.Add(cue);
        Save();
        return cue;
    }

    public void Update(Guid id, string phrase, string text)
    {
        int index = cues.FindIndex(c => c.Id == id);
        if (index < 0) throw new StoreError("Cue not found");
        cues[index] = Validate(new VoiceCue { Id = id, Phrase = phrase, Text = text, CreatedAt = cues[index].CreatedAt });
        Save();
    }

    public void Delete(Guid id)
    {
        int index = cues.FindIndex(c => c.Id == id);
        if (index < 0) throw new StoreError("Cue not found");
        if (Unreadable) throw new StoreError("Cues could not be read");
        cues.RemoveAt(index);
        Save();
    }

    VoiceCue Validate(VoiceCue cue)
    {
        if (Unreadable) throw new StoreError("Cues could not be read");
        var phrase = TermText.CollapseWhitespace(cue.Phrase);
        var key = VoiceCueText.Words(phrase);
        int length = SwiftText.Count(phrase);
        if (length < 1 || length > MaxPhraseLength || !SwiftText.Characters(phrase).Any(SwiftText.IsLetter) || key.Count == 0)
            throw new StoreError($"The phrase needs at least one letter and at most {MaxPhraseLength} characters");
        if (key.Count == 1 && VoiceCueText.Reserved.Contains(key[0])) throw new StoreError($"“{phrase}” is already a command word");
        if (cues.Any(c => c.Id != cue.Id && VoiceCueText.Words(c.Phrase).SequenceEqual(key))) throw new StoreError("Another cue already uses that phrase");
        int textLength = SwiftText.Count(cue.Text);
        if (textLength < 1 || textLength > MaxTextLength || SwiftText.Characters(cue.Text).All(SwiftText.IsWhitespace))
            throw new StoreError($"The text needs 1 to {MaxTextLength} characters");
        return new VoiceCue { Id = cue.Id, Phrase = phrase, Text = cue.Text, CreatedAt = cue.CreatedAt };
    }
}
